param([string]$Output = "$PSScriptRoot/../artifacts/publish")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Push-Location $root
try {
    dotnet publish GeminiNexus.Server -c Release -o "$Output/server" --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'Server publish failed' }
    dotnet publish GeminiNexus.Client.Wasm -c Release -o "$Output/client" --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'Client publish failed' }
    Write-Host "Published server: $Output/server; static site: $Output/client/wwwroot"
} finally { Pop-Location }
