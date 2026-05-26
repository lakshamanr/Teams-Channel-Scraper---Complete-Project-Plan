# Teams Channel Intelligence Extractor (TCIE)

## Build
dotnet restore && dotnet build TeamsChannelScraper.sln
dotnet run --project TeamsChannelScraper.WPF/TeamsChannelScraper.WPF.csproj

## Stack
- .NET 8, C# 12, WPF (net8.0-windows)
- Microsoft Playwright 1.60.0 for browser automation (NOT Selenium)
- Newtonsoft.Json 13.0.3, CsvHelper 33.0.1
- MVVM (no DI framework — manual composition in App.xaml.cs)

## Architecture
MVVM strict: Views → ViewModels → Services → Models (no reverse refs)
Utilities (LoggerService, ConfigManager, Constants, Exceptions, RelayCommand) are cross-cutting.

## Key rules (enforced in code review)
- No .Result or .Wait() — WPF deadlock risk
- PasswordBox via code-behind only (not data binding)
- Path.Combine always — never string concatenation for paths
- All async methods accept CancellationToken cancellationToken = default
- ObservableCollection mutations via Application.Current.Dispatcher.Invoke()
- Progress<T> constructed on UI thread (captures SynchronizationContext)
- Services throw, ViewModels catch → surface to StatusMessage
- OperationCanceledException caught separately (not an error)
- Password never persisted, never logged — only email via DPAPI
- Export format enum includes All (loops all 3 formats in service)
- RelayCommand uses CommandManager.RequerySuggested for auto CanExecute refresh

## Namespaces
- TeamsChannelScraper.WPF
- TeamsChannelScraper.WPF.Models
- TeamsChannelScraper.WPF.Services
- TeamsChannelScraper.WPF.ViewModels
- TeamsChannelScraper.WPF.Views
- TeamsChannelScraper.WPF.Utilities

## Session persistence
Playwright StorageStateAsync → %APPDATA%\TeamsChannelScraper\session.json
Max age: 30 days (Constants.SessionMaxAgeDays)

## Export folders
%USERPROFILE%\Documents\TeamsExports\{ChannelName}\{yyyy-MM-dd_HHmmss}\
