# Code Signing Policy

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

---

## 1. Publisher & Build Provenance

- **Official Repository**: `https://github.com/nrjycyd/sitelift`
- **License**: [MIT License](LICENSE)
- **Build & Signing Pipeline**: All official Windows release packages (`SiteLift-vX.Y.Z-win-x64.zip`) are built from publicly auditable source code in this repository via GitHub Actions ([`.github/workflows/build-release.yml`](.github/workflows/build-release.yml)) running on GitHub-hosted `windows-latest` runners, tested automatically by `tests/SiteLift.Tests`, and submitted directly from GitHub Actions to SignPath.io for signing.
- **Certificate Holder**: Signed release binaries carry a certificate issued to **SignPath Foundation**.

## 2. Team Roles

| Role | GitHub Account | Responsibilities |
| :--- | :--- | :--- |
| **Committer / Maintainer** | `@nrjycyd` | Authoring code changes, reviewing pull requests, and preparing releases |
| **Reviewer** | `@nrjycyd` | Verifying CI test results and release contents before tagging |
| **Approver** | `@nrjycyd` | Approving SignPath signing requests for tagged releases (`v*`) |

## 3. Scope of Signed Binaries

Official Windows portable packages (`SiteLift-vX.Y.Z-win-x64.zip`) contain the following binaries built from this repository and its open-source NuGet dependency:

- **First-party binaries (built from `src/`)**:
  - `SiteLift.App.exe`
  - `SiteLift.App.dll`
  - `SiteLift.Core.dll`
- **Bundled open-source dependency (MIT License, restored from NuGet `WPF-UI`)**:
  - `Wpf.Ui.dll`
  - `Wpf.Ui.Abstractions.dll`
  - Upstream packages are currently published unsigned on NuGet; signing them alongside first-party binaries ensures compatibility with Windows 11 Smart App Control.

No proprietary components, adware, installers with bundled offers, or closed-source binaries are included.

## 4. Privacy Policy & Network Activity Disclosure

SiteLift is a local-first Windows + WSL static site publishing workbench. **SiteLift collects zero telemetry, analytics, crash dumps, or personal usage data.**

- **Local Storage Only**: Project configurations, application preferences, and diagnostic logs are stored locally under `%LOCALAPPDATA%\SiteLift`. Site profiles (`.sitelift/profile.json`) are stored locally in the user's site repository and excluded via `.git/info/exclude`.
- **Network Connections (Strictly User-Controlled or Read-Only)**:
  1. **Git Operations**: Invoking local `git.exe` / `wsl.exe` to fetch, pull, or push against user-configured Git remotes (using the user's own Git Credential Manager or SSH agent).
  2. **Update Check**: On startup and when manually clicking "Check for Updates", SiteLift sends a read-only HTTPS GET request to the public GitHub Releases API (`https://api.github.com/repos/nrjycyd/sitelift/releases/latest`) solely to compare version numbers. No personal or device identifiers are sent, and updates are **never downloaded or installed without explicit user confirmation**.
  3. **Optional AI Helpers (Disabled by Default)**: Only when explicitly enabled and configured by the user, SiteLift sends compact site-profile summaries, document excerpts, commit change lists, or redacted error logs to the user's chosen OpenAI-compatible API endpoint (or local Ollama). API keys are encrypted locally using Windows DPAPI (`CryptProtectData`) and are never included in project exports or diagnostic bundles.
- **No Unsolicited Software**: SiteLift does not install background services, scheduled tasks, or third-party offers.
