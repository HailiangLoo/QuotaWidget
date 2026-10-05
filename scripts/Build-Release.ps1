$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\QuotaWidget\QuotaWidget.csproj'
$xml = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$xml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid project version.' }
$name = "QuotaWidget-v$version-win-x64"
$artifacts = Join-Path $root 'artifacts'
$stage = Join-Path $artifacts ('build-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
dotnet publish $project -c Release -r win-x64 --self-contained true -o $stage -p:ContinuousIntegrationBuild=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
foreach ($file in @('README.md','README.zh-CN.md','CONTRIBUTING.md','CONTRIBUTING.zh-CN.md','Start Widget.cmd','Sign in to Claude.cmd','Demo.cmd','LICENSE','NOTICE.md','启动小挂件.cmd','登录Claude.cmd','演示模式.cmd')) {
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination $stage
}
Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $stage -Recurse
$licenses = Join-Path $stage 'licenses'
[IO.Directory]::CreateDirectory($licenses) | Out-Null
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages' }
$deps = Get-Content -LiteralPath (Join-Path $stage 'QuotaWidget.deps.json') -Raw | ConvertFrom-Json
$runtimeCount = 0
foreach ($library in $deps.libraries.PSObject.Properties.Name) {
    if ($library -match '^runtimepack\.(Microsoft\.(?:NETCore|WindowsDesktop)\.App\.Runtime\.win-x64)/(.+)$') {
        $package = $Matches[1].ToLowerInvariant(); $packageVersion = $Matches[2]
        $packageRoot = Join-Path (Join-Path $nuget $package) $packageVersion
        $notices = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object Name -match '^(LICENSE|THIRD-PARTY-NOTICES|ThirdPartyNotices)(\.(TXT|MD))?$')
        if (!$notices.Count) { throw "Runtime license missing: $package $packageVersion" }
        $dest = Join-Path $licenses "$package-$packageVersion"
        [IO.Directory]::CreateDirectory($dest) | Out-Null
        foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $dest }
        $runtimeCount++
    }
}
if ($runtimeCount -lt 2) { throw 'Expected both .NET and Windows Desktop runtime license sets.' }
if (Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object Extension -eq '.pdb') { throw 'Release contains debug symbols.' }
$zip = Join-Path $artifacts ($name+'.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $artifacts ($name+'.sha256')), "$hash  $name.zip`n", [Text.UTF8Encoding]::new($false))
Write-Output "Created $name.zip with bundled runtime and notices."
