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
- **Save settings** confirms that your settings are kept, and says where.

#### Where shared DevKit keeps settings
Shared DevKit keeps every person's settings and TFS token on the DevKit server, encrypted for that person alone, so they never depend on the browser: a browser that deletes its data when it closes, another browser or PC, a redeploy, a package switch or another port all find the same settings. Nothing needs installing on anyone's PC. See [Shared on a server](#shared-on-a-server) for how they are protected.

---

## Tech Stack

| Layer | Technology |
|-------|------------|
| Runtime | .NET 10.0 LTS (SDK floor `10.0.100`, rolling forward to the newest installed major; see [`global.json`](global.json)) |
| UI | Blazor Server (Interactive Server Components) |
| Language | C# (nullable + implicit usings enabled) |
| Integration | TFS / Azure DevOps Server REST API (default `api-version 5.0`), Windows sign-in (Negotiate/NTLM) or Basic auth with a PAT |
| Front‑end | Hand‑written CSS ([`app.css`](DevKit.Web/wwwroot/app.css) plus `components.css`, `tools.css`, `merging.css`, `code-compare.css`) and vanilla JS ([`app.js`](DevKit.Web/wwwroot/app.js), `markdown.js`, `markdown-viewer.js`), no SPA framework |
| Persistence | Per-user JSON file (`%LOCALAPPDATA%\DevKit\usersettings.json`); in shared mode, one sealed file per person in `%LOCALAPPDATA%\DevKit\vault` on the server; no database |

---

## Getting Started

### Prerequisites

- **.NET 10 SDK** (`10.0.100` or newer; `global.json` rolls forward to the latest installed .NET 10 SDK, never to another major). A published build is self-contained and needs no .NET install.
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
       "SessionDays": 30
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

   Shared DevKit checks that folder at every start, and does not start when another account than this one, SYSTEM and administrators may change it or anything in it, or when it is a link: whoever could change it could change devkit.json or the keys that sign people in. That happens when `DEVKIT_SETTINGS_PATH` moves the folder to another drive, where Windows lets every signed-in user change what is made there. The message names who may change what; `tools\devkit-lock.bat "<folder>"` closes it. A member of this machine's Administrators group counts as one of the administrators, also when a permission names their own account, as an IT account given the whole profile does: an administrator may take any file over anyway. A folder that is not there yet is made closed.

2. **Start DevKit** there with `scripts\devkit-host.bat` (or `devkit host`). It cleans, restores and builds the Release build, then starts DevKit shared on port 5850, on every name of the machine, in Production. It stops before building when devkit.json is missing or port 5850 is already in use. `devkit run` starts the last build again without rebuilding. To run DevKit on a machine without building it there, see [Run it on another machine](#run-it-on-another-machine).

3. **Everyone opens** `http://<machine name>:5850` (for example `http://devbox:5850`) from their own PC. There is no link to pass around.

Without the file DevKit runs in local mode, which only lets in the machine it runs on. `devkit local` forces local mode even when the file says otherwise.

- **Signing in by itself.** A page opened without a sign-in tries Windows sign-in first, so a browser that signs in with Windows by itself goes straight on to the page and never shows the sign-in page. A Windows sign-in counts only when the browser made it for one of DevKit's own names (`HostNames`): one someone collected on a site of their own and passed on to DevKit (NTLM relay) names their site, and is refused. Chrome and Edge also name the port, so a site someone runs on another port of this same server is refused too; browsers that leave the port out cannot be told apart from such a site, so on a server others sign in to, use Chrome or Edge. Over https a Windows sign-in is also bound to the TLS connection it came in on (Extended Protection), so an answer a browser gave inside another connection does not complete it. A browser that asks for a password first shows its own password box; *Cancel* there shows the sign-in page.
- **Sign-in page.** *Sign in with your Windows account* signs a browser on the domain in with nothing to type. *Sign in as someone else* takes a Windows username and password, which Windows itself checks against the domain; DevKit keeps neither. It is offered only over https or on the server itself: over plain http from another PC the password would cross the network unencrypted, so the form is turned off and a posted password is refused before Windows sees it. A refusal never says whether the account exists, is locked or is disabled; the audit log does.
- **Session.** A sign-in lasts **30 days** (`SessionDays`) and then signs in again, by itself where the browser can. **Sign out** in the top bar ends it in every tab of that browser and shows the sign-in page, so the next person can sign in. Sign-outs are kept in `%LOCALAPPDATA%\DevKit\signed-out.txt` until those sessions would have ended, so a restart or a package switch never brings one back. A session works only from the address it signed in from, so a copied session cookie is useless on any other machine; a PC whose address changes signs in again, and the audit log records it, once every 10 minutes per session. When DevKit listens on https alone, the cookie is `__Host-devkit-session`: `Secure`, for DevKit's own name and the whole site, which browsers enforce, so no plain http page and no other site of the domain can set or replace it. The first start of a version with this cookie signs everyone in once more, which Windows sign-in does by itself.
- **Several people, one machine.** Everyone has their own session and their own settings, wherever they sit. Sign-in limits per address (10 typed passwords and 30 Windows sign-in requests a minute) apply to other machines; browsers on the server itself all come from the same address, so they are not limited by it, and wrong passwords stay limited per account. People signed in to the server itself (for example over Remote Desktop) each use their own Windows account and their own browser profile, so each signs in as themselves and sees only their own token and settings. So that one person cannot slow DevKit down for everyone, each may have 30 DevKit pages at once (counting pages whose connection dropped a moment ago and that DevKit keeps for 30 minutes so they can rejoin), and one person's pages hold at most 3 of the 5 TFS requests DevKit sends at a time.
- **Each person's own TFS token and settings, kept encrypted on the server.** After signing in, each person enters their PAT once in **⚙ Settings**. It and their settings are kept in one folder on the DevKit server, `%LOCALAPPDATA%\DevKit\vault` of the account DevKit runs as, beside devkit.json and outside every package. So the same settings come back in every browser and on every PC, after the browser deletes its data, and through every redeploy, package switch, restart and port change.
  - **Sealed for that person.** DevKit seals each person's settings with AES-256-GCM under a key of their own, bound to their Windows account, so settings sealed for one person never open as another's, and a changed byte is refused. Their key is locked by the Windows domain to the account DevKit runs as (DPAPI-NG), so a copied file opens nowhere else, and no key is kept anywhere in readable form. Sealing keeps settings secret but does not show who sealed them (anyone can lock a key to an account, as anyone can encrypt to a public key), so DevKit only takes sealed settings from the folder, where it alone writes.
  - **A folder nobody else can open.** At every start DevKit makes the folder open to the account it runs as alone, as owner with full control and nothing inherited: no other user of the server and no administrators group is listed on it. Before every read and save it checks that again, and each file read for its owner and permissions, and refuses the folder or file (nothing read or saved, an error in the log) if it was opened up or replaced by a link or junction, until a restart closes it again. DevKit does not start at all when it cannot close the folder.
  - **Nobody named.** Each file is named by a hash of the person's Windows account, so no name shows, and holds only sealed text.
  - **Whole saves, one at a time.** A save replaces the person's file whole, keeping the one before as a backup, one save at a time per person, also across the two DevKit processes of a package switch, even when one of them was started at boot in another Windows session; a tab's save adds only what it changed to what is kept by then, so tabs never undo each other.
  - **Administrators.** Someone who copies the files, or reads them from a backup or another disk, gets nothing readable. Other people who sign in to DevKit cannot open anyone's settings but their own. Every program that runs as the account DevKit runs as can, though, so when that is someone's own account, signed in to a desktop on the server, everything they run can; DevKit warns about this in its log at start. No program can stop an administrator of the server who takes over the account DevKit runs as itself (resets its password, or runs a program as it), since DevKit must open the settings to use them. To make that as hard as possible, run DevKit as an account of its own that nobody signs in with, ideally a group managed service account, and have IT audit the folder: `auditpol /set /subcategory:"File System" /success:enable /failure:enable` and a SACL on the folder (`icacls`, needs an administrator).
  - **Moving to another server.** Copy the `vault` folder to the same place under the account DevKit runs as there, or keep it on a share only that account can reach; any server running as that account opens it, with no keys to copy.
  - **When the domain does not answer.** DevKit asks a domain controller to open a person's key, or uses keys Windows already has from one. While it cannot, Settings says so and nothing is saved, so nobody's settings are overwritten; reloading the page once the domain answers brings them back.
  - **Moved in once.** On a person's first visit, settings an earlier DevKit kept in the older server folder (`%LOCALAPPDATA%\DevKit\users`) or in their browser's local storage move into the folder, and are then deleted there. Both were encrypted with keys only this server has. Settings the DevKit before this one kept sealed in the browser's cookies are not moved, since anyone could plant a cookie that opens as someone else's from any site of the domain; the browser is only told to drop them, and the person enters their settings once.
- **Windows sign-in to TFS** acts as the account DevKit runs as, so in shared mode only that account may use it; everyone else uses their PAT.
- **The TFS server is fixed** for everyone by `TfsUrl`, so nobody can point DevKit, and their token, at another server.
- **Audit log.** Sign-ins, sign-outs and failed attempts are recorded as `user@machine`, with the reason, in `%LOCALAPPDATA%\DevKit\audit\signin-<yyyy-MM>.log` on the server. Passwords and tokens never appear in it.
- **Guessing protection.** After 3 wrong passwords an account waits 15 minutes before it may try again, which stays under common domain lockout limits. Passwords Windows is still checking count too, so guesses sent at once cannot pass that limit together. When 30 wrong passwords reach DevKit within a minute, for any accounts, every typed password waits a minute, so one password guessed for many accounts stays slow; Windows sign-in goes on working. Each address may send 10 password sign-ins and 30 Windows sign-in requests a minute.
- **Who may sign in.** Everyone the domain knows, unless `AllowedGroups` names the Windows groups whose members may. A sign-in from anyone else is refused and audited; for a typed password the page says only that Windows did not accept it, so a guesser never learns the password was right.
- **No error details leave the server.** Unhandled errors show a plain page and go to the log, even when DevKit runs in Development.
- **Nothing from IT.** Single sign-on uses NTLM, so it works by machine name, full name or IP address without an SPN or delegation.

| `DevKit` setting in devkit.json | Default | Meaning |
|---|---|---|
| `Mode` | `Local` | `Hosted`: shared, with the sign-in page. `Local`: only for the person who started it, with the printed link. |
| `TfsUrl` | none; needed in hosted mode | The one TFS collection everyone works against. |
| `SessionDays` | `30` | How long a sign-in lasts, from 1 to 30 days. |
| `HostNames` | this machine's names and addresses | The host names browsers may use; any other name is refused. |
| `Domain` | this machine's domain | The domain of a username typed without one. |
| `AuditFolder` | `%LOCALAPPDATA%\DevKit\audit` | Where the audit log is written. The keys that encrypt sessions are in `%LOCALAPPDATA%\DevKit\keys`. |
| `AllowPlainHttp` | `false` | Whether shared DevKit may listen on plain http that other machines reach. Off, it refuses to start without https (see below). |
| `AllowedGroups` | everyone the domain knows | The Windows groups whose members may sign in, by name (`["MT\\DevKit Users"]`) or SID. The account DevKit runs as always may. A group Windows does not know stops DevKit from starting. Someone taken out of a group can no longer sign in, and their session ends with `SessionDays`, so set that lower if it matters. |

> Over plain `http`, the session cookie, the PAT someone types in Settings and every page cross the network unencrypted, so **shared DevKit refuses to start on plain http that other machines reach**, and says why. Give devkit.json a Kestrel endpoint with a certificate (see below): it replaces the script's http address, the session cookie becomes `Secure`, and *Sign in as someone else* is offered. Plain http on the server's own address (`localhost`, `127.0.0.1`) is allowed. To run shared on plain http all the same, set `"AllowPlainHttp": true` in the `DevKit` section; *Sign in with your Windows account* then sends no password, and the typed-password form is turned off.
>
> Over https every answer tells the browser to use only https for DevKit's address for a year (`Strict-Transport-Security`, for that name only, not the whole domain). No answer names the web server DevKit runs on.
>
> At start, DevKit opens one test connection to its https endpoint. It prints the link to share with the short machine name when the certificate covers it, since browsers sign in with Windows automatically only for names without dots. When the test fails, which a certificate whose private key Windows cannot use does, it logs an Error that says so.

### Run it on another machine

To keep DevKit running on a machine that stays on, make a package on your own machine and copy it there. That machine needs no .NET install and no copy of this repository. Each package has a folder of its own, so a new version is built and copied while the old one keeps running, and the switch to it takes seconds, with a maintenance page meanwhile.

1. **Make a package** with `scripts\devkit-deploy.bat` (or `devkit publish`). It restores, cleans and publishes DevKit self-contained for 64-bit Windows into a new folder in `Release\` at the root of this repository, named after the time it was made, for example `Release\DevKit_29_10_2026_03_29_PM`. It never touches a package that runs, so DevKit stays up while it builds.

   | In `Release\` | What it is |
   |---|---|
   | `Run Latest DevKit.bat` | Makes the newest package the DevKit that runs, with a maintenance page meanwhile (see below) |
   | `DevKit_<day>_<month>_<year>_<hour>_<minute>_<AM or PM>\` | One package: `app\` (DevKit, with the .NET runtime it needs), `devkit-run.bat` (keeps that DevKit running), `tools\` (the checks it shares with `devkit-host.bat`, the script that writes its Startup file, `devkit-boot-task.bat`, which has it start with the machine, and `devkit-lock.bat`, which locks a folder's permissions), `README.md` (this file), `build.txt` (when, by whom and from which commit it was made, and how many files were not committed then) and `package.txt` (how many files the package has, their size and the SHA-256 of each) |
   | `live.txt` | The package that runs and the id of the switch that started it, for the scripts of packages made before the live state below |

   **The package carries a patched .NET.** Windows Update never patches a runtime inside a package, so a package takes the runtime that `DevKitPatchedRuntime` in [`DevKit.Web/DevKit.Web.csproj`](DevKit.Web/DevKit.Web.csproj) names, downloaded from nuget.org, whatever SDK builds it: 10.0.14, Microsoft's security release of 25 October 2026. When Microsoft ships a .NET 10 security release, raise that number, make a new package and switch to it. The package's own program refuses to write its `package.txt` for a runtime older than that number, so an unpatched package is never made.

   **The tests run first**, and no package is made when one fails. No settings, tokens or keys ever go into a package: DevKit keeps those per account on the machine it runs on. Nor does the `.pdb`, which would name this machine's source files. `package.txt` is written last, by the new package's own `DevKit.Web.exe --write-manifest`, and the switch script is copied only after it, so a package the script could not finish never looks whole, and is removed. Packages are named to the minute, so a second package in the same minute has to wait for the next one; the folder is made in one step, so two runs at once never share it. A clock that reads earlier than the newest package's name is refused, since the switch would not take the new package for the newest. A package made from files not yet committed says so, and so does its version (`<commit>.uncommitted`). An earlier version's `devkit-run-latest.bat` in `Release\` is removed, so only `Run Latest DevKit.bat` is there to run.

   **`Release\` is left to the account that makes the packages, SYSTEM and administrators only** (`tools\devkit-lock.bat`). DevKit runs its packages as the account that runs the switch, so anyone else who could change a package, or `Run Latest DevKit.bat`, could run their own code as that account. On a new drive, Windows lets every signed-in user change what is made there, so without this anyone who can sign in to the server could. Lock the repository folder too, once: `tools\devkit-lock.bat "D:\Projects\katard\Tools\DevKit"`.
2. **Copy the new package folder and `Run Latest DevKit.bat`** to the other machine, into the same folder each time, for example `D:\DevKit`. The first time, lock that folder there, signed in as the account DevKit will run as: `D:\DevKit\DevKit_...\tools\devkit-lock.bat "D:\DevKit"`; the switch refuses a folder other accounts may change, and says so. A package copied only in part does not match its `package.txt`, and the switch leaves it alone until the copy is whole.
3. **Create devkit.json there** for the account DevKit will run as, as in step 1 above, with a certificate if you want https. **When DevKit ran on another machine before**, run it here as the same Windows account: it then opens everyone's settings, which stay in their browsers, with nothing copied from the old machine, so nobody enters anything again. Under another account, people's settings stay unread until DevKit runs as the account that saved them, and anyone who saves in between replaces theirs.
4. **Run `Run Latest DevKit.bat`** there, signed in as that account. Like `devkit-host.bat`, it stops before starting anything when devkit.json is missing; it then starts DevKit shared, in Production, on port 5850.

**Updating it.** Make a new package, copy it there as in step 2, and run `Run Latest DevKit.bat` again. It makes the newest package the one that runs, and whoever opens DevKit meanwhile finds a maintenance page rather than nothing. The switch itself is the new package's `DevKit.Web.exe --maintenance`, and it says each step in the script's window:

1. It takes the port for this switch, so two switches never run at once, even from two remote desktop sessions or two accounts: a second one says another switch is running and changes nothing. It checks every file of the package against the SHA-256 in its `package.txt`, and that no account but this one, SYSTEM and administrators may change the package or `Release\`; a package that fails either check is refused, with the reason, and nothing changes. Its maintenance page gets ready, on DevKit's own address and with its certificate loaded; when devkit.json's address is on another port than the one switched, or is plain http that shared DevKit would refuse, or DevKit's folder is one shared DevKit would not start in, nothing changes either. When the package is damaged, its program does not even start, so nothing is stopped: DevKit keeps running, and the package is marked as failed.
2. It asks the DevKit that runs to make way. That DevKit stops listening at once and finishes the requests it is serving, such as a branch or pull request someone is creating; one from a package made before it could be asked is stopped. The maintenance page takes the port at once. Everyone who opens DevKit now reads "DevKit is being updated" (a 503 with `Retry-After`), and the page loads again every 10 seconds. It also says which version is starting, by when its package was made, how long the update has run and how many seconds are left until it checks again, with a band along the bottom that fills up until then, and has a "Check now" button; it runs no script. When a person opens the page or presses Check now, its text comes in with a short motion; the browser's own checks every 10 seconds show it still, and with reduced motion turned on the text only fades in and the band is left out. A "Stop checking" link turns the browser's own checks off (a screen reader starts again from the top at each one) by adding `checks=off` to the address, and "Start checking" turns them on again; the rest of the address stays as it was. When a new version does not start and the switch goes back, the page says so. A tab that was already open shows the same message over the page instead of "Reconnecting".
3. It starts the new package in a minimized "DevKit" window. When that DevKit is about to listen, it asks the maintenance page to make way and takes the port over, and every waiting tab opens it by itself. The switch counts it as done only once something answers on the switched port.
4. When that DevKit does not take the port over (its script ends, or 4 minutes pass), the switch stops it, marks the package as failed (`switch-failed.txt` in it, with the reason) and starts the package that ran before again the same way, so DevKit comes back by itself. Only when no earlier package can run does DevKit stay down, and the script says so.

A switch takes about 5 seconds. In testing, the port was closed twice for a fraction of a second, about 0.15 to 0.2 seconds each with http or https: once while the old DevKit made way, once while the new one took over; the rest of the time it answered with DevKit or the maintenance page. A package made before switches existed gets the port free instead, which keeps it closed for a second or so. The outcome is written to `devkit-run.log`. Closing the script's window while the new DevKit starts does not stop that DevKit: it takes the port by itself.

**Which package runs** is kept per Windows account and port beside devkit.json (`%LOCALAPPDATA%\DevKit\live-5850.txt`: the package's folder and the switch's id), so every Release folder on the machine follows it. The `devkit-run.bat` of any other package, or of an earlier run of the same package, closes instead of starting its DevKit again, wherever its folder is; and the Startup file starts that package at sign-in.

**Everyone's settings stay through every switch.** They live in the server's settings folder beside devkit.json (see above), so a new package, a new folder, a restart or going back to an earlier package reads them as they were; that includes the Dashboard's grid or list view. A save carries only what it changed, so a tab opened earlier never undoes what another tab saved, and a value a later DevKit saved, such as a new view, is kept. Packages made from this version on also write back the fields they do not know. Packages made before it drop them when someone saves, so the script warns and asks before going back to one (`--yes` answers for it).

- **Running it again** with no name restarts the newest package that has not failed and whose `package.txt` matches its files, as often as you like. A package without `package.txt`, made before DevKit checked its packages, or one still being copied or made, is never picked by itself; name it to run it. Old packages beyond the 10 newest are removed after each switch that worked, except one still in use (a DevKit runs from it, or its keep-alive script waits in it to start it again, as for another account) and `DevKit_Previous`.
- **Going back.** Pass an earlier package's folder name: `"Run Latest DevKit.bat" DevKit_28_10_2026_11_02_AM`. Naming a package that failed tries it again; once it works it is no longer marked.
- **The newest package** is the one whose name says the latest time. Folder dates are not used, since copying changes them, and only whole packages count.
- **The single package of earlier versions.** Before packages had folders of their own, `Release\` held one package directly (`app\`, `devkit-run.bat`, `tools\`, `README.md`). The first switch moves it into `DevKit_Previous\`, and the `devkit-run.bat` that ran it ends instead of starting it again. To go back to it, name it: `"Run Latest DevKit.bat" DevKit_Previous`; it gets the switch's own scripts first.

How it stays up:

- **When DevKit stops with an error**, `devkit-run.bat` starts it again after 30 seconds. When it stops with an error within 5 minutes of starting 5 times in a row, something needs fixing first, such as devkit.json or the certificate, so the script stops too and leaves the messages on screen.
- **Each start and stop** is written, with the time and the exit code, to `devkit-run.log` beside devkit.json (`%LOCALAPPDATA%\DevKit\devkit-run.log`). Past 5 MB it becomes `devkit-run.1.log`, replacing the one before, so the log never fills the disk. DevKit's own messages stay in the window.
- **To stop it**, press Ctrl+C in its window and answer Y, or close the window.
- **It runs while that account is signed in**, unless it starts with the machine (below). A disconnected Remote Desktop session keeps it running; signing out stops it.
- **It starts with the machine** once you run `tools\devkit-boot-task.bat 5850` from the package (or `devkit boot`) in a command prompt opened with "Run as administrator", signed in as the account DevKit runs as. It adds the scheduled task "DevKit 5850", which runs a minute after each start of the machine, whether that account is signed in or not, and starts the package the live state names, as the Startup file does. Windows asks for the account's password once and keeps it for the task; DevKit never sees it. After that DevKit is back after every restart, such as for Windows updates, without anyone signing in, and keeps running when the account signs out. When the account's password changes, run it again. A switch run later over remote desktop still reaches that DevKit, as the switch and DevKit talk over named pipes every session sees. To stop it: `schtasks /delete /tn "DevKit 5850" /f`, or `devkit remove`.
- **It starts again by itself at sign-in.** Each start puts `DevKit-<port>.cmd`, for example `DevKit-5850.cmd`, in that account's own Startup folder (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`, also `shell:startup`): a short file that starts the `devkit-run.bat` of the package the live state names, so after a switch that failed and went back it starts the package that runs, never the failed one. Each port has its own, so DevKits on two ports both start; the `DevKit.cmd` of earlier versions is removed when it was for the same port. It is rewritten only when it would start DevKit differently. So after a restart DevKit starts, in a minimized window, as soon as that account signs in; when it already runs, as after a start with the machine, the window closes by itself. No administrator rights or stored password are needed. To stop it starting by itself, delete `DevKit-5850.cmd` from that folder, or run `devkit remove`. A run with its own `DEVKIT_SETTINGS_PATH`, such as a test copy, adds nothing there.
- **No PowerShell.** `Run Latest DevKit.bat`, `devkit-run.bat` and the scripts that write `DevKit-<port>.cmd`, add the task and lock folders are plain batch files, since endpoint protection such as Cortex XDR blocks a PowerShell script that writes to the Startup folder ("Suspicious script detected"). A `DevKit.lnk` shortcut an earlier DevKit put there is removed, so DevKit starts once.
- **An update keeps everyone's settings and sign-ins.** Settings stay in the `vault` folder, and the session keys in `%LOCALAPPDATA%\DevKit` of that account, not in the package.
- **Another port.** `set DEVKIT_PORT=5860` before running `Run Latest DevKit.bat` uses that port instead of 5850. With https, the port is the one in devkit.json's Kestrel `Url`, so keep the two the same: the switch refuses, and changes nothing, when they differ.

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
| **Sign in with** | **Windows account**: DevKit signs in to TFS as the Windows account it runs as, and no token is stored. It needs an `https` server address, or one on this machine: over plain http the sign-in could be passed on to another server. **Personal Access Token**: for a server that does not accept your Windows account. |
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
├─ nuget.config                     # Every package from nuget.org alone
├─ .github/workflows/ci.yml         # Windows CI: secrets and vulnerable packages refused, build, tests, coverage, Sonar report
├─ .github/dependabot.yml           # Raises the pinned actions and the packages weekly
├─ devkit.bat                       # CLI entry point: host / run / local / publish / autostart / boot / status
├─ scripts/
│  ├─ devkit-host.bat               # Clean, restore, build, then start DevKit shared on this machine
│  ├─ devkit-deploy.bat             # Make a package in a folder of its own in Release\
│  ├─ devkit-run-latest.bat         # In Release\ as "Run Latest DevKit.bat": picks a package, runs the switch
│  └─ devkit-run.bat                # In each package: start DevKit shared and keep it running
├─ tools/
│  ├─ devkit-boot-task.bat          # In the package: has DevKit start with the machine (scheduled task)
│  ├─ devkit-lock.bat               # In the package: leaves a folder to this account, SYSTEM and administrators
│  ├─ devkit-startup.bat            # In the package: writes DevKit-<port>.cmd in the account's Startup folder
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
   │  ├─ LocalAccessGate.cs         # Sign-in key and cookie; refuses other machines, hosts, accounts and origins
   │  ├─ ConnectionAccounts.cs      # Which Windows account opened a connection from this machine
   │  ├─ PersonCircuits.cs          # Shared mode: how many pages each person may have
   │  ├─ MaintenanceMode.cs         # The page shown while DevKit is updated
   │  ├─ DevKitSwitch.cs            # The switch to another package: hand-over, going back on failure
   │  ├─ PortHandover.cs            # Holder and newcomer of the port make way for each other
   │  ├─ LivePackage.cs             # Which package runs on a port, for every Release folder
   │  ├─ SettingsChanges.cs         # A save's changes, applied to what another tab saved
   │  ├─ PageStateService.cs        # Cached TFS data, per-tool state
   │  └─ ThemeService.cs            # Theme handling
   ├─ Models/                       # WorkItem, Tfs*, TfsGitModels, *Settings
   └─ wwwroot/                      # app|components|tools|merging|code-compare|signin|maintenance.css,
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
- **Only the person who started DevKit can use it.** Everyone signed in to the same machine can reach a localhost port, and so can any web page open in your browser; without a lock they would all act with your PAT. Each start creates a random 256-bit key, prints the sign-in link and opens it; the link swaps the key for an `HttpOnly`, `SameSite=Strict` cookie and every other request gets a locked page. Requests from another machine, addressed to another host name (DNS rebinding), or sent from another origin (including the Blazor WebSocket) are refused. So are requests from a program another Windows account on the same machine runs, cookie or not: a browser sends a localhost cookie to every port of localhost, so another account's program on a shared server could be handed a copy, and DevKit asks Windows which account opened each connection. A restart creates a new key, so open the new link it prints; tabs from before it are signed out. Set `DEVKIT_NO_BROWSER=1` to only print the link.
- In local mode the app answers on **`localhost`** only; shared mode answers on this machine's own names (see above). Both send hardening response headers (including a CSP).
- **Shared mode** replaces the link with the sign-in page; see [Shared on a server](#shared-on-a-server). Requests for another host name, or sent from another site (forms, the WebSocket), are still refused; every sign-in form carries an anti-forgery token; and the session cookie is `HttpOnly`. The keys that encrypt sessions stay on the server, encrypted for the account DevKit runs as; losing them only signs everyone out. Everyone's settings are sealed, each under a key of their own that the domain locks to the account DevKit runs as, and kept in a folder on the server only that account can open (see [Shared on a server](#shared-on-a-server)). The settings folder an earlier DevKit kept on the server, or a file in it, replaced by a link is refused.
- The app ships with **zero NuGet packages**: PAT encryption uses the OS DPAPI directly via P/Invoke, and shared mode's Windows sign-in uses .NET's own NTLM support, with no third‑party dependency. Every package the build needs (the test framework, SonarAnalyzer, a package's runtime) comes from nuget.org alone ([`nuget.config`](nuget.config)), so a package of the same name on another feed this machine knows is never picked. CI refuses a package with a known vulnerability and a committed key, certificate or token, and its actions are pinned to release commits, which Dependabot raises.
- **Pages run no script from outside DevKit.** The Content Security Policy allows DevKit's own scripts only, connections only to DevKit itself, and images from DevKit and from https sites (for the pictures a Markdown Viewer document links to). The Markdown Viewer turns only `http`, `https` and `mailto` addresses into links, read the way the browser reads them, so an address dressed up with tabs or control characters is not a link.

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

# Self-contained package in a new folder in Release\; "Run Latest DevKit.bat" there switches to it (see "Run it on another machine")
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
| Shared mode: signed in again every time the browser was closed | The browser deletes every site's cookies when it closes. That is usually a company policy: `DefaultCookiesSetting` = 4 under `HKLM\SOFTWARE\Policies\Google\Chrome` or `...\Microsoft\Edge`. The sign-in is a cookie, so it goes with it; settings are kept on the server and stay. DevKit signs in again by itself where the browser signs in with Windows. To keep the sign-in too, ask IT to add DevKit's address, for example `https://devbox.example.com:5850`, to *Allow cookies on specific sites* (`CookiesAllowedForUrls`) in both browsers' policy. In Chrome, a person can instead add that address once under *Settings > Privacy and security > Site settings > Additional content settings > On-device site data > Allowed to save data on your device*. |
| Shared mode: signed out before the 30 days were up | The PC's address changed (another network, Wi-Fi, VPN), and a session works only from the address it signed in from. A browser that signs in with Windows by itself does so again; any other shows the sign-in page. |
| `devkit-host.bat` or `devkit-run.bat`: "Port 5850 is already in use" | A DevKit is already running. Close its window, then run the script again. To replace a running DevKit with a package, use `Run Latest DevKit.bat`, which stops it for you. |
| `devkit-deploy.bat`: "already exists: a package was made this minute" | Packages are named to the minute. Wait a minute, then run it again. |
| `Run Latest DevKit.bat`: "is in use by ..., which is not DevKit" | Another program listens on DevKit's port, and the script stops only DevKit. Stop that program, or set `DEVKIT_PORT` to another port. |
| `Run Latest DevKit.bat`: "did not start, so the package that ran before runs again" | The new package's DevKit did not take the port over, so the switch went back, and the package is marked as failed. The lines above it, and `devkit-run.log`, say why. Fix that and make a new package, or name the failed one to try it again. |
| `Run Latest DevKit.bat`: "DevKit.Web.exe ... did not start, so nothing was changed" | The package is damaged or incomplete, so DevKit kept running. Make the package again and copy the whole folder. |
| `Run Latest DevKit.bat`: "another switch on port ... is still running" | Someone ran the script twice, or in another window. Wait for that switch to finish. |
| `Run Latest DevKit.bat`: "has no package.txt, so its files cannot be checked" | The package named was made before DevKit checked its packages, and going back to it drops settings later packages added when someone saves; or it was copied only in part. Copy it again when in doubt. Answer Y to run it all the same, or pass `--yes` when nobody is there to answer. |
| `Run Latest DevKit.bat`: "... may change ..., so it could run its own code as this account" | Another account may change the package or `Release\`, as Windows allows on a new drive. Run `tools\devkit-lock.bat "<Release folder>"` as the account DevKit runs as, then run the switch again. |
| `Run Latest DevKit.bat`: "is not the file the package was made with", "is missing" or "which package.txt does not list" | The package changed after it was made, or was copied in part. Copy the whole package folder again, or make a new one. |
| `Run Latest DevKit.bat`: "would listen on ..., but not on port ..." | devkit.json's Kestrel `Url` is on another port than `DEVKIT_PORT` (5850 unless set). Make them the same; nothing was changed. |
| DevKit or the switch: "plain http that other machines reach" | Shared DevKit refuses plain http other machines reach. Give devkit.json an https endpoint, or set `"AllowPlainHttp": true` in its `DevKit` section. |
| `devkit-deploy.bat`: "The clock reads ..., but ... was made then or later" | The machine's clock is behind the newest package's name. Wait until it is past that time, or fix the clock. |
| `devkit-boot-task.bat`: "Run this from a command prompt opened with Run as administrator" | Only an administrator may add a task that runs at startup. Open the command prompt with "Run as administrator", signed in as the account DevKit runs as. |
| `Run Latest DevKit.bat`: "is not a whole DevKit package" | The name passed is not a `DevKit_...` folder beside the script, or that folder is missing files: `app\DevKit.Web.exe`, `devkit-run.bat`, or files `package.txt` counts. Copy the whole package folder. |
| Shared DevKit stops at start: "DevKit's folder cannot be trusted" | Another account may change DevKit's folder (devkit.json, keys, sign-outs) or something in it, or the folder is a link. Run `tools\devkit-lock.bat "<folder>"` as the account DevKit runs as, make that account the owner of anything another account owns there, then start DevKit again. A member of this machine's Administrators group is never the account named. The switch checks this too before it stops the DevKit that runs, and then changes nothing. Local mode only warns about it in its window. |
| `devkit-deploy.bat`: "package.txt was not written ... older than ..., the .NET release with Microsoft's latest security fixes" | The package would carry an unpatched .NET. Check `DevKitPatchedRuntime` in `DevKit.Web.csproj`, make the package again, and copy the new one. |
| Settings: "Windows sign-in needs an https server address" | Over plain http your Windows sign-in could be passed on to another server. Enter the server's https address, or sign in with a Personal Access Token. |
| Shared mode: a new page does not respond, and DevKit logged "Refused a new page for an account that has 30 DevKit pages already" | That account has 30 DevKit pages open, or dropped a moment ago. Close some tabs, then reload the page. |
| Local mode: another Windows account on the same machine gets 403 | DevKit opens only for the Windows account that started it, whoever holds its link or cookie. That account opens it; anyone else starts their own DevKit. |
| `devkit-run.bat`: "so it is not started again" | DevKit stopped with an error soon after starting, 5 times in a row. The messages above it in the window say why, often a missing `TfsUrl` or a certificate Windows cannot use. Fix that, then run `Run Latest DevKit.bat` again. |
| Shared mode: Settings says your settings were saved by DevKit running under another Windows account | DevKit now runs as another account than the one that saved them, for example after a move to another machine. Stop DevKit, start it again as the account it ran as before, and reload the page; nothing is lost as long as nobody saved in between. Saving here instead replaces them with settings sealed for this account. |
| Shared mode: Settings says DevKit could not open your settings just now | The domain did not open the person's key, usually because no domain controller answered. Nothing is saved meanwhile, so nothing is overwritten. Reload the page in a minute. If it keeps happening for everyone, DevKit's window says why at start: shared DevKit runs as a domain account, on a machine in the domain. |
| Browser doesn't open, or the page says DevKit is locked | Open the `DevKit is ready: …` link from the DevKit window. A bookmark to http://localhost:5850 works once the browser has signed in during this run. |

---

## License

DevKit is released under the [MIT License](LICENSE).
