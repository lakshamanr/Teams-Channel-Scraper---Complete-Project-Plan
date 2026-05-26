using System.Windows;
using TeamsChannelScraper.WPF.Services;
using TeamsChannelScraper.WPF.Utilities;
using TeamsChannelScraper.WPF.ViewModels;

namespace TeamsChannelScraper.WPF;

public partial class App : Application
{
    private ITeamsScraper? _scraper;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        LoggerService.Initialize();

        _scraper = new TeamsScraperService(LoggerService.Instance);
        var parser = new MessageParserService();
        var exportService = new ExportService(LoggerService.Instance);
        var configManager = new ConfigManager();

        var mainVm = new MainViewModel(_scraper, parser, exportService, configManager);
        var mainWindow = new MainWindow { DataContext = mainVm };
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_scraper is not null)
        {
            // Task.Run + Wait(timeout) is intentional here: OnExit is synchronous and
            // cannot be awaited. The 5-second timeout prevents hanging on shutdown
            // without blocking the UI thread during normal operation.
            var cleanupTask = Task.Run(async () =>
            {
                await _scraper.SaveSessionAsync();
                await _scraper.DisposePlaywrightAsync();
            });
            cleanupTask.Wait(TimeSpan.FromSeconds(5));
        }
        base.OnExit(e);
    }
}
