using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GeminiNexus.Server.Infrastructure;
using GeminiNexus.Server.Providers;
using GeminiNexus.Shared;

namespace GeminiNexus.Server.Application;

public sealed partial class RunCoordinator(WorkspaceStore store,IChatProvider provider,ToolExecutor tools,ServerOptions options,ILogger<RunCoordinator> logger) : BackgroundService
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
        var started=System.Diagnostics.Stopwatch.StartNew();var usage=new JsonObject();string? modelVersion=null,finishReason=null;long? firstTokenMs=null;
        var queueMs=Math.Max(0,(long)(DateTimeOffset.UtcNow-DateTimeOffset.Parse(run.CreatedAt)).TotalMilliseconds);
        try
        {
            if(run.CancelRequested)cancel.Cancel();cancel.Token.ThrowIfCancellationRequested();
            var payload=await provider.Prepare(request,cancel.Token);
            await store.Append(run.Id,worker,"request",TraceRedactor.Redact(payload,options.ApiKey),cancel.Token);
            for(var toolRound=0;toolRound<8;toolRound++)
            {
                finished=false;finishReason=null;var roundParts=new JsonArray();
                await foreach(var chunk in provider.Stream(run.Model,payload,cancel.Token))
                {
                    using(var metadata=JsonDocument.Parse(chunk.RawJson))
                    {
                        if(metadata.RootElement.TryGetProperty("usageMetadata",out var report))foreach(var field in report.EnumerateObject())usage[field.Name]=JsonNode.Parse(field.Value.GetRawText());
                        if(metadata.RootElement.TryGetProperty("modelVersion",out var version))modelVersion=version.GetString();
                    }
                    if(chunk.Text.Length>0)firstTokenMs??=started.ElapsedMilliseconds;
                    finishReason=chunk.FinishReason??finishReason;
                    traceBytes+=Encoding.UTF8.GetByteCount(chunk.RawJson);
                    if(traceBytes>options.MaxTraceBytes||text.Length+chunk.Text.Length>options.MaxResponseChars)throw new InvalidOperationException("הפלט הגיע למגבלת הזיכרון או הדיבאג שהוגדרה בשרת");
                    await store.Append(run.Id,worker,"provider",TraceRedactor.Redact(chunk.RawJson,options.ApiKey),cancel.Token);
                    if(chunk.Text.Length>0){text.Append(chunk.Text);await store.Append(run.Id,worker,"delta",JsonSerializer.Serialize(new TextDelta(chunk.Text),NexusJson.Default.TextDelta),cancel.Token);}
                    foreach(var part in JsonNode.Parse(chunk.PartsJson)!.AsArray()){var copy=part?.DeepClone();parts.Add(copy);roundParts.Add(part?.DeepClone());}
                    if(chunk.FinishReason is not null){finished=true;if(chunk.FinishReason!="STOP"){status=chunk.FinishReason=="MAX_TOKENS"?"truncated":"blocked";error=chunk.FinishReason;}}
                }
                if(!finished){status="interrupted";error="הספק סגר את הזרם ללא סיבת סיום";break;}
                if(!roundParts.Any(p=>p is JsonObject o&&o.ContainsKey("functionCall")))break;
                var responses=await tools.Execute(run.Id,roundParts,cancel.Token);
                await store.Append(run.Id,worker,"tool",responses.ToJsonString(),cancel.Token);
                payload=GeminiProvider.ContinueWithTools(payload,roundParts,responses);
                if(toolRound==7){status="failed";error="המודל חרג ממספר סבבי הכלים המותר";}
            }
        }
        catch(OperationCanceledException){status=stop.IsCancellationRequested?"interrupted":"cancelled";error=stop.IsCancellationRequested?"השרת נסגר":"הריצה בוטלה או הגיעה לזמן הקצוב";}
        catch(Exception ex){status="failed";error=ex is Domain.WorkspaceException?ex.Message:"העיבוד נכשל; בדוק את לוג השרת";WorkerError(logger,ex);}
        finally
        {
            monitorStop.Cancel();try{await monitor;}catch(OperationCanceledException){}
            using var finishTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await store.Finish(run.Id,worker,text.ToString(),parts.ToJsonString(),status,error,finishTimeout.Token,metrics:new RunMetrics(usage.ToJsonString(),modelVersion,finishReason,started.ElapsedMilliseconds,firstTokenMs,queueMs));
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
