# QuotaWidget

**English** · [简体中文](README.zh-CN.md)

A small Windows desktop widget for Claude and Codex quota, usage trends, and local chat activity. See remaining quota and reset countdowns without reopening usage pages.

**[Download for Windows](https://github.com/HailiangLoo/QuotaWidget/releases/latest)** · [Privacy](docs/PRIVACY.md) · [Development & testing](CONTRIBUTING.md)

## Screenshots

All screenshots use synthetic demo data. Supports light and dark themes, full and compact modes, and Claude, Codex, or both.

### Full mode

<img src="docs/images/both-expanded-en.png" width="324" alt="Full mode with Claude and Codex" />
<img src="docs/images/codex-expanded-en.png" width="324" alt="Full mode with Codex only" />

### Cumulative view

<img src="docs/images/both-cumulative-en.png" width="324" alt="Cumulative Claude, Fable and Codex usage" />

Each chart chooses Rate or Total independently. The numbers in its header remain recorded totals.

### Compact mode

<img src="docs/images/both-compact-en.png" width="264" alt="Compact mode with Claude and Codex" />
<img src="docs/images/codex-compact-en.png" width="264" alt="Compact mode with Codex only" />

## Features

- Claude: five-hour, total weekly, and Fable weekly quota.
- Codex: weekly quota, plus a five-hour gauge when the account's interface provides it.
- Independent rate or cumulative views for each provider, with 1h, 5h, 12h, 24h, 3d, and all-history ranges.
- Local chat activity, cache reminders, token summaries, and confirmed subagent grouping.
- Always on top, system tray, compact mode, and layouts that adapt to narrow windows.
- English and Simplified Chinese, with an instant language switch in Settings.

## Getting started

1. Download `QuotaWidget-v0.13.0-win-x64.zip` from [Releases](https://github.com/HailiangLoo/QuotaWidget/releases/latest) and extract it into a folder of your choice.
2. Open `QuotaWidget.exe` or `Start Widget.cmd`. The download includes the .NET runtime; no separate runtime installation is needed.
3. On first launch, choose the providers to monitor and complete official sign-in, then select **Start monitoring**. No quota collection or chat-log reading begins before this step. The gear remains available in compact mode.
4. Settings shows each provider’s connection status, sign-in/install instructions, a connection check, and a disconnect button. Disconnect stops widget monitoring; it does not sign out of the official app or delete history.
5. Choose a language in Settings. Language defaults to the Windows display language: Chinese on Chinese systems, English otherwise. You can choose **English** or **简体中文** explicitly.

**Codex:** install and sign in to the official Codex Windows app first. The widget checks the signature of the bundled CLI, queries quota through it, and reuses the existing sign-in.

Official installation and sign-in guides: [Codex / ChatGPT](https://learn.chatgpt.com/docs/app), [ChatGPT authentication](https://learn.chatgpt.com/docs/auth), and [Claude Code](https://code.claude.com/docs/en/setup).

**Claude:** requires a supported official Claude Code CLI, or the CLI bundled with the Claude Windows app. Open `Sign in to Claude.cmd` to complete the widget's separate sign-in. If no compatible CLI is found, update the official app or set `claudeExePath` in `settings.json`.

To preview the widget without an account, open `Demo.cmd`. Demo data is stored separately from real data.

Requires Windows 10/11, x64. Release binaries are currently unsigned.

## Data and accuracy

Data stays under `%USERPROFILE%\.quotawidget\data` for the current Windows user. It is not uploaded to GitHub with the application. Use `--data-dir "your-directory"` to choose a different location. The app does not add itself to Windows startup automatically.

Official quota readings are the source of truth. **Rates estimate a trend from discrete quota readings and local activity.** Totals use valid raw readings; gaps are not invented or filled with zero. Activity on other devices or the web may be absent from local logs. Token totals are not an account bill and are not converted into precise per-chat weekly quota costs.

See [How smoothing works and its accuracy limits](docs/ALGORITHM.md). Smoothing estimates timing; it cannot reveal an exact instantaneous consumption rate. Fable-only fragments can share one display stroke in both chart modes while header totals remain independent.

The two providers use different quota units and cannot be added together. Fable conversion is enabled only for recognized supported plans. Provider interfaces, plans, and quota windows can change; a missing window is never presented as zero usage.

See [Privacy](docs/PRIVACY.md) for the data access details. Use demo screenshots or redacted information in bug reports; do not upload your entire data directory.

## Build from source

Install the .NET 10 SDK on Windows, then run:

```powershell
dotnet run --project tests/QuotaWidget.Tests -c Release
dotnet run --project tests/QuotaWidget.UiTests -c Release
powershell -File scripts/Build-Release.ps1
```

Release files are written to `artifacts/`. Tests do not require a signed-in account by default. Checks that use installed official CLIs are opt-in; see [CONTRIBUTING.md](CONTRIBUTING.md).

For an English demo, run `QuotaWidget.exe --demo --language en`. Use `--language zh-CN` for Chinese or `--language auto` to follow Windows.

## License

Source code and original artwork are licensed under [MIT](LICENSE). This is an independent project, not an official Anthropic or OpenAI product, and is not endorsed by either company. Their names and trademarks belong to their respective owners. Provider icons are read from the user's local app installation and are not redistributed in source or release packages. See [NOTICE](NOTICE.md) for runtime and trademark details.
