namespace TeamsChannelScraper.WPF.Models;

public sealed record ScrapingProgressUpdate(
    int MessagesProcessed,
    int TotalEstimated,
    string CurrentActivity,
    bool IsIndeterminate = false);
