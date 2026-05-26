using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using TeamsChannelScraper.WPF.Models;
using TeamsChannelScraper.WPF.Services;
using TeamsChannelScraper.WPF.Utilities;

namespace TeamsChannelScraper.WPF.ViewModels;

public sealed class ResultsViewModel : ViewModelBase
{
    private readonly IExportService _exportService;
    private readonly LoggerService _logger;

    private string _searchText = string.Empty;
    private ThreadStructure? _selectedThread;
    private string _selectedMarkdown = string.Empty;
    private string _statusMessage = string.Empty;
    private ExportedData? _data;

    private readonly CollectionViewSource _collectionViewSource;

    public ResultsViewModel(IExportService exportService, LoggerService logger)
    {
        _exportService = exportService;
        _logger        = logger;

        AllThreads = new ObservableCollection<ThreadStructure>();

        _collectionViewSource = new CollectionViewSource { Source = AllThreads };
        _collectionViewSource.Filter += OnThreadFilter;
        FilteredThreads = _collectionViewSource.View;

        ExportCommand = new RelayCommand(async () => await ExecuteExportAsync(),
            () => _data is not null && !string.IsNullOrEmpty(_data.ChannelName));
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
    }

    public ObservableCollection<ThreadStructure> AllThreads { get; }
    public ICollectionView FilteredThreads { get; }
    public ObservableCollection<CategoryStat> CategoryStats { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            SetProperty(ref _searchText, value);
            FilteredThreads.Refresh();
        }
    }

    public ThreadStructure? SelectedThread
    {
        get => _selectedThread;
        set
        {
            SetProperty(ref _selectedThread, value);
            SelectedMarkdown = value?.GeneratedMarkdown ?? string.Empty;
        }
    }

    public string SelectedMarkdown
    {
        get => _selectedMarkdown;
        private set => SetProperty(ref _selectedMarkdown, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public int TotalMessages => _data?.TotalMessageCount ?? 0;
    public int TotalThreads  => _data?.Threads.Count ?? 0;

    public RelayCommand ExportCommand      { get; }
    public RelayCommand ClearSearchCommand { get; }

    public void LoadData(ExportedData data)
    {
        _data = data;

        // All ObservableCollection mutations on UI thread
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            AllThreads.Clear();
            CategoryStats.Clear();

            foreach (var t in data.Threads)
                AllThreads.Add(t);

            foreach (var g in data.Threads.GroupBy(t => t.InferredCategory).OrderBy(g => g.Key))
                CategoryStats.Add(new CategoryStat(g.Key, g.Count()));
        });

        OnPropertyChanged(nameof(TotalMessages));
        OnPropertyChanged(nameof(TotalThreads));
        StatusMessage = $"Loaded {data.Threads.Count} threads ({data.TotalMessageCount} messages).";
    }

    private void OnThreadFilter(object sender, FilterEventArgs e)
    {
        if (e.Item is not ThreadStructure thread) { e.Accepted = false; return; }
        if (string.IsNullOrWhiteSpace(_searchText))  { e.Accepted = true;  return; }

        var term = _searchText.Trim();
        e.Accepted =
            Contains(thread.Root.Category, term) ||
            Contains(thread.Root.Author, term)   ||
            Contains(thread.Root.Content, term)  ||
            thread.Replies.Any(r => Contains(r.Content, term) || Contains(r.Author, term));
    }

    private static bool Contains(string source, string term) =>
        source.Contains(term, StringComparison.OrdinalIgnoreCase);

    private async Task ExecuteExportAsync()
    {
        if (_data is null) return;
        try
        {
            StatusMessage = "Exporting...";
            var folder = _exportService.ResolveExportFolder(_data.ChannelName, DateTime.Now);
            var path   = await _exportService.ExportAsync(_data, ExportFormat.All, folder);
            StatusMessage = $"Exported to {path}";
            _logger.LogSuccess($"Export complete: {path}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
            _logger.LogError(ex);
        }
    }
}

public sealed record CategoryStat(string Category, int Count);
