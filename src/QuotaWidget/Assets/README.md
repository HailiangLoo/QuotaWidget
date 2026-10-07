# Assets

`quota-companion.png` (window) and `quota-companion.ico` (EXE) share the tray's
`QuotaIcon.cs` renderer and bundled `claude-badge.png` / `codex-badge.png` eyes.
Regenerate them with `pwsh -File scripts/Update-Icons.ps1`; add `-Check` to verify.
The ICO includes 16–256 px frames for Windows Explorer and different display scales.

The badge images come from the official Claude and Codex Windows application icons.
They are included to keep the application icon consistent on machines with or without
those apps installed. In-widget provider labels still use local installed-package
icons when available, with original letter/terminal badges as a fallback.
No icon is downloaded at runtime.

The face drawing code is MIT-licensed; provider names and logos belong to their
respective owners and are not relicensed under MIT. They identify the services
being monitored and do not imply sponsorship or endorsement. See `NOTICE.md`.
