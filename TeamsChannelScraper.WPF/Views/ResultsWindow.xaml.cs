using System.Windows;
using TeamsChannelScraper.WPF.ViewModels;

namespace TeamsChannelScraper.WPF.Views;

public partial class ResultsWindow : Window
{
    public ResultsWindow(ResultsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
