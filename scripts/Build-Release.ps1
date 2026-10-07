#requires -Version 7.0
param([switch]$AllowPrerelease)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Update-Icons.ps1') -Check
$project = Join-Path $root 'src\QuotaWidget\QuotaWidget.csproj'
$xml = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$xml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.-]+)?$') { throw 'Invalid project version.' }
if ($version.Contains('-') -and !$AllowPrerelease) { throw 'Set a release version, or pass -AllowPrerelease for a local build.' }
$name = "QuotaWidget-v$version-win-x64"
$artifacts = Join-Path $root 'artifacts'
$work = Join-Path $artifacts ('build-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$resolved = Join-Path $work 'resolved'
$portable = Join-Path $work 'portable'
[IO.Directory]::CreateDirectory($work) | Out-Null

# Resolve the exact runtime packs before bundling their notices. Never scrape all
# installed versions, or copy anything out of a user's private app installation.
dotnet publish $project -c Release -r win-x64 --self-contained true -o $resolved -p:PublishSingleFile=false -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw 'Runtime resolution publish failed.' }
foreach ($runtimeFile in @('hostfxr.dll','hostpolicy.dll','coreclr.dll','System.Private.CoreLib.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $resolved $runtimeFile))) { throw "Self-contained runtime missing: $runtimeFile" }
}
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages' }
$deps = Get-Content -LiteralPath (Join-Path $resolved 'QuotaWidget.deps.json') -Raw | ConvertFrom-Json
$noticesText = [Text.StringBuilder]::new()
$runtimeNames = @()
foreach ($library in $deps.libraries.PSObject.Properties.Name | Sort-Object) {
    if ($library -match '^runtimepack\.(Microsoft\.(?:NETCore|WindowsDesktop)\.App\.Runtime\.win-x64)/(.+)$') {
        $package = $Matches[1].ToLowerInvariant(); $packageVersion = $Matches[2]
        $packageRoot = Join-Path (Join-Path $nuget $package) $packageVersion
        $notices = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object Name -match '^(LICENSE|THIRD-PARTY-NOTICES|ThirdPartyNotices)(\.(TXT|MD))?$' | Sort-Object FullName)
        if (!$notices.Count) { throw "Runtime license missing: $package $packageVersion" }
        $runtimeNames += "$package/$packageVersion"
        foreach ($notice in $notices) {
            [void]$noticesText.AppendLine("===== $package/$packageVersion : $($notice.Name) =====")
            [void]$noticesText.AppendLine([IO.File]::ReadAllText($notice.FullName))
        }
    }
}
if ($runtimeNames.Count -ne 2) { throw 'Expected both .NET and Windows Desktop runtime license sets.' }
$noticesFile = Join-Path $work 'runtime-notices.txt'
[IO.File]::WriteAllText($noticesFile, $noticesText.ToString(), [Text.UTF8Encoding]::new($false))
dotnet publish $project -c Release -p:PublishProfile=Portable -o $portable -p:ContinuousIntegrationBuild=true "-p:BundledRuntimeNotices=$noticesFile"
if ($LASTEXITCODE -ne 0) { throw 'Single-file publish failed.' }
$files = @(Get-ChildItem -LiteralPath $portable -Recurse -File)
if ($files.Count -ne 1 -or $files[0].Name -ne 'QuotaWidget.exe') { throw 'Portable output must contain exactly one EXE, with no sidecars.' }

# Test a renamed copy in an otherwise empty Unicode/space path, using a fresh
# native-extraction cache. These processes never access real accounts or data.
$smokeDir = Join-Path $work 'single EXE 单文件'
[IO.Directory]::CreateDirectory($smokeDir) | Out-Null
$smokeExe = Join-Path $smokeDir ($name+'.exe')
Copy-Item -LiteralPath $files[0].FullName -Destination $smokeExe
function Invoke-PackagedApp([string[]]$AppArguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($smokeExe)
    $start.WorkingDirectory = $smokeDir
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $work 'native-cache'
    foreach ($argument in $AppArguments) { $start.ArgumentList.Add($argument) }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(30000)) { $process.Kill($true); throw 'Single-file startup timed out.' }
        if ($process.ExitCode -ne 0) { throw "Single-file startup failed: $($process.ExitCode)" }
    } finally { $process.Dispose() }
    Write-Output ("Packaged check completed in {0:N1}s: {1}" -f $watch.Elapsed.TotalSeconds,($AppArguments -join ' '))
}
$exportedNotices = Join-Path $work 'exported-notices.txt'
Invoke-PackagedApp @('--export-licenses',$exportedNotices)
$exported = [IO.File]::ReadAllText($exportedNotices)
foreach ($required in @([IO.File]::ReadAllText((Join-Path $root 'LICENSE')), $noticesText.ToString()) + $runtimeNames) {
    if (!$exported.Contains($required)) { throw 'License text did not survive bundling.' }
}
foreach ($scenario in @('full','compact','setup','usage')) {
    $png = Join-Path $work ($scenario+'.png')
    $arguments = @('--demo','--scenario','showcase','--snapshot',$png,'--language','en')
    switch ($scenario) {
        'full' { $arguments += '--expanded' }
        'compact' { $arguments += '--compact' }
        'setup' { $arguments += @('--setup','--language','zh-CN') }
        'usage' { $arguments += @('--usage-card','codex','--usage-pinned') }
    }
    Invoke-PackagedApp $arguments
    if (!(Test-Path -LiteralPath $png) -or (Get-Item -LiteralPath $png).Length -lt 1000 -or (Test-Path -LiteralPath ($png+'.error.txt'))) {
        throw "Single-file $scenario snapshot failed."
    }
}
if (@(Get-ChildItem -LiteralPath $smokeDir -Force).Count -ne 1) { throw 'The portable app wrote files beside its EXE.' }
$exe = Join-Path $artifacts ($name+'.exe')
Copy-Item -LiteralPath $smokeExe -Destination $exe -Force
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $artifacts ($name+'.sha256')), "$hash  $name.exe`n", [Text.UTF8Encoding]::new($false))
Write-Output ("Created {0}.exe ({1:N1} MB) with embedded runtime and notices. Verification files: {2}" -f $name,((Get-Item -LiteralPath $exe).Length/1000000),$work)
