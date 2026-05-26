using TeamsChannelScraper.WPF.Models;

namespace TeamsChannelScraper.WPF.ViewModels;

public sealed class ScrapingProgressViewModel : ViewModelBase
{
    private int _messagesProcessed;
    private int _totalEstimated;
    private string _currentActivity = "Initializing...";
    private bool _isIndeterminate = true;

    // Progress<T> constructed here on UI thread — captures SynchronizationContext
    public IProgress<ScrapingProgressUpdate> Progress { get; }

    public ScrapingProgressViewModel()
    {
        Progress = new Progress<ScrapingProgressUpdate>(OnProgressUpdate);
    }

    public int MessagesProcessed
    {
        get => _messagesProcessed;
        private set => SetProperty(ref _messagesProcessed, value);
    }

    public int TotalEstimated
    {
        get => _totalEstimated;
        private set => SetProperty(ref _totalEstimated, value);
    }

    public string CurrentActivity
    {
        get => _currentActivity;
        private set => SetProperty(ref _currentActivity, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }

    public double ProgressPercent =>
        TotalEstimated > 0 ? (double)MessagesProcessed / TotalEstimated * 100 : 0;

    private void OnProgressUpdate(ScrapingProgressUpdate update)
    {
        MessagesProcessed = update.MessagesProcessed;
        TotalEstimated    = update.TotalEstimated;
        CurrentActivity   = update.CurrentActivity;
        IsIndeterminate   = update.IsIndeterminate;
        OnPropertyChanged(nameof(ProgressPercent));
    }
}
