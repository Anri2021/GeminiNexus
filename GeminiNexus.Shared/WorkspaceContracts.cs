using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiNexus.Shared;

public sealed record UserInfo(string Id, string Name, bool IsAdmin = false);
public sealed record UserAccount(string Id, string Name, int Sessions);
public sealed record ChangePassword(string CurrentPassword, string NewPassword);
public sealed record LoginRequest(string Name, string Password);
public sealed record ApiError(string Error, string TraceId = "");
public sealed record Conversation(string Id, string Title, string Model, string UpdatedAt, bool Pinned, int MessageCount);
public sealed record ConversationPage(Conversation[] Items, string? NextCursor);
public sealed record CreateConversation(string Title = "שיחה חדשה", string Model = "");
public sealed record RenameConversation(string Title, bool Pinned);
public sealed record ForkRequest(string MessageId, bool Before = false);
public sealed record Message(string Id, string ConversationId, string RunId, long Ordinal, string Role,
    string Content, string PartsJson, string CreatedAt, string Status);
public sealed record MessagePage(Message[] Items, bool HasMore, long? NextBefore, bool HasNewer = false);
public sealed record Attachment(string Name, string MimeType, string Data);
public sealed record SubmitRun(string ConversationId, string Prompt, string Model, string IdempotencyKey,
    GenerationSettings Settings, Attachment[]? Attachments = null);
public sealed record GenerationSettings
{
    public int ContextMaxTurns { get; init; } = 20;
    public int ContextTokenBudget { get; init; } = 24000;
    public int MaxOutputTokens { get; init; } = 8192;
    public double Temperature { get; init; } = 1;
    public string SystemInstruction { get; init; } = "";
    public string AdvancedJson { get; init; } = "{}";
}
public sealed record Run(string Id, string ConversationId, string Model, string Status, string CreatedAt,
    string UpdatedAt, long LastSequence, string? Error, bool CancelRequested);
public sealed record RunEvent(string RunId, long Sequence, string Kind, string Json, string CreatedAt);
public sealed record RunEventPage(RunEvent[] Items, bool HasMore);
public sealed record ModelInfo(string Id, string Name, int InputTokenLimit, int OutputTokenLimit, string[] Methods);
public sealed record ModelCatalog(ModelInfo[] Items, string? Error = null);
public sealed record WorkspaceSettings
{
    public int ConversationPageSize { get; init; } = 30;
    public int MessagePageSize { get; init; } = 30;
    public int MaxBufferedTurns { get; init; } = 60;
    public int MaxBufferedBytes { get; init; } = 4194304;
    public int OverscanCount { get; init; } = 4;
    public int PrefetchPages { get; init; } = 1;
    public int RenderIntervalMs { get; init; } = 80;
    public string Theme { get; init; } = "dark";
    public GenerationSettings Generation { get; init; } = new();
}
public sealed record PluginDefinition(string Id, string Name, string Kind, string Version, string Execution,
    bool Enabled, string ConfigurationJson);
public sealed record PluginList(PluginDefinition[] Items);
public sealed record PlaygroundRequest(string Method, string Path, string Body = "{}");
public sealed record PlaygroundResponse(int Status, string ContentType, string Body);
public sealed record TokenCountRequest(string Model, string ContentsJson);
public sealed record TokenCountResult(int TotalTokens, bool Estimated, string? Error = null);
public sealed record HealthStatus(string Status);
public sealed record StoredRunRequest(SubmitRun Request, string ContentsJson);
public sealed record TextDelta(string Text);
public sealed record RunMetrics(string UsageJson, string? ModelVersion, string? FinishReason, long ElapsedMs, long? FirstTokenMs, long QueueMs);
public sealed record RunCompletion(string Status, string? Error);
public sealed record UploadLimit(int MaxAttachmentBytes, int MaxPromptChars);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(UserInfo))]
[JsonSerializable(typeof(UserAccount[]))]
[JsonSerializable(typeof(ChangePassword))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(Conversation))]
[JsonSerializable(typeof(ConversationPage))]
[JsonSerializable(typeof(CreateConversation))]
[JsonSerializable(typeof(RenameConversation))]
[JsonSerializable(typeof(ForkRequest))]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(MessagePage))]
[JsonSerializable(typeof(SubmitRun))]
[JsonSerializable(typeof(StoredRunRequest))]
[JsonSerializable(typeof(Run))]
[JsonSerializable(typeof(Run[]))]
[JsonSerializable(typeof(RunEvent))]
[JsonSerializable(typeof(RunEventPage))]
[JsonSerializable(typeof(ModelCatalog))]
[JsonSerializable(typeof(WorkspaceSettings))]
[JsonSerializable(typeof(PluginDefinition))]
[JsonSerializable(typeof(PluginList))]
[JsonSerializable(typeof(PlaygroundRequest))]
[JsonSerializable(typeof(PlaygroundResponse))]
[JsonSerializable(typeof(TokenCountRequest))]
[JsonSerializable(typeof(TokenCountResult))]
[JsonSerializable(typeof(HealthStatus))]
[JsonSerializable(typeof(TextDelta))]
[JsonSerializable(typeof(RunCompletion))]
[JsonSerializable(typeof(RunMetrics))]
[JsonSerializable(typeof(UploadLimit))]
[JsonSerializable(typeof(JsonElement))]
public partial class NexusJson : JsonSerializerContext;
