# Teams Channel Intelligence Extractor (TCIE)

A WPF desktop application that scrapes Microsoft Teams channel messages using browser automation, parses them into structured threads, and exports organized documentation.

## Prerequisites

- Windows 10/11
- .NET 8 SDK
- PowerShell 7+ (`pwsh`)
- Microsoft Teams web access

## Build & Run

```powershell
dotnet restore
dotnet build TeamsChannelScraper.sln -c Release

# Install Playwright browsers (first time only)
pwsh TeamsChannelScraper.WPF/bin/Release/net8.0-windows/playwright.ps1 install chromium

dotnet run --project TeamsChannelScraper.WPF/TeamsChannelScraper.WPF.csproj
```

## Features

- Browser-based Teams scraping via Microsoft Playwright
- Authenticated session persistence (skip re-login on subsequent runs)
- Thread structure detection (root messages + replies)
- Automatic category inference (Database, API, Deployment, Performance, Infrastructure, Security)
- Real-time search across all extracted threads
- Export to JSON, CSV, Markdown, or all three formats
- Light professional UI with Teams-purple accent

## Export Location

`%USERPROFILE%\Documents\TeamsExports\{ChannelName}\{yyyy-MM-dd_HHmmss}\`

## Architecture

MVVM strict — Views → ViewModels → Services → Models. No DI framework.
Playwright for .NET handles all browser automation.
DPAPI (Windows) used for email persistence. Passwords are never stored.

## Notes on Teams DOM Selectors

Teams web HTML changes frequently. If scraping stops finding messages, update the CSS selectors in `Utilities/Constants.cs`. Use the `/review-selectors` skill to inspect live Teams DOM.
