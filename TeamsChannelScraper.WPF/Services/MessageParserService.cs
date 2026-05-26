using System.Text;
using System.Text.RegularExpressions;
using TeamsChannelScraper.WPF.Models;
using TeamsChannelScraper.WPF.Utilities;

namespace TeamsChannelScraper.WPF.Services;

public sealed class MessageParserService : IMessageParser
{
    public IReadOnlyList<ThreadStructure> BuildThreads(IReadOnlyList<TeamMessage> messages)
    {
        var roots = messages.Where(m => string.IsNullOrEmpty(m.ParentId)).ToList();
        var repliesMap = messages
            .Where(m => !string.IsNullOrEmpty(m.ParentId))
            .GroupBy(m => m.ParentId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TeamMessage>)g.OrderBy(m => m.Timestamp).ToList());

        var threads = roots.Select(root =>
        {
            root.Category = InferCategory(root);
            var replies = repliesMap.TryGetValue(root.Id, out var r) ? r : [];
            var thread = new ThreadStructure
            {
                Root = root,
                Replies = replies
            };
            thread.RelevanceScore = ComputeRelevance(thread);
            return thread;
        }).ToList();

        // Generate markdown after all threads built (so scores are set)
        foreach (var t in threads)
            t.GeneratedMarkdown = RenderMarkdownSync(t);

        return threads.OrderByDescending(t => t.RelevanceScore).ToList();
    }

    public string InferCategory(TeamMessage message)
    {
        var text = message.Content.ToLowerInvariant();

        if (Constants.DatabaseKeywords.Any(k => text.Contains(k)))    return "Database";
        if (Constants.ApiKeywords.Any(k => text.Contains(k)))         return "API";
        if (Constants.DeploymentKeywords.Any(k => text.Contains(k)))  return "Deployment";
        if (Constants.PerformanceKeywords.Any(k => text.Contains(k))) return "Performance";
        if (Constants.InfraKeywords.Any(k => text.Contains(k)))       return "Infrastructure";
        if (Constants.SecurityKeywords.Any(k => text.Contains(k)))    return "Security";
        return "General";
    }

    public string ParseMessageContent(string rawHtml)
    {
        if (string.IsNullOrWhiteSpace(rawHtml)) return string.Empty;
        // Strip HTML tags with regex, decode common entities
        var text = Regex.Replace(rawHtml, "<[^>]+>", " ");
        text = text.Replace("&amp;", "&")
                   .Replace("&lt;", "<")
                   .Replace("&gt;", ">")
                   .Replace("&nbsp;", " ")
                   .Replace("&quot;", "\"");
        return Regex.Replace(text, @"\s{2,}", " ").Trim();
    }

    public async Task<string> RenderThreadAsMarkdownAsync(
        ThreadStructure thread,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield(); // make truly async for interface compliance
        cancellationToken.ThrowIfCancellationRequested();
        return RenderMarkdownSync(thread);
    }

    private static double ComputeRelevance(ThreadStructure thread)
    {
        double score = 0;
        score += Math.Min(thread.Replies.Count * 10, 50);
        score += Math.Min(thread.Root.Content.Length / 20.0, 30);
        if (thread.InferredCategory != "General") score += 20;
        return Math.Round(score, 2);
    }

    private static string RenderMarkdownSync(ThreadStructure thread)
    {
        var sb = new StringBuilder();
        var preview = thread.Root.Content.Length > 80
            ? thread.Root.Content[..80] + "..."
            : thread.Root.Content;

        sb.AppendLine($"## [{thread.InferredCategory}] {preview}");
        sb.AppendLine();
        sb.AppendLine($"**Author:** {thread.Root.Author}  ");
        sb.AppendLine($"**Posted:** {thread.Root.Timestamp:yyyy-MM-dd HH:mm}  ");
        sb.AppendLine($"**Replies:** {thread.Replies.Count}  ");
        sb.AppendLine($"**Relevance Score:** {thread.RelevanceScore}");
        sb.AppendLine();
        sb.AppendLine("### Problem / Question");
        sb.AppendLine();
        sb.AppendLine(thread.Root.Content);
        sb.AppendLine();

        if (thread.Replies.Count > 0)
        {
            sb.AppendLine("### Replies");
            sb.AppendLine();
            foreach (var reply in thread.Replies)
                sb.AppendLine($"- **{reply.Author}** ({reply.Timestamp:HH:mm}): {reply.Content}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        return sb.ToString();
    }
}
