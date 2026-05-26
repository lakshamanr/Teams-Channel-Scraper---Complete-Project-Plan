using System.Windows;
using TeamsChannelScraper.WPF.ViewModels;

namespace TeamsChannelScraper.WPF.Views;

public partial class ScrapingWindow : Window
{
    public ScrapingWindow(ScrapingProgressViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
