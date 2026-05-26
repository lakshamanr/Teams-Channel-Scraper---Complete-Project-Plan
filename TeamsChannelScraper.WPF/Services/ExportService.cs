using System.Text;
using CsvHelper;
using Newtonsoft.Json;
using System.Globalization;
using TeamsChannelScraper.WPF.Models;
using TeamsChannelScraper.WPF.Utilities;

namespace TeamsChannelScraper.WPF.Services;

public sealed class ExportService : IExportService
{
    private readonly LoggerService _logger;

    public ExportService(LoggerService logger)
    {
        _logger = logger;
    }

    public string ResolveExportFolder(string channelName, DateTime exportTimestamp)
    {
        var safeName = MakeSafeDirectoryName(channelName);
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Constants.ExportsRootFolder,
            safeName,
            exportTimestamp.ToString("yyyy-MM-dd_HHmmss"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    public async Task<string> ExportAsync(
        ExportedData data,
        ExportFormat format,
        string outputFolder,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputFolder);

        if (format == ExportFormat.All)
        {
            await ExportJsonAsync(data, outputFolder, cancellationToken);
            await ExportCsvAsync(data, outputFolder, cancellationToken);
            await ExportMarkdownAsync(data, outputFolder, cancellationToken);
            return outputFolder;
        }

        return format switch
        {
            ExportFormat.Json     => await ExportJsonAsync(data, outputFolder, cancellationToken),
            ExportFormat.Csv      => await ExportCsvAsync(data, outputFolder, cancellationToken),
            ExportFormat.Markdown => await ExportMarkdownAsync(data, outputFolder, cancellationToken),
            _                     => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    private async Task<string> ExportJsonAsync(ExportedData data, string folder, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, "messages.json");
        var json = JsonConvert.SerializeObject(data, Formatting.Indented);
        await File.WriteAllTextAsync(path, json, cancellationToken);
        _logger.Log($"JSON exported → {path}");
        return path;
    }

    private async Task<string> ExportCsvAsync(ExportedData data, string folder, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, "messages.csv");
        var rows = data.Threads.SelectMany(t =>
            new[] { (Root: t.Root, Thread: t, IsReply: false) }
            .Concat(t.Replies.Select(r => (Root: r, Thread: t, IsReply: true))))
            .Select(x => new
            {
                ThreadId  = x.Thread.Root.Id,
                Category  = x.Thread.InferredCategory,
                Score     = x.Thread.RelevanceScore,
                MessageId = x.Root.Id,
                Author    = x.Root.Author,
                Timestamp = x.Root.Timestamp,
                IsReply   = x.IsReply,
                Content   = x.Root.Content,
                IsEdited  = x.Root.IsEdited
            }).ToList();

        await using var writer = new StreamWriter(path, false, Encoding.UTF8);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        await csv.WriteRecordsAsync(rows, cancellationToken);
        _logger.Log($"CSV exported → {path}");
        return path;
    }

    private async Task<string> ExportMarkdownAsync(ExportedData data, string folder, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Teams Channel Export — {data.ChannelName}");
        sb.AppendLine($"_Exported: {data.ExportedAt:yyyy-MM-dd HH:mm} UTC_");
        sb.AppendLine($"_Threads: {data.Threads.Count} | Messages: {data.TotalMessageCount}_");
        sb.AppendLine();

        foreach (var group in data.Threads.GroupBy(t => t.InferredCategory).OrderBy(g => g.Key))
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            foreach (var thread in group)
                sb.Append(thread.GeneratedMarkdown);
        }

        var path = Path.Combine(folder, "messages.md");
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, cancellationToken);
        _logger.Log($"Markdown exported → {path}");
        return path;
    }

    private static string MakeSafeDirectoryName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
