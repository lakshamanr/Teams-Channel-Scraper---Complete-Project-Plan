namespace TeamsChannelScraper.WPF.Models;

public sealed class ScrapingConfig
{
    public string TeamName { get; init; } = string.Empty;
    public string ChannelName { get; init; } = string.Empty;
    public string UserEmail { get; init; } = string.Empty;
    // Password is NEVER stored here — passed separately via SecureString
    public int MaxMessages { get; init; } = 500;
    public bool IncludeReplies { get; init; } = true;
    public bool IncludeAttachments { get; init; } = false;
    public bool UseHeadlessBrowser { get; init; } = false;
    public DateTimeOffset? ScrapeAfter { get; init; }
    public DateTimeOffset? ScrapeBefore { get; init; }
}
