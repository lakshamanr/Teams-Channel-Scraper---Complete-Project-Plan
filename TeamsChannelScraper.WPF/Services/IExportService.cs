using TeamsChannelScraper.WPF.Models;

namespace TeamsChannelScraper.WPF.Services;

public enum ExportFormat
{
    Json,
    Csv,
    Markdown,
    All   // loops all three formats
}

public interface IExportService
{
    Task<string> ExportAsync(
        ExportedData data,
        ExportFormat format,
        string outputFolder,
        CancellationToken cancellationToken = default);

    string ResolveExportFolder(string channelName, DateTime exportTimestamp);
}
