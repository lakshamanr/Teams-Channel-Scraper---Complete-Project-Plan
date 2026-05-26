namespace TeamsChannelScraper.WPF.Models;

public sealed record ScrapingResult(
    IReadOnlyList<TeamMessage> Messages,
    bool WasAborted,
    string? ErrorMessage = null);
