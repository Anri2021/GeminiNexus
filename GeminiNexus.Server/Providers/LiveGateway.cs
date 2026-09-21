using System.Buffers;
using System.Net.WebSockets;
using GeminiNexus.Server.Application;
using GeminiNexus.Server.Domain;
using Microsoft.AspNetCore.Builder;

namespace GeminiNexus.Server.Providers;

/// <summary>Authenticated, bounded WebSocket bridge. The provider key never reaches a client.</summary>
public static class LiveGateway
{
    public static void Map(WebApplication app, ServerOptions options)
    {
        app.Map("/api/live", async context =>
        {
            if(context.User.Identity?.IsAuthenticated!=true){context.Response.StatusCode=401;return;}
            if(!context.WebSockets.IsWebSocketRequest){context.Response.StatusCode=426;return;}
            if(string.IsNullOrWhiteSpace(options.ApiKey))throw new WorkspaceException(503,"מפתח Gemini לא הוגדר בשרת");
            if(context.Request.Headers.Origin.FirstOrDefault() is {Length:>0} origin&&origin!=$"{context.Request.Scheme}://{context.Request.Host}")throw new WorkspaceException(403,"מקור הבקשה אינו מורשה");

            using var upstream=new ClientWebSocket();
            upstream.Options.SetRequestHeader("x-goog-api-key",options.ApiKey);
            upstream.Options.KeepAliveInterval=TimeSpan.FromSeconds(20);
            await upstream.ConnectAsync(new Uri(options.LiveApiUrl),context.RequestAborted);
            using var client=await context.WebSockets.AcceptWebSocketAsync();
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var a=Pump(client,upstream,options.MaxEventBytes,linked.Token);
            var b=Pump(upstream,client,options.MaxEventBytes,linked.Token);
            await Task.WhenAny(a,b);linked.Cancel();
            try{await Task.WhenAll(a,b);}catch(OperationCanceledException){}
            if(client.State is WebSocketState.Open or WebSocketState.CloseReceived)await client.CloseAsync(WebSocketCloseStatus.NormalClosure,"session ended",CancellationToken.None);
            if(upstream.State is WebSocketState.Open or WebSocketState.CloseReceived)await upstream.CloseAsync(WebSocketCloseStatus.NormalClosure,"session ended",CancellationToken.None);
        }).RequireAuthorization();
    }

    private static async Task Pump(WebSocket source,WebSocket destination,int maxMessageBytes,CancellationToken ct)
    {
        using var owner=MemoryPool<byte>.Shared.Rent(Math.Min(maxMessageBytes,64*1024));
        var total=0;
        while(!ct.IsCancellationRequested&&source.State is (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            var result=await source.ReceiveAsync(owner.Memory,ct);
            if(result.MessageType==WebSocketMessageType.Close)break;
            total=checked(total+result.Count);
            if(total>maxMessageBytes)
            {
                await destination.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig,"message exceeds configured limit",ct);
                break;
            }
            await destination.SendAsync(owner.Memory[..result.Count],result.MessageType,result.EndOfMessage,ct);
            if(result.EndOfMessage)total=0;
        }
    }
}
