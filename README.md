# DevKit

> A developer workflow suite for **TFS / Azure DevOps** — branch automation, sprint planning verification, cross‑repo cherry‑picking, and handy day‑to‑day utilities, all in one local web app.

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![Blazor](https://img.shields.io/badge/Blazor-Server-512BD4)
![C#](https://img.shields.io/badge/C%23-nullable%20enabled-239120)
![Platform](https://img.shields.io/badge/platform-Windows-0078D6)
![Version](https://img.shields.io/badge/version-1.0.0-informational)

DevKit is a self‑hosted **Blazor Server** application that runs on your machine, connects to an on‑premises **TFS / Azure DevOps Server** via its REST API, and streamlines the repetitive parts of a sprint: creating well‑named branches, validating estimates, merging fixes across repositories, and more. Launch it and it opens automatically in your browser at **http://localhost:5850**.

---

## Table of Contents

- [Features](#features)
- [Tech Stack](#tech-stack)
- [Getting Started](#getting-started)
- [Configuration](#configuration)
- [Project Structure](#project-structure)
- [Security](#security)
- [Building & Publishing](#building--publishing)
- [Troubleshooting](#troubleshooting)
- [License](#license)

---

## Features

DevKit groups several focused tools behind a single navigation rail.

### 🔀 Branch Creator
Browse work items (Requirements / Change Requests / Bugs) for a selected **Area Path** and **Sprint**, then generate a standardized branch in one click.
- Auto‑generates branch names from a consistent pattern: `{TEAM}/{sprint}/{id}_{title}`.
- Suggests a matching PR title.
- Checks name availability and surfaces **existing branches** that already reference the work item.
- Creates the branch off a chosen base branch and **links it back to the work item** in TFS.
- Per‑repository base‑branch defaults (e.g. `main`, with repo‑specific overrides).

### 🗑 Branch Delete
Bulk cleanup of old sprint branches across up to 10 repositories at once.
- **Sprint‑driven** — pick an area and old sprints to see the branches their work items link, plus any branch named after one of those sprints (`{team}/{sprint}/{id}_{title}`).
- **Unattached team branches** — branches carrying the team prefix (e.g. `FXCPMGR`) that no work item links: checked against the selected sprints' work items and the work item named in the branch.
- Filters for work‑item link (All / Linked / Unattached), sprint, **Created by**, and search by branch, work item id or title.
- **Kept out of reach** — `main`, `master`, `develop`, `release`, `hotfix`, any `release/*` branch, every repository's QA and merging branch from Settings, and any branch with an active pull request. If pull requests can't be read, the whole repository is locked.
- Batched deletes with a progress bar; each branch's result is read back from TFS, and a branch pushed to since it was loaded is refused rather than deleted.

### 📐 Size Check
Validate that requirement sizes line up with the work actually planned. Lives in the **TFS Insight Hub** as its *Size Check* tab; the old `/planning` link redirects there.
- Loads a sprint's requirements with their **child tasks**.
- **Type** filter starts on Requirement + Change Request — tick **Bug** to pull bugs into the grid.
- Compares the requirement **Size** against summed task **Original / Remaining / Completed / Revised** hours.
- Computes a suggested **Calculated Size** = past‑sprint completed work + current‑sprint original estimate.
- Inline size editing, "Verify Size" mismatch filter, and **bulk‑apply** of calculated sizes.
- Writes back across process templates — CMMI `Size`, Scrum `Effort`, Agile `StoryPoints`.

### ⛙ Merge Tool (Cherry‑Pick)
Find and forward‑port changes tied to a work item.
- Searches **Pull Requests and commits** referencing a work item ID across all repositories.
- **Cherry‑picks** commits into a chosen base branch of a **locally cloned** repo using the local `git` CLI (fetch → checkout → pull → cherry‑pick → push).
- Detects "already applied" commits and **merge conflicts**; on conflict it can open the solution in **Visual Studio** for manual resolution and tracks cherry‑pick status/history.

### 🧾 Code Merging Sheet
Answers "did every work item's code reach the release branch?" for one or more sprints across up to 10 repositories. Each repository has a **QA branch** (where pull requests land) and a **merging branch** (where the code must end up), set under *Branch setup*.
- **Verify All** — the quick commit check. A commit counts as on the branch when it is there itself, as a `git cherry-pick -x` copy naming it, or as a copy with the same **author and author time** (git keeps both through a cherry-pick, so a message changed while fixing a conflict still matches). A message alone counts only when it is long, unique on the branch and not shared by two commits on the sheet. Reverts flip a commit to *rolled back*; commits older than the scan window are looked up one by one.
- **Compare Code** — the authoritative check. Each completed pull request's own change (its merge commit against the QA commit it merged onto) is compared with the merging branch **file by file and block by block**, ignoring whitespace and blank lines. Files identical to a known version are settled by git object id without downloading anything; the rest are compared line by line. Code that arrived under another message, a squash or a hand-fixed conflict reads as present; code **lost while resolving a conflict** reads as missing even when every commit is on the branch — flagged as *Code lost*. Lines QA changed again later are checked against QA's current version.
- **Three scopes** — the whole sheet (toolbar), one work item, or one pull request (the `</>` buttons). A pull request too big for the sheet-wide run (over 500 files) is marked *Too large here* and can be checked on its own, which allows up to 20,000 files.
- Per file: *Present*, *Matches QA*, *Missing*, *Partly merged* (with **Show lines** — the expected lines, each marked on branch / missing), or *Not compared* for binary and oversized files. A progress bar shows pull requests, files, percent and time left; **Stop** keeps finished results and restores the rest.

### 📝 Markdown Viewer
A live, GitHub‑style Markdown editor — no external dependencies.
- Editor / Split / Preview modes with live rendering.
- Import and export `.md`, copy as text, and live line / word / character counts.

### ⏱ Workday Calculator
Track your day from clock in/out entries.
- Computes total worked time, remaining time, and **estimated departure**.
- Live‑updating donut progress (refreshes every second).
- Full day (8h 30m), Half day (4h 30m), and Movement (7h 30m) targets; flexible time formats (`9:22`, `09:30:00 AM`, `13:05`, …).

### ⚙ Settings
- TFS / Azure DevOps **Server URL** + **Personal Access Token**, with a **Test Connection** button.
- **Team name** and default **Area / Sprint / Repository** that auto‑fill the other tools.
- **Default project path** scanning — finds locally cloned repos by their `.git` folder and **auto‑maps** them to TFS repos via the git remote URL.

---

## Tech Stack

| Layer | Technology |
|-------|------------|
| Runtime | .NET 10.0 LTS (SDK floor `10.0.100`, rolling forward to the newest installed major — see [`global.json`](DevKit.Web/global.json)) |
| UI | Blazor Server — Interactive Server Components |
| Language | C# (nullable + implicit usings enabled) |
| Integration | TFS / Azure DevOps Server REST API (default `api-version 5.0`), Basic auth with a PAT |
| Local Git | Shells out to the `git` CLI for cherry‑pick / merge operations |
| Front‑end | Hand‑written CSS ([`app.css`](DevKit.Web/wwwroot/app.css) plus `components.css`, `tools.css`, `merging.css`, `code-compare.css`) and vanilla JS ([`app.js`](DevKit.Web/wwwroot/app.js), `markdown.js`, `markdown-viewer.js`) — no SPA framework |
| Persistence | Local JSON file (`usersettings.json`) — no database |

---

## Getting Started

### Prerequisites

- **.NET 10 SDK** (`10.0.100` or newer — `global.json` rolls forward to the latest installed major). A published build is self-contained and needs no .NET install.
- **Git CLI** available on your `PATH` (required by the Merge Tool's cherry‑pick).
- **Windows** is recommended — the Merge Tool's "Open in Visual Studio" and the browser auto‑launch rely on Windows shell integration.
- Access to a **TFS / Azure DevOps Server** instance and a **Personal Access Token**.

### Run

```bash
git clone <repository-url>
cd DevKit/DevKit.Web
dotnet run
```

> Or from the repository root: `dotnet run --project DevKit.Web`

The app starts on **http://localhost:5850** and opens your default browser automatically. On first launch, go to **⚙ Settings**, enter your TFS URL and PAT, and click **Save & Connect**.

---

## Configuration

All configuration is done in‑app on the **Settings** page and stored locally — nothing needs to be edited by hand.

| Setting | Description |
|---------|-------------|
| **Server URL** | Your TFS / Azure DevOps collection, e.g. `https://tfs.example.com/tfs/YourCollection`. |
| **Personal Access Token** | A PAT for the same server. Scope it to **Code (read & write)** and **Work Items (read & write)** so branch creation, linking, and size updates work. |
| **Team Name** | Used as the prefix in generated branch names. |
| **Default Area / Sprint / Repository** | Pre‑selected across Branch Creator, Merge Tool, and Planning. |
| **Default Project Path** | Root folder containing your cloned repos. DevKit scans it for `.git` folders and maps each to a TFS repo by its remote URL. |

The TFS API version is configurable in [`appsettings.json`](DevKit.Web/appsettings.json) under `Tfs:ApiVersion` (default `5.0`).

---

## Project Structure

```
DevKit/
├─ README.md
├─ .gitignore
├─ devkit.bat                       # CLI entry point: run / publish / autostart / status
├─ scripts/
│  ├─ _set-sdk.bat                  # Resolves the SDK the CLI selects for global.json
│  └─ build|clean|restore|run.bat   # Wrappers; each calls _set-sdk.bat first
└─ DevKit.Web/                      # Blazor Server application
   ├─ Program.cs                    # DI registration, render mode, browser auto-launch
   ├─ DevKit.Web.csproj             # net10.0, nullable + implicit usings
   ├─ Directory.Build.props         # Product / Version metadata
   ├─ global.json                   # Pinned .NET SDK
   ├─ appsettings.json              # Logging + Tfs:ApiVersion
   ├─ Components/
   │  ├─ App.razor, Routes.razor    # Root document & router
   │  ├─ GlassDropdown.razor        # Shared dropdown component
   │  ├─ Layout/                    # MainLayout, NavMenu
   │  ├─ SizeCheckPanel.razor       # Size Check grid (hosted by the Insight Hub)
   │  ├─ CapacityPlanningPanel.razor # Capacity Planning tab (Insight Hub)
   │  ├─ TaskCreationPanel.razor    # Task Creation tab (Insight Hub)
   │  └─ Pages/                     # BranchCreator, BranchDelete, MergeTool,
   │                                #   Insights, MarkdownViewer,
   │                                #   WorkdayCalculator, Settings
   ├─ Services/
   │  ├─ TfsApiService.cs           # TFS/Azure DevOps REST client
   │  ├─ CodeMergingService.*.cs    # Code Merging Sheet: work items, PRs, commits
   │  ├─ MergeVerificationService.cs # Commit check (BranchHistoryIndex)
   │  ├─ CodeCompareService*.cs     # Code check: runs, scopes, limits, progress
   │  ├─ GitObjectReader.cs         # Diffs, trees, blobs from TFS, cached per run
   │  ├─ CodePresenceAnalyzer.cs    # Is a change's block on the branch?
   │  ├─ LineDiff.cs, TextLines.cs  # Line diff (Myers) and line normalisation
   │  ├─ GitCommandService.cs       # Local git CLI (cherry-pick, branches)
   │  ├─ SettingsService.cs         # usersettings.json + local repo scan/mapping
   │  ├─ PageStateService.cs        # Cached TFS data, per-tool state
   │  └─ ThemeService.cs            # Theme handling
   ├─ Models/                       # WorkItem, Tfs*, MergeModels, *Settings
   └─ wwwroot/                      # app|components|tools|merging|code-compare.css,
                                    #   app.js, markdown.js, markdown-viewer.js
```

---

## Security

- **The Personal Access Token is encrypted at rest** in `usersettings.json` using Windows DPAPI (current‑user scope), written next to the running binary (`AppContext.BaseDirectory`). Only your Windows account can decrypt it; a token from a previous version is migrated to the encrypted form automatically on first run. A source build keeps it in `bin/<Configuration>/<target framework>/`, so after a target‑framework upgrade the first run copies it over from the previous framework's folder (`net9.0` → `net10.0`) instead of starting unconfigured. The file is **git‑ignored** and never leaves your machine — but still treat it like a credential: don't copy it into source control, screenshots, or shared folders.
- Use a **least‑privilege PAT** (Code + Work Items only) and set a reasonable expiry.
- Prefer an **`https`** server URL — the PAT is sent as HTTP Basic auth, so over plain `http` to a remote host it travels unencrypted. Settings warns when a non‑local `http` URL is used.
- The app binds to **`localhost`**, sends hardening response headers (including a CSP), and is intended to run on a developer's own machine, not as a shared/hosted service. The browser is auto‑launched only for a local loopback URL.
- The Merge Tool runs real `git` commands (including `push`) against your local clones — branch names and commit SHAs are validated before use, and arguments are passed without shell parsing. Review the target branch before cherry‑picking.
- The app ships with **zero NuGet packages** — PAT encryption uses the OS DPAPI directly via P/Invoke, with no third‑party dependency.

---

## Building & Publishing

```bash
# Build
dotnet build DevKit.Web/DevKit.Web.sln -c Release

# Framework-dependent publish to ./publish
dotnet publish DevKit.Web/DevKit.Web.csproj -c Release -o ./publish
```

### .NET SDK selection

The wrapper scripts in [`scripts/`](scripts/) and `devkit.bat` do **not** hardcode an SDK
version or install path. Each one calls [`scripts/_set-sdk.bat`](scripts/_set-sdk.bat)
first, which runs `dotnet --version` from `DevKit.Web/` (the folder that owns
`global.json`), lets the CLI apply the `rollForward` rule, then points
`MSBuildSDKsPath` at exactly the SDK the CLI resolved. MSBuild and the CLI therefore
always agree.

`global.json` is the only place a version number belongs. Installing a newer SDK needs
no change here; if no installed SDK satisfies `global.json`, the scripts fail with the
list of installed SDKs and exit 1. Never reintroduce a literal
`set MSBuildSDKsPath=C:\Program Files\dotnet\sdk\<version>\Sdks` — it breaks on the
next patch install.

---

## Troubleshooting

| Symptom | Likely cause / fix |
|---------|--------------------|
| "Test Connection" fails | Check the Server URL and that the PAT is valid and unexpired with the right scopes. |
| No repos/areas/sprints load | The configured account may lack access, or the API version differs — adjust `Tfs:ApiVersion`. |
| Merge Tool can't cherry‑pick | Ensure `git` is on `PATH`, the repo is cloned under your Default Project Path, and it mapped to the right TFS repo on the Settings page. |
| Browser doesn't open | Navigate to http://localhost:5850 manually (auto‑launch is best‑effort). |

---

## License

No license file is currently included. Treat this repository as **proprietary / internal** unless a `LICENSE` is added.
