namespace GeminiNexus.Server.Application;

public sealed class ServerOptions(IConfiguration config)
{
    public string DatabaseProvider { get; } = config["Database:Provider"] ?? "Sqlite";
    public string ConnectionString { get; } = config["Database:ConnectionString"] ?? "Data Source=data/nexus.db;Default Timeout=15";
    public int ApiRequestsPerMinute { get; } = Read(config,"RateLimits:ApiRequestsPerMinute",120,10,100000);
    public int Workers { get; } = Read(config, "Processing:Workers", 4, 1, 64);
    public int PerUserConcurrency { get; } = Read(config, "Processing:PerUserConcurrency", 2, 1, 32);
    public int MaxQueuedPerUser { get; } = Read(config, "Processing:MaxQueuedPerUser", 32, 1, 1000);
    public int RunTimeoutSeconds { get; } = Read(config, "Processing:RunTimeoutSeconds", 600, 10, 7200);
    public int MaxPromptChars { get; } = Read(config, "Limits:MaxPromptChars", 100000, 1, 1000000);
    public int MaxResponseChars { get; } = Read(config, "Limits:MaxResponseChars", 1000000, 1000, 10000000);
    public int MaxAttachmentBytes { get; } = Read(config, "Limits:MaxAttachmentBytes", 104857600, 1024, 1073741824);
    public int MaxPdfBytes { get; } = Read(config, "Limits:MaxPdfBytes", 52428800, 1024, 104857600);
    public int MaxAttachments { get; } = Read(config, "Limits:MaxAttachments", 10, 1, 10);
    public int MaxTraceBytes { get; } = Read(config, "Limits:MaxTraceBytes", 16777216, 4096, 134217728);
    public int TraceRetentionDays { get; } = Read(config, "Limits:TraceRetentionDays", 14, 1, 3650);
    public int MaxHistoryBytes { get; } = Read(config,"Limits:MaxHistoryBytes",33554432,1048576,134217728);
    public int MaxEventBytes { get; } = Read(config, "Limits:MaxEventBytes", 4194304, 4096, 16777216);
    public string DefaultModel { get; } = config["Gemini:DefaultModel"] ?? "gemini-3.8-flash";
    public string ApiKey { get; } = ReadSecret(config, "Gemini:ApiKey", "Gemini:ApiKeyFile");
    public string ApiBaseUrl { get; } = config["Gemini:BaseUrl"] ?? "https://generativelanguage.googleapis.com/v1beta/";
    public string LiveApiUrl { get; } = config["Gemini:LiveUrl"] ?? "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
    public string AdminName { get; } = config["Auth:AdminName"] ?? "admin";
    public string AdminPassword { get; } = ReadSecret(config, "Auth:AdminPassword", "Auth:AdminPasswordFile");
    public bool AllowInsecureLocal { get; } = bool.TryParse(config["Auth:AllowInsecureLocal"],out var allowInsecureLocal)&&allowInsecureLocal;
    public string PluginDirectory { get; } = config["Plugins:Directory"] ?? "data/plugins";
    public string WasmtimePath { get; } = config["Plugins:WasmtimePath"] ?? "wasmtime";
    public int PluginTimeoutSeconds { get; } = Read(config,"Plugins:TimeoutSeconds",10,1,60);
    public string PasswordResetPublicBaseUrl { get; } = config["PasswordReset:PublicBaseUrl"] ?? "";
    public string PasswordResetPickupDirectory { get; } = config["PasswordReset:PickupDirectory"] ?? "";
    public string SmtpHost { get; } = config["PasswordReset:Smtp:Host"] ?? "";
    public int SmtpPort { get; } = Read(config,"PasswordReset:Smtp:Port",587,1,65535);
    public string SmtpUser { get; } = config["PasswordReset:Smtp:User"] ?? "";
    public string SmtpPassword { get; } = ReadSecret(config,"PasswordReset:Smtp:Password","PasswordReset:Smtp:PasswordFile");
    public string SmtpFrom { get; } = config["PasswordReset:Smtp:From"] ?? "";
    private static int Read(IConfiguration config, string key, int fallback, int min, int max)
    {
        if (config[key] is not { } value) return fallback;
        if (!int.TryParse(value, out var number) || number < min || number > max)
            throw new InvalidOperationException($"Invalid configuration: {key} ({min}..{max})");
        return number;
    }
    private static string ReadSecret(IConfiguration config, string key, string fileKey)
        => config[fileKey] is { Length: > 0 } path ? File.ReadAllText(path).Trim() : config[key] ?? "";
}
