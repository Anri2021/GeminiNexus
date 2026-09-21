param(
    [string]$AdminName = "admin"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\GeminiNexus.Server\GeminiNexus.Server.csproj"

$password = Read-Host "בחר סיסמת מנהל באורך 16 תווים לפחות" -AsSecureString
$plainPassword = [System.Net.NetworkCredential]::new("", $password).Password
if ($plainPassword.Length -lt 16) {
    throw "סיסמת המנהל חייבת להכיל לפחות 16 תווים."
}

dotnet user-secrets set "Auth:AdminName" $AdminName --project $project
dotnet user-secrets set "Auth:AdminPassword" $plainPassword --project $project

$geminiKey = Read-Host "מפתח Gemini חדש (Enter לדילוג)" -AsSecureString
$plainGeminiKey = [System.Net.NetworkCredential]::new("", $geminiKey).Password
if ($plainGeminiKey.Length -gt 0) {
    dotnet user-secrets set "Gemini:ApiKey" $plainGeminiKey --project $project
}

$plainPassword = $null
$plainGeminiKey = $null
Write-Host "ההגדרה הושלמה. הפעל את GeminiNexus.Server ופתח http://127.0.0.1:5000/."
