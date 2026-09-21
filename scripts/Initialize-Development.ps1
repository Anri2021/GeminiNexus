param(
    [string]$AdminName = "admin",
    [string]$AdminPassword = "",
    [string]$GeminiApiKey = ""
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\GeminiNexus.Server\GeminiNexus.Server.csproj"

if ([string]::IsNullOrEmpty($AdminPassword)) {
    $password = Read-Host "בחר סיסמת מנהל מקומית באורך 6 תווים לפחות" -AsSecureString
    $AdminPassword = [System.Net.NetworkCredential]::new("", $password).Password
}
if ($AdminPassword.Length -lt 6) {
    throw "סיסמת המנהל המקומית חייבת להכיל לפחות 6 תווים. בפרודקשן נדרשים לפחות 16 תווים."
}

dotnet user-secrets set "Auth:AdminName" $AdminName --project $project
dotnet user-secrets set "Auth:AdminPassword" $AdminPassword --project $project

if ([string]::IsNullOrEmpty($GeminiApiKey)) {
    $geminiKey = Read-Host "מפתח Gemini חדש (Enter לדילוג)" -AsSecureString
    $GeminiApiKey = [System.Net.NetworkCredential]::new("", $geminiKey).Password
}
if ($GeminiApiKey.Length -gt 0) {
    dotnet user-secrets set "Gemini:ApiKey" $GeminiApiKey --project $project
}

$AdminPassword = $null
$GeminiApiKey = $null
Write-Host "ההגדרה הושלמה. הפעל את GeminiNexus.Server ופתח http://127.0.0.1:5000/."
