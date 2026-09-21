param(
    [string]$AdminName = "admin",
    [string]$AdminPassword = "",
    [string]$GeminiApiKey = ""
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\GeminiNexus.Server\GeminiNexus.Server.csproj"

if ([string]::IsNullOrEmpty($AdminPassword)) {
    $password = Read-Host "Choose a local administrator password (at least 6 characters)" -AsSecureString
    $AdminPassword = [System.Net.NetworkCredential]::new("", $password).Password
}
if ($AdminPassword.Length -lt 6) {
    throw "The local administrator password must contain at least 6 characters. Production requires at least 16 characters."
}

dotnet user-secrets set "Auth:AdminName" $AdminName --project $project
dotnet user-secrets set "Auth:AdminPassword" $AdminPassword --project $project

if ([string]::IsNullOrEmpty($GeminiApiKey)) {
    $geminiKey = Read-Host "Gemini API key (press Enter to skip)" -AsSecureString
    $GeminiApiKey = [System.Net.NetworkCredential]::new("", $geminiKey).Password
}
if ($GeminiApiKey.Length -gt 0) {
    dotnet user-secrets set "Gemini:ApiKey" $GeminiApiKey --project $project
}

$AdminPassword = $null
$GeminiApiKey = $null
Write-Host "Setup completed. Start GeminiNexus.Server and open http://127.0.0.1:5000/."
