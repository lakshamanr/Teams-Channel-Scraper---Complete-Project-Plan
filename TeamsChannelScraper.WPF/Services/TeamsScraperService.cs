using Microsoft.Playwright;
using TeamsChannelScraper.WPF.Models;
using TeamsChannelScraper.WPF.Utilities;

namespace TeamsChannelScraper.WPF.Services;

public sealed class TeamsScraperService : ITeamsScraper
{
    private readonly LoggerService _logger;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private bool _disposed;

    public TeamsScraperService(LoggerService logger)
    {
        _logger = logger;
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

    public async Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        var path = SessionFilePath;
        if (!File.Exists(path)) return false;

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
        if (age.TotalDays > Constants.SessionMaxAgeDays)
        {
            _logger.Log("Session expired, will re-authenticate.");
            return false;
        }

        await EnsureBrowserAsync(false, cancellationToken);
        _context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            StorageStatePath = path
        });
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
            await _page.GotoAsync("https://teams.microsoft.com", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            // Fill email
            await _page.Locator("input#i0116").WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            await _page.Locator("input#i0116").FillAsync(email);
            await _page.Locator("input[type=submit]").ClickAsync();

            // Fill password
            await _page.Locator("input#i0118").WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            await _page.Locator("input#i0118").FillAsync(password);
            await _page.Locator("input[type=submit]").ClickAsync();

            // Stay signed in prompt (optional)
            try
            {
                var staySignedIn = _page.Locator("input#idBtn_Back");
                await staySignedIn.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
                await staySignedIn.ClickAsync(); // click "No" to stay-signed-in
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

        try
        {
            if (_page is null)
                throw new TeamsScraperException("Not authenticated. Call LoginAsync or TryRestoreSessionAsync first.");

            progress.Report(new ScrapingProgressUpdate(0, config.MaxMessages, "Navigating to channel...", true));

            await RetryAsync(async () =>
            {
                await _page.GotoAsync("https://teams.microsoft.com", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                return true;
            }, cancellationToken);

            int previousCount = 0;
            int noNewMessageRounds = 0;

            for (int scroll = 0; scroll < Constants.MaxScrollAttempts && messages.Count < config.MaxMessages; scroll++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var articles = _page.Locator("[role='article']");
                int count = await articles.CountAsync();

                for (int i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var el = articles.Nth(i);
                    var msg = await ParseMessageElementAsync(el, cancellationToken);
                    if (msg is not null && !messages.Any(m => m.Id == msg.Id))
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

                // Scroll up to load older messages
                await _page.EvaluateAsync("() => { const el = document.querySelector('[data-tid=\"message-pane\"]'); if (el) el.scrollTop = 0; }");
                await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await Task.Delay(Constants.ScrollPauseMs, cancellationToken);
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

    private async Task<TeamMessage?> ParseMessageElementAsync(ILocator el, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var id = await el.GetAttributeAsync("data-message-id") ?? Guid.NewGuid().ToString();
            var parentId = await el.GetAttributeAsync("data-reply-to") ?? string.Empty;

            var author = await SafeGetTextAsync(el, "[data-testid='message-author']")
                      ?? await SafeGetTextAsync(el, ".author-name")
                      ?? "Unknown";

            var timestampRaw = await SafeGetAttributeAsync(el, "time[datetime]", "datetime");
            DateTime.TryParse(timestampRaw, out var timestamp);

            var content = await SafeGetTextAsync(el, "[data-testid='message-body']")
                       ?? await SafeGetTextAsync(el, ".message-body")
                       ?? string.Empty;

            return new TeamMessage
            {
                Id = id,
                ParentId = parentId,
                Author = author,
                Content = content,
                RawHtml = string.Empty,
                Timestamp = timestamp == default ? DateTime.UtcNow : timestamp
            };
        }
        catch
        {
            return null;
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
