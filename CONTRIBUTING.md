# Development

**English** · [简体中文](CONTRIBUTING.zh-CN.md)

Use Windows and the .NET 10 SDK. The interface uses WPF; storage uses Windows' built-in SQLite. The core project has no third-party NuGet dependencies.

```powershell
dotnet run --project tests/QuotaWidget.Tests -c Release
dotnet run --project tests/QuotaWidget.UiTests -c Release
dotnet run --project src/QuotaWidget -- --demo --scenario codex-plus --language en
```

Core tests use temporary directories and synthetic records. WPF tests exercise real layouts and handlers without starting background collectors.

To refresh the README gallery, run `pwsh -File scripts/Update-Screenshots.ps1` with PowerShell 7. This uses the `showcase` demo scenario: continuous synthetic collection with both Codex quota windows. Other demo scenarios retain simulated sleep and connection gaps for diagnostics. Real sleep labels require recorded Windows power events; an idle chat does not imply sleep.

Optional official CLI integration checks are skipped by default. To run them explicitly on your own computer:

```powershell
$env:QUOTAWIDGET_LIVE_TESTS = '1'
dotnet run --project tests/QuotaWidget.Tests -c Release
Remove-Item Env:QUOTAWIDGET_LIVE_TESTS
```

These checks locate and verify installed official executables. Some invoke the CLI with an empty temporary configuration. Do not provide personal credentials to CI. The `--cache-probe`, `--lifecycle-probe`, and `--codex-probe` flags are explicit local diagnostics; their output should not be attached to public issues without review and redaction.

## Languages

`src/QuotaWidget.Core/Strings.en.json` maps Chinese source text to English. `Loc.T` handles plain text; `Loc.F` handles composite formats. Keep all format arguments in translations. Static WPF labels use the `Translate` attached properties so open controls update without a restart. Keep machine identifiers, stored metadata, provider responses, and user chat/project names out of translation.

Check both `--language en` and `--language zh-CN`, all monitoring modes, and compact/full layouts. Keep chart labels short and put longer explanations in tooltips. The test suite checks resource formats, language persistence, live switching, narrow layouts, and language-independent quota calculations.

## Releases

```powershell
pwsh -File scripts/Build-Release.ps1
powershell -File scripts/Test-PublicTree.ps1
```

Use PowerShell 7. For a local prerelease version, pass `-AllowPrerelease`; normal release builds reject prerelease versions. The script resolves the exact .NET and Windows Desktop runtime packs, embeds their licenses and third-party notices, then uses the `Portable` publish profile to produce a compressed, self-contained single EXE. Native libraries extract automatically into the .NET temporary cache at startup; the user does not need to manage a runtime folder. WPF is not trimmed. See Microsoft's [single-file deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).

The script checks that the publish folder contains only the EXE, copies it to an empty Unicode/space path under a different filename, and tests embedded-license export and full, compact, setup and usage-detail snapshots with a fresh extraction cache. Snapshots use synthetic data and do not collect real quota or read private logs.

Build fresh binaries from source with debug symbols removed and compiler paths mapped. Do not copy a private installation directory. Publish only the newly generated EXE and checksum from `artifacts/`, not the intermediate build directories. Review `git diff --cached` before pushing; automated scans cannot identify every kind of private information. Updating is manual: exit the widget and replace the EXE; settings and history remain in the separate user data directory.

Add meaningful regression coverage for functional changes. Chart changes must preserve recorded totals, real work boundaries, and provider isolation. Switching view ranges or monitoring modes must not change the estimation basis at the same timestamp.
