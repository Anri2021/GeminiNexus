[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$ConnectionString,
    [Parameter(Mandatory)][string]$DataProtectionPath,
    [Parameter(Mandatory)][switch]$ConfirmRestore
)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmRestore) { throw 'Restore requires -ConfirmRestore and a stopped Gemini Nexus service.' }
$archivePath = [IO.Path]::GetFullPath($Archive)
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw 'Backup archive does not exist.' }
$work = Join-Path ([IO.Path]::GetDirectoryName($archivePath)) ('.nexus-restore-' + [Guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $archivePath -DestinationPath $work
try {
    $manifest = Get-Content -LiteralPath (Join-Path $work 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.version -ne 1) { throw 'Unsupported backup format.' }
    if ($manifest.provider -eq 'Sqlite') {
        $builder = [System.Data.Common.DbConnectionStringBuilder]::new(); $builder.ConnectionString = $ConnectionString
        $database = [IO.Path]::GetFullPath([string]$builder['Data Source'])
        $parent = [IO.Path]::GetDirectoryName($database); [IO.Directory]::CreateDirectory($parent) | Out-Null
        Copy-Item -LiteralPath (Join-Path $work 'database.sqlite') -Destination $database -Force
        & sqlite3 $database 'PRAGMA integrity_check;'
        if ($LASTEXITCODE -ne 0) { throw 'Restored SQLite integrity check failed.' }
    } elseif ($manifest.provider -eq 'Postgres') {
        & pg_restore --clean --if-exists --no-owner --no-privileges --dbname $ConnectionString (Join-Path $work 'database.dump')
        if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL restore failed.' }
    } else { throw 'Unsupported database provider in backup.' }
    $keys = [IO.Path]::GetFullPath($DataProtectionPath); [IO.Directory]::CreateDirectory($keys) | Out-Null
    Copy-Item -Path (Join-Path $work 'data-protection-keys/*') -Destination $keys -Recurse -Force
    Write-Output 'Restore completed. Start the service and verify /health/ready before accepting traffic.'
} finally { if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force } }
