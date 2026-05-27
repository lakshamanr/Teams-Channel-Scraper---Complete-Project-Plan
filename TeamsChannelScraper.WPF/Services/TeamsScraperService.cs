using System.IO;
using Microsoft.Playwright;
using TeamsChannelScraper.WPF.Models;
using TeamsChannelScraper.WPF.Utilities;

namespace TeamsChannelScraper.WPF.Services;

public sealed class TeamsScraperService : ITeamsScraper
{
    private readonly LoggerService _logger;
    private readonly ScrapingStateDb _db;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private bool _disposed;

    // JS snippet that resolves the Teams message-pane element (embeds safely in template strings)
    private const string GetPaneJs = "document.querySelector('[data-tid=\"message-pane\"]')";

    public TeamsScraperService(LoggerService logger, ScrapingStateDb db)
    {
        _logger = logger;
        _db     = db;
    }

    private string SessionFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Constants.AppDataFolder,
        Constants.SessionFileName);

    private async Task EnsureBrowserAsync(bool headless, CancellationToken cancellationToken)
    {
        if (_playwright is null)
            _playwright = await Playwright.CreateAsync();

        if (_browser is null)
        {
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless,
                Args = ["--disable-blink-features=AutomationControlled", "--no-sandbox"]
            });
        }
    }

    public async Task<bool> TryRestoreSessionAsync(bool headless = false, CancellationToken cancellationToken = default)
    {
        var path = SessionFilePath;
        if (!File.Exists(path)) return false;

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
        if (age.TotalDays > Constants.SessionMaxAgeDays)
        {
            _logger.Log("Session expired, will re-authenticate.");
            File.Delete(path);
            return false;
        }

        await EnsureBrowserAsync(headless, cancellationToken);
        try
        {
            _context = await _browser!.NewContextAsync(new BrowserNewContextOptions
            {
                StorageStatePath = path
            });
        }
        catch (PlaywrightException ex)
        {
            // Corrupt / unreadable session file — delete it so the next run falls through to LoginAsync
            _logger.LogWarning($"Session file unreadable ({ex.Message}). Deleting and re-authenticating.");
            try { File.Delete(path); } catch { }
            return false;
        }

        _page = await _context.NewPageAsync();
        _logger.Log("Session restored from disk.");
        return true;
    }

    public async Task LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        await EnsureBrowserAsync(false, cancellationToken);

        _context ??= await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
        });
        _page = await _context.NewPageAsync();

        try
        {
            await _page.GotoAsync("https://teams.microsoft.com",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            // Type email with human-like key-by-key delay
            await _page.Locator("input#i0116").WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            await HumanTypeAsync(_page.Locator("input#i0116"), email, cancellationToken);
            await Task.Delay(Random.Shared.Next(300, 700), cancellationToken);
            await _page.Locator("input[type=submit]").ClickAsync();

            // Type password
            await _page.Locator("input#i0118").WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            await HumanTypeAsync(_page.Locator("input#i0118"), password, cancellationToken);
            await Task.Delay(Random.Shared.Next(300, 700), cancellationToken);
            await _page.Locator("input[type=submit]").ClickAsync();

            // "Stay signed in?" prompt — click No
            try
            {
                var staySignedIn = _page.Locator("input#idBtn_Back");
                await staySignedIn.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
                await staySignedIn.ClickAsync();
            }
            catch { /* prompt not shown — continue */ }

            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            _logger.Log("Login successful.");

            await SaveSessionAsync(cancellationToken);
        }
        catch (PlaywrightException ex)
        {
            throw new TeamsScraperException("Login failed during browser interaction.", ex);
        }
    }

    public async Task<ScrapingResult> ScrapeChannelAsync(
        ScrapingConfig config,
        IProgress<ScrapingProgressUpdate> progress,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<TeamMessage>();
        // In-memory set for fast within-session dedup; SQLite handles cross-session dedup
        var seenIds  = new HashSet<string>();

        try
        {
            if (_page is null)
                throw new TeamsScraperException("Not authenticated. Call LoginAsync or TryRestoreSessionAsync first.");

            // Step 1 — navigate to Teams homepage then click into the target channel
            progress.Report(new ScrapingProgressUpdate(0, config.MaxMessages, "Navigating to channel...", true));
            await NavigateToChannelAsync(config, cancellationToken);

            // Step 2 — scroll all the way to the TOP of history (human-like) before collecting
            progress.Report(new ScrapingProgressUpdate(0, config.MaxMessages, "Scrolling to top of message history...", true));
            await ScrollToTopHumanAsync(cancellationToken);

            // Step 3 — scrape: collect visible messages, then scroll DOWN to reveal the next batch
            int noNewMessageRounds = 0;
            int previousCount     = 0;

            for (int scroll = 0; scroll < Constants.MaxScrollAttempts && messages.Count < config.MaxMessages; scroll++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var articles = _page.Locator("[role='article']");
                int count    = await articles.CountAsync();

                for (int i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var el  = articles.Nth(i);
                    var msg = await ParseMessageElementAsync(el, cancellationToken);
                    if (msg is null) continue;

                    // Fast in-memory check first, then persistent SQLite check
                    if (seenIds.Contains(msg.Id)) continue;
                    if (_db.IsAlreadyScraped(msg.Id, config.ChannelName)) continue;

                    seenIds.Add(msg.Id);
                    _db.MarkScraped(msg.Id, config.ChannelName);
                    messages.Add(msg);
                }

                progress.Report(new ScrapingProgressUpdate(
                    messages.Count, config.MaxMessages,
                    $"Scraped {messages.Count} messages...", false));

                if (messages.Count == previousCount)
                {
                    noNewMessageRounds++;
                    if (noNewMessageRounds >= 3) break;
                }
                else
                {
                    noNewMessageRounds = 0;
                }
                previousCount = messages.Count;

                // Human-like scroll DOWN to reveal the next window of messages
                await HumanScrollDownAsync(cancellationToken);
            }

            progress.Report(new ScrapingProgressUpdate(
                messages.Count, messages.Count,
                $"Complete. {messages.Count} messages collected.", false));

            return new ScrapingResult(messages.AsReadOnly(), false);
        }
        catch (OperationCanceledException)
        {
            return new ScrapingResult(messages.AsReadOnly(), true, "Cancelled by user.");
        }
        catch (PlaywrightException ex)
        {
            throw new TeamsScraperException("Browser error during scraping.", ex);
        }
    }

    // ── Channel navigation ─────────────────────────────────────────────────

    private async Task NavigateToChannelAsync(ScrapingConfig config, CancellationToken ct)
    {
        await RetryAsync(async () =>
        {
            await _page!.GotoAsync("https://teams.microsoft.com",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            return true;
        }, ct);

        if (string.IsNullOrWhiteSpace(config.ChannelName)) return;

        // Human pause before touching the sidebar
        await Task.Delay(Random.Shared.Next(800, 1800), ct);

        try
        {
            // Strategy 1: Teams uses span[id^="title-channel-list-item-"] for channel names
            var channelLocator = _page!
                .Locator("span[id^='title-channel-list-item-']")
                .Filter(new LocatorFilterOptions { HasText = config.ChannelName })
                .First;

            bool found = false;
            try
            {
                await channelLocator.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
                found = true;
            }
            catch { /* fall through to strategy 2 */ }

            if (!found)
            {
                // Strategy 2: ARIA treeitem by display text
                channelLocator = _page
                    .GetByRole(AriaRole.Treeitem)
                    .Filter(new LocatorFilterOptions { HasText = config.ChannelName })
                    .First;
                await channelLocator.WaitForAsync(new LocatorWaitForOptions { Timeout = 8000 });
            }

            // Human-like: hover first, small pause, then click
            await channelLocator.HoverAsync();
            await Task.Delay(Random.Shared.Next(300, 700), ct);
            await channelLocator.ClickAsync();
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Task.Delay(Random.Shared.Next(1000, 2000), ct);
            _logger.Log($"Navigated to channel: {config.ChannelName}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning($"Could not auto-navigate to '{config.ChannelName}': {ex.Message}. Using current view.");
        }
    }

    // ── Human-like scrolling ───────────────────────────────────────────────

    /// <summary>
    /// Scrolls the Teams message pane UP to the oldest loaded message using randomised
    /// increments and pauses. Teams lazily loads older history as scrollTop approaches 0,
    /// so we slow down near the top to allow fetches to complete.
    /// </summary>
    private async Task ScrollToTopHumanAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 80; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var scrollTop = await _page!.EvaluateAsync<int>(
                $"() => {{ const el = {GetPaneJs}; return el ? Math.floor(el.scrollTop) : -1; }}");

            if (scrollTop <= 10) break; // reached top of loaded history

            var scrollBy = Random.Shared.Next(300, 700);
            await _page.EvaluateAsync(
                $"() => {{ const el = {GetPaneJs}; if (el) el.scrollTop = Math.max(0, el.scrollTop - {scrollBy}); }}");

            // Randomised delay; occasionally pause longer to let Teams fetch older messages
            var delay = Random.Shared.Next(300, 900);
            if (Random.Shared.Next(0, 6) == 0) delay += Random.Shared.Next(1200, 3000);
            await Task.Delay(delay, ct);
        }

        // Final wait for Teams to finish loading the oldest batch
        await _page!.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Task.Delay(Random.Shared.Next(1500, 3000), ct);
        _logger.Log("Reached top of message history — starting collection.");
    }

    /// <summary>
    /// Scrolls DOWN a small random amount to reveal the next window of messages,
    /// mimicking a human reading through the thread.
    /// </summary>
    private async Task HumanScrollDownAsync(CancellationToken ct)
    {
        var scrollBy = Random.Shared.Next(200, 500);
        await _page!.EvaluateAsync(
            $"() => {{ const el = {GetPaneJs}; if (el) el.scrollTop += {scrollBy}; }}");

        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Randomised human-like pause; occasionally longer (simulates reading)
        var delay = Random.Shared.Next(900, 2500);
        if (Random.Shared.Next(0, 5) == 0) delay += Random.Shared.Next(1500, 4000);
        await Task.Delay(delay, ct);
    }

    // ── Message parsing ────────────────────────────────────────────────────

    private async Task<TeamMessage?> ParseMessageElementAsync(ILocator el, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var domId    = await el.GetAttributeAsync("data-message-id");
            var parentId = await el.GetAttributeAsync("data-reply-to") ?? string.Empty;

            var author = await SafeGetTextAsync(el, "[data-testid='message-author']", cancellationToken)
                      ?? await SafeGetTextAsync(el, ".author-name", cancellationToken)
                      ?? "Unknown";

            var timestampRaw = await SafeGetAttributeAsync(el, "time[datetime]", "datetime", cancellationToken);
            DateTime.TryParse(timestampRaw, out var timestamp);
            if (timestamp == default) timestamp = DateTime.UtcNow;

            var content = await SafeGetTextAsync(el, "[data-testid='message-body']", cancellationToken)
                       ?? await SafeGetTextAsync(el, ".message-body", cancellationToken)
                       ?? string.Empty;

            // Use the stable DOM ID when present; otherwise derive a deterministic content hash
            // so the SAME message always maps to the SAME ID across scroll passes.
            var stableId = !string.IsNullOrEmpty(domId)
                ? domId
                : ScrapingStateDb.ComputeContentId(author, content, timestamp);

            return new TeamMessage
            {
                Id        = stableId,
                ParentId  = parentId,
                Author    = author,
                Content   = content,
                RawHtml   = string.Empty,
                Timestamp = timestamp
            };
        }
        catch
        {
            return null;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Types text one character at a time with random inter-key delays to avoid bot detection.</summary>
    private static async Task HumanTypeAsync(ILocator locator, string text, CancellationToken ct)
    {
        await locator.ClickAsync();
        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            await locator.PressSequentiallyAsync(
                ch.ToString(),
                new LocatorPressSequentiallyOptions { Delay = Random.Shared.Next(60, 160) });
        }
    }

    private async Task<string?> SafeGetTextAsync(ILocator parent, string selector, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return await parent.Locator(selector).First.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 2000 }); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<string?> SafeGetAttributeAsync(ILocator parent, string selector, string attr, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return await parent.Locator(selector).First.GetAttributeAsync(attr); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<T> RetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        int delay = Constants.RetryBaseDelayMs;
        for (int attempt = 1; attempt <= Constants.MaxRetryAttempts; attempt++)
        {
            try { return await operation(); }
            catch (Exception ex) when (attempt < Constants.MaxRetryAttempts && ex is not OperationCanceledException)
            {
                _logger.Log($"Attempt {attempt} failed: {ex.Message}. Retrying in {delay}ms...");
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }
        throw new TeamsScraperException($"Operation failed after {Constants.MaxRetryAttempts} attempts.");
    }

    public async Task SaveSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_context is null) return;
        var path = SessionFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await _context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = path });
        _logger.Log("Session saved.");
    }

    public async Task DisposePlaywrightAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_context is not null) await _context.DisposeAsync();
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
