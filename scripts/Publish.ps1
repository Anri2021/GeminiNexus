param(
    [string]$Output = "$PSScriptRoot/../artifacts/publish",
    [string]$RuntimeIdentifier = 'linux-x64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Push-Location $root
try {
    & g++ -O3 -shared -fPIC native/nexus_native.cpp -o native/libnexus_native.so
    if ($LASTEXITCODE -ne 0) { throw 'Native kernel build failed' }
    dotnet publish GeminiNexus.Server -c Release -r $RuntimeIdentifier -p:PublishAot=true -p:StripSymbols=true -o "$Output/server" --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'Server publish failed' }
    dotnet publish GeminiNexus.Client.Wasm -c Release -o "$Output/client" --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'Client publish failed' }
    Write-Host "Published server: $Output/server; static site: $Output/client/wwwroot"
} finally { Pop-Location }
