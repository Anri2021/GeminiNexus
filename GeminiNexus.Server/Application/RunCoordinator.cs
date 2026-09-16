using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GeminiNexus.Server.Infrastructure;
using GeminiNexus.Server.Providers;
using GeminiNexus.Shared;

namespace GeminiNexus.Server.Application;

public sealed partial class RunCoordinator(WorkspaceStore store,IChatProvider provider,ServerOptions options,ILogger<RunCoordinator> logger) : BackgroundService
{
    private readonly Channel<bool> wake=Channel.CreateBounded<bool>(new BoundedChannelOptions(64){FullMode=BoundedChannelFullMode.DropWrite});
    public void Signal()=>wake.Writer.TryWrite(true);
    protected override Task ExecuteAsync(CancellationToken stoppingToken)=>Task.WhenAll(Enumerable.Range(0,options.Workers).Select(i=>Worker(i,stoppingToken)).Append(Maintenance(stoppingToken)));
    private async Task Maintenance(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested){try{await store.Recover(ct);await Task.Delay(TimeSpan.FromSeconds(15),ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch(Exception ex){WorkerError(logger,ex);await Task.Delay(1000,ct);}}
    }
    private async Task Worker(int number,CancellationToken stop)
    {
        var worker=Guid.NewGuid().ToString("N")+number;
        while(!stop.IsCancellationRequested)
        {
            try
            {
                var job=await store.Claim(worker,stop);
                if(job is null)
                {
                    using var wait=CancellationTokenSource.CreateLinkedTokenSource(stop);wait.CancelAfter(750);
                    try{await wake.Reader.ReadAsync(wait.Token);}catch(OperationCanceledException)when(!stop.IsCancellationRequested){}continue;
                }
                await Process(job.Value.Run,job.Value.Request,worker,stop);
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch(Exception ex){WorkerError(logger,ex);await Task.Delay(1000,stop);}
        }
    }
    private async Task Process(Run run,StoredRunRequest request,string worker,CancellationToken stop)
    {
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(stop);cancel.CancelAfter(TimeSpan.FromSeconds(options.RunTimeoutSeconds));
        using var monitorStop=new CancellationTokenSource();
        var monitor=Monitor(run.Id,worker,cancel,monitorStop.Token);
        var text=new StringBuilder();var parts=new JsonArray();var status="completed";string? error=null;long traceBytes=0;var finished=false;
        var started=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if(run.CancelRequested)cancel.Cancel();cancel.Token.ThrowIfCancellationRequested();
            var payload=await provider.Prepare(request,cancel.Token);
            await store.Append(run.Id,worker,"request",TraceRedactor.Redact(payload,options.ApiKey),cancel.Token);
            await foreach(var chunk in provider.Stream(run.Model,payload,cancel.Token))
            {
                traceBytes+=Encoding.UTF8.GetByteCount(chunk.RawJson);
                if(traceBytes>options.MaxTraceBytes||text.Length+chunk.Text.Length>options.MaxResponseChars)throw new InvalidOperationException("הפלט הגיע למגבלת הזיכרון או הדיבאג שהוגדרה בשרת");
                await store.Append(run.Id,worker,"provider",TraceRedactor.Redact(chunk.RawJson,options.ApiKey),cancel.Token);
                if(chunk.Text.Length>0){text.Append(chunk.Text);await store.Append(run.Id,worker,"delta",JsonSerializer.Serialize(new TextDelta(chunk.Text),NexusJson.Default.TextDelta),cancel.Token);}
                foreach(var part in JsonNode.Parse(chunk.PartsJson)!.AsArray())parts.Add(part?.DeepClone());
                if(chunk.FinishReason is not null){finished=true;if(chunk.FinishReason!="STOP"){status=chunk.FinishReason=="MAX_TOKENS"?"truncated":"blocked";error=chunk.FinishReason;}}
            }
            if(!finished){status="interrupted";error="הספק סגר את הזרם ללא סיבת סיום";}
            if(parts.Any(p=>p is JsonObject o&&o.ContainsKey("functionCall"))){status="requires_action";error="המודל ביקש הפעלת כלי; תוצאת הכלי טרם סופקה";}
        }
        catch(OperationCanceledException){status=stop.IsCancellationRequested?"interrupted":"cancelled";error=stop.IsCancellationRequested?"השרת נסגר":"הריצה בוטלה או הגיעה לזמן הקצוב";}
        catch(Exception ex){status="failed";error=ex is Domain.WorkspaceException?ex.Message:"העיבוד נכשל; בדוק את לוג השרת";WorkerError(logger,ex);}
        finally
        {
            monitorStop.Cancel();try{await monitor;}catch(OperationCanceledException){}
            using var finishTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await store.Finish(run.Id,worker,text.ToString(),parts.ToJsonString(),status,error,finishTimeout.Token);
            Finished(logger,run.Id,status,started.ElapsedMilliseconds);
        }
    }
    private async Task Monitor(string id,string worker,CancellationTokenSource cancel,CancellationToken stop)
    {
        try{while(!stop.IsCancellationRequested){if(await store.Heartbeat(id,worker,stop)){cancel.Cancel();return;}await Task.Delay(1000,stop);}}
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception ex){WorkerError(logger,ex);cancel.Cancel();}
    }
    [LoggerMessage(Level=LogLevel.Error,Message="Run worker failed")]
    private static partial void WorkerError(ILogger logger,Exception exception);
    [LoggerMessage(Level=LogLevel.Information,Message="Run {RunId} ended with {Status} after {ElapsedMs} ms")]
    private static partial void Finished(ILogger logger,string runId,string status,long elapsedMs);
}
