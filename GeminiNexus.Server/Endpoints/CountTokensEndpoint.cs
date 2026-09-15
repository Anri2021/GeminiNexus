namespace GeminiNexus.Server.Endpoints;

using System.Text;
using System.Text.Json;
using FastEndpoints;
using GeminiNexus.Shared.Contracts;

public sealed class CountTokensEndpoint(IConfiguration config, IHttpClientFactory httpClientFactory)
    : Endpoint<CountTokensRequest, CountTokensResponse>
{
    public override void Configure()
    {
        Post("/api/chat/count-tokens");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CountTokensRequest req, CancellationToken ct)
    {
        var apiKey = config["Gemini:ApiKey"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var client = httpClientFactory.CreateClient("GeminiClient");
        var model = string.IsNullOrWhiteSpace(req.Model) ? "gemini-2.5-flash" : req.Model;
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:countTokens?key={apiKey}";

        var escaped = JsonEncodedText.Encode(req.Prompt).ToString();
        var payload = $$"""{"contents":[{"parts":[{"text":"{{escaped}}"}]}]}""";
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(url, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            await HttpContext.Response.WriteAsJsonAsync(new CountTokensResponse(req.Prompt.Length / 4), AppJsonSerializerContext.Default.CountTokensResponse, ct);
            return;
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        int count = doc.RootElement.TryGetProperty("totalTokens", out var prop) ? prop.GetInt32() : req.Prompt.Length / 4;
        await HttpContext.Response.WriteAsJsonAsync(new CountTokensResponse(count), AppJsonSerializerContext.Default.CountTokensResponse, ct);
    }
}