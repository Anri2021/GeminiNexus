param(
    [string]$Output = "$PSScriptRoot/../artifacts/publish",
    [string]$RuntimeIdentifier = 'linux-x64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$clientOutput = Join-Path ([IO.Path]::GetTempPath()) ("gemininexus-wasm-" + [Guid]::NewGuid().ToString('N'))
Push-Location $root
try {
    & g++ -O3 -shared -fPIC native/nexus_native.cpp -o native/libnexus_native.so
    if ($LASTEXITCODE -ne 0) { throw 'Native kernel build failed' }
    dotnet publish GeminiNexus.Server -c Release -r $RuntimeIdentifier -p:PublishAot=true -p:StripSymbols=true -o "$Output/server" --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'Server publish failed' }
    dotnet publish GeminiNexus.Client.Wasm -c Release -o $clientOutput --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'Client publish failed' }
    New-Item -ItemType Directory -Force -Path "$Output/server/wwwroot" | Out-Null
    Copy-Item -Recurse -Force "$clientOutput/wwwroot/*" "$Output/server/wwwroot/"
    Write-Host "Published unified server and WASM application: $Output/server"
} finally {
    Pop-Location
    if (Test-Path $clientOutput) { Remove-Item -Recurse -Force $clientOutput }
}
