using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiNexus.Shared;

public sealed record UserInfo(string Id, string Name, bool IsAdmin = false);
public sealed record UserAccount(string Id, string Name, int Sessions, string Email = "");
public sealed record ChangePassword(string CurrentPassword, string NewPassword);
public sealed record LoginRequest(string Name, string Password);
public sealed record CreateUserRequest(string Name,string Password,string Email = "");
public sealed record PasswordResetRequest(string Email);
public sealed record PasswordResetConfirm(string Token,string NewPassword);
public sealed record PasswordResetAccepted(string Status = "accepted");
public sealed record ApiError(string Error, string TraceId = "");
public sealed record Conversation(string Id, string Title, string Model, string UpdatedAt, bool Pinned, int MessageCount);
public sealed record ConversationPage(Conversation[] Items, string? NextCursor);
public sealed record CreateConversation(string Title = "שיחה חדשה", string Model = "");
public sealed record RenameConversation(string Title, bool Pinned);
public sealed record ForkRequest(string MessageId, bool Before = false);
public sealed record Message(string Id, string ConversationId, string RunId, long Ordinal, string Role,
    string Content, string PartsJson, string CreatedAt, string Status);
public sealed record MessagePage(Message[] Items, bool HasMore, long? NextBefore, bool HasNewer = false);
public sealed record Attachment(string Name,string MimeType,string Data = "",string FileId = "",string FileUri = "",long SizeBytes = 0,string State = "active",string? ExpiresAt = null);
public sealed record SubmitRun(string ConversationId, string Prompt, string Model, string IdempotencyKey,
    GenerationSettings Settings, Attachment[]? Attachments = null);
public sealed record GenerationSettings
{
    public int ContextMaxTurns { get; init; } = 20;
    public int ContextTokenBudget { get; init; } = 1000000;
    public int MaxOutputTokens { get; init; } = 8192;
    public double Temperature { get; init; } = 1;
    public string ThinkingLevel { get; init; } = "medium";
    public bool IncludeThoughts { get; init; } = true;
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
public sealed record PluginTool(string Name,string Description,string InputSchemaJson);
public sealed record PluginManifest(string Id,string Version,string Name,string Runtime,string EntryPoint,
    string[] Permissions,string[] Environments,PluginTool[] Tools);
public sealed record PluginDefinition(string Id, string Name, string Kind, string Version, string Execution,
    bool Enabled, string ConfigurationJson,PluginManifest? Manifest = null,string PackageReference = "",string PackageHash = "");
public sealed record PluginList(PluginDefinition[] Items);
public sealed record PluginPackageRequest(PluginManifest Manifest,string WasmBase64,bool Enabled = true,string Execution = "server");
public sealed record PlaygroundRequest(string Method, string Path, string Body = "{}");
public sealed record PlaygroundResponse(int Status, string ContentType, string Body);
public sealed record ProviderCapability(string Id, string Name, string[] Methods, bool Asynchronous, bool Realtime = false);
public sealed record ProviderCapabilityCatalog(ProviderCapability[] Items);
public sealed record ProviderOperationRequest(string Capability, string Method, string Resource, string Body = "{}");
public sealed record ProviderOperation(string Id, string Capability, string? ProviderName, string Status,
    string RequestJson, string ResponseJson, string CreatedAt, string UpdatedAt);
public sealed record ProviderOperationList(ProviderOperation[] Items);
public sealed record TokenCountRequest(string Model, string ContentsJson);
public sealed record TokenCountResult(int TotalTokens, bool Estimated, string? Error = null);
public sealed record HealthStatus(string Status);
public sealed record StoredRunRequest(SubmitRun Request, string ContentsJson);
public sealed record TextDelta(string Text);
public sealed record ThoughtDelta(string Text);
public sealed record RunMetrics(string UsageJson, string? ModelVersion, string? FinishReason, long ElapsedMs, long? FirstTokenMs, long QueueMs);
public sealed record RunCompletion(string Status, string? Error);
public sealed record UploadLimit(long MaxAttachmentBytes,int MaxPromptChars,int MaxAttachments = 10,long MaxPdfBytes = 52428800);
public sealed record RetentionPolicy(int ConversationDays = 0,int FileDays = 0,bool DeleteArchived = false);
public sealed record ReadinessStatus(string Status,int SchemaVersion,string DatabaseProvider,int QueuedRuns,int RunningRuns);
public sealed record SimilarityRequest(float[] Left,float[] Right,string Backend = "auto");
public sealed record SimilarityResult(float Value,string Backend,bool OpenClAvailable,long KernelInvocations);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(UserInfo))]
[JsonSerializable(typeof(UserAccount[]))]
[JsonSerializable(typeof(ChangePassword))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(CreateUserRequest))]
[JsonSerializable(typeof(PasswordResetRequest))]
[JsonSerializable(typeof(PasswordResetConfirm))]
[JsonSerializable(typeof(PasswordResetAccepted))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(Conversation))]
[JsonSerializable(typeof(ConversationPage))]
[JsonSerializable(typeof(CreateConversation))]
[JsonSerializable(typeof(RenameConversation))]
[JsonSerializable(typeof(ForkRequest))]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(MessagePage))]
[JsonSerializable(typeof(Attachment))]
[JsonSerializable(typeof(Attachment[]))]
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
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginPackageRequest))]
[JsonSerializable(typeof(PlaygroundRequest))]
[JsonSerializable(typeof(PlaygroundResponse))]
[JsonSerializable(typeof(ProviderCapabilityCatalog))]
[JsonSerializable(typeof(ProviderOperationRequest))]
[JsonSerializable(typeof(ProviderOperation))]
[JsonSerializable(typeof(ProviderOperationList))]
[JsonSerializable(typeof(TokenCountRequest))]
[JsonSerializable(typeof(TokenCountResult))]
[JsonSerializable(typeof(HealthStatus))]
[JsonSerializable(typeof(TextDelta))]
[JsonSerializable(typeof(ThoughtDelta))]
[JsonSerializable(typeof(RunCompletion))]
[JsonSerializable(typeof(RunMetrics))]
[JsonSerializable(typeof(UploadLimit))]
[JsonSerializable(typeof(RetentionPolicy))]
[JsonSerializable(typeof(ReadinessStatus))]
[JsonSerializable(typeof(SimilarityRequest))]
[JsonSerializable(typeof(SimilarityResult))]
[JsonSerializable(typeof(JsonElement))]
public partial class NexusJson : JsonSerializerContext;
