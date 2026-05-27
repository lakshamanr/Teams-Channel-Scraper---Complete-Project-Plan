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

    // Candidate selectors for the Teams scrollable message container (tried in order)
    private static readonly string[] PaneCandidates =
    [
        "[data-tid='message-pane']",           // classic Teams
        "[data-tid='messageList']",            // some classic versions
        "[role='log']",                        // new Teams fallback
        "#message-list",                       // new Teams
        ".fui-ChatMessageList",                // Fluent UI new Teams
        "[data-testid='message-list']",        // test-id based
        ".ts-message-list-content",            // Teams internal
        "[aria-label='Conversation']",         // aria-label based
    ];

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
            _logger.Log("Navigating to Teams...");
            // Navigate to Teams — it will redirect to login.microsoftonline.com
            await _page.GotoAsync("https://teams.microsoft.com",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            // Wait for the Microsoft login redirect to complete
            _logger.Log("Waiting for Microsoft login page...");
            await _page.WaitForURLAsync(
                url => url.Contains("login.microsoftonline.com") || url.Contains("login.microsoft.com"),
                new PageWaitForURLOptions { Timeout = 30000 });
            _logger.Log($"Login page loaded: {_page.Url}");

            // ── Email step ──────────────────────────────────────────────
            _logger.Log("Filling email...");
            var emailInput = _page.Locator("input[type='email'], input#i0116").First;
            await emailInput.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            await emailInput.ClickAsync();
            await emailInput.FillAsync(email);
            await Task.Delay(Random.Shared.Next(400, 800), cancellationToken);

            var nextBtn = _page.Locator("input[type='submit'], button[type='submit']").First;
            await nextBtn.ClickAsync();
            _logger.Log("Email submitted — waiting for password field...");

            // ── Password step ───────────────────────────────────────────
            var passwordInput = _page.Locator("input[type='password'], input#i0118").First;
            await passwordInput.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            await passwordInput.ClickAsync();
            await passwordInput.FillAsync(password);
            await Task.Delay(Random.Shared.Next(400, 800), cancellationToken);

            var signInBtn = _page.Locator("input[type='submit'], button[type='submit']").First;
            await signInBtn.ClickAsync();
            _logger.Log("Password submitted — waiting for Teams to load...");

            // ── "Stay signed in?" prompt (optional) ────────────────────
            try
            {
                var staySignedIn = _page.Locator("input#idBtn_Back");
                await staySignedIn.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
                await staySignedIn.ClickAsync();
                _logger.Log("Dismissed 'Stay signed in' prompt.");
            }
            catch { /* prompt not shown — continue */ }

            // Wait for Teams app to finish loading (URL changes away from login)
            await _page.WaitForURLAsync(
                url => !url.Contains("login.microsoftonline.com") && !url.Contains("login.microsoft.com"),
                new PageWaitForURLOptions { Timeout = 45000 });

            _logger.Log($"Teams loaded. Current URL: {_page.Url}");
            await Task.Delay(2000, cancellationToken); // let the SPA finish rendering

            await SaveSessionAsync(cancellationToken);
            _logger.Log("Login successful. Session saved.");
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
        var seenIds  = new HashSet<string>();

        try
        {
            if (_page is null)
                throw new TeamsScraperException("Not authenticated. Call LoginAsync or TryRestoreSessionAsync first.");

            // Step 1 — navigate to the target channel
            progress.Report(new ScrapingProgressUpdate(0, config.MaxMessages, "Navigating to channel...", true));
            await NavigateToChannelAsync(config, cancellationToken);

            // Step 2 — detect which element is the scrollable message container
            var paneSelector = await DetectPaneSelectorAsync(cancellationToken);
            _logger.Log($"Message pane detected: {paneSelector ?? "(none — will use page scroll)"}");

            // Step 3 — scroll all the way to the VERY TOP (oldest messages) before collecting
            progress.Report(new ScrapingProgressUpdate(0, config.MaxMessages, "Scrolling to first message...", true));
            await ScrollToTopHumanAsync(paneSelector, cancellationToken);

            // Step 4 — collect messages, scrolling DOWN through the thread
            int noNewMessageRounds = 0;
            int previousCount     = 0;

            for (int scroll = 0; scroll < Constants.MaxScrollAttempts && messages.Count < config.MaxMessages; scroll++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var articles = _page.Locator("[role='article']");
                int count    = await articles.CountAsync();
                _logger.Log($"Scroll pass {scroll + 1}: {count} article elements visible.");

                for (int i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var el  = articles.Nth(i);
                    var msg = await ParseMessageElementAsync(el, cancellationToken);
                    if (msg is null) continue;

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

                await HumanScrollDownAsync(paneSelector, cancellationToken);
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

    // ── Pane detection ─────────────────────────────────────────────────────

    /// <summary>
    /// Tries each known selector and returns the first one that resolves to a
    /// scrollable element (scrollHeight > clientHeight). Falls back to null,
    /// in which case callers use window/page scroll.
    /// </summary>
    private async Task<string?> DetectPaneSelectorAsync(CancellationToken ct)
    {
        foreach (var sel in PaneCandidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var found = await _page!.EvaluateAsync<bool>(
                    $"() => {{ const el = document.querySelector('{sel}'); " +
                    $"return el !== null && el.scrollHeight > el.clientHeight; }}");
                if (found)
                    return sel;
            }
            catch { /* selector syntax error or page not ready — continue */ }
        }

        // Last resort: find the deepest div that has overflow scroll/auto and contains messages
        var dynamic = await _page!.EvaluateAsync<string?>("""
            () => {
                const msgs = document.querySelectorAll("[role='article']");
                if (!msgs.length) return null;
                let el = msgs[0].parentElement;
                while (el && el !== document.body) {
                    const style = window.getComputedStyle(el);
                    if ((style.overflow === 'auto' || style.overflow === 'scroll' ||
                         style.overflowY === 'auto' || style.overflowY === 'scroll') &&
                        el.scrollHeight > el.clientHeight) {
                        return el.getAttribute('data-tid') || el.id
                            ? '[data-tid="' + el.getAttribute('data-tid') + '"]'
                            : null;
                    }
                    el = el.parentElement;
                }
                return null;
            }
            """);

        if (dynamic is not null)
            _logger.Log($"Pane detected dynamically: {dynamic}");
        else
            _logger.LogWarning("Could not detect message pane — will use window scroll.");

        return dynamic;
    }

    // ── Channel navigation ─────────────────────────────────────────────────

    private async Task NavigateToChannelAsync(ScrapingConfig config, CancellationToken ct)
    {
        _logger.Log("Navigating to Teams...");
        await RetryAsync(async () =>
        {
            await _page!.GotoAsync("https://teams.microsoft.com",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            // Give the SPA time to settle and load the channel list
            await Task.Delay(Random.Shared.Next(2000, 3500), ct);
            return true;
        }, ct);

        _logger.Log($"Teams URL: {_page!.Url}");

        if (string.IsNullOrWhiteSpace(config.ChannelName)) return;

        await Task.Delay(Random.Shared.Next(800, 1500), ct);

        try
        {
            // Strategy 1: Teams uses span[id^="title-channel-list-item-"] for channel names
            var channelLocator = _page
                .Locator("span[id^='title-channel-list-item-']")
                .Filter(new LocatorFilterOptions { HasText = config.ChannelName })
                .First;

            bool found = false;
            try
            {
                await channelLocator.WaitForAsync(new LocatorWaitForOptions { Timeout = 6000 });
                found = true;
            }
            catch { }

            if (!found)
            {
                // Strategy 2: ARIA treeitem by display text
                _logger.Log("Strategy 1 failed, trying ARIA treeitem...");
                channelLocator = _page
                    .GetByRole(AriaRole.Treeitem)
                    .Filter(new LocatorFilterOptions { HasText = config.ChannelName })
                    .First;
                await channelLocator.WaitForAsync(new LocatorWaitForOptions { Timeout = 8000 });
                found = true;
            }

            if (found)
            {
                await channelLocator.HoverAsync();
                await Task.Delay(Random.Shared.Next(300, 600), ct);
                await channelLocator.ClickAsync();
                _logger.Log($"Clicked channel: {config.ChannelName}");
                await Task.Delay(Random.Shared.Next(1500, 2500), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning($"Could not navigate to '{config.ChannelName}': {ex.Message}. Using current view.");
        }
    }

    // ── Human-like scrolling ───────────────────────────────────────────────

    private async Task ScrollToTopHumanAsync(string? paneSelector, CancellationToken ct)
    {
        _logger.Log("Scrolling to top of message history (oldest messages first)...");
        int stuckCount = 0;
        int lastScrollTop = int.MaxValue;

        for (int attempt = 0; attempt < 100; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var scrollTop = await GetScrollTopAsync(paneSelector);
            if (scrollTop <= 10) break;

            // Detect if we're stuck (scroll position not changing)
            if (Math.Abs(scrollTop - lastScrollTop) < 5)
            {
                stuckCount++;
                if (stuckCount >= 5) break; // truly at top
                await Task.Delay(Random.Shared.Next(800, 1500), ct); // wait for Teams to load more
                continue;
            }

            stuckCount   = 0;
            lastScrollTop = scrollTop;

            var scrollBy = Random.Shared.Next(300, 700);
            await ScrollByAsync(paneSelector, -scrollBy);

            var delay = Random.Shared.Next(300, 800);
            if (Random.Shared.Next(0, 7) == 0) delay += Random.Shared.Next(1000, 2500);
            await Task.Delay(delay, ct);
        }

        await Task.Delay(Random.Shared.Next(2000, 3500), ct);
        _logger.Log("Reached top — starting collection from oldest messages.");
    }

    private async Task HumanScrollDownAsync(string? paneSelector, CancellationToken ct)
    {
        var scrollBy = Random.Shared.Next(200, 500);
        await ScrollByAsync(paneSelector, scrollBy);

        var delay = Random.Shared.Next(900, 2500);
        if (Random.Shared.Next(0, 5) == 0) delay += Random.Shared.Next(1500, 4000);
        await Task.Delay(delay, ct);
    }

    private async Task<int> GetScrollTopAsync(string? paneSelector)
    {
        if (paneSelector is null)
            return await _page!.EvaluateAsync<int>("() => window.scrollY");

        return await _page!.EvaluateAsync<int>(
            $"() => {{ const el = document.querySelector('{paneSelector}'); return el ? Math.floor(el.scrollTop) : window.scrollY; }}");
    }

    private async Task ScrollByAsync(string? paneSelector, int delta)
    {
        if (paneSelector is null)
        {
            await _page!.EvaluateAsync($"() => window.scrollBy(0, {delta})");
            return;
        }

        if (delta < 0)
        {
            // Scroll UP (toward oldest messages)
            await _page!.EvaluateAsync(
                $"() => {{ const el = document.querySelector('{paneSelector}'); if (el) el.scrollTop = Math.max(0, el.scrollTop - {Math.Abs(delta)}); else window.scrollBy(0, {delta}); }}");
        }
        else
        {
            // Scroll DOWN (reveal newer messages)
            await _page!.EvaluateAsync(
                $"() => {{ const el = document.querySelector('{paneSelector}'); if (el) el.scrollTop += {delta}; else window.scrollBy(0, {delta}); }}");
        }
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
                      ?? await SafeGetTextAsync(el, "[class*='author']", cancellationToken)
                      ?? "Unknown";

            var timestampRaw = await SafeGetAttributeAsync(el, "time[datetime]", "datetime", cancellationToken);
            DateTime.TryParse(timestampRaw, out var timestamp);
            if (timestamp == default) timestamp = DateTime.UtcNow;

            var content = await SafeGetTextAsync(el, "[data-testid='message-body']", cancellationToken)
                       ?? await SafeGetTextAsync(el, ".message-body", cancellationToken)
                       ?? await SafeGetTextAsync(el, "[class*='body']", cancellationToken)
                       ?? string.Empty;

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
