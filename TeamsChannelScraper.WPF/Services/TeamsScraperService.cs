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

    // Selectors that confirm Teams has fully initialised (any one present = ready).
    // IMPORTANT: attribute values use double quotes so they can be safely embedded
    // in single-quoted JS strings without conflicting with the string delimiter.
    private static readonly string[] TeamsReadySelectors =
    [
        "[data-tid=\"app-bar-wrapper\"]",
        "[data-tid=\"message-pane-list-surface\"]",
        "[data-tid=\"chat-pane\"]",
        "[data-tid=\"searchBoxInput\"]",
        "[data-tid=\"topBarSearchInput\"]",
        "[data-tid=\"left-rail\"]",
        "[data-tid=\"app-layout-area\"]",
    ];

    // Scrollable message-container candidates (most specific first).
    private static readonly string[] PaneCandidates =
    [
        // New Teams (v2) — most reliable data-tid values
        "[data-tid=\"chat-messages-list\"]",
        "[data-tid=\"chat-messages-container\"]",
        "[data-tid=\"message-pane-list-viewport\"]",
        "[data-tid=\"chat-pane-runway\"]",
        "[data-tid=\"message-pane-list-runway\"]",
        "[data-tid=\"message-pane-list-container\"]",
        "[data-tid=\"message-pane-list-surface\"]",
        "[data-tid=\"chat-pane-list\"]",
        "[data-tid=\"channel-pane-runway\"]",
        // Classic Teams
        "[data-tid=\"message-pane\"]",
        "[data-tid=\"messageList\"]",
        // Generic fallbacks
        "[role=\"log\"]",
        "#message-list",
        "#channel-pane",
        ".fui-ChatMessageList",
        "[data-testid=\"message-list\"]",
        ".ts-message-list-content",
        "[aria-label=\"Conversation\"]",
    ];

    // Individual message element selectors (most reliable first).
    private static readonly string[] MessageItemSelectors =
    [
        "div[data-mid]",                             // new Teams — most stable, confirmed 2025
        "[data-tid=\"message-pane-list-item\"]",     // new Teams — channel messages
        "[data-tid=\"chat-pane-message\"]",           // new Teams — chat messages
        "[data-tid=\"chat-message\"]",                // new Teams — chat
        "[id^=\"post-message-renderer-\"]",           // new Teams — channel posts
        "[role=\"article\"]",                         // classic Teams fallback
    ];

    private string? _detectedMessageSelector;

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
                Args =
                [
                    "--disable-blink-features=AutomationControlled",
                    "--no-sandbox",
                    "--disable-dev-shm-usage",
                ]
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
            await _page.GotoAsync("https://teams.microsoft.com/v2/",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });

            await Task.Delay(2000, cancellationToken);
            var currentUrl = _page.Url;
            _logger.Log($"Initial URL: {currentUrl}");

            if (IsLoginUrl(currentUrl))
            {
                await PerformMicrosoftLoginAsync(email, password, cancellationToken);
            }
            else if (!IsTeamsUrl(currentUrl))
            {
                // SSO redirect may still be in progress — wait a moment
                await Task.Delay(2000, cancellationToken);
                if (IsLoginUrl(_page.Url))
                    await PerformMicrosoftLoginAsync(email, password, cancellationToken);
            }

            _logger.Log("Waiting for Teams to fully load...");
            await WaitForTeamsReadyAsync(cancellationToken);

            await SaveSessionAsync(cancellationToken);
            _logger.Log("Login successful. Session saved.");
        }
        catch (PlaywrightException ex)
        {
            throw new TeamsScraperException("Login failed during browser interaction.", ex);
        }
    }

    private static bool IsLoginUrl(string url) =>
        url.Contains("login.microsoftonline.com") || url.Contains("login.microsoft.com");

    private static bool IsTeamsUrl(string url) =>
        url.Contains("teams.microsoft.com") || url.Contains("teams.cloud.microsoft");

    private async Task PerformMicrosoftLoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        _logger.Log($"Microsoft login page: {_page!.Url}");

        // ── Email ───────────────────────────────────────────────────────────
        _logger.Log("Entering email...");
        var emailInput = _page.Locator("input#i0116, input[name='loginfmt'], input[type='email']").First;
        await emailInput.WaitForAsync(new LocatorWaitForOptions { Timeout = 20000 });
        await emailInput.ClickAsync();
        await emailInput.FillAsync(email);
        await Task.Delay(Random.Shared.Next(400, 800), cancellationToken);

        // #idSIButton9 is the stable Next/Sign-in button ID on login.microsoftonline.com
        var nextBtn = _page.Locator("input#idSIButton9, input[type='submit']").First;
        await nextBtn.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        await nextBtn.ClickAsync();
        _logger.Log("Email submitted — waiting for password field...");

        // ── Password ────────────────────────────────────────────────────────
        var passwordInput = _page.Locator("input#i0118, input[name='passwd'], input[type='password']").First;
        await passwordInput.WaitForAsync(new LocatorWaitForOptions { Timeout = 20000 });
        await passwordInput.ClickAsync();
        await passwordInput.FillAsync(password);
        await Task.Delay(Random.Shared.Next(400, 800), cancellationToken);

        var signInBtn = _page.Locator("input#idSIButton9, input[type='submit']").First;
        await signInBtn.ClickAsync();
        _logger.Log("Password submitted — handling post-login prompts...");

        // ── Post-login prompts (KMSI, MFA, conditional access) ─────────────
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1500, cancellationToken);

            var url = _page.Url;
            _logger.Log($"Post-login URL: {url[..Math.Min(url.Length, 80)]}");

            // Teams loaded — done
            if (!IsLoginUrl(url) && IsTeamsUrl(url))
            {
                _logger.Log("Teams URL detected — login complete.");
                return;
            }

            // "Stay signed in?" Yes button is also #idSIButton9 (same ID, different page)
            try
            {
                var kmsiYes = _page.Locator("input#idSIButton9");
                if (await kmsiYes.CountAsync() > 0)
                {
                    await kmsiYes.First.ClickAsync();
                    _logger.Log("Clicked 'Stay signed in: Yes'.");
                    continue;
                }
            }
            catch { }

            // "Stay signed in?" No button (fallback)
            try
            {
                var kmsiNo = _page.Locator("input#idBtn_Back");
                if (await kmsiNo.CountAsync() > 0)
                {
                    await kmsiNo.First.ClickAsync();
                    _logger.Log("Clicked 'Stay signed in: No'.");
                    continue;
                }
            }
            catch { }

            if (IsLoginUrl(url))
                _logger.Log("Waiting for user to complete MFA or conditional access...");
        }

        if (!IsTeamsUrl(_page.Url))
            throw new TeamsScraperException(
                "Login timed out after 90 s. If MFA is required, complete it in the browser window, then retry.");
    }

    private async Task WaitForTeamsReadyAsync(CancellationToken cancellationToken)
    {
        // Pass each selector as a JS argument to avoid single-quote conflicts in JS strings.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var selector in TeamsReadySelectors)
            {
                try
                {
                    var found = await _page!.EvaluateAsync<bool>(
                        "(s) => document.querySelector(s) !== null", selector);
                    if (found)
                    {
                        _logger.Log($"Teams ready — detected: {selector}");
                        await Task.Delay(1500, cancellationToken);
                        return;
                    }
                }
                catch { }
            }

            _logger.Log("Teams still loading...");
            await Task.Delay(2000, cancellationToken);
        }

        _logger.LogWarning("Teams ready-check timed out after 90 s — proceeding anyway.");
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

            progress.Report(new ScrapingProgressUpdate(0, config.MaxMessages, "Navigating to channel...", true));
            await NavigateToChannelAsync(config, cancellationToken);

            var paneSelector = await DetectPaneSelectorAsync(cancellationToken);
            _logger.Log($"Message pane: {paneSelector ?? "(window scroll)"}");

            progress.Report(new ScrapingProgressUpdate(0, config.MaxMessages, "Scrolling to first message...", true));
            await ScrollToTopHumanAsync(paneSelector, cancellationToken);

            int noNewRounds   = 0;
            int previousCount = 0;

            for (int scroll = 0; scroll < Constants.MaxScrollAttempts && messages.Count < config.MaxMessages; scroll++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var msgSelector = await DetectMessageSelectorAsync(cancellationToken);
                var articles    = _page.Locator(msgSelector);
                int count       = await articles.CountAsync();
                _logger.Log($"Pass {scroll + 1}: {count} messages visible ({msgSelector}).");

                for (int i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var msg = await ParseMessageElementAsync(articles.Nth(i), cancellationToken);
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
                    noNewRounds++;
                    if (noNewRounds >= 3) break;
                }
                else
                {
                    noNewRounds = 0;
                }
                previousCount = messages.Count;

                await HumanScrollDownAsync(paneSelector, cancellationToken);
            }

            progress.Report(new ScrapingProgressUpdate(
                messages.Count, messages.Count,
                $"Complete — {messages.Count} messages collected.", false));

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

    // ── Message selector detection ─────────────────────────────────────────

    private async Task<string> DetectMessageSelectorAsync(CancellationToken ct)
    {
        if (_detectedMessageSelector is not null) return _detectedMessageSelector;

        foreach (var sel in MessageItemSelectors)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Pass selector as argument — avoids single-quote conflicts in JS strings
                var count = await _page!.EvaluateAsync<int>(
                    "(s) => document.querySelectorAll(s).length", sel);
                if (count > 0)
                {
                    _logger.Log($"Message selector locked in: {sel} ({count} items)");
                    _detectedMessageSelector = sel;
                    return sel;
                }
            }
            catch { }
        }

        _detectedMessageSelector = "[role='article']";
        return _detectedMessageSelector;
    }

    // ── Pane detection ─────────────────────────────────────────────────────

    private async Task<string?> DetectPaneSelectorAsync(CancellationToken ct)
    {
        foreach (var sel in PaneCandidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var found = await _page!.EvaluateAsync<bool>(
                    "(s) => { const el = document.querySelector(s); return el !== null && el.scrollHeight > el.clientHeight; }",
                    sel);
                if (found) return sel;
            }
            catch { }
        }

        // Dynamic fallback: walk up from the first visible message element
        var dynamic = await _page!.EvaluateAsync<string?>("""
            () => {
                const candidates = [
                    '[data-tid="message-pane-list-item"]',
                    '[data-tid="chat-pane-message"]',
                    '[role="listitem"][data-mid]',
                    '[id^="post-message-renderer-"]',
                    '[role="article"]'
                ];
                let first = null;
                for (const s of candidates) {
                    first = document.querySelector(s);
                    if (first) break;
                }
                if (!first) return null;
                let el = first.parentElement;
                while (el && el !== document.body) {
                    const s = window.getComputedStyle(el);
                    const ov = [s.overflow, s.overflowY];
                    if (ov.some(v => v === 'auto' || v === 'scroll')
                            && el.scrollHeight > el.clientHeight) {
                        const tid = el.getAttribute('data-tid');
                        if (tid) return '[data-tid="' + tid + '"]';
                        if (el.id) return '#' + el.id;
                        return null;
                    }
                    el = el.parentElement;
                }
                return null;
            }
            """);

        if (dynamic is not null)
            _logger.Log($"Pane detected dynamically: {dynamic}");
        else
            _logger.LogWarning("Could not detect message pane — using window scroll.");

        return dynamic;
    }

    // ── Channel navigation ─────────────────────────────────────────────────

    private async Task NavigateToChannelAsync(ScrapingConfig config, CancellationToken ct)
    {
        _logger.Log("Navigating to Teams...");
        await RetryAsync(async () =>
        {
            await _page!.GotoAsync("https://teams.microsoft.com/v2/",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            return true;
        }, ct);

        await WaitForTeamsReadyAsync(ct);
        _logger.Log($"Teams URL: {_page!.Url}");

        if (string.IsNullOrWhiteSpace(config.ChannelName)) return;

        await Task.Delay(Random.Shared.Next(800, 1500), ct);
        _detectedMessageSelector = null; // reset for the new channel

        var found = false;

        // Strategy 1: new Teams — data-testid*="channel-list-item"
        if (!found)
        {
            try
            {
                var loc = _page.Locator("[data-testid*='channel-list-item']")
                               .Filter(new LocatorFilterOptions { HasText = config.ChannelName })
                               .First;
                await loc.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
                await loc.HoverAsync();
                await Task.Delay(Random.Shared.Next(200, 500), ct);
                await loc.ClickAsync();
                _logger.Log($"Channel found (data-testid): {config.ChannelName}");
                found = true;
            }
            catch { }
        }

        // Strategy 2: classic Teams — span with id prefix
        if (!found)
        {
            try
            {
                var loc = _page.Locator("span[id^='title-channel-list-item-']")
                               .Filter(new LocatorFilterOptions { HasText = config.ChannelName })
                               .First;
                await loc.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
                await loc.ClickAsync();
                _logger.Log($"Channel found (id prefix): {config.ChannelName}");
                found = true;
            }
            catch { }
        }

        // Strategy 3: ARIA treeitem by text
        if (!found)
        {
            try
            {
                var loc = _page.GetByRole(AriaRole.Treeitem)
                               .Filter(new LocatorFilterOptions { HasText = config.ChannelName })
                               .First;
                await loc.WaitForAsync(new LocatorWaitForOptions { Timeout = 8000 });
                await loc.ClickAsync();
                _logger.Log($"Channel found (treeitem): {config.ChannelName}");
                found = true;
            }
            catch { }
        }

        // Strategy 4: any element with data-tid containing "channel" and matching text
        if (!found)
        {
            try
            {
                var loc = _page.Locator("[data-tid*='channel']")
                               .Filter(new LocatorFilterOptions { HasText = config.ChannelName })
                               .First;
                await loc.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
                await loc.ClickAsync();
                _logger.Log($"Channel found (data-tid wildcard): {config.ChannelName}");
                found = true;
            }
            catch { }
        }

        if (found)
            await Task.Delay(Random.Shared.Next(2500, 4000), ct);
        else
            _logger.LogWarning($"Could not navigate to '{config.ChannelName}' — using current view.");
    }

    // ── Human-like scrolling ───────────────────────────────────────────────

    private async Task ScrollToTopHumanAsync(string? paneSelector, CancellationToken ct)
    {
        _logger.Log("Scrolling to top (oldest messages)...");
        int stuckCount    = 0;
        int lastScrollTop = int.MaxValue;

        for (int attempt = 0; attempt < 100; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var scrollTop = await GetScrollTopAsync(paneSelector);
            if (scrollTop <= 10) break;

            if (Math.Abs(scrollTop - lastScrollTop) < 5)
            {
                stuckCount++;
                if (stuckCount >= 5) break;
                await Task.Delay(Random.Shared.Next(800, 1500), ct);
                continue;
            }

            stuckCount    = 0;
            lastScrollTop = scrollTop;

            await ScrollByAsync(paneSelector, -Random.Shared.Next(300, 700));

            var delay = Random.Shared.Next(300, 800);
            if (Random.Shared.Next(0, 7) == 0) delay += Random.Shared.Next(1000, 2500);
            await Task.Delay(delay, ct);
        }

        await Task.Delay(Random.Shared.Next(2000, 3500), ct);
        _logger.Log("At top — beginning collection.");
    }

    private async Task HumanScrollDownAsync(string? paneSelector, CancellationToken ct)
    {
        await ScrollByAsync(paneSelector, Random.Shared.Next(200, 500));
        var delay = Random.Shared.Next(900, 2500);
        if (Random.Shared.Next(0, 5) == 0) delay += Random.Shared.Next(1500, 4000);
        await Task.Delay(delay, ct);
    }

    private async Task<int> GetScrollTopAsync(string? paneSelector)
    {
        if (paneSelector is null)
            return await _page!.EvaluateAsync<int>("() => window.scrollY");

        // Pass selector as argument to avoid JS string escaping issues
        return await _page!.EvaluateAsync<int>(
            "(s) => { const el = document.querySelector(s); return el ? Math.floor(el.scrollTop) : window.scrollY; }",
            paneSelector);
    }

    private async Task ScrollByAsync(string? paneSelector, int delta)
    {
        if (paneSelector is null)
        {
            await _page!.EvaluateAsync("(d) => window.scrollBy(0, d)", delta);
            return;
        }

        if (delta < 0)
        {
            await _page!.EvaluateAsync(
                "({s, d}) => { const el = document.querySelector(s); if (el) el.scrollTop = Math.max(0, el.scrollTop - d); else window.scrollBy(0, -d); }",
                new { s = paneSelector, d = Math.Abs(delta) });
        }
        else
        {
            await _page!.EvaluateAsync(
                "({s, d}) => { const el = document.querySelector(s); if (el) el.scrollTop += d; else window.scrollBy(0, d); }",
                new { s = paneSelector, d = delta });
        }
    }

    // ── Message parsing ────────────────────────────────────────────────────

    private async Task<TeamMessage?> ParseMessageElementAsync(ILocator el, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // New Teams uses data-mid; classic uses data-message-id; fall back to element id
            var domId = await el.GetAttributeAsync("data-mid")
                     ?? await el.GetAttributeAsync("data-message-id")
                     ?? await el.GetAttributeAsync("id");

            // New Teams uses data-reply-chain-id; classic uses data-reply-to
            var parentId = await el.GetAttributeAsync("data-reply-chain-id")
                        ?? await el.GetAttributeAsync("data-reply-to")
                        ?? string.Empty;

            // Author — data-tid="message-author-name" is the stable new-Teams selector
            var author =
                await SafeGetTextAsync(el, "[data-tid='message-author-name']", cancellationToken)
             ?? await SafeGetTextAsync(el, "[data-tid*='author']", cancellationToken)
             ?? await SafeGetTextAsync(el, "[data-testid='message-author']", cancellationToken)
             ?? await SafeGetTextAsync(el, ".author-name", cancellationToken)
             ?? await SafeGetTextAsync(el, "[class*='author']", cancellationToken)
             ?? "Unknown";

            // Timestamp — data-tid="message-timestamp" or standard <time datetime="...">
            var timestampRaw =
                await SafeGetAttributeAsync(el, "[data-tid='message-timestamp'], [data-tid*='timestamp']", "datetime", cancellationToken)
             ?? await SafeGetAttributeAsync(el, "time[datetime]", "datetime", cancellationToken)
             ?? await SafeGetAttributeAsync(el, "time", "title", cancellationToken);
            DateTime.TryParse(timestampRaw, out var timestamp);
            if (timestamp == default) timestamp = DateTime.UtcNow;

            // Content — data-tid="message-body" is the stable new-Teams selector
            var content =
                await SafeGetTextAsync(el, "[data-tid='message-body']", cancellationToken)
             ?? await SafeGetTextAsync(el, "[data-tid*='message-body']", cancellationToken)
             ?? await SafeGetTextAsync(el, "[data-testid='message-body']", cancellationToken)
             ?? await SafeGetTextAsync(el, ".message-body", cancellationToken)
             ?? await SafeGetTextAsync(el, "[class*='body']", cancellationToken)
             ?? await SafeInnerTextAsync(el, cancellationToken)
             ?? string.Empty;

            var stableId = !string.IsNullOrEmpty(domId)
                ? domId
                : ScrapingStateDb.ComputeContentId(author, content, timestamp);

            return new TeamMessage
            {
                Id        = stableId,
                ParentId  = parentId,
                Author    = author,
                Content   = content.Trim(),
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
        try
        {
            return await parent.Locator(selector).First
                               .InnerTextAsync(new LocatorInnerTextOptions { Timeout = 2000 });
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<string?> SafeInnerTextAsync(ILocator el, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return await el.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 2000 }); }
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
