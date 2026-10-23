# DevKit

> Developer workflow tools for **TFS / Azure DevOps**: branch automation, sprint planning verification, code merging checks, and day‑to‑day utilities, all in one local web app.

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![Blazor](https://img.shields.io/badge/Blazor-Server-512BD4)
![C#](https://img.shields.io/badge/C%23-nullable%20enabled-239120)
![Platform](https://img.shields.io/badge/platform-Windows-0078D6)
![Version](https://img.shields.io/badge/version-1.0.0-informational)

DevKit is a self‑hosted **Blazor Server** application that runs on your machine, connects to an on‑premises **TFS / Azure DevOps Server** via its REST API, and handles the repetitive parts of a sprint: creating well‑named branches, validating estimates, checking that merged work reached the release branch, and more. Launch it and it opens automatically in your browser at **http://localhost:5850**.

---

## Table of Contents

- [Features](#features)
- [Tech Stack](#tech-stack)
- [Getting Started](#getting-started)
- [Shared on a server](#shared-on-a-server)
  - [Run it on another machine](#run-it-on-another-machine)
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

### 🗑 Branch Cleanup
Bulk cleanup of old sprint branches across up to 10 repositories at once.
- **Sprint‑driven**: pick an area and old sprints to see the branches their work items link, plus any branch named after one of those sprints (`{team}/{sprint}/{id}_{title}`).
- **Unattached team branches**: branches carrying the team prefix (e.g. `FXCPMGR`) that no work item links: checked against the selected sprints' work items and the work item named in the branch.
- Filters for work‑item link (All / Linked / Unattached), sprint, **Created by**, and search by branch, work item id or title.
- **Kept out of reach**: `main`, `master`, `develop`, `release`, `hotfix`, any `release/*` branch, every repository's QA and merging branch from Settings, and any branch an active pull request merges from or into. Pull requests and Settings are checked again just before deleting. If pull requests can't all be read, the whole repository is locked.
- Batched deletes with a progress bar; each branch's result is read back from TFS, and a branch pushed to since it was loaded is refused rather than deleted.

### 📐 Size Check
Validate that requirement sizes line up with the work actually planned. Lives in **Sprint Insights** as its *Size Check* tab; the old `/planning` link redirects there.
- Loads a sprint's requirements with their **child tasks**.
- **Type** filter starts on Requirement + Change Request; tick **Bug** to pull bugs into the grid.
- Compares the requirement **Size** against summed task **Original / Remaining / Completed / Revised** hours.
- Computes a suggested **Calculated Size** = past‑sprint completed work + current‑sprint original estimate.
- Inline size editing, "Verify Size" mismatch filter, and **bulk‑apply** of calculated sizes.
- Writes back across process templates: CMMI `Size`, Scrum `Effort`, Agile `StoryPoints`.

### 🧾 Code Merging Sheet
Answers "did every work item's code reach the release branch?" for one or more sprints across up to 10 repositories. Each repository has a **QA branch** (where pull requests land) and a **merging branch** (where the code must end up), set under *Branch setup*.
- **Verify All**: the quick commit check. A commit counts as on the branch when it is there itself, as a `git cherry-pick -x` copy naming it, or as a copy with the same **author and author time** (git keeps both through a cherry-pick, so a message changed while fixing a conflict still matches). A message alone counts only when it is long, unique on the branch and not shared by two commits on the sheet. Reverts flip a commit to *rolled back*; commits older than the scan window are looked up one by one.
- **Compare Code**: the authoritative check. Each completed pull request's own change (its merge commit against the QA commit it merged onto) is compared with the merging branch **file by file and block by block**, ignoring whitespace and blank lines. Files identical to a known version are settled by git object id without downloading anything; the rest are compared line by line. Code that arrived under another message, a squash or a hand-fixed conflict reads as present; code **lost while resolving a conflict** reads as missing even when every commit is on the branch, and is flagged as *Code lost*. Lines QA changed again later are checked against QA's current version.
- **Three scopes**: the whole sheet (toolbar), one work item, or one pull request (the `</>` buttons). A pull request too big for the sheet-wide run (over 500 files) is marked *Too large here* and can be checked on its own, which allows up to 20,000 files.
- Per file: *Present*, *Matches QA*, *Missing*, *Partly merged* (with **Show lines**, which lists the expected lines, each marked on branch / missing), or *Not compared* for binary and oversized files. A progress bar shows pull requests, files, percent and time left; **Stop** keeps finished results and restores the rest.

### 📝 Markdown Viewer
A live, GitHub‑style Markdown editor with no external dependencies.
- Editor / Split / Preview modes with live rendering.
- Import and export `.md`, copy as text, and live line / word / character counts.

### ⏱ Workday Calculator
Track your day from clock in/out entries.
- Computes total worked time, remaining time, and **estimated departure**.
- Live‑updating donut progress (refreshes every second).
- Full day (8h 30m), Half day (4h 30m), and Movement (7h 30m) targets; flexible time formats (`9:22`, `09:30:00 AM`, `13:05`, …).

### ⚙ Settings
- TFS / Azure DevOps **Server URL**, signed in with your **Windows account** (no token) or a **Personal Access Token**, with a **Test Connection** button that shows the account TFS signed you in as.
- **Team name** and default **Area / Sprint / Repository** that auto‑fill the other tools. The default sprint is either the **current sprint**, worked out from today's date and the sprint dates in TFS, or a sprint you choose.
- **Most-used repositories**, listed first in every repository picker and in the branch defaults.
- **Default branches for code merging**, per repository. Left empty, they are automatic: the newest release branch by version (such as `release/cp-26.5.2`) as the merging target, and `main` as the QA branch, falling back to the repository's default branch.
- **Export / Import** of all preferences as a file, to move them to another PC or share them. The TFS connection and token are never included, and an import never changes them.

---

## Tech Stack

| Layer | Technology |
|-------|------------|
| Runtime | .NET 10.0 LTS (SDK floor `10.0.100`, rolling forward to the newest installed major; see [`global.json`](global.json)) |
| UI | Blazor Server (Interactive Server Components) |
| Language | C# (nullable + implicit usings enabled) |
| Integration | TFS / Azure DevOps Server REST API (default `api-version 5.0`), Windows sign-in (Negotiate/NTLM) or Basic auth with a PAT |
| Front‑end | Hand‑written CSS ([`app.css`](DevKit.Web/wwwroot/app.css) plus `components.css`, `tools.css`, `merging.css`, `code-compare.css`) and vanilla JS ([`app.js`](DevKit.Web/wwwroot/app.js), `markdown.js`, `markdown-viewer.js`), no SPA framework |
| Persistence | Per-user JSON file (`%LOCALAPPDATA%\DevKit\usersettings.json`); in shared mode, each person's own browser; no database |

---

## Getting Started

### Prerequisites

- **.NET 10 SDK** (`10.0.100` or newer; `global.json` rolls forward to the latest installed major). A published build is self-contained and needs no .NET install.
- **Windows** is recommended: the PAT is protected with Windows DPAPI and the browser auto‑launch relies on Windows shell integration.
- Access to a **TFS / Azure DevOps Server** instance, with your **Windows account** (an on-premises server on your domain) or a **Personal Access Token**.

### Run

```bash
git clone <repository-url>
cd DevKit/DevKit.Web
dotnet run
```

> Or from the repository root: `dotnet run --project DevKit.Web`

The app starts on **http://localhost:5850**, prints a sign-in link (`DevKit is ready: http://localhost:5850/?k=…`) and opens it in your default browser. On first launch, go to **⚙ Settings**, enter your TFS URL, keep **Windows account** (or choose **Personal Access Token** and enter one), and click **Save & Connect**. To share DevKit with the team instead, see [Shared on a server](#shared-on-a-server).

---

## Shared on a server

DevKit can run once on a shared machine for the whole team. What makes it shared is a file on that machine, never anything in this repository: `devkit.json` in DevKit's folder of the account it runs as, `%LOCALAPPDATA%\DevKit\devkit.json`.

1. **Create the file** on the shared machine, signed in as the account DevKit will run as:

   ```json
   {
     "DevKit": {
       "Mode": "Hosted",
       "TfsUrl": "https://tfs.example.com/tfs/Collection",
       "SessionDays": 5
     }
   }
   ```

   The folder is already private to that account. To make sure only it, SYSTEM and Administrators can change the file, run the following, one command after the other:

   ```bash
   icacls "%LOCALAPPDATA%\DevKit\devkit.json" /inheritance:r
   ```

   ```bash
   icacls "%LOCALAPPDATA%\DevKit\devkit.json" /grant:r "%USERNAME%:F" "SYSTEM:F" "Administrators:F"
   ```

2. **Start DevKit** there with `scripts\devkit-host.bat` (or `devkit host`). It cleans, restores and builds the Release build, then starts DevKit shared on port 5850, on every name of the machine, in Production. It stops before building when devkit.json is missing or port 5850 is already in use. `devkit run` starts the last build again without rebuilding. To run DevKit on a machine without building it there, see [Run it on another machine](#run-it-on-another-machine).

3. **Everyone opens** `http://<machine name>:5850` (for example `http://devbox:5850`) from their own PC. There is no link to pass around.

Without the file DevKit runs in local mode, which only lets in the machine it runs on. `devkit local` forces local mode even when the file says otherwise.

- **Sign-in page.** *Sign in with your Windows account* signs a browser on the domain in with nothing to type. *Sign in as someone else* takes a Windows username and password, which Windows itself checks against the domain; DevKit keeps neither. It is offered only over https or on the server itself: over plain http from another PC the password would cross the network unencrypted, so the form is turned off and a posted password is refused before Windows sees it. A refusal never says whether the account exists, is locked or is disabled; the audit log does.
- **Session.** A sign-in lasts **5 days** (`SessionDays`) and then asks again. **Sign out** in the top bar ends it in every tab of that browser, so the next person can sign in. A session works only from the address it signed in from, so a copied session cookie is useless on any other machine; a PC whose address changes signs in again, and the audit log records it.
- **Several people, one machine.** Everyone has their own session and their own settings, wherever they sit. People signed in to the server itself (for example over Remote Desktop) each use their own Windows account and their own browser profile, so each signs in as themselves and sees only their own token and settings.
- **Each person's own TFS token and settings, kept only on their own PC.** After signing in, each person enters their PAT once in **⚙ Settings**. It and their settings are kept **in their own browser on their own PC, never on the server**. DevKit seals them with AES-256-GCM for their Windows account, under a key of their own that the Windows domain locks to the account DevKit runs as (DPAPI-NG). The browser keeps them, with that locked key, in cookies that are `HttpOnly`, `SameSite=Strict`, `Secure` over https, and sent only to DevKit's `/devkit-vault` address, so other sites never receive them. The browser also encrypts its cookies on disk for the person's Windows account. The server keeps no key and no settings.
  - **They follow DevKit to another server, with nothing copied.** The cookies belong to the machine's DNS domain (for example `metatreesystems.com`), and the domain opens their keys for the account DevKit runs as on any machine in it. So every DevKit server in the domain running as that same Windows account reads them: they come back on every later visit, after DevKit restarts or is updated, and when DevKit moves to another machine. A browser that opens DevKit by its short name or IP address keeps them for that name only.
  - **They stay on that PC.** Another browser or PC starts empty: enter the PAT once there. Clearing the browser's cookies removes them.
  - **Nobody else's.** Cookie names are a hash of the account, so no names show, and settings sealed for one account never open as another's. Only a program running as the account DevKit runs as can have the domain open a person's key, so another site in the domain that got someone's cookies could not read them; whoever signs in to Windows as that account could.
  - **When the domain does not answer.** DevKit asks a domain controller to open a person's key, or uses keys Windows already has from one. While it cannot, Settings says so and nothing is saved, so nobody's settings are overwritten; reloading the page once the domain answers brings them back.
  - **Moved off the server once.** Settings an earlier DevKit kept on the server (`%LOCALAPPDATA%\DevKit\users`) or in a browser move into the person's browser on their next visit, and are then deleted from the server.
- **Windows sign-in to TFS** acts as the account DevKit runs as, so in shared mode only that account may use it; everyone else uses their PAT.
- **The TFS server is fixed** for everyone by `TfsUrl`, so nobody can point DevKit, and their token, at another server.
- **Audit log.** Sign-ins, sign-outs and failed attempts are recorded as `user@machine`, with the reason, in `%LOCALAPPDATA%\DevKit\audit\signin-<yyyy-MM>.log` on the server. Passwords and tokens never appear in it.
- **Guessing protection.** After 3 wrong passwords an account waits 15 minutes before it may try again, which stays under common domain lockout limits. Each address may send 10 password sign-ins and 30 Windows sign-in requests a minute.
- **No error details leave the server.** Unhandled errors show a plain page and go to the log, even when DevKit runs in Development.
- **Nothing from IT.** Single sign-on uses NTLM, so it works by machine name, full name or IP address without an SPN or delegation.

| `DevKit` setting in devkit.json | Default | Meaning |
|---|---|---|
| `Mode` | `Local` | `Hosted`: shared, with the sign-in page. `Local`: only for the person who started it, with the printed link. |
| `TfsUrl` | none; needed in hosted mode | The one TFS collection everyone works against. |
| `SessionDays` | `5` | How long a sign-in lasts, from 1 to 30 days. |
| `HostNames` | this machine's names and addresses | The host names browsers may use; any other name is refused. |
| `Domain` | this machine's domain | The domain of a username typed without one. |
| `AuditFolder` | `%LOCALAPPDATA%\DevKit\audit` | Where the audit log is written. The keys that encrypt sessions are in `%LOCALAPPDATA%\DevKit\keys`. |

> Over plain `http`, the session cookie, the PAT someone types in Settings and every page cross the network unencrypted. *Sign in with your Windows account* sends no password, and the typed-password form is turned off. To serve https instead, give the same devkit.json a Kestrel endpoint with a certificate: it replaces the script's http address, the session cookie becomes `Secure`, and *Sign in as someone else* comes back.
>
> At start, DevKit opens one test connection to its https endpoint. It prints the link to share with the short machine name when the certificate covers it, since browsers sign in with Windows automatically only for names without dots. When the test fails, which a certificate whose private key Windows cannot use does, it logs an Error that says so.

### Run it on another machine

To keep DevKit running on a machine that stays on, make a package on your own machine and copy it there. That machine needs no .NET install and no copy of this repository.

1. **Make the package** with `scripts\devkit-deploy.bat` (or `devkit publish`). It restores, cleans and publishes DevKit self-contained for 64-bit Windows into `Release\` at the root of this repository:

   | In `Release\` | What it is |
   |---|---|
   | `devkit-run.bat` | Starts DevKit shared and keeps it running |
   | `app\` | DevKit, with the .NET runtime it needs |
   | `tools\` | The checks `devkit-run.bat` shares with `devkit-host.bat`, and the script that writes its Startup file |
   | `README.md` | This file |

   `Release\` is made again from nothing each time, so keep nothing else in it. No settings, tokens or keys ever go into it: DevKit keeps those per account on the machine it runs on. The script stops when a DevKit is running from `Release\` on your machine, and removes a package it could not finish.
2. **Copy the whole `Release` folder** to the other machine, for example to `D:\DevKit`.
3. **Create devkit.json there** for the account DevKit will run as, as in step 1 above, with a certificate if you want https. **When DevKit ran on another machine before**, run it here as the same Windows account: it then opens everyone's settings, which stay in their browsers, with nothing copied from the old machine, so nobody enters anything again. Under another account, people's settings stay unread until DevKit runs as the account that saved them, and anyone who saves in between replaces theirs.
4. **Run `devkit-run.bat`** there, signed in as that account. Like `devkit-host.bat`, it stops before starting when devkit.json is missing or port 5850 is in use, then starts DevKit shared, in Production, on port 5850.

How it stays up:

- **When DevKit stops with an error**, `devkit-run.bat` starts it again after 30 seconds. When it stops with an error within 5 minutes of starting 5 times in a row, something needs fixing first, such as devkit.json or the certificate, so the script stops too and leaves the messages on screen.
- **Each start and stop** is written, with the time and the exit code, to `devkit-run.log` beside devkit.json (`%LOCALAPPDATA%\DevKit\devkit-run.log`). DevKit's own messages stay in the window.
- **To stop it**, press Ctrl+C in its window and answer Y, or close the window.
- **It runs while that account is signed in.** A disconnected Remote Desktop session keeps it running; signing out stops it.
- **It starts again by itself at sign-in.** Each start puts `DevKit.cmd`, a five-line file that starts `devkit-run.bat`, in that account's own Startup folder (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`, also `shell:startup`), or replaces it when the package moved or the port changed, and leaves it alone otherwise. So after a restart DevKit starts, in a minimized window, as soon as that account signs in; until someone signs in, it waits. No administrator rights or stored password are needed. To stop it starting by itself, delete `DevKit.cmd` from that folder, or run `devkit remove`. A run with its own `DEVKIT_SETTINGS_PATH`, such as a test copy, adds nothing there.
- **No PowerShell.** `devkit-run.bat` and the script that writes `DevKit.cmd` are plain batch files, since endpoint protection such as Cortex XDR blocks a PowerShell script that writes to the Startup folder ("Suspicious script detected"). A `DevKit.lnk` shortcut an earlier DevKit put there is removed, so DevKit starts once.
- **To update it**, make a new package, stop DevKit on that machine, replace its folder with the new `Release` folder, and run `devkit-run.bat` again. Everyone's settings stay in their browsers, and the session keys in `%LOCALAPPDATA%\DevKit` of that account, not in the package.
- **Another port.** `set DEVKIT_PORT=5860` before running either script uses that port instead of 5850. With https, the port is the one in devkit.json's Kestrel `Url`, so keep the two the same.

### DevKit's own certificate (no IT needed)

Signed in as the account DevKit runs as, create a certificate for this machine's two names only. Its key stays in that account's store and cannot be exported; it is no certificate authority, so it can vouch for nothing but these names:

```powershell
New-SelfSignedCertificate -Subject "CN=DevKit $env:COMPUTERNAME" -DnsName $env:COMPUTERNAME, ([System.Net.Dns]::GetHostEntry('').HostName) `
    -FriendlyName 'DevKit https' -CertStoreLocation Cert:\CurrentUser\My -Provider 'Microsoft Software Key Storage Provider' `
    -KeyAlgorithm ECDSA_nistP256 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -Type Custom -KeyUsage DigitalSignature `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1', '2.5.29.19={critical}{text}ca=0') -NotAfter (Get-Date).AddYears(2)
```

The key is ECDSA, not RSA, on purpose. Windows serves the cipher suites in the order its policy lists them, and policies often put the RSA ones with AES-CBC first. Browsers refuse HTTP/2 over CBC (`ERR_HTTP2_INADEQUATE_TRANSPORT_SECURITY`), while an ECDSA certificate gets ECDHE-ECDSA with AES-GCM. To see the order on a server, run `Get-TlsCipherSuite`.

Save its public part to share, with the thumbprint the command printed:

```powershell
Export-Certificate -Cert Cert:\CurrentUser\My\<thumbprint> -FilePath "$env:LOCALAPPDATA\DevKit\DevKit-$env:COMPUTERNAME.cer"
```

Then point devkit.json at it (`DEVBOX` stands for the machine's name). `AllowInvalid` is there because the server need not trust its own certificate; browsers are the ones that check it:

```json
"Kestrel": { "Endpoints": { "Https": { "Url": "https://*:5850",
  "Certificate": { "Subject": "DevKit DEVBOX", "Store": "My", "Location": "CurrentUser", "AllowInvalid": true } } } }
```

Every PC trusts it once, the server included, since each Windows account trusts for itself: double-click the `.cer` file, choose **Install Certificate**, then **Current User**, put it in **Trusted Root Certification Authorities**, and confirm. Windows shows the thumbprint to compare with the one you announce. Until then that PC's browser warns that the connection is not private. IT can instead push the file to every PC through Group Policy. Two years on, make a new one the same way, and everyone installs the new file.

### A company certificate

A certificate from a CA every company PC already trusts needs no install anywhere. With its .pfx (certificate and private key), the account DevKit runs as imports it into its own store, with no administrator rights and the key not exportable. The password is typed into the prompt:

```powershell
Import-PfxCertificate -FilePath "<path to the .pfx>" -CertStoreLocation Cert:\CurrentUser\My -Password (Read-Host -AsSecureString "pfx password")
```

Then delete the .pfx, and point devkit.json at the certificate:

```json
"Kestrel": { "Endpoints": { "Https": { "Url": "https://*:5850", "Protocols": "Http1",
  "Certificate": { "Subject": "<its subject>", "Store": "My", "Location": "CurrentUser" } } } }
```

- `Subject` is matched against the certificate's subject (*Issued To*), not its alternative names, so a wildcard certificate is found by what its subject says.
- `"Protocols": "Http1"` is for an RSA key on a server whose cipher policy lists the RSA AES-CBC suites first: over CBC, browsers refuse HTTP/2. DevKit's pages work the same over HTTP/1.1. Leave it out for an ECDSA key.
- People open the name the certificate covers, which DevKit prints. A wildcard for the domain does not cover the short machine name.
- Imported into the machine's store instead (`Cert:\LocalMachine\My`, by an administrator), the key must be a machine key that the DevKit account may read (`certlm.msc`, *All Tasks*, then *Manage Private Keys*). A certificate whose key went into someone else's profile still loads, but every connection fails; DevKit's test connection at start logs that.

---

## Configuration

All configuration is done in‑app on the **Settings** page and stored locally; nothing needs to be edited by hand.

| Setting | Description |
|---------|-------------|
| **Server URL** | Your TFS / Azure DevOps collection, e.g. `https://tfs.example.com/tfs/YourCollection`. |
| **Sign in with** | **Windows account**: DevKit signs in to TFS as the Windows account it runs as, and no token is stored. **Personal Access Token**: for a server that does not accept your Windows account. |
| **Personal Access Token** | Only for PAT sign-in: a PAT for the same server. Scope it to **Code (read & write)** and **Work Items (read & write)** so branch creation, linking, and size updates work. |
| **Team Name** | Used as the prefix in generated branch names. |
| **Default Area / Sprint / Repository** | Pre‑selected across Branch Creator, the Code Merging Sheet, and Sprint Insights. The sprint is either the **current sprint** (today's date against the sprint dates in TFS, in the default area's project) or one you choose. Changing the sprint on one page leaves the other pages on the default. |
| **Most-Used Repositories** | Pinned repositories, shown first in every repository picker and in the branch defaults table. |
| **Default Branches for Code Merging** | The QA branch and merging target the Code Merging Sheet starts with, per repository. Empty means automatic: the newest `release/...-x.y.z` branch, else `main`, else the repository's default branch, for the merging target; `main`, else the repository's default branch, for the QA branch. |
| **Export / Import** | Buttons in the Settings header. The file holds every preference above, never the TFS connection or token. |

The TFS API version is configurable in [`appsettings.json`](DevKit.Web/appsettings.json) under `Tfs:ApiVersion` (default `5.0`).

---

## Project Structure

```
DevKit/
├─ README.md
├─ .gitignore
├─ DevKit.sln                       # The app and its tests
├─ global.json                      # Pinned .NET SDK
├─ Directory.Build.props            # Shared build settings (opt-in SonarAnalyzer)
├─ .github/workflows/ci.yml         # Windows CI: build, tests, coverage, Sonar report
├─ devkit.bat                       # CLI entry point: host / run / local / publish / autostart / status
├─ scripts/
│  ├─ devkit-host.bat               # Clean, restore, build, then start DevKit shared on this machine
│  ├─ devkit-deploy.bat             # Package DevKit into Release\ to run on another machine
│  └─ devkit-run.bat                # In the package: start DevKit shared and keep it running
├─ tools/
│  ├─ devkit-startup.bat            # In the package: writes DevKit.cmd in the account's Startup folder
│  ├─ set-hosted-env.bat            # Checks and settings shared by devkit-host.bat and devkit-run.bat
│  └─ set-sdk.bat                   # Resolves the SDK the CLI selects for global.json
├─ DevKit.Tests/                    # xUnit tests, end-to-end tests that run the built app
└─ DevKit.Web/                      # Blazor Server application
   ├─ Program.cs                    # DI registration, render mode, sign-in link and browser launch
   ├─ DevKit.Web.csproj             # net10.0, nullable + implicit usings
   ├─ Directory.Build.props         # Product / Version metadata
   ├─ appsettings.json              # Logging + Tfs:ApiVersion
   ├─ Components/
   │  ├─ App.razor, Routes.razor    # Root document & router
   │  ├─ GlassDropdown.razor        # Shared dropdown component
   │  ├─ Layout/                    # MainLayout, NavMenu
   │  ├─ SizeCheckPanel.razor       # Size Check grid (hosted by Sprint Insights)
   │  ├─ CapacityPlanningPanel.razor # Capacity Planning tab (Sprint Insights)
   │  ├─ TaskCreationPanel.razor    # Task Creation tab (Sprint Insights)
   │  └─ Pages/                     # BranchCreator, BranchDelete, CodeMergingSheet,
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
   │  ├─ SettingsService*.cs        # Settings in memory, PAT encryption, repository branch defaults
   │  ├─ SettingsFile.cs            # Per-user settings file: location, atomic save, backup, permissions
   │  ├─ LocalAccessGate.cs         # Sign-in key and cookie; refuses other machines, hosts and origins
   │  ├─ PageStateService.cs        # Cached TFS data, per-tool state
   │  └─ ThemeService.cs            # Theme handling
   ├─ Models/                       # WorkItem, Tfs*, TfsGitModels, *Settings
   └─ wwwroot/                      # app|components|tools|merging|code-compare.css,
                                    #   app.js, markdown.js, markdown-viewer.js
```

---

## Security

- **Settings live in a per-user file**, `%LOCALAPPDATA%\DevKit\usersettings.json`, which Settings shows. On Windows only your account, SYSTEM and Administrators can open it, and a rebuild, `dotnet clean` or `git clean` never touches it. Builds before this one kept the file beside the program (`bin/<Configuration>/<target framework>/`); the first start moves it, checks the copy, and deletes the old one. Set `DEVKIT_SETTINGS_PATH` to a full path to use a different file; that file is used as it is and nothing is moved into it.
- **Saves cannot tear or vanish silently.** Each save writes a private temporary file, flushes it, and swaps it in, keeping the previous version as `usersettings.json.bak`. A damaged file is set aside as `usersettings.json.corrupt-<time>` and the backup restored; a file DevKit could not read at startup is never saved over. A save that fails is shown in Settings and in the top bar instead of being reported as done.
- **The Personal Access Token is encrypted at rest** with Windows DPAPI (current‑user scope) and bound to the server URL, so it only decrypts for the server it was saved for: pointing the file at another server cannot hand the token over. A token in an older format (plaintext, or encrypted without the server) is re-encrypted automatically on first run. A token that cannot be decrypted is kept unchanged until you enter a new one. The file is **git‑ignored** and never leaves your machine, but still treat it like a credential: don't copy it into source control, screenshots, or shared folders.
- **Windows sign-in stores no token.** DevKit answers TFS's Negotiate/NTLM challenge as the Windows account it runs as, through an HTTP client of its own, so a PAT that TFS refuses fails instead of quietly going through as that account. The sign-in only goes to the server you entered, never on a redirect. Switching to Windows sign-in deletes a stored PAT, and the settings backup that still held it. With Windows sign-in DevKit acts with your full TFS rights, as your browser does, since there are no PAT scopes.
- Use a **least‑privilege PAT** (Code + Work Items only) and set a reasonable expiry.
- Prefer an **`https`** server URL: the PAT is sent as HTTP Basic auth, so over plain `http` to a remote host it travels unencrypted; a Windows sign-in sends no password, but its exchange can be relayed and the data is readable. Settings warns when a non‑local `http` URL is used.
- **Only the person who started DevKit can use it.** Everyone signed in to the same machine can reach a localhost port, and so can any web page open in your browser; without a lock they would all act with your PAT. Each start creates a random 256-bit key, prints the sign-in link and opens it; the link swaps the key for an `HttpOnly`, `SameSite=Strict` cookie and every other request gets a locked page. Requests from another machine, addressed to another host name (DNS rebinding), or sent from another origin (including the Blazor WebSocket) are refused. A restart creates a new key, so open the new link it prints; tabs from before it are signed out. Set `DEVKIT_NO_BROWSER=1` to only print the link.
- In local mode the app answers on **`localhost`** only; shared mode answers on this machine's own names (see above). Both send hardening response headers (including a CSP).
- **Shared mode** replaces the link with the sign-in page; see [Shared on a server](#shared-on-a-server). Requests for another host name, or sent from another site (forms, the WebSocket), are still refused; every sign-in form carries an anti-forgery token; and the session cookie is `HttpOnly`. The keys that encrypt sessions stay on the server, encrypted for the account DevKit runs as; losing them only signs everyone out. Everyone's settings are sealed in their own browsers, each under a key of their own that the domain locks to the account DevKit runs as (see [Shared on a server](#shared-on-a-server)), and the server keeps no settings and no key to them. The settings folder an earlier DevKit kept on the server, or a file in it, replaced by a link is refused.
- The app ships with **zero NuGet packages**: PAT encryption uses the OS DPAPI directly via P/Invoke, and shared mode's Windows sign-in uses .NET's own NTLM support, with no third‑party dependency.

---

## Building & Publishing

```bash
# Build the app and the tests (CI builds with -warnaserror)
dotnet build DevKit.sln -c Release

# Run the tests; the end-to-end ones start the built app on a free port, with a test settings file
dotnet test DevKit.sln -c Release

# SonarAnalyzer report, without failing the build
dotnet build DevKit.sln -c Release -p:DevKitSonarAnalyzer=true

# Framework-dependent publish to ./publish
dotnet publish DevKit.Web/DevKit.Web.csproj -c Release -o ./publish

# Self-contained package in Release\ to run on another machine (see "Run it on another machine")
scripts\devkit-deploy.bat
```

### .NET SDK selection

[`scripts/devkit-host.bat`](scripts/devkit-host.bat), [`scripts/devkit-deploy.bat`](scripts/devkit-deploy.bat)
and `devkit.bat` do **not** hardcode an SDK version or install path. Each one calls [`tools/set-sdk.bat`](tools/set-sdk.bat)
first, which runs `dotnet --version` from the repository root (the folder that owns
`global.json`), lets the CLI apply the `rollForward` rule, then points
`MSBuildSDKsPath` at exactly the SDK the CLI resolved. MSBuild and the CLI therefore
always agree.

`global.json` is the only place a version number belongs. Installing a newer SDK needs
no change here; if no installed SDK satisfies `global.json`, the scripts fail with the
list of installed SDKs and exit 1. `scripts\devkit-host.bat` and `scripts\devkit-deploy.bat`
exit with the failing step's exit code; pass `--no-pause` to run them unattended. Never reintroduce a literal
`set MSBuildSDKsPath=C:\Program Files\dotnet\sdk\<version>\Sdks`. It breaks on the
next patch install.

---

## Troubleshooting

| Symptom | Likely cause / fix |
|---------|--------------------|
| "Test Connection" fails | Check the Server URL. With Windows sign-in, check that your Windows account can open TFS in a browser, or use a PAT. With a PAT, check that it is valid and unexpired with the right scopes. |
| No repos/areas/sprints load | The configured account may lack access, or the API version differs; adjust `Tfs:ApiVersion`. |
| DevKit stops at start: "DevKit:TfsUrl is needed in hosted mode" | devkit.json says `"Mode": "Hosted"` but has no valid `TfsUrl`. The message names the file to fix. |
| Shared mode: the Windows button shows the sign-in page again | The browser did not sign in with Windows (for example, the address is not in its intranet zone). Open DevKit by machine name, or use the username and password form, which is there over https or on the server itself. |
| Shared mode over https: the browser says the connection was closed, and DevKit logged "a test connection to it failed" at start | Windows cannot use the certificate's private key for the account DevKit runs as, or the certificate covers neither of the machine's names. Use [DevKit's own certificate](#devkits-own-certificate-no-it-needed), or re-import the company .pfx and grant the key as [described](#a-company-certificate). |
| Shared mode over https: Edge or Chrome shows `ERR_HTTP2_INADEQUATE_TRANSPORT_SECURITY` | The server's cipher policy chose an AES-CBC suite for an RSA certificate. Make [DevKit's own certificate](#devkits-own-certificate-no-it-needed) with an ECDSA key, as shown there. |
| Shared mode over https: the browser warns the connection is not private | That PC has not installed DevKit's certificate yet. Install the `.cer` file as described under [DevKit's own certificate](#devkits-own-certificate-no-it-needed). |
| Shared mode: signed out before the 5 days were up | The PC's address changed (another network, Wi-Fi, VPN), and a session works only from the address it signed in from. Sign in again. |
| `devkit-host.bat` or `devkit-run.bat`: "Port 5850 is already in use" | A DevKit is already running. Close its window, then run the script again. |
| `devkit-deploy.bat`: "Release is in use, so it was left as it is" | A DevKit on your machine is running from `Release\` (for example one `devkit run` started), which locks its files. Close it, then run the script again. |
| `devkit-run.bat`: "so it is not started again" | DevKit stopped with an error soon after starting, 5 times in a row. The messages above it in the window say why, often a missing `TfsUrl` or a certificate Windows cannot use. Fix that, then run `devkit-run.bat` again. |
| Shared mode: Settings says your settings were saved by DevKit running under another Windows account | DevKit now runs as another account than the one that saved them, for example after a move to another machine. Stop DevKit, start it again as the account it ran as before, and reload the page; nothing is lost as long as nobody saved in between. Saving here instead replaces them with settings sealed for this account. |
| Shared mode: Settings says DevKit could not open your settings just now | The domain did not open the person's key, usually because no domain controller answered. Nothing is saved meanwhile, so nothing is overwritten. Reload the page in a minute. If it keeps happening for everyone, DevKit's window says why at start: shared DevKit runs as a domain account, on a machine in the domain. |
| Browser doesn't open, or the page says DevKit is locked | Open the `DevKit is ready: …` link from the DevKit window. A bookmark to http://localhost:5850 works once the browser has signed in during this run. |

---

## License

DevKit is released under the [MIT License](LICENSE).
