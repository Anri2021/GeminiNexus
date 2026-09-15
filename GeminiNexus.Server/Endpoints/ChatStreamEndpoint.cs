namespace GeminiNexus.Server.Endpoints;

using FastEndpoints;
using GeminiNexus.Server.Infrastructure;
using GeminiNexus.Shared.Contracts;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

public sealed class ChatStreamEndpoint(DatabaseService db, IConfiguration config, IHttpClientFactory httpClientFactory)
    : Endpoint<StreamChatRequest>
{
    public override void Configure()
    {
        Post("/api/chat/stream");
        AllowAnonymous();
    }

    public override async Task HandleAsync(StreamChatRequest req, CancellationToken ct)
    {
        var apiKey = config["Gemini:ApiKey"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            HttpContext.Response.StatusCode = 500;
            await HttpContext.Response.WriteAsync("Error: Gemini API Key not configured.", ct);
            return;
        }

        HttpContext.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        HttpContext.Response.Headers.CacheControl = "no-cache";
        HttpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        var client = httpClientFactory.CreateClient("GeminiClient");
        var model = string.IsNullOrWhiteSpace(req.Model) ? "gemini-2.5-flash" : req.Model;
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse&key={apiKey}";

        // שמירת הודעת המשתמש ל-SQLite
        await db.SaveMessageAsync(new ChatMessage(0, req.SessionId, "user", req.Prompt, 0, 0, 0, 0, 0, DateTime.UtcNow), ct);

        // הכנת גוף הבקשה (JSON)
        var escapedPrompt = JsonEncodedText.Encode(req.Prompt).ToString();

        var genConfig = new StringBuilder();
        if (req.Temperature.HasValue) genConfig.Append($", \"temperature\": {req.Temperature.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
        if (req.TopP.HasValue) genConfig.Append($", \"topP\": {req.TopP.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
        if (req.TopK.HasValue) genConfig.Append($", \"topK\": {req.TopK.Value}");

        var configSection = genConfig.Length > 0
            ? $", \"generationConfig\": {{ {genConfig.ToString()[2..]} }}"
            : string.Empty;

        var payload = $$"""
{
    "contents": [{"parts": [{"text": "{{escapedPrompt}}"}]}]
    {{configSection}}
}
""";

        using var requestMsg = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(requestMsg, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorMsg = await response.Content.ReadAsStringAsync(ct);
            HttpContext.Response.StatusCode = (int)response.StatusCode;
            await HttpContext.Response.WriteAsync($"שגיאת Gemini API: {errorMsg}", ct);
            return;
        }

        await using var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
        var pipeReader = PipeReader.Create(upstreamStream);
        var pipeWriter = PipeWriter.Create(HttpContext.Response.Body);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await pipeReader.ReadAsync(ct);
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted) break;

                foreach (var segment in buffer)
                {
                    pipeWriter.Write(segment.Span);
                }

                await pipeWriter.FlushAsync(ct);
                pipeReader.AdvanceTo(buffer.End);
            }
        }
        finally
        {
            stopwatch.Stop();
        }
    }
}