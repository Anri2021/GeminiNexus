namespace GeminiNexus.Server.Endpoints;

using FastEndpoints;
using GeminiNexus.Server.Infrastructure;
using GeminiNexus.Shared.Contracts;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;

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
            await SendStringAsync("Error: Gemini API Key not configured.", 500, cancellation: ct);
            return;
        }

        // הגדרת Header עבור SSE
        HttpContext.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        HttpContext.Response.Headers.CacheControl = "no-cache";
        HttpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        var client = httpClientFactory.CreateClient("GeminiClient");
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{req.Model}:streamGenerateContent?alt=sse&key={apiKey}";

        // שמירת פרומפט המשתמש ל-DB
        await db.SaveMessageAsync(new ChatMessage(0, req.SessionId, "user", req.Prompt, 0, 0, 0, 0, 0, DateTime.UtcNow), ct);

        // הכנת גוף הבקשה
        var payload = $$"""{"contents":[{"parts":[{"text":"{{req.Prompt}}"}]}]}""";
        using var requestMsg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(requestMsg, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
        var pipeReader = PipeReader.Create(upstreamStream);
        var pipeWriter = PipeWriter.Create(HttpContext.Response.Body);

        var stopwatch = Stopwatch.StartNew();
        var contentAccumulator = new StringBuilder(4096);
        int candidateTokens = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await pipeReader.ReadAsync(ct);
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted) break;

                // הזרמת החבילה ישירות ל-Response Body
                foreach (var segment in buffer)
                {
                    pipeWriter.Write(segment.Span);
                }

                await pipeWriter.FlushAsync(ct);
                candidateTokens++; // הערכה ראשונית לקצב טוקנים ב-stream

                pipeReader.AdvanceTo(buffer.End);
            }
        }
        finally
        {
            stopwatch.Stop();
            var tps = stopwatch.Elapsed.TotalSeconds > 0 ? candidateTokens / stopwatch.Elapsed.TotalSeconds : 0;

            // שמירת מענה ה-AI ל-DB
            await db.SaveMessageAsync(new ChatMessage(
                0,
                req.SessionId,
                "model",
                contentAccumulator.ToString(),
                req.Prompt.Length / 4,
                candidateTokens,
                0,
                tps,
                stopwatch.ElapsedMilliseconds,
                DateTime.UtcNow
            ), CancellationToken.None);
        }
    }
}