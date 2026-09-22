param(
    [string]$AdminName = "admin",
    [string]$AdminPassword = "",
    [string]$GeminiApiKey = ""
)

$ErrorActionPreference = "Stop"
$repository = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$settingsFile = Join-Path $repository "nexus.settings.json"

if ([string]::IsNullOrEmpty($AdminPassword)) {
    $password = Read-Host "Choose a local administrator password (at least 6 characters)" -AsSecureString
    $AdminPassword = [System.Net.NetworkCredential]::new("", $password).Password
}
if ($AdminPassword.Length -lt 6) {
    throw "The local administrator password must contain at least 6 characters. Production requires at least 16 characters."
}

if ([string]::IsNullOrEmpty($GeminiApiKey)) {
    $geminiKey = Read-Host "Gemini API key (press Enter to skip)" -AsSecureString
    $GeminiApiKey = [System.Net.NetworkCredential]::new("", $geminiKey).Password
}
$configuration = [ordered]@{
    Urls = "https://localhost:5000"
    AllowedHosts = "localhost"
    Gemini = [ordered]@{
        ApiKey = $GeminiApiKey
        BaseUrl = "https://generativelanguage.googleapis.com/v1beta/"
        LiveUrl = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent"
        DefaultModel = "gemini-3.8-flash"
    }
    Auth = [ordered]@{
        AdminName = $AdminName
        AdminPassword = $AdminPassword
        AllowInsecureLocal = $true
        KeyPath = "data/keys"
    }
    Database = [ordered]@{
        Provider = "Sqlite"
        ConnectionString = "Data Source=data/nexus.db;Default Timeout=15"
    }
    Processing = [ordered]@{
        Workers = 4
        PerUserConcurrency = 2
        MaxQueuedPerUser = 32
        RunTimeoutSeconds = 600
    }
    RateLimits = [ordered]@{
        ApiRequestsPerMinute = 120
    }
    Limits = [ordered]@{
        MaxPromptChars = 100000
        MaxResponseChars = 1000000
        MaxAttachmentBytes = 104857600
        MaxPdfBytes = 52428800
        MaxAttachments = 10
        MaxTraceBytes = 16777216
        MaxHistoryBytes = 33554432
        MaxEventBytes = 4194304
        TraceRetentionDays = 14
    }
    Plugins = [ordered]@{
        Directory = "data/plugins"
        WasmtimePath = "wasmtime"
        TimeoutSeconds = 10
    }
    PasswordReset = [ordered]@{
        PublicBaseUrl = "https://localhost:5000/"
        PickupDirectory = "data/password-reset"
        Smtp = [ordered]@{
            Host = ""
            Port = 587
            User = ""
            Password = ""
            From = ""
        }
    }
    Proxy = [ordered]@{
        KnownProxies = @()
    }
}

$configuration | ConvertTo-Json -Depth 8 | Set-Content -Path $settingsFile -Encoding UTF8

$AdminPassword = $null
$GeminiApiKey = $null
Write-Host "Setup completed: $settingsFile"
Write-Host "Start GeminiNexus.Server and open https://localhost:5000/."
