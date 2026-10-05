$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $files = @(git -c core.quotepath=false ls-files)
    if ($LASTEXITCODE -ne 0 -or !$files.Count) { throw 'Run after staging the public files with git add.' }
    $bad = @()
    foreach ($file in $files) {
        if ($file -match '(^|/)(bin|obj|qa|data|claude-auth|history|chat-sessions|tokens)/|\.(pdb|sqlite|db|jsonl|log|zip)$|(^|/)(settings|latest|auth)\.json$') {
            $bad += "$file : private data or build output"
            continue
        }
        if ([IO.Path]::GetExtension($file) -in @('.png','.ico')) { continue }
        $text = [IO.File]::ReadAllText((Join-Path $root $file))
        # Report filenames only: never echo a possible secret into logs.
        if ($text -match '(?i)(?:ghp_|github_pat_)[A-Za-z0-9_]{20,}|sk-(?:ant-)?[A-Za-z0-9_-]{24,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----') {
            $bad += "$file : possible credential"
        }
        if ($text -match '(?i)[A-Z]:[\\/]Users[\\/][^\s"''\\/]+') {
            $bad += "$file : absolute user-profile path"
        }
    }
    if ($bad.Count) { $bad | Write-Output; throw 'Public-tree check failed.' }
    Write-Output "PASS: $($files.Count) tracked files; no private data paths, build output, or recognized credential patterns. Review still required."
} finally { Pop-Location }
