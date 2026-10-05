# Privacy and local data

**English** · [简体中文](PRIVACY.zh-CN.md)

QuotaWidget has no project-operated server, telemetry, or log-upload endpoint. Official CLIs communicate with their respective services to query quota.

| Source | Purpose | Stored locally |
| --- | --- | --- |
| Official Claude CLI | Quota queries and official sign-in | Quota readings and a separate CLI configuration; the official CLI manages credentials |
| Official Codex app-server | Read-only quota queries for the current account | Quota readings and hashed account/plan identifiers, not the raw account ID returned by the service |
| Local Claude/Codex logs | Tokens, work start/end times, chat metadata, and cache reminders | Usage counters, models, timestamps, chat IDs/titles, project names, and parent relationships |
| Local app/session metadata | Running state and display names | Necessary session state; no browser cookies or password-store access |
| Installed Windows app information | Local provider icons and official CLI discovery | Compatibility cache; the installed-app list is not uploaded |

The widget reads local log files to extract metadata. Those source files may contain conversation text. The widget does not copy full chat bodies into its index or upload them. Its local index still contains sensitive titles, project names, paths, and usage records; treat it as private data.

The default directory is `%USERPROFILE%\.quotawidget\data`. Its `claude-auth` directory holds the official CLI's separate sign-in configuration. Other directories store history, events, and token indexes. **Do not commit, share, or package the entire data directory.** `.gitignore` is a guard against accidental commits, not a substitute for reviewing files.

Settings can disable provider monitoring, token tracking, and chat reminders separately. Disabling tracking does not delete saved data. Exit the widget and back up anything you want to keep before deleting history.

Quitting does not sign out of Claude by default. If sign out on exit is enabled, the official CLI performs the sign-out. This project does not claim that removing local credentials also revokes the token on the server.

`--demo` uses synthetic data only. Documentation screenshots are generated from demo mode. The repository does not include the developer's account configuration, real history, session databases, or sign-in state.

First-use setup must be completed before quota collection or chat-log reading begins. Disconnecting a provider stops widget monitoring and retains sign-in and history; Claude sign-out is a separate action. Provider marks in the tray also come from locally installed apps, without downloading or redistributing those assets.
