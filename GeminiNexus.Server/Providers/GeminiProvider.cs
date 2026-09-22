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
using Polly;
using Polly.Retry;

namespace GeminiNexus.Server.Providers;

public sealed record ProviderChunk(string RawJson, string Text, string ThoughtText, string PartsJson, string? FinishReason);
public sealed record ProviderFile(string Name,string Uri,string MimeType,long SizeBytes,string State,string? ExpirationTime);
public interface IChatProvider
{
    Task<string> Prepare(StoredRunRequest request,CancellationToken ct);
    IAsyncEnumerable<ProviderChunk> Stream(string model,string payload,CancellationToken ct);
}
public interface IModelCatalog { Task<ModelCatalog> Models(CancellationToken ct); }
public interface ITokenCounter { Task<int> Count(string model,string contents,CancellationToken ct); }
public interface IProviderExplorer { Task<PlaygroundResponse> Execute(PlaygroundRequest request,CancellationToken ct); }
public interface IProviderOperations
{
    ProviderCapabilityCatalog Capabilities { get; }
    Task<PlaygroundResponse> Execute(ProviderOperationRequest request,CancellationToken ct);
}

public sealed partial class GeminiProvider(IHttpClientFactory factory,ServerOptions options) : IChatProvider,IModelCatalog,ITokenCounter,IProviderExplorer,IProviderOperations
{
    private static readonly ProviderCapabilityCatalog ProviderCapabilities = new([
        new("interactions","אינטראקציות",["GET","POST","DELETE"],true),
        new("embeddings","Embeddings",["POST"],false),
        new("batch","Batch",["GET","POST","DELETE"],true),
        new("files","קבצים מרוחקים",["GET","POST","DELETE"],true),
        new("cache","מטמון הקשר",["GET","POST","PATCH","DELETE"],false),
        new("media","תמונה, אודיו ווידאו",["GET","POST"],true),
        new("operations","פעולות ארוכות",["GET","DELETE"],true),
        new("live","Live דו־כיווני",["WEBSOCKET"],false,true)
    ]);
    ProviderCapabilityCatalog IProviderOperations.Capabilities => ProviderCapabilities;
    private readonly SemaphoreSlim catalogGate=new(1,1);
    private readonly ResiliencePipeline<HttpResponseMessage> retryPipeline=new ResiliencePipelineBuilder<HttpResponseMessage>().AddRetry(new RetryStrategyOptions<HttpResponseMessage>
    {
        MaxRetryAttempts=2,Delay=TimeSpan.FromMilliseconds(200),BackoffType=DelayBackoffType.Exponential,UseJitter=true,
        ShouldHandle=new PredicateBuilder<HttpResponseMessage>().Handle<HttpRequestException>().HandleResult(static response=>response.StatusCode is HttpStatusCode.RequestTimeout or (HttpStatusCode)429||response.StatusCode>=HttpStatusCode.InternalServerError),
        OnRetry=static outcome=>{outcome.Outcome.Result?.Dispose();return default;}
    }).Build();
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
    private ValueTask<HttpResponseMessage> SendIdempotent(Func<HttpRequestMessage> request,CancellationToken ct)=>retryPipeline.ExecuteAsync(async token=>
    {
        using var message=request();return await factory.CreateClient("Gemini").SendAsync(message,HttpCompletionOption.ResponseHeadersRead,token);
    },ct);
    public async Task<int> Count(string model,string contents,CancellationToken ct)
    {
        ValidateModel(model);using var response=await SendIdempotent(()=>Request(HttpMethod.Post,$"models/{model}:countTokens","{\"contents\":"+contents+"}"),ct);
        var body=await ReadBounded(response.Content,options.MaxEventBytes,ct);if(!response.IsSuccessStatusCode)throw new WorkspaceException((int)response.StatusCode,"ספירת הטוקנים נכשלה: "+SafeError(body));
        using var json=JsonDocument.Parse(body);return json.RootElement.GetProperty("totalTokens").GetInt32();
    }
    public async Task<string> Prepare(StoredRunRequest stored,CancellationToken ct)
    {
        var settings=stored.Request.Settings;var contents=JsonNode.Parse(stored.ContentsJson)!.AsArray();
        var config=JsonNode.Parse(settings.AdvancedJson)!.AsObject();
        // The owned conversation snapshot always wins over provider extension fields.
        config["contents"]=contents;
        config["systemInstruction"]=new JsonObject{["parts"]=new JsonArray(new JsonObject{["text"]=settings.SystemInstruction})};
        // Native image models use generateContent but reject function declarations.
        if(!IsNativeImageModel(stored.Request.Model))PluginEngine.AddBuiltInToolDeclarations(config);
        var generation=config["generationConfig"] as JsonObject??new JsonObject();
        config["generationConfig"]=generation.Parent is null?generation:generation.DeepClone();generation=config["generationConfig"]!.AsObject();
        generation["temperature"]=settings.Temperature;generation["maxOutputTokens"]=settings.MaxOutputTokens;
        ApplyThinkingConfig(generation,stored.Request.Model,settings);
        // Exact input accounting includes tools, system instructions and all multimodal parts.
        while(true)
        {
            var countedRequest=config.DeepClone().AsObject();countedRequest["model"]="models/"+stored.Request.Model;
            var countPayload=new JsonObject{["generateContentRequest"]=countedRequest}.ToJsonString();
            using var response=await SendIdempotent(()=>Request(HttpMethod.Post,$"models/{stored.Request.Model}:countTokens",countPayload),ct);
            var body=await ReadBounded(response.Content,options.MaxEventBytes,ct);
            if(!response.IsSuccessStatusCode)throw new WorkspaceException((int)response.StatusCode,"לא ניתן לאמת את תקציב ההקשר: "+SafeError(body));
            using var json=JsonDocument.Parse(body);var tokens=json.RootElement.GetProperty("totalTokens").GetInt32();
            if(tokens<=settings.ContextTokenBudget)break;
            if(contents.Count<=1)throw new WorkspaceException(400,"הפרומפט והקבצים חורגים מתקציב ההקשר; הגדל את התקציב או קצר את הקלט");
            contents.RemoveAt(0);while(contents.Count>1&&contents[0]?["role"]?.GetValue<string>()!="user")contents.RemoveAt(0);
        }
        return config.ToJsonString();
    }
    internal static void ApplyThinkingConfig(JsonObject generation,string model,GenerationSettings settings)
    {
        if(!model.StartsWith("gemini-3",StringComparison.OrdinalIgnoreCase)&&!model.StartsWith("gemini-2.5",StringComparison.OrdinalIgnoreCase)){generation.Remove("thinkingConfig");return;}
        if(IsNativeImageModel(model)&&!model.StartsWith("gemini-3.1-flash",StringComparison.OrdinalIgnoreCase)){generation.Remove("thinkingConfig");return;}
        var thinking=new JsonObject{{"includeThoughts",settings.IncludeThoughts}};var level=(settings.ThinkingLevel??"medium").ToLowerInvariant();
        if(model.StartsWith("gemini-3",StringComparison.OrdinalIgnoreCase))
        {
            if(model.StartsWith("gemini-3.1-flash",StringComparison.OrdinalIgnoreCase)&&model.Contains("-image",StringComparison.OrdinalIgnoreCase))level=level=="high"?"high":"minimal";
            if(level=="minimal"&&(model.StartsWith("gemini-3.8",StringComparison.OrdinalIgnoreCase)||model.StartsWith("gemini-3.7",StringComparison.OrdinalIgnoreCase)||model.StartsWith("gemini-3.1-pro",StringComparison.OrdinalIgnoreCase)))level="low";
            if(level!="auto")thinking["thinkingLevel"]=level;
        }
        else
        {
            var pro=model.Contains("pro",StringComparison.OrdinalIgnoreCase);
            thinking["thinkingBudget"]=level switch{"minimal"=>pro?128:0,"low"=>1024,"medium"=>8192,"high"=>pro?32768:24576,_=>-1};
        }
        generation["thinkingConfig"]=thinking;
    }
    internal static bool IsNativeImageModel(string model)=>model.StartsWith("gemini-",StringComparison.OrdinalIgnoreCase)&&model.Contains("-image",StringComparison.OrdinalIgnoreCase);
    public async IAsyncEnumerable<ProviderChunk> Stream(string model,string payload,[EnumeratorCancellation] CancellationToken ct)
    {
        ValidateModel(model);using var request=Request(HttpMethod.Post,$"models/{model}:streamGenerateContent?alt=sse",payload);
        using var response=await factory.CreateClient("Gemini").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!response.IsSuccessStatusCode)throw new WorkspaceException((int)response.StatusCode,SafeError(await ReadBounded(response.Content,options.MaxEventBytes,ct)));
        await using var stream=await response.Content.ReadAsStreamAsync(ct);
        await foreach(var raw in SseReader.Read(stream,options.MaxEventBytes,ct))yield return Parse(TraceRedactor.Redact(raw,options.ApiKey));
    }
    public static string ContinueWithTools(string payload,JsonArray modelParts,JsonArray toolResponses)
    {
        var root=JsonNode.Parse(payload)!.AsObject();var contents=root["contents"]!.AsArray();
        contents.Add((JsonNode)new JsonObject{{"role","model"},{"parts",modelParts.DeepClone()}});
        contents.Add((JsonNode)new JsonObject{{"role","user"},{"parts",toolResponses.DeepClone()}});
        return root.ToJsonString();
    }
    internal static ProviderChunk Parse(string raw)
    {
        using var doc=JsonDocument.Parse(raw);var text=new StringBuilder();var thoughts=new StringBuilder();var parts=new JsonArray();string? finish=null;
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
                    if(part.TryGetProperty("text",out var t))
                    {
                        if(part.TryGetProperty("thought",out var thought)&&thought.GetBoolean())thoughts.Append(t.GetString());else text.Append(t.GetString());
                    }
                }
                break;
            }
        }
        if(doc.RootElement.TryGetProperty("promptFeedback",out var feedback)&&feedback.TryGetProperty("blockReason",out var block))finish="BLOCKED:"+block.GetString();
        return new(raw,text.ToString(),thoughts.ToString(),parts.ToJsonString(),finish);
    }
    public async Task<ModelCatalog> Models(CancellationToken ct)
    {
        if(cached is not null&&catalogExpires>DateTimeOffset.UtcNow)return cached;
        await catalogGate.WaitAsync(ct);
        try
        {
            if(cached is not null&&catalogExpires>DateTimeOffset.UtcNow)return cached;
            var list=new List<ModelInfo>();string? page=null;var pageTokens=new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var path="models?pageSize=1000"+(page is null?"":"&pageToken="+Uri.EscapeDataString(page));
                using var response=await SendIdempotent(()=>Request(HttpMethod.Get,path),ct);
                var body=await ReadBounded(response.Content,options.MaxEventBytes,ct);if(!response.IsSuccessStatusCode)throw new WorkspaceException(502,"לא ניתן לטעון מודלים");
                using var doc=JsonDocument.Parse(body);
                foreach(var m in doc.RootElement.GetProperty("models").EnumerateArray())
                {
                    var methods=m.TryGetProperty("supportedGenerationMethods",out var a)?a.EnumerateArray().Select(x=>x.GetString()!).ToArray():[];
                    list.Add(new(m.GetProperty("name").GetString()!.Replace("models/",""),m.GetProperty("displayName").GetString()!,m.TryGetProperty("inputTokenLimit",out var i)?i.GetInt32():0,m.TryGetProperty("outputTokenLimit",out var o)?o.GetInt32():0,methods));
                }
                page=doc.RootElement.TryGetProperty("nextPageToken",out var p)?p.GetString():null;
                if(!string.IsNullOrEmpty(page)&&!pageTokens.Add(page))throw new WorkspaceException(502,"הספק החזיר עימוד מודלים לא תקין");
            }while(!string.IsNullOrEmpty(page));
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

    public Task<PlaygroundResponse> Execute(ProviderOperationRequest request,CancellationToken ct)
    {
        var capability=ProviderCapabilities.Items.FirstOrDefault(x=>x.Id==request.Capability)
            ??throw new WorkspaceException(400,"יכולת ספק אינה נתמכת");
        var method=request.Method.ToUpperInvariant();
        if(!capability.Methods.Contains(method,StringComparer.Ordinal))throw new WorkspaceException(400,"הפעולה אינה נתמכת ביכולת זו");
        if(capability.Realtime)throw new WorkspaceException(400,"Live דורש חיבור WebSocket ייעודי");
        var resource=request.Resource.TrimStart('/');
        var allowed=request.Capability switch
        {
            "interactions"=>resource.StartsWith("interactions",StringComparison.Ordinal),
            "embeddings"=>resource.StartsWith("models/",StringComparison.Ordinal)&&resource.Contains("embedContent",StringComparison.Ordinal),
            "batch"=>resource.StartsWith("batches",StringComparison.Ordinal)||resource.Contains("batch",StringComparison.OrdinalIgnoreCase),
            "files"=>resource.StartsWith("files",StringComparison.Ordinal),
            "cache"=>resource.StartsWith("cachedContents",StringComparison.Ordinal),
            "media"=>resource.StartsWith("models/",StringComparison.Ordinal)&&(resource.Contains("predict",StringComparison.OrdinalIgnoreCase)||resource.Contains("generate",StringComparison.OrdinalIgnoreCase)),
            "operations"=>resource.StartsWith("operations/",StringComparison.Ordinal),
            _=>false
        };
        if(!allowed)throw new WorkspaceException(400,"המשאב אינו מתאים ליכולת שנבחרה");
        if(request.Capability=="files"&&method=="POST")return UploadFile(request.Body,ct);
        return Execute(new PlaygroundRequest(method,resource,request.Body),ct);
    }

    private async Task<PlaygroundResponse> UploadFile(string body,CancellationToken ct)
    {
        using var document=JsonDocument.Parse(body);var root=document.RootElement;
        if(!root.TryGetProperty("displayName",out var displayNameElement)||!root.TryGetProperty("mimeType",out var mimeTypeElement)||!root.TryGetProperty("dataBase64",out var dataElement))throw new WorkspaceException(400,"העלאת קובץ דורשת displayName, mimeType ו־dataBase64");
        var displayName=displayNameElement.GetString()??"";var mimeType=mimeTypeElement.GetString()??"";var encoded=dataElement.GetString()??"";
        if(displayName.Length is <1 or >255||mimeType.Length is <1 or >120)throw new WorkspaceException(400,"מטא־נתוני הקובץ אינם תקינים");
        byte[] bytes;try{bytes=Convert.FromBase64String(encoded);}catch(FormatException){throw new WorkspaceException(400,"תוכן הקובץ אינו Base64 תקין");}
        if(bytes.Length is <1||bytes.Length>options.MaxAttachmentBytes)throw new WorkspaceException(413,"הקובץ חורג ממגבלת ההעלאה");
        await using var stream=new MemoryStream(bytes,false);var file=await UploadFile(displayName,mimeType,stream,bytes.Length,ct);
        return new(200,"application/json",new JsonObject{{"name",file.Name},{"uri",file.Uri},{"mimeType",file.MimeType},{"sizeBytes",file.SizeBytes},{"state",file.State},{"expirationTime",file.ExpirationTime}}.ToJsonString());
    }
    public async Task<ProviderFile> UploadFile(string displayName,string mimeType,Stream content,long length,CancellationToken ct)
    {
        if(displayName.Length is <1 or >255||mimeType.Length is <1 or >120||length is <1)throw new WorkspaceException(400,"מטא־נתוני הקובץ אינם תקינים");
        if(length>options.MaxAttachmentBytes||(mimeType.Equals("application/pdf",StringComparison.OrdinalIgnoreCase)&&length>options.MaxPdfBytes))throw new WorkspaceException(413,"הקובץ חורג ממגבלת ההעלאה");
        var uploadEndpoint=new Uri(new Uri(options.ApiBaseUrl),"../upload/v1beta/files");
        var metadata=new JsonObject{{"file",new JsonObject{{"display_name",displayName}}}}.ToJsonString();
        using var start=Request(HttpMethod.Post,uploadEndpoint.ToString(),metadata);start.Headers.Add("X-Goog-Upload-Protocol","resumable");start.Headers.Add("X-Goog-Upload-Command","start");start.Headers.Add("X-Goog-Upload-Header-Content-Length",length.ToString(System.Globalization.CultureInfo.InvariantCulture));start.Headers.Add("X-Goog-Upload-Header-Content-Type",mimeType);
        using var startResponse=await factory.CreateClient("Gemini").SendAsync(start,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!startResponse.IsSuccessStatusCode)throw new WorkspaceException((int)startResponse.StatusCode,"פתיחת העלאת הקובץ נכשלה: "+SafeError(await ReadBounded(startResponse.Content,options.MaxEventBytes,ct)));
        if(!startResponse.Headers.TryGetValues("X-Goog-Upload-URL",out var locations)||locations.FirstOrDefault() is not {Length:>0} location)throw new WorkspaceException(502,"הספק לא החזיר כתובת העלאה");
        using var upload=new HttpRequestMessage(HttpMethod.Post,location){Content=new StreamContent(content)};upload.Headers.Add("X-Goog-Upload-Offset","0");upload.Headers.Add("X-Goog-Upload-Command","upload, finalize");upload.Content.Headers.ContentType=new MediaTypeHeaderValue(mimeType);upload.Content.Headers.ContentLength=length;
        using var response=await factory.CreateClient("Gemini").SendAsync(upload,HttpCompletionOption.ResponseHeadersRead,ct);var responseBody=await ReadBounded(response.Content,options.MaxEventBytes,ct);
        if(!response.IsSuccessStatusCode)throw new WorkspaceException((int)response.StatusCode,"העלאת הקובץ נכשלה: "+SafeError(responseBody));
        var file=ParseFile(responseBody);var deadline=DateTimeOffset.UtcNow.AddMinutes(2);
        while(file.State.Equals("processing",StringComparison.OrdinalIgnoreCase)&&DateTimeOffset.UtcNow<deadline)
        {
            await Task.Delay(1000,ct);using var statusRequest=Request(HttpMethod.Get,file.Name);using var statusResponse=await factory.CreateClient("Gemini").SendAsync(statusRequest,HttpCompletionOption.ResponseHeadersRead,ct);var statusBody=await ReadBounded(statusResponse.Content,options.MaxEventBytes,ct);
            if(!statusResponse.IsSuccessStatusCode)throw new WorkspaceException((int)statusResponse.StatusCode,"בדיקת מצב הקובץ נכשלה: "+SafeError(statusBody));file=ParseFile(statusBody);
        }
        if(!file.State.Equals("active",StringComparison.OrdinalIgnoreCase))throw new WorkspaceException(502,"הקובץ לא הפך לזמין לעיבוד בזמן שהוקצב");return file;
    }
    public async Task DeleteFile(string name,CancellationToken ct)
    {using var request=Request(HttpMethod.Delete,name);using var response=await factory.CreateClient("Gemini").SendAsync(request,ct);if(!response.IsSuccessStatusCode&&response.StatusCode!=HttpStatusCode.NotFound)throw new WorkspaceException((int)response.StatusCode,"מחיקת הקובץ מהספק נכשלה");}
    private static ProviderFile ParseFile(string body)
    {
        using var document=JsonDocument.Parse(body);var root=document.RootElement;if(root.TryGetProperty("file",out var nested))root=nested;
        var name=root.GetProperty("name").GetString()??throw new WorkspaceException(502,"תגובת הקובץ חסרה מזהה");var uri=root.GetProperty("uri").GetString()??throw new WorkspaceException(502,"תגובת הקובץ חסרה כתובת");
        var mime=root.TryGetProperty("mimeType",out var m)?m.GetString()??"application/octet-stream":"application/octet-stream";var size=root.TryGetProperty("sizeBytes",out var s)&&long.TryParse(s.ToString(),out var parsed)?parsed:0;
        var state=root.TryGetProperty("state",out var st)?st.GetString()??"active":"active";var expiry=root.TryGetProperty("expirationTime",out var e)?e.GetString():null;return new(name,uri,mime,size,state,expiry);
    }
}
