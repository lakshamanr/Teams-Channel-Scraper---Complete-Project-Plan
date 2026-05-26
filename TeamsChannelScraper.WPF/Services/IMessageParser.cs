using TeamsChannelScraper.WPF.Models;

namespace TeamsChannelScraper.WPF.Services;

public interface IMessageParser
{
    IReadOnlyList<ThreadStructure> BuildThreads(IReadOnlyList<TeamMessage> messages);
    string InferCategory(TeamMessage message);
    Task<string> RenderThreadAsMarkdownAsync(ThreadStructure thread, CancellationToken cancellationToken = default);
    string ParseMessageContent(string rawHtml);
}
