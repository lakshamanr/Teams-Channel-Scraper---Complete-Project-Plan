using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using TeamsChannelScraper.WPF.ViewModels;

namespace TeamsChannelScraper.WPF;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            ((System.Collections.Specialized.INotifyCollectionChanged)vm.Logger.Entries)
                .CollectionChanged += LogEntries_CollectionChanged;
    }

    // Auto-scroll log to bottom on new entries
    private void LogEntries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
    }

    // PasswordBox cannot data-bind — handled here in code-behind
    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is PasswordBox pb)
            vm.OnPasswordChanged(pb.SecurePassword);
    }
}
