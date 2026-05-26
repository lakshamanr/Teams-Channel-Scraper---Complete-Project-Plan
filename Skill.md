# Project Skills

## /build
Run: dotnet restore && dotnet build TeamsChannelScraper.sln -c Release

## /clean
Remove bin/ and obj/ from TeamsChannelScraper.WPF/

## /scrape
Trigger scrape: loads config, launches Playwright, authenticates, scrolls messages, parses threads, exports.
Entry: MainViewModel.StartScrapingCommand

## /export
Re-export last scraped data without re-running browser.
Uses: ExportService.ExportAsync(LastExportedData, ExportFolder, ExportFormat)

## /review-selectors
Open headful Chrome at teams.microsoft.com and log all message container attributes found.
Used to update Constants.cs selector values when Teams DOM changes.
