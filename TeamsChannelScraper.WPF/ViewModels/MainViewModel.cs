using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Input;
using TeamsChannelScraper.WPF.Models;
using TeamsChannelScraper.WPF.Services;
using TeamsChannelScraper.WPF.Utilities;
using TeamsChannelScraper.WPF.Views;

namespace TeamsChannelScraper.WPF.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly ITeamsScraper _scraper;
    private readonly IMessageParser _parser;
    private readonly IExportService _exporter;
    private readonly ConfigManager _configManager;
    private readonly LoggerService _logger = LoggerService.Instance;

    // Password held as SecureString — never as plain string field
    private SecureString _securePassword = new();
    private CancellationTokenSource? _cts;

    // Configuration properties
    private string _teamName    = string.Empty;
    private string _channelName = string.Empty;
    private string _userEmail   = string.Empty;
    private int    _maxMessages = Constants.DefaultMaxMessages;
    private bool   _includeReplies = true;
    private bool   _useHeadless    = false;
    private ExportFormat _exportFormat = ExportFormat.All;
    private string _exportFolder = string.Empty;

    // Status properties
    private string _statusMessage = "Ready.";
    private bool   _isScraping    = false;
    private bool   _isLoggedIn    = false;

    public MainViewModel(
        ITeamsScraper scraper,
        IMessageParser parser,
        IExportService exporter,
        ConfigManager configManager)
    {
        _scraper       = scraper;
        _parser        = parser;
        _exporter      = exporter;
        _configManager = configManager;

        Progress = new ScrapingProgressViewModel();

        StartScrapingCommand = new RelayCommand(async () => await ExecuteStartScrapingAsync(),
            () => !IsScraping && !string.IsNullOrWhiteSpace(UserEmail));
        CancelScrapingCommand = new RelayCommand(ExecuteCancel,
            () => IsScraping);
        SaveConfigCommand  = new RelayCommand(ExecuteSaveConfig);
        LoadConfigCommand  = new RelayCommand(ExecuteLoadConfig);
        BrowseFolderCommand = new RelayCommand(ExecuteBrowseFolder);
        ClearLogsCommand   = new RelayCommand(() =>
            System.Windows.Application.Current.Dispatcher.Invoke(() => Logger.Entries.Clear()));

        Logger = LoggerService.Instance;

        // Load saved config on startup
        ExecuteLoadConfig();
    }

    // Config properties
    public string TeamName    { get => _teamName;    set => SetProperty(ref _teamName,    value); }
    public string ChannelName { get => _channelName; set => SetProperty(ref _channelName, value); }
    public string UserEmail   { get => _userEmail;   set => SetProperty(ref _userEmail,   value); }
    public int MaxMessages    { get => _maxMessages;  set => SetProperty(ref _maxMessages,  value); }
    public bool IncludeReplies { get => _includeReplies; set => SetProperty(ref _includeReplies, value); }
    public bool UseHeadless   { get => _useHeadless;  set => SetProperty(ref _useHeadless,  value); }
    public ExportFormat ExportFormat { get => _exportFormat; set => SetProperty(ref _exportFormat, value); }
    public string ExportFolder { get => _exportFolder; set => SetProperty(ref _exportFolder, value); }

    // Status properties
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
    public bool IsScraping { get => _isScraping; private set => SetProperty(ref _isScraping, value); }
    public bool IsLoggedIn { get => _isLoggedIn; private set => SetProperty(ref _isLoggedIn,  value); }

    public ScrapingProgressViewModel Progress { get; }
    public LoggerService Logger { get; }

    public IReadOnlyList<ExportFormat> ExportFormats { get; } =
        Enum.GetValues<ExportFormat>().ToList();

    // Commands
    public RelayCommand StartScrapingCommand  { get; }
    public RelayCommand CancelScrapingCommand { get; }
    public RelayCommand SaveConfigCommand     { get; }
    public RelayCommand LoadConfigCommand     { get; }
    public RelayCommand BrowseFolderCommand   { get; }
    public RelayCommand ClearLogsCommand      { get; }

    // Called from MainWindow.xaml.cs code-behind on PasswordBox.PasswordChanged
    public void OnPasswordChanged(SecureString securePassword)
    {
        _securePassword.Dispose();
        _securePassword = securePassword.Copy();
    }

    private async Task ExecuteStartScrapingAsync()
    {
        IsScraping = true;
        _cts = new CancellationTokenSource();

        var config = BuildConfig();
        var token  = _cts.Token;

        try
        {
            // Authenticate if needed
            if (!IsLoggedIn)
            {
                StatusMessage = "Authenticating...";
                var restored = await _scraper.TryRestoreSessionAsync(UseHeadless, token);
                if (!restored)
                {
                    StatusMessage = "Logging in...";
                    var ptr = Marshal.SecureStringToGlobalAllocUnicode(_securePassword);
                    try
                    {
                        var password = Marshal.PtrToStringUni(ptr)!;
                        await _scraper.LoginAsync(UserEmail, password, token);
                    }
                    finally
                    {
                        Marshal.ZeroFreeGlobalAllocUnicode(ptr);
                    }
                }
                IsLoggedIn = true;
            }

            StatusMessage = "Scraping channel...";
            Logger.Log($"Starting scrape of '{ChannelName}'...");

            var result = await _scraper.ScrapeChannelAsync(config, Progress.Progress, token);

            if (result.WasAborted)
            {
                StatusMessage = "Scraping cancelled.";
                return;
            }

            StatusMessage = $"Parsing {result.Messages.Count} messages...";
            var threads = _parser.BuildThreads(result.Messages);

            var exported = new ExportedData
            {
                ChannelName  = ChannelName,
                ExportedAt   = DateTime.UtcNow,
                Threads      = threads,
                SourceConfig = config
            };

            // Resolve export folder
            var folder = string.IsNullOrWhiteSpace(ExportFolder)
                ? _exporter.ResolveExportFolder(ChannelName, DateTime.Now)
                : ExportFolder;

            StatusMessage = "Exporting...";
            await _exporter.ExportAsync(exported, ExportFormat, folder);

            StatusMessage = $"Done. {threads.Count} threads from {result.Messages.Count} messages exported to {folder}";
            Logger.LogSuccess(StatusMessage);

            // Open results window
            OpenResultsWindow(exported);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scraping cancelled by user.";
            Logger.Log("Scraping cancelled.");
        }
        catch (TeamsScraperException ex)
        {
            StatusMessage = $"Scrape failed: {ex.Message}";
            Logger.LogError(ex);
        }
        catch (Exception ex)
        {
            StatusMessage = "An unexpected error occurred. See log for details.";
            Logger.LogError(ex);
        }
        finally
        {
            IsScraping = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ExecuteCancel() => _cts?.Cancel();

    private ScrapingConfig BuildConfig() => new()
    {
        TeamName           = TeamName,
        ChannelName        = ChannelName,
        UserEmail          = UserEmail,
        MaxMessages        = MaxMessages,
        IncludeReplies     = IncludeReplies,
        UseHeadlessBrowser = UseHeadless
    };

    private void ExecuteSaveConfig()
    {
        _configManager.SaveConfig(BuildConfig(), ExportFormat.ToString(), ExportFolder);
        Logger.Log("Configuration saved.");
    }

    private void ExecuteLoadConfig()
    {
        var (cfg, exportFormatName, exportFolder) = _configManager.LoadConfig();
        TeamName       = cfg.TeamName;
        ChannelName    = cfg.ChannelName;
        UserEmail      = cfg.UserEmail;
        MaxMessages    = cfg.MaxMessages;
        IncludeReplies = cfg.IncludeReplies;
        UseHeadless    = cfg.UseHeadlessBrowser;
        ExportFolder   = exportFolder;
        if (Enum.TryParse<ExportFormat>(exportFormatName, out var fmt))
            ExportFormat = fmt;
    }

    private void ExecuteBrowseFolder()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select export folder",
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            ExportFolder = dlg.SelectedPath;
    }

    private void OpenResultsWindow(ExportedData data)
    {
        var vm = new ResultsViewModel(_exporter, _logger);
        vm.LoadData(data);
        var window = new ResultsWindow(vm);
        window.Show();
    }
}
