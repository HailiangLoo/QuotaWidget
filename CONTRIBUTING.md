# Development

**English** · [简体中文](CONTRIBUTING.zh-CN.md)

Use Windows and the .NET 10 SDK. The interface uses WPF; storage uses Windows' built-in SQLite. The core project has no third-party NuGet dependencies.

```powershell
dotnet run --project tests/QuotaWidget.Tests -c Release
dotnet run --project tests/QuotaWidget.UiTests -c Release
dotnet run --project src/QuotaWidget -- --demo --scenario codex-plus --language en
```

Core tests use temporary directories and synthetic records. WPF tests exercise real layouts and handlers without starting background collectors.

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
powershell -File scripts/Build-Release.ps1
powershell -File scripts/Test-PublicTree.ps1
```

Build fresh binaries from source with debug symbols removed and compiler paths mapped. Do not copy a private installation directory. Publish only the newly generated zip and checksum from `artifacts/`. Review `git diff --cached` before pushing; automated scans cannot identify every kind of private information.

Add meaningful regression coverage for functional changes. Chart changes must preserve recorded totals, real work boundaries, and provider isolation. Switching view ranges or monitoring modes must not change the estimation basis at the same timestamp.
