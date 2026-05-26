namespace TeamsChannelScraper.WPF.Models;

public sealed class TeamMessage
{
    public string Id { get; init; } = string.Empty;
    public string ParentId { get; init; } = string.Empty; // empty string = root message
    public string Author { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;  // parsed plain text, not raw HTML
    public string RawHtml { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string Category { get; set; } = "General";     // mutable: assigned by parser
    public IReadOnlyList<string> AttachmentUrls { get; init; } = [];
    public IReadOnlyList<string> Reactions { get; init; } = [];
    public bool IsEdited { get; init; }
}
