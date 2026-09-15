namespace GeminiNexus.Shared.Contracts;

using System.Text.Json.Serialization;
using MemoryPack;

[MemoryPackable]
public partial record struct CitationInfo(string? Uri, string? License, int StartIndex, int EndIndex);

public readonly record struct CountTokensRequest(string Model, string Prompt);
public readonly record struct CountTokensResponse(int TotalTokens);

[MemoryPackable]
public partial record struct UserProfile(string Id, string DisplayName, string AvatarUrl);

[MemoryPackable]
public partial record struct ModelDefinition(string Id, string DisplayName, string ContextLimitDescription, int MaxTokens);

// נתוני טלמטריה מתקדמים מה-API
[MemoryPackable]
public partial record struct MessageTelemetry(
    string FinishReason,
    string ModelVersion,
    int CachedTokens,
    string SafetySummary,
    string GroundingSourcesJson,
    string RawJson,
    List<CitationInfo>? Citations = null);

public readonly record struct StreamChatRequest(
    string SessionId,
    string UserId,
    string Prompt,
    string Model = "gemini-2.5-flash",
    int ContextLimit = 10,
    float? Temperature = null,
    float? TopP = null,
    int? TopK = null);

[MemoryPackable]
public partial record struct ChatMessage(
    long Id,
    string SessionId,
    string Role,
    string Content,
    int PromptTokens,
    int CandidateTokens,
    int CachedTokens,
    double TokensPerSecond,
    long ElapsedMilliseconds,
    DateTime CreatedAtUtc,
    MessageTelemetry? Telemetry = null);

public readonly record struct TokenUsageSummary(
    int TotalPromptTokens,
    int TotalCandidateTokens,
    int TotalCachedTokens,
    double CurrentTokensPerSec);

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StreamChatRequest))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(UserProfile))]
[JsonSerializable(typeof(ModelDefinition))]
[JsonSerializable(typeof(TokenUsageSummary))]
[JsonSerializable(typeof(MessageTelemetry))]
[JsonSerializable(typeof(CitationInfo))]
[JsonSerializable(typeof(List<CitationInfo>))]
[JsonSerializable(typeof(CountTokensRequest))]
[JsonSerializable(typeof(CountTokensResponse))]

public partial class AppJsonSerializerContext : JsonSerializerContext;