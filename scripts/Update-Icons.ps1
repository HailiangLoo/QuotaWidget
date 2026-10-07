#requires -Version 7.0
param([switch]$Check)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src/QuotaWidget/Assets'
# Compile the same renderer used by the tray, without starting the widget.
$references = @([Drawing.Bitmap].Assembly.Location, [Drawing.Color].Assembly.Location,
    (Join-Path $PSHOME 'System.Runtime.dll'), (Join-Path $PSHOME 'System.ComponentModel.Primitives.dll'))
$references += @(Get-ChildItem -LiteralPath $PSHOME -Filter 'System.Private.Windows*.dll' | ForEach-Object FullName)
Add-Type -TypeDefinition (Get-Content -LiteralPath (Join-Path $root 'src/QuotaWidget/QuotaIcon.cs') -Raw) -ReferencedAssemblies $references -CompilerOptions '/nullable:enable'
$claude = [Drawing.Bitmap]::new((Join-Path $assets 'claude-badge.png'))
$codex = [Drawing.Bitmap]::new((Join-Path $assets 'codex-badge.png'))
function Save-Asset([string]$Name, [byte[]]$Bytes) {
    $path = Join-Path $assets $Name
    if ($Check) {
        if (!(Test-Path -LiteralPath $path) -or
            [Convert]::ToHexString([IO.File]::ReadAllBytes($path)) -ne [Convert]::ToHexString($Bytes)) {
            throw "Stale icon asset: $Name. Run scripts/Update-Icons.ps1."
        }
    } else { [IO.File]::WriteAllBytes($path, $Bytes) }
}
try {
    $frames = @(foreach ($size in @(16,20,24,32,40,48,64,96,128,256)) {
        $bitmap = [QuotaWidget.App.QuotaIcon]::Render($size,$true,$true,$claude,$codex)
        $png = [IO.MemoryStream]::new()
        try {
            $bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{ size=$size; bytes=$png.ToArray() }
        } finally { $png.Dispose(); $bitmap.Dispose() }
    })
    Save-Asset 'quota-companion.png' $frames[-1].bytes
    $ico = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($ico)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = [byte]($frame.size % 256)
            $writer.Write($dimension); $writer.Write($dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.bytes.Length); $writer.Write([uint32]$offset)
            $offset += $frame.bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.bytes) }
        Save-Asset 'quota-companion.ico' $ico.ToArray()
    } finally { $writer.Dispose(); $ico.Dispose() }
    Write-Output 'Window/EXE icon matches the tray renderer at 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 px.'
} finally { $claude.Dispose(); $codex.Dispose() }
