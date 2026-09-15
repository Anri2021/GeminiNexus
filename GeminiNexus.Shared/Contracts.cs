namespace GeminiNexus.Shared.Contracts;

using System.Text.Json.Serialization;
using MemoryPack;

[MemoryPackable]
public partial record struct UserProfile(string Id, string DisplayName, string AvatarUrl);

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
    DateTime CreatedAtUtc);

public readonly record struct StreamChatRequest(
    string SessionId,
    string UserId,
    string Prompt,
    string Model = "gemini-2.5-flash",
    int ContextLimit = 10);

public readonly record struct TokenUsageSummary(
    int TotalPromptTokens,
    int TotalCandidateTokens,
    int TotalCachedTokens,
    double CurrentTokensPerSec);

// Source Generator ל-JSON ללא Reflection (NativeAOT-Safe)
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StreamChatRequest))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(UserProfile))]
[JsonSerializable(typeof(TokenUsageSummary))]
public partial class AppJsonSerializerContext : JsonSerializerContext;