using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Channels;
using System.Windows.Threading;

namespace TeamsChannelScraper.WPF.Utilities;

public sealed class LoggerService
{
    private static LoggerService? _instance;
    public static LoggerService Instance => _instance ?? throw new InvalidOperationException("LoggerService not initialized.");

    private readonly Dispatcher _dispatcher;
    private readonly Channel<string> _channel;
    private readonly string _logFilePath;
    private readonly Task _writerTask;

    public ObservableCollection<string> Entries { get; } = new();

    private LoggerService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _channel    = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Constants.AppDataFolder,
            Constants.LogFolder);
        Directory.CreateDirectory(logDir);
        _logFilePath = Path.Combine(logDir, $"tcie_{DateTime.Now:yyyy-MM-dd}.log");

        _writerTask = Task.Run(ProcessChannelAsync);
    }

    public static void Initialize()
    {
        _instance ??= new LoggerService(Dispatcher.CurrentDispatcher);
    }

    public void Log(string message)          => Write($"[{DateTime.Now:HH:mm:ss}] INFO  {message}");
    public void LogError(string message)     => Write($"[{DateTime.Now:HH:mm:ss}] ERROR {message}");
    public void LogError(Exception ex)       => Write($"[{DateTime.Now:HH:mm:ss}] ERROR {ex.Message}\n{ex.StackTrace}");
    public void LogWarning(string message)   => Write($"[{DateTime.Now:HH:mm:ss}] WARN  {message}");
    public void LogSuccess(string message)   => Write($"[{DateTime.Now:HH:mm:ss}] OK    {message}");

    private void Write(string entry)
    {
        _channel.Writer.TryWrite(entry);
    }

    private async Task ProcessChannelAsync()
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync())
        {
            // Update UI-bound collection on UI thread
            _dispatcher.Invoke(() =>
            {
                Entries.Add(entry);
                if (Entries.Count > 200) Entries.RemoveAt(0);
            });

            // Write to log file (background — no UI thread needed)
            try { await File.AppendAllTextAsync(_logFilePath, entry + Environment.NewLine); }
            catch { /* log file write failure must not crash the app */ }
        }
    }
}
