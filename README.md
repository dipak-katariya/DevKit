# DevKit

> A developer workflow suite for **TFS / Azure DevOps** — branch automation, sprint planning verification, cross‑repo cherry‑picking, and handy day‑to‑day utilities, all in one local web app.

![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)
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
Bulk branch cleanup for any repository.
- Filterable list of all branches with short SHAs.
- **Protected‑branch guard** — `main`, `master`, `develop`, `release`, `hotfix`, and any `release/*` branch cannot be selected.
- Select‑all / deselect‑all, an explicit confirmation modal, and a per‑branch delete log.

### 📐 Sprint Planning Verification
Validate that requirement sizes line up with the work actually planned.
- Loads a sprint's requirements with their **child tasks**.
- Compares the requirement **Size** against summed task **Original / Remaining / Completed / Revised** hours.
- Computes a suggested **Calculated Size** = past‑sprint completed work + current‑sprint original estimate.
- Inline size editing, "Verify Size" mismatch filter, and **bulk‑apply** of calculated sizes.
- Writes back across process templates — CMMI `Size`, Scrum `Effort`, Agile `StoryPoints`.

### ⛙ Merge Tool (Cherry‑Pick)
Find and forward‑port changes tied to a work item.
- Searches **Pull Requests and commits** referencing a work item ID across all repositories.
- **Cherry‑picks** commits into a chosen base branch of a **locally cloned** repo using the local `git` CLI (fetch → checkout → pull → cherry‑pick → push).
- Detects "already applied" commits and **merge conflicts**; on conflict it can open the solution in **Visual Studio** for manual resolution and tracks cherry‑pick status/history.

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
| Runtime | .NET 9.0 (SDK pinned to `9.0.312` via [`global.json`](DevKit.Web/global.json)) |
| UI | Blazor Server — Interactive Server Components |
| Language | C# (nullable + implicit usings enabled) |
| Integration | TFS / Azure DevOps Server REST API (default `api-version 5.0`), Basic auth with a PAT |
| Local Git | Shells out to the `git` CLI for cherry‑pick / merge operations |
| Front‑end | Hand‑written CSS ([`app.css`](DevKit.Web/wwwroot/app.css)) and vanilla JS ([`app.js`](DevKit.Web/wwwroot/app.js)) — no SPA framework |
| Persistence | Local JSON file (`usersettings.json`) — no database |

---

## Getting Started

### Prerequisites

- **.NET 9 SDK** (`9.0.312` or a newer 9.x — `global.json` rolls forward to the latest installed major).
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
└─ DevKit.Web/                      # Blazor Server application
   ├─ Program.cs                    # DI registration, render mode, browser auto-launch
   ├─ DevKit.Web.csproj             # net9.0, nullable + implicit usings
   ├─ Directory.Build.props         # Product / Version metadata
   ├─ global.json                   # Pinned .NET SDK
   ├─ appsettings.json              # Logging + Tfs:ApiVersion
   ├─ Components/
   │  ├─ App.razor, Routes.razor    # Root document & router
   │  ├─ GlassDropdown.razor        # Shared dropdown component
   │  ├─ Layout/                    # MainLayout, NavMenu
   │  └─ Pages/                     # BranchCreator, BranchDelete, MergeTool,
   │                                #   PlanningVerification, MarkdownViewer,
   │                                #   WorkdayCalculator, Settings
   ├─ Services/
   │  ├─ TfsApiService.cs           # TFS/Azure DevOps REST client
   │  ├─ GitCommandService.cs       # Local git CLI (cherry-pick, branches)
   │  ├─ SettingsService.cs         # usersettings.json + local repo scan/mapping
   │  ├─ PageStateService.cs        # Cached TFS data, per-tool state
   │  └─ ThemeService.cs            # Theme handling
   ├─ Models/                       # WorkItem, Tfs*, MergeModels, *Settings
   └─ wwwroot/                      # app.css, app.js
```

---

## Security

- **The Personal Access Token is stored in plaintext** in `usersettings.json`, written next to the running binary (`AppContext.BaseDirectory`). This file is **git‑ignored** and never leaves your machine — but treat it like a credential: don't copy it into source control, screenshots, or shared folders.
- Use a **least‑privilege PAT** (Code + Work Items only) and set a reasonable expiry.
- The app binds to **`localhost`** and is intended to run on a developer's own machine, not as a shared/hosted service.
- The Merge Tool runs real `git` commands (including `push`) against your local clones — review the target branch before cherry‑picking.

---

## Building & Publishing

```bash
# Build
dotnet build DevKit.Web/DevKit.Web.sln -c Release

# Framework-dependent publish to ./publish
dotnet publish DevKit.Web/DevKit.Web.csproj -c Release -o ./publish
```

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
