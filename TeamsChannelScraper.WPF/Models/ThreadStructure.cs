namespace TeamsChannelScraper.WPF.Models;

public sealed class ThreadStructure
{
    public TeamMessage Root { get; init; } = null!;
    public IReadOnlyList<TeamMessage> Replies { get; init; } = [];
    public int TotalCount => 1 + Replies.Count;
    public string InferredCategory => Root.Category;
    public double RelevanceScore { get; set; }
    public string GeneratedMarkdown { get; set; } = string.Empty;
}
