#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $root 'src\QuotaWidget\QuotaWidget.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Screenshot build failed.' }
$exe = Join-Path $root 'src\QuotaWidget\bin\Release\net10.0-windows\QuotaWidget.exe'
$images = Join-Path $root 'docs\images'
$views = @(
    @{ Name='both-expanded'; Monitor='both'; Layout='expanded'; Mode='rate'; Width=324 },
    @{ Name='codex-expanded'; Monitor='codex'; Layout='expanded'; Mode='rate'; Width=324 },
    @{ Name='both-cumulative'; Monitor='both'; Layout='expanded'; Mode='cumulative'; Width=324 },
    @{ Name='both-compact'; Monitor='both'; Layout='compact'; Mode='rate'; Width=264 },
    @{ Name='codex-compact'; Monitor='codex'; Layout='compact'; Mode='rate'; Width=264 },
    @{ Name='claude-compact'; Monitor='claude'; Layout='compact'; Mode='rate'; Width=264 },
    @{ Name='claude-usage'; Monitor='both'; Layout='expanded'; Mode='rate'; Width=324; Extra=@('--usage-card','claude','--usage-pinned','--usage-range','0') },
    @{ Name='codex-usage'; Monitor='both'; Layout='expanded'; Mode='rate'; Width=324; Extra=@('--usage-card','codex','--usage-pinned','--usage-range','0') },
    @{ Name='settings'; Monitor='both'; Layout='expanded'; Mode='rate'; Width=324; Extra=@('--settings') }
)
foreach ($language in @('zh-CN','en')) {
    foreach ($view in $views) {
        $suffix = if ($language -eq 'en') { '-en' } else { '' }
        $image = Join-Path $images ($view.Name+$suffix+'.png')
        $arguments = @('--demo','--scenario','showcase','--snapshot',$image,
            '--language',$language,'--theme','dark','--range','300','--scale','2',
            '--monitor',$view.Monitor,('--'+$view.Layout),'--chart-mode',$view.Mode,
            '--width',[string]$view.Width)
        if($view.Extra) { $arguments += $view.Extra }
        # ArgumentList preserves paths containing spaces. Snapshot mode uses a temporary demo root.
        $start = [Diagnostics.ProcessStartInfo]::new($exe)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        if (!$process.WaitForExit(30000)) { throw 'Screenshot rendering timed out.' }
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $image)) { throw "Screenshot failed: $image" }
        if (Test-Path -LiteralPath ($image+'.error.txt')) { throw "Screenshot renderer reported an error: $image" }
        $process.Dispose()
    }
}
Write-Output 'Updated eighteen synthetic gallery screenshots, including both single-provider compact views, usage details and settings.'
