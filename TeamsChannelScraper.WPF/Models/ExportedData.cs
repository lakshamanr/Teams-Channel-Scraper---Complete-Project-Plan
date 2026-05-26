namespace TeamsChannelScraper.WPF.Models;

public sealed class ExportedData
{
    public string ChannelName { get; init; } = string.Empty;
    public DateTime ExportedAt { get; init; }
    public IReadOnlyList<ThreadStructure> Threads { get; init; } = [];
    public ScrapingConfig SourceConfig { get; init; } = null!;
    public int TotalMessageCount => Threads.Sum(t => t.TotalCount);
}
