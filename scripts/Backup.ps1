[CmdletBinding()]
param(
    [ValidateSet('Sqlite','Postgres')][string]$Provider = 'Sqlite',
    [Parameter(Mandatory)][string]$ConnectionString,
    [Parameter(Mandatory)][string]$DataProtectionPath,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$destinationPath = [IO.Path]::GetFullPath($Destination)
$keysPath = [IO.Path]::GetFullPath($DataProtectionPath)
if (-not (Test-Path -LiteralPath $keysPath -PathType Container)) { throw 'Data-protection key directory does not exist.' }
[IO.Directory]::CreateDirectory($destinationPath) | Out-Null
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$work = Join-Path $destinationPath ('.nexus-backup-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
try {
    if ($Provider -eq 'Sqlite') {
        $builder = [System.Data.Common.DbConnectionStringBuilder]::new(); $builder.ConnectionString = $ConnectionString
        $database = [IO.Path]::GetFullPath([string]$builder['Data Source'])
        if (-not (Test-Path -LiteralPath $database -PathType Leaf)) { throw 'SQLite database does not exist.' }
        & sqlite3 $database ".timeout 30000" ".backup '$((Join-Path $work 'database.sqlite').Replace("'", "''"))'"
        if ($LASTEXITCODE -ne 0) { throw 'SQLite online backup failed.' }
    } else {
        & pg_dump --format=custom --no-owner --no-privileges --file (Join-Path $work 'database.dump') --dbname $ConnectionString
        if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL backup failed.' }
    }
    Copy-Item -LiteralPath $keysPath -Destination (Join-Path $work 'data-protection-keys') -Recurse
    @{ version=1; createdAt=[DateTimeOffset]::UtcNow.ToString('O'); provider=$Provider } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'manifest.json') -Encoding utf8NoBOM
    $archive = Join-Path $destinationPath ("gemininexus-$stamp.zip")
    Compress-Archive -Path (Join-Path $work '*') -DestinationPath $archive -CompressionLevel Optimal
    Get-FileHash -Algorithm SHA256 -LiteralPath $archive | Select-Object Path,Hash
} finally { if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force } }
