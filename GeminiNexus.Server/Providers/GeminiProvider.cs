using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GeminiNexus.Server.Application;
using GeminiNexus.Server.Domain;
using GeminiNexus.Shared;

namespace GeminiNexus.Server.Providers;

public sealed record ProviderChunk(string RawJson, string Text, string PartsJson, string? FinishReason);
public interface IChatProvider
{
    Task<string> Prepare(StoredRunRequest request,CancellationToken ct);
    IAsyncEnumerable<ProviderChunk> Stream(string model,string payload,CancellationToken ct);
}
public interface IModelCatalog { Task<ModelCatalog> Models(CancellationToken ct); }
public interface ITokenCounter { Task<int> Count(string model,string contents,CancellationToken ct); }
public interface IProviderExplorer { Task<PlaygroundResponse> Execute(PlaygroundRequest request,CancellationToken ct); }

public sealed partial class GeminiProvider(IHttpClientFactory factory,ServerOptions options) : IChatProvider,IModelCatalog,ITokenCounter,IProviderExplorer
{
    private readonly SemaphoreSlim catalogGate=new(1,1);
    private ModelCatalog? cached; private DateTimeOffset catalogExpires;
    [GeneratedRegex("^[A-Za-z0-9._-]{1,120}$",RegexOptions.CultureInvariant)]
    private static partial Regex ModelPattern();
    public static void ValidateModel(string model){if(!ModelPattern().IsMatch(model))throw new WorkspaceException(400,"מזהה מודל אינו תקין");}
    private HttpRequestMessage Request(HttpMethod method,string path,string? payload=null)
    {
        if(string.IsNullOrWhiteSpace(options.ApiKey))throw new WorkspaceException(503,"מפתח Gemini לא הוגדר בשרת");
        var request=new HttpRequestMessage(method,path);
        request.Headers.Add("x-goog-api-key",options.ApiKey);
        if(payload is not null)request.Content=new StringContent(payload,Encoding.UTF8,"application/json");return request;
    }
    private string SafeError(string body)
    {
        try{return TraceRedactor.Redact(body,options.ApiKey);}
        catch(JsonException){return options.ApiKey.Length==0?body:body.Replace(options.ApiKey,"[REDACTED]",StringComparison.Ordinal);}
    }
    private async Task<string> ReadBounded(HttpContent content,int limit,CancellationToken ct)
    {
        await using var stream=await content.ReadAsStreamAsync(ct);using var buffer=new MemoryStream();
        var rented=System.Buffers.ArrayPool<byte>.Shared.Rent(16384);
        try{int size;while((size=await stream.ReadAsync(rented.AsMemory(),ct))>0){if(buffer.Length+size>limit)throw new WorkspaceException(502,"תגובת הספק חורגת ממגבלת הגודל");buffer.Write(rented,0,size);}return Encoding.UTF8.GetString(buffer.GetBuffer(),0,(int)buffer.Length);}
        finally{System.Buffers.ArrayPool<byte>.Shared.Return(rented,clearArray:true);}
    }
    public async Task<int> Count(string model,string contents,CancellationToken ct)
    {
        ValidateModel(model);using var request=Request(HttpMethod.Post,$"models/{model}:countTokens","{\"contents\":"+contents+"}");
        using var response=await factory.CreateClient("Gemini").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        var body=await ReadBounded(response.Content,options.MaxEventBytes,ct);if(!response.IsSuccessStatusCode)throw new WorkspaceException((int)response.StatusCode,"ספירת הטוקנים נכשלה: "+SafeError(body));
        using var json=JsonDocument.Parse(body);return json.RootElement.GetProperty("totalTokens").GetInt32();
    }
    public async Task<string> Prepare(StoredRunRequest stored,CancellationToken ct)
    {
        var settings=stored.Request.Settings;var contents=JsonNode.Parse(stored.ContentsJson)!.AsArray();
        var config=JsonNode.Parse(settings.AdvancedJson)!.AsObject();
        // The owned conversation snapshot always wins over provider extension fields.
        config["contents"]=contents;
        config["systemInstruction"]=new JsonObject{["parts"]=new JsonArray{new JsonObject{["text"]=settings.SystemInstruction}}};
        var generation=config["generationConfig"] as JsonObject??new JsonObject();
        config["generationConfig"]=generation.Parent is null?generation:generation.DeepClone();generation=config["generationConfig"]!.AsObject();
        generation["temperature"]=settings.Temperature;generation["maxOutputTokens"]=settings.MaxOutputTokens;
        // Exact input accounting includes tools, system instructions and all multimodal parts.
        while(true)
        {
            var countedRequest=config.DeepClone().AsObject();countedRequest["model"]="models/"+stored.Request.Model;
            using var req=Request(HttpMethod.Post,$"models/{stored.Request.Model}:countTokens",new JsonObject{["generateContentRequest"]=countedRequest}.ToJsonString());
            using var response=await factory.CreateClient("Gemini").SendAsync(req,HttpCompletionOption.ResponseHeadersRead,ct);
            var body=await ReadBounded(response.Content,options.MaxEventBytes,ct);
            if(!response.IsSuccessStatusCode)throw new WorkspaceException((int)response.StatusCode,"לא ניתן לאמת את תקציב ההקשר: "+SafeError(body));
            using var json=JsonDocument.Parse(body);var tokens=json.RootElement.GetProperty("totalTokens").GetInt32();
            if(tokens<=settings.ContextTokenBudget)break;
            if(contents.Count<=1)throw new WorkspaceException(400,"הפרומפט והקבצים חורגים מתקציב ההקשר; הגדל את התקציב או קצר את הקלט");
            contents.RemoveAt(0);while(contents.Count>1&&contents[0]?["role"]?.GetValue<string>()!="user")contents.RemoveAt(0);
        }
        return config.ToJsonString();
    }
    public async IAsyncEnumerable<ProviderChunk> Stream(string model,string payload,[EnumeratorCancellation] CancellationToken ct)
    {
        ValidateModel(model);using var request=Request(HttpMethod.Post,$"models/{model}:streamGenerateContent?alt=sse",payload);
        using var response=await factory.CreateClient("Gemini").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!response.IsSuccessStatusCode)throw new WorkspaceException((int)response.StatusCode,SafeError(await ReadBounded(response.Content,options.MaxEventBytes,ct)));
        await using var stream=await response.Content.ReadAsStreamAsync(ct);
        await foreach(var raw in SseReader.Read(stream,options.MaxEventBytes,ct))yield return Parse(TraceRedactor.Redact(raw,options.ApiKey));
    }
    internal static ProviderChunk Parse(string raw)
    {
        using var doc=JsonDocument.Parse(raw);var text=new StringBuilder();var parts=new JsonArray();string? finish=null;
        if(doc.RootElement.TryGetProperty("error",out var error))throw new WorkspaceException(502,error.GetRawText());
        if(doc.RootElement.TryGetProperty("candidates",out var candidates))
        {
            foreach(var candidate in candidates.EnumerateArray())
            {
                // Full candidates remain in raw events; primary candidate forms this conversation branch.
                if(candidate.TryGetProperty("index",out var index)&&index.GetInt32()!=0)continue;
                if(candidate.TryGetProperty("finishReason",out var f))finish=f.GetString();
                if(!candidate.TryGetProperty("content",out var content)||!content.TryGetProperty("parts",out var pp))continue;
                foreach(var part in pp.EnumerateArray())
                {
                    parts.Add(JsonNode.Parse(part.GetRawText()));
                    if(part.TryGetProperty("text",out var t)&&(!part.TryGetProperty("thought",out var thought)||!thought.GetBoolean()))text.Append(t.GetString());
                }
                break;
            }
        }
        if(doc.RootElement.TryGetProperty("promptFeedback",out var feedback)&&feedback.TryGetProperty("blockReason",out var block))finish="BLOCKED:"+block.GetString();
        return new(raw,text.ToString(),parts.ToJsonString(),finish);
    }
    public async Task<ModelCatalog> Models(CancellationToken ct)
    {
        if(cached is not null&&catalogExpires>DateTimeOffset.UtcNow)return cached;
        await catalogGate.WaitAsync(ct);
        try
        {
            if(cached is not null&&catalogExpires>DateTimeOffset.UtcNow)return cached;
            var list=new List<ModelInfo>();string? page=null;
            do
            {
                using var request=Request(HttpMethod.Get,"models?pageSize=100"+(page is null?"":"&pageToken="+Uri.EscapeDataString(page)));
                using var response=await factory.CreateClient("Gemini").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
                var body=await ReadBounded(response.Content,options.MaxEventBytes,ct);if(!response.IsSuccessStatusCode)throw new WorkspaceException(502,"לא ניתן לטעון מודלים");
                using var doc=JsonDocument.Parse(body);
                foreach(var m in doc.RootElement.GetProperty("models").EnumerateArray())
                {
                    var methods=m.TryGetProperty("supportedGenerationMethods",out var a)?a.EnumerateArray().Select(x=>x.GetString()!).ToArray():[];
                    list.Add(new(m.GetProperty("name").GetString()!.Replace("models/",""),m.GetProperty("displayName").GetString()!,m.TryGetProperty("inputTokenLimit",out var i)?i.GetInt32():0,m.TryGetProperty("outputTokenLimit",out var o)?o.GetInt32():0,methods));
                }
                page=doc.RootElement.TryGetProperty("nextPageToken",out var p)?p.GetString():null;
            }while(!string.IsNullOrEmpty(page)&&list.Count<2000);
            cached=new(list.ToArray());catalogExpires=DateTimeOffset.UtcNow.AddMinutes(10);return cached;
        }
        finally{catalogGate.Release();}
    }
    [GeneratedRegex("^(models|files|cachedContents|batches|fileSearchStores|operations|interactions)(/[A-Za-z0-9._:-]+)*(:[A-Za-z]+)?(\\?[A-Za-z0-9%=&._-]+)?$",RegexOptions.CultureInvariant)]
    private static partial Regex ExplorerPath();
    public async Task<PlaygroundResponse> Execute(PlaygroundRequest request,CancellationToken ct)
    {
        if(request.Method is not ("GET" or "POST" or "DELETE" or "PATCH")||!ExplorerPath().IsMatch(request.Path)||request.Path.Contains("..")||request.Path.Contains("key=",StringComparison.OrdinalIgnoreCase)||request.Body.Length>options.MaxEventBytes)throw new WorkspaceException(400,"נתיב או גוף בקשת API אינם תקינים");
        if(request.Path.Contains("stream",StringComparison.OrdinalIgnoreCase))throw new WorkspaceException(400,"להזרמה יש להשתמש במסך השיחה; המעבדה מיועדת לבקשות חד־פעמיות");
        using var message=Request(new HttpMethod(request.Method),request.Path,request.Method is "GET" or "DELETE"?null:request.Body);
        using var response=await factory.CreateClient("Gemini").SendAsync(message,HttpCompletionOption.ResponseHeadersRead,ct);
        return new((int)response.StatusCode,response.Content.Headers.ContentType?.ToString()??"application/json",SafeError(await ReadBounded(response.Content,options.MaxTraceBytes,ct)));
    }
}
