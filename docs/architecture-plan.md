# Teams Channel Scraper — Multi-Agent Build Pipeline Plan

## Source Directory (all agents write here)

**Repo root:** `/home/user/Teams-Channel-Scraper---Complete-Project-Plan/`  
**Branch:** `claude/teams-channel-scraper-WCKDx`  
**Remote:** `origin` (local proxy at `127.0.0.1:45387`)  

Every agent in every phase writes to this directory. No agent writes to `/tmp`, `~`, or any other location. The final push targets `origin/claude/teams-channel-scraper-WCKDx`.

---

## Context
Build a WPF C# desktop app (Teams Channel Intelligence Extractor) from a blank repo. The project spec is fully defined in the task description. Rather than one monolithic build, we run a **parallel + sequential multi-agent pipeline** where each agent owns a narrow slice of the codebase, then a final agent verifies the whole thing compiles.

---

## Automation Framework Decision

**Chosen: Microsoft Playwright for .NET**

Reasons over Selenium:
- Auto-downloads Chromium — no ChromeDriver version pinning
- Built-in reliable async waits (`WaitForSelectorAsync`, `WaitForLoadStateAsync`)
- Better handling of SPAs / dynamic content (Teams is a React SPA)
- First-class `net8.0` support, maintained by Microsoft
- Headless works out of the box without extra flags

NuGet package: `Microsoft.Playwright` (replaces `Selenium.WebDriver` + `Selenium.WebDriver.ChromeDriver` + `HtmlAgilityPack`)

---

## Pipeline Overview

```
Phase 0.5: Framework Eval        → 1 agent  (Playwright patterns, Teams DOM research)
         ↓
Phase 0a:  Architecture Design    → 1 agent  (design layers, patterns, ADR doc)
         ↓
Phase 0b:  Architecture Review #1 → 1 agent  (review ADR before any code is written)
         ↓
Phase 0c:  Scaffold               → 1 agent  (dirs + project files based on reviewed ADR)
         ↓
Phase 1:   Parallel build         → 3 agents simultaneously:
           ├── Agent A: Models (4 files)
           ├── Agent B: Services + Interfaces — Playwright impl (6 files)
           └── Agent C: Utilities (3 files)
         ↓
Phase 1R:  Architecture Review #2 → 1 agent  (review Models + Services layer: contracts, DI, error handling)
         ↓
Phase 2:   Parallel build         → 2 agents simultaneously:
           ├── Agent D: ViewModels + Search (4 files)  ← apply review fixes from 1R
           └── Agent E: WPF Views / XAML — light theme (5 files)
         ↓
Phase 2R:  Architecture Review #3 → 1 agent  (review MVVM compliance, UI/VM separation, search wiring)
         ↓
Phase 3:   Wire-up                → 1 agent  (App.xaml.cs + MainWindow.xaml.cs + apply 2R fixes)
         ↓
Phase 3R:  Architecture Review #4 → 1 agent  (final end-to-end review: data flow, security, thread safety)
         ↓
Phase 4a:  Code Review            → 1 agent  (read all files, flag compile-level issues)
         ↓
Phase 4b:  Verify + Fix           → 1 agent  (dotnet build, fix remaining issues from 3R + 4a)
         ↓
Phase 5:   Commit + Push          → orchestrator
```

**Total agents: ~14**  
Review agents (#1–#4) each receive the diff of what was written in the preceding phase plus the original ADR, so they can check for drift from the approved design.

---

## Phase 0.5 — Framework Evaluation Agent (sequential, first)

**Responsibility:** Research Playwright .NET patterns for Microsoft Teams web scraping. Outputs a findings doc (in-memory / returned as text) that the Scaffold and Services agents use.

**Research tasks:**
1. Confirm `Microsoft.Playwright` NuGet package name and current stable version for .NET 8
2. Identify the correct `playwright install chromium` CLI command for CI/headless environments
3. Find known Teams web DOM selectors (data attributes on message elements) from public sources
4. Identify Teams login flow: AAD OAuth redirect → email field → password field → MFA prompt handling
5. Confirm `IBrowserContext` persistence pattern for storing authenticated session (avoid re-login on each run)
6. Identify async wait strategies: `WaitForSelectorAsync`, `WaitForLoadStateAsync("networkidle")`, `Page.EvaluateAsync` for scroll

**Output used by:** Phase 0 (project file package versions), Phase 1B (TeamScraperService implementation selectors and auth flow)

---

## Phase 0a — Architecture Design Agent (sequential, after 0.5)

**Responsibility:** Produce a binding architecture decision record (ADR) used by all implementation agents.

**Outputs:**
- Layer diagram: UI → ViewModels → Services → Models (MVVM strict, no Model references in Views)
- Interface contracts (names, method signatures, return types) — lock these before any code is written so agents don't conflict
- Naming conventions: file-scoped namespaces, `Async` suffix on async methods, `I` prefix on interfaces, `ViewModel` suffix on VMs
- Thread safety rules: all `ObservableCollection` mutations on UI Dispatcher, `CancellationToken` passed through all async chains
- Error boundary: every `Task`-returning method wraps in try/catch, surfaces to ViewModel's `StatusMessage`, never swallows exceptions silently
- Playwright session strategy: persist `IBrowserContext` state to `%APPDATA%\TeamsChannelScraper\session.json` to skip re-auth on subsequent runs
- Export folder convention: `%USERPROFILE%\Documents\TeamsExports\{ChannelName}\{yyyy-MM-dd_HHmmss}\`
- Search strategy: `CollectionViewSource` with `Filter` delegate, updated on `SearchText` `PropertyChanged`, filters across category + author + content fields

---

## Phase 0b — Architecture Review #1 (sequential, after 0a)

**Input:** ADR from Phase 0a  
**Checks:**
- SOLID violations (any service doing more than one thing?)
- Missing error paths (Playwright browser crash, Teams login failure, MFA)
- Data privacy (is plaintext password ever logged or serialized?)
- MVVM compliance planned correctly (no service types in Views)?
- Search strategy sound (`CollectionViewSource` with async data)?
- Cancellation token propagated through all async chains?

**Output:** Approved ADR + correction list → Phase 0c scaffold agent uses this as ground truth.

---

## Architecture Review #2 (Phase 1R — after Models + Services are written)

**Input:** All files written in Phase 1 (Models/, Services/, Utilities/) + approved ADR  
**Checks:**
- Do service interfaces match the ADR contracts exactly?
- Does `TeamScraperService` properly dispose `IPlaywright` / `IBrowser`?
- Are `CancellationToken` params present on all async service methods?
- Does `ConfigManager` DPAPI path use `%APPDATA%`, not hardcoded path?
- Does `ExportService` use `Path.Combine` (never string concat) for paths?
- Is `LoggerService` Dispatcher-safe for cross-thread calls?

**Output:** Issue list → Phase 2 agents receive this and fix any identified problems as they write ViewModels/Views.

---

## Architecture Review #3 (Phase 2R — after ViewModels + Views are written)

**Input:** All files written in Phase 2 (ViewModels/, Views/, XAML) + ADR + Review #2 issues  
**Checks:**
- MVVM: do any XAML code-behinds reference Service types directly (only ViewModel and Window allowed)?
- `PasswordBox` wired via code-behind event, not binding?
- `CollectionViewSource` filter delegate correctly updates when `SearchText` changes?
- Light theme applied consistently — no `#1E1E1E` or dark colors in any XAML?
- `BoolToVisibilityConverter` declared in `App.xaml` resources?
- Log `ListBox` background is near-white (`#FAFAFA`), not dark?

**Output:** Issue list → Phase 3 wire-up agent fixes these during wiring.

---

## Architecture Review #4 (Phase 3R — final end-to-end review)

**Input:** All written files + ADR + all previous review outputs  
**Checks (end-to-end data flow):**
- `App.xaml.cs` wires all services correctly (no missing dependency)?
- `MainWindow.xaml` `StartupUri` matches class name?
- `App.xaml` `x:Class` matches `App.xaml.cs` namespace?
- Export folder defaults to `%USERPROFILE%\Documents\TeamsExports` if left blank?
- `ResultsWindow` search TextBox binding reaches `ResultsViewModel.SearchText`?
- `ScrapingWindow` cancellation button wired to `CancellationTokenSource.Cancel()`?
- No direct `Task.Result` or `.Wait()` calls (deadlock risk in WPF)?

**Output:** Final go/no-go for Phase 4b verify agent. If issues found, Phase 4b fixes them before building.

---

## Phase 0c — Scaffold Agent (sequential, after 0b)

> **Caveman mode (auto-approve):** Once the plan is approved, the orchestrator runs all phases without pausing for confirmation. Each agent writes its files and the pipeline advances automatically. The orchestrator only stops if `dotnet build` fails with errors it cannot diagnose.

**Additional files created by Scaffold agent (beyond project structure):**

### CLAUDE.md (repo root)

Contains everything Claude needs to work on this project without re-reading the spec:
```markdown
# Teams Channel Intelligence Extractor (TCIE)

## Build
dotnet restore && dotnet build TeamsChannelScraper.sln
dotnet run --project TeamsChannelScraper.WPF/TeamsChannelScraper.WPF.csproj

## Architecture
MVVM — Views → ViewModels → Services → Models (strict, no cross-layer refs)
Playwright for .NET (not Selenium) — handles Teams browser automation
DPAPI password encryption via System.Security.Cryptography.ProtectedData
Light professional theme (#F5F5F5 bg, #6264A7 Teams-purple accent)
Search via CollectionViewSource.Filter on ResultsViewModel.SearchText

## Namespaces
TeamsChannelScraper.WPF          (App, MainWindow)
TeamsChannelScraper.WPF.Models   
TeamsChannelScraper.WPF.Services 
TeamsChannelScraper.WPF.ViewModels
TeamsChannelScraper.WPF.Views    
TeamsChannelScraper.WPF.Utilities

## Key rules
- All ObservableCollection writes: Dispatcher.Invoke(...)
- All async methods: accept CancellationToken, no .Result/.Wait()
- Passwords: never log, never write plaintext to disk (DPAPI only)
- Paths: always Path.Combine, never string concat
- Export folder: %USERPROFILE%\Documents\TeamsExports\{channel}\{timestamp}\
```

### Skill.md (repo root)

Defines custom slash commands for this project:
```markdown
# Project Skills

## /scrape
Trigger a full scrape run against the configured Teams channel.
Steps: load config → launch Playwright browser → authenticate → scroll-load messages → parse threads → export.
Entry point: MainViewModel.StartScrapingCommand

## /export
Re-export the last scraped dataset without re-running the browser.
Uses: ExportService.ExportAsync(LastExportedData, ExportFolder, ExportFormat)

## /review-selectors  
Open a headful Chrome window pointed at Teams and log all [data-message-id] element attributes found. Used to update Constants.cs selector values when Teams DOM changes.

## /build
Run: dotnet restore && dotnet build TeamsChannelScraper.sln -c Release

## /clean
Remove bin/ and obj/ folders from TeamsChannelScraper.WPF/
```

### .claude/settings.json (auto-approve permissions)

Configure Claude Code to allow common operations without prompting:
```json
{
  "permissions": {
    "allow": [
      "Bash(dotnet:*)",
      "Bash(mkdir:*)",
      "Bash(git add:*)",
      "Bash(git commit:*)",
      "Bash(git push:*)",
      "Bash(pwsh:*)"
    ]
  }
}
```


**Responsibility:** Create the skeleton that all other agents write into.

Files to create:
- `TeamsChannelScraper.sln`
- `TeamsChannelScraper.WPF/TeamsChannelScraper.WPF.csproj` (NuGet refs below)
- `TeamsChannelScraper.WPF/App.xaml` (stub)
- `TeamsChannelScraper.WPF/App.xaml.cs` (stub)
- `TeamsChannelScraper.WPF/MainWindow.xaml` (stub)
- `TeamsChannelScraper.WPF/MainWindow.xaml.cs` (stub)
- Empty directories: Models/, Services/, ViewModels/, Views/, Utilities/
- `README.md`

**.csproj NuGet packages (Playwright replaces Selenium):**
```xml
<PackageReference Include="Microsoft.Playwright" Version="1.44.0" />
<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
<PackageReference Include="CsvHelper" Version="33.0.1" />
```
`System.Security.Cryptography.ProtectedData` is built into `net8.0-windows` — no extra package needed. `HtmlAgilityPack` and `Selenium.*` packages are **not** included.

Target framework: `net8.0-windows`, `<UseWPF>true</UseWPF>`, `<OutputType>WinExe</OutputType>`

---

## Phase 1A — Models Agent (parallel)

**Files:** `TeamsChannelScraper.WPF/Models/`
- `TeamMessage.cs` — Id, Author, Timestamp, Content, ParentMessageId, ReplyCount, Replies, IsEdited, EditedTimestamp
- `ThreadStructure.cs` — ThreadId, Problem, Solutions, InferredCategory, RelevanceScore, FirstMessageTime, LastMessageTime, TotalMessages, GeneratedMarkdown
- `ExportedData.cs` — ExportedAt, ChannelName, ChannelUrl, TotalMessagesScraped, TotalThreadsIdentified, CategoriesCount dict, Threads list, ExportPaths list
- `ScrapingConfig.cs` — TeamName, ChannelName, ChannelUrl, MicrosoftEmail, MicrosoftPassword, MaxMessages=1000, ScrollDelayMs=500, IncludeReplies, HeadlessBrowser, ExportFormat

Namespace: `TeamsChannelScraper.WPF.Models`

---

## Phase 1B — Services Agent (parallel)

**Files:** `TeamsChannelScraper.WPF/Services/`
- `ITeamsScraper.cs` — interface + `ScrapingProgress` record
- `TeamScraperService.cs` — **Playwright** implementation: `Playwright.CreateAsync()` → `IBrowser` → `IBrowserContext` (with stored session state), Teams AAD login flow (`WaitForSelectorAsync`, fill email/password), `WaitForLoadStateAsync("networkidle")`, scroll-to-load via `Page.EvaluateAsync("window.scrollTo(0, 0)")`, `QuerySelectorAllAsync` with data-attribute selectors from Phase 0.5 findings, RetryAsync helper
- `IMessageParser.cs` — interface: ParseThreads, InferCategory, GenerateMarkdown
- `MessageParserService.cs` — builds ThreadStructure list from flat messages, keyword dict for 6 categories, relevance score (reply count + content length heuristic), Markdown builder using StringBuilder
- `IExportService.cs` — interface: ExportToJsonAsync, ExportToCsvAsync, ExportToMarkdownAsync, ExportAllAsync
- `ExportService.cs` — JSON via Newtonsoft, CSV via CsvHelper, Markdown one-file-per-category + README index

Namespace: `TeamsChannelScraper.WPF.Services`

---

## Phase 1C — Utilities Agent (parallel)

**Files:** `TeamsChannelScraper.WPF/Utilities/`
- `LoggerService.cs` — timestamped entries, LogLevel enum (Info/Warning/Error/Success), ObservableCollection<string>, max 100 entries
- `ConfigManager.cs` — SaveConfig/LoadConfig using Newtonsoft.Json + DPAPI encrypt/decrypt (ProtectedData.Protect/Unprotect, DataProtectionScope.CurrentUser)
- `Constants.cs` — CSS selector strings, default paths, category keyword lists

Namespace: `TeamsChannelScraper.WPF.Utilities`

---

## Phase 2A — ViewModels Agent (parallel, after Phase 1)

> **Search in ResultsViewModel:** Add `SearchText` property. Expose a `CollectionViewSource`-backed `FilteredThreads` that filters `Threads` in real-time: match on `InferredCategory`, `Problem.Author`, `Problem.Content`, and any reply content. Clear filter when `SearchText` is empty.



**Files:** `TeamsChannelScraper.WPF/ViewModels/`
- `RelayCommand.cs` — ICommand implementation with Action<object?> and Predicate<object?> (reusable for all commands)
- `ViewModelBase.cs` — INotifyPropertyChanged base with `SetProperty<T>` helper
- `MainViewModel.cs` — all configuration properties (TeamName, ChannelUrl, Email, Password, MaxMessages, ScrollDelayMs, IncludeReplies, HeadlessBrowser), status properties (Status, ProgressPercentage, MessagesScraped, ThreadsIdentified, ElapsedTime, IsScrapingInProgress), ObservableCollection<string> Logs, commands: StartScraping, StopScraping, SaveConfig, LoadConfig, ExportResults, ClearLogs, OpenOutputFolder
- `ScrapingProgressViewModel.cs` — wraps ScrapingProgress for display
- `ResultsViewModel.cs` — binds to ExportedData, exposes category chart data, thread list, selected thread detail

Namespace: `TeamsChannelScraper.WPF.ViewModels`

---

## Phase 2B — Views Agent (parallel, after Phase 1)

> **UI Constraints:**  
> - **No dark theme.** Use a clean light professional palette: background `#F5F5F5`, text `#1A1A1A`, accent `#6264A7` (Teams purple), white cards/group boxes with subtle `#E0E0E0` borders.  
> - **Search is a first-class feature** in ResultsWindow: a TextBox filter above the thread list that filters by keyword across thread content, author name, and category in real-time (binds to `ResultsViewModel.SearchText`, which filters `FilteredThreads` using a `CollectionViewSource`).



**Files:** `TeamsChannelScraper.WPF/Views/` and root XAML:
- `Resources/Styles.xaml` — **light professional theme**: background #F5F5F5, foreground #1A1A1A, accent #6264A7 (Teams purple), subtle border/shadow on group boxes, clean Button and TextBox templates. No dark backgrounds anywhere.
- `MainWindow.xaml` — complete layout per spec: Configuration group box (team/channel/url/email/password/max-messages/scroll-delay/checkboxes), Status group box (status label + ProgressBar + counters + elapsed timer), Logs ListBox (auto-scroll), Export Options group box (format radio buttons + output path + folder picker button)
- `MainWindow.xaml.cs` — minimal code-behind: DataContext = new MainViewModel(), PasswordBox handling (PasswordBox.PasswordChanged → ViewModel), log auto-scroll, folder picker dialog
- `Views/ResultsWindow.xaml` — summary stats, category bars (Grid with width-bound rectangles), thread ListView with detail panel
- `Views/ResultsWindow.xaml.cs` — DataContext = ResultsViewModel

Namespace: `TeamsChannelScraper.WPF` / `TeamsChannelScraper.WPF.Views`

---

## Phase 3 — Wire-up Agent (sequential, after Phases 1+2)

**Responsibility:** Connect everything in App.xaml.cs and finalize MainWindow plumbing.
- `App.xaml` — merge Styles.xaml ResourceDictionary, StartupUri=MainWindow.xaml
- `App.xaml.cs` — global exception handler, service instantiation (no DI framework: `new TeamScraperService()`, `new MessageParserService()`, `new ExportService()`, `new LoggerService()` wired into MainViewModel)
- Verify all `using` statements and namespaces are consistent

---

## Phase 4 — Verify Agent (sequential)

**Responsibility:** Run `dotnet build` and fix any compile errors.
- Run `dotnet restore && dotnet build` from repo root
- Fix namespace mismatches, missing using directives, type errors
- Re-run until build succeeds (0 errors)
- Note: WPF app won't run on Linux (Windows-only), but must compile cleanly

---

## Phase 5 — Orchestrator commits and pushes

After Phase 4 green:
```
git add -A
git commit -m "feat: implement Teams Channel Intelligence Extractor WPF app"
git push -u origin claude/teams-channel-scraper-WCKDx
```

---

## Critical Files Summary

| File | Phase | Notes |
|------|-------|-------|
| `TeamsChannelScraper.WPF.csproj` | 0 | NuGet refs, WPF target |
| `Models/TeamMessage.cs` | 1A | Root data model |
| `Services/TeamScraperService.cs` | 1B | Selenium + retry logic |
| `Services/MessageParserService.cs` | 1B | Category + markdown |
| `Services/ExportService.cs` | 1B | JSON/CSV/MD export |
| `Utilities/ConfigManager.cs` | 1C | DPAPI password storage |
| `ViewModels/MainViewModel.cs` | 2A | MVVM hub |
| `MainWindow.xaml` | 2B | UI entry point |
| `App.xaml.cs` | 3 | Service wiring |

---

## UI Theme Summary (enforced across all views)

| Element | Value |
|---|---|
| Window background | `#F5F5F5` |
| Card / GroupBox background | `White` with `#E0E0E0` border |
| Primary text | `#1A1A1A` |
| Secondary / label text | `#555555` |
| Accent (buttons, selection) | `#6264A7` (Teams purple) |
| Success indicators | `#107C10` (green) |
| Error indicators | `#D83B01` (red) |
| Font | Segoe UI, 12px base |
| Log list background | `#FAFAFA` (near-white, not dark) |

## Search Feature (ResultsWindow)

```
┌─ Results ──────────────────────────────────────────────────┐
│  Messages: 234  │  Threads: 45  │  Category Stats ▼        │
├─────────────────────────────────────────────────────────────┤
│  🔍 Search: [___________________________________]  [Clear]  │
│     Filter by: ● All  ○ Category  ○ Author  ○ Content       │
├──────────────────┬──────────────────────────────────────────┤
│  Thread List     │  Selected Thread Preview                  │
│  ─────────────   │  ────────────────────────────────────────│
│  [Database] DB.. │  ## [Database] Thread                    │
│  [API] Auth..    │  **john.doe** — 2026-05-20 09:15         │
│  [Deploy] Pi..   │                                          │
│  ...             │  Database connection timeout on prod...   │
│                  │                                          │
│                  │  ### Replies (5)                         │
│                  │  - jane.smith (09:20): Check pool size.. │
│                  │  - john.doe (09:25): Pool is 100...      │
└──────────────────┴──────────────────────────────────────────┘
```

`ResultsViewModel.SearchText` filters `FilteredThreads` (a `CollectionViewSource`) on every keystroke — no button press needed.

## Phase 4a — Code Review Agent (sequential, after Phase 3)

**Responsibility:** Read every written file and flag issues before the build runs.

**Checks:**
- Services never import ViewModel types (dependency direction)
- No plaintext password in logs or JSON output
- All `async` methods accept and forward `CancellationToken`
- All `ObservableCollection` writes go through `Dispatcher.Invoke`
- `IDisposable` implemented on `TeamScraperService` (Playwright `IPlaywright` disposed)
- XAML `x:Class` attributes match code-behind namespaces exactly
- `PasswordBox` value wired via code-behind, not binding (WPF security constraint)
- Export paths use `Path.Combine`, never string concatenation
- `CollectionViewSource` filter refreshes on `SearchText` change

**Output:** List of issues → passed directly to Phase 4b agent for fixing before build.

---

## Verification

1. `dotnet restore` — all NuGet packages resolve
2. `dotnet build -c Release` — 0 errors, 0 warnings (goal)
3. Inspect generated output: `bin/Release/net8.0-windows/TeamsChannelScraper.WPF.exe` exists
4. Review key files for correctness: MessageParserService category logic, ConfigManager DPAPI, ExportService CSV headers
