using TeamsChannelScraper.WPF.Models;

namespace TeamsChannelScraper.WPF.Services;

public interface ITeamsScraper
{
    Task<ScrapingResult> ScrapeChannelAsync(
        ScrapingConfig config,
        IProgress<ScrapingProgressUpdate> progress,
        CancellationToken cancellationToken = default);

    Task SaveSessionAsync(CancellationToken cancellationToken = default);
    Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken = default);

    Task LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default);

    Task DisposePlaywrightAsync();
}
