using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GeminiNexus.Shared;
using GeminiNexus.Server.Providers;
using GeminiNexus.Server.Application;
using GeminiNexus.Server.Domain;
using GeminiNexus.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

var count=0;
void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
var html=SafeMarkdown.Render("# Title\n**bold** and `code`\n<script>alert(1)</script>\n[x](javascript:alert(1))\n[x](https://example.com/\"bad)");
Check(html.Contains("<h1>Title</h1>")&&html.Contains("<strong>bold</strong>"),"Markdown formatting");
Check(!html.Contains("<script>")&&!html.Contains("href=\"javascript:")&&!html.Contains("/\"bad"),"Markdown XSS encoding");
var changed=PluginEngine.Apply("input","",[new("a","prefix","prefix","1","server",true,"{\"text\":\"first\"}")],"server");
Check(changed.Prompt=="first\ninput","Portable plugin execution");
var events=new List<string>();
await foreach(var raw in SseReader.Read(new OneByteStream(Encoding.UTF8.GetBytes("data: {\"text\":\"שלום\"}\r\n\r\ndata: [DONE]\n\n")),4096,default))events.Add(raw);
Check(events.Count==1&&events[0].Contains("שלום"),"SSE fragmented multibyte UTF-8");
var rejected=false;
try{await foreach(var raw in SseReader.Read(new MemoryStream(Encoding.UTF8.GetBytes("data: "+new string('x',8192))),4096,default)){} }catch(WorkspaceException){rejected=true;}
Check(rejected,"SSE byte limit before newline");
events.Clear();await foreach(var raw in SseReader.Read(new MemoryStream(Encoding.UTF8.GetBytes("data: {}")),4096,default))events.Add(raw);
Check(events.Count==0,"Incomplete SSE event is not committed");
rejected=false;try{JsonSerializer.Deserialize("{\"name\":null,\"password\":\"x\"}",NexusJson.Default.LoginRequest);}catch(JsonException){rejected=true;}
Check(rejected,"Null authentication field rejected");
var trace=TraceRedactor.Redact("{\"nested\":{\"authorization\":\"secret\"},\"text\":\"fixture-key\"}","fixture-key");
Check(!trace.Contains("secret")&&!trace.Contains("fixture-key"),"Trace secret redaction");
var parsed=GeminiProvider.Parse("{\"candidates\":[{\"index\":0,\"content\":{\"parts\":[{\"text\":\"private\",\"thought\":true},{\"text\":\"visible\",\"thoughtSignature\":\"signature\"}]},\"finishReason\":\"STOP\"},{\"index\":1,\"content\":{\"parts\":[{\"text\":\"alternative\"}]}}],\"unknown\":true}");
Check(parsed.Text=="visible"&&parsed.ThoughtText=="private"&&parsed.PartsJson.Contains("signature")&&parsed.RawJson.Contains("alternative"),"Thought summaries are separated while all provider parts are retained");
var thinking=new System.Text.Json.Nodes.JsonObject();GeminiProvider.ApplyThinkingConfig(thinking,"gemini-3.8-flash",new(){ThinkingLevel="high",IncludeThoughts=true});
Check(thinking.ToJsonString().Contains("thinkingLevel\":\"high")&&thinking.ToJsonString().Contains("includeThoughts\":true"),"Gemini 3 thinking level mapping");
thinking=new();GeminiProvider.ApplyThinkingConfig(thinking,"gemini-2.5-flash",new(){ThinkingLevel="low"});Check(thinking.ToJsonString().Contains("thinkingBudget\":1024"),"Gemini 2.5 thinking budget mapping");
thinking=new();GeminiProvider.ApplyThinkingConfig(thinking,"gemini-2.5-pro",new(){ThinkingLevel="high"});Check(thinking.ToJsonString().Contains("thinkingBudget\":32768"),"Gemini 2.5 Pro high thinking budget mapping");
thinking=new();GeminiProvider.ApplyThinkingConfig(thinking,"gemini-3.8-flash",new(){ThinkingLevel="minimal"});Check(thinking.ToJsonString().Contains("thinkingLevel\":\"low"),"Unsupported Gemini 3 minimal level is normalized safely");
thinking=new();GeminiProvider.ApplyThinkingConfig(thinking,"gemini-3.1-flash-image",new(){ThinkingLevel="medium"});Check(thinking.ToJsonString().Contains("thinkingLevel\":\"minimal"),"Nano Banana 2 thinking level is normalized to a supported value");
thinking=new();GeminiProvider.ApplyThinkingConfig(thinking,"gemini-2.5-flash-image",new(){ThinkingLevel="high"});Check(!thinking.ContainsKey("thinkingConfig"),"Legacy Nano Banana omits unsupported thinking configuration");
var nanoBanana=new ModelInfo("gemini-3.1-flash-image","Nano Banana 2",65536,32768,["generateContent"]);
Check(ModelCatalogGrouping.Category(nanoBanana)==ModelCatalogGrouping.Image&&ModelCatalogGrouping.Family(nanoBanana)=="Nano Banana"&&ModelCatalogGrouping.SupportsChat(nanoBanana),"Nano Banana remains visible and chat-capable");
var unknownModel=new ModelInfo("future-model-v1","Future Model",1000,1000,["predict"]);
Check(ModelCatalogGrouping.Category(unknownModel)==ModelCatalogGrouping.Unclassified&&!ModelCatalogGrouping.SupportsChat(unknownModel),"Unknown catalog entries remain visible without enabling unsupported chat");
if(args.Length>0)
{
    using var serverLaunch=JsonDocument.Parse(File.ReadAllText(Path.Combine(args[0],"GeminiNexus.Server","Properties","launchSettings.json")));
    Check(serverLaunch.RootElement.GetProperty("profiles").GetProperty("https").GetProperty("applicationUrl").GetString()=="https://localhost:5000","Hosted server listens on HTTPS port 5000");
    Check(!File.Exists(Path.Combine(args[0],"GeminiNexus.Client.Wasm","Properties","launchSettings.json")),"WASM has no standalone launch ports");
    Check(!Directory.Exists(Path.Combine(args[0],"GeminiNexus.Client.Maui")),"MAUI client is removed");
    Check(!File.Exists(Path.Combine(args[0],"deploy","Caddyfile")),"Reverse-proxy configuration is external to the repository");
}
var left=new float[1024];var right=new float[1024];Array.Fill(left,2);Array.Fill(right,3);ComputeKernels.Dot(left,right,"simd");
var allocated=GC.GetAllocatedBytesForCurrentThread();for(var i=0;i<1000;i++)ComputeKernels.Dot(left,right,"simd");
Check(GC.GetAllocatedBytesForCurrentThread()==allocated&&ComputeKernels.Dot(left,right,"simd")==6144,"SIMD hot path is zero-allocation");
using(var native=new NativeFloatBuffer(128)){native.Span.Fill(7);Check(native.Span[127]==7,"Aligned native-memory buffer");}
var gpuContract=false;
if(ComputeKernels.OpenClAvailable())gpuContract=Math.Abs(ComputeKernels.Dot(left,right,"gpu")-6144)<0.01f;
else{try{ComputeKernels.Dot(left,right,"gpu");}catch(PlatformNotSupportedException){gpuContract=true;}}
Check(gpuContract,"OpenCL GPU contract or explicit unavailable result");
var services=new ServiceCollection().AddLogging().AddSingleton<IJSRuntime,NoJs>().AddSingleton<NavigationManager>(new FixtureNavigation())
    .AddSingleton(new HttpClient(new Fixture()){BaseAddress=new Uri("https://fixture.invalid/")}).AddSingleton<WorkspaceClient>().BuildServiceProvider();
await using(var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>()))
{
    await renderer.Dispatcher.InvokeAsync(async()=>
    {
        var output=await renderer.RenderComponentAsync<GeminiNexus.UI.Pages.Workspace>();
        var markup=output.ToHtmlString();Check(markup.Contains("nexus-app")&&markup.Contains("composer")&&markup.Contains("sidebar"),"Real workspace Razor renders");
        Check(markup.Contains("model-kind")&&markup.Contains("model-family")&&markup.Contains("model-version")&&markup.Contains("run-options"),"Model category, family, version, and thinking controls render");
        if(args.Length>0)
        {
            var directory=Path.Combine(args[0],"artifacts","checks");Directory.CreateDirectory(directory);
            var css=File.ReadAllText(Path.Combine(args[0],"GeminiNexus.UI","wwwroot","workspace.css"));
            File.WriteAllText(Path.Combine(directory,"workspace.html"),"<!doctype html><html lang=\"he\" dir=\"rtl\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>"+css+"</style>"+markup+"</html>");
        }
    });
}
Console.WriteLine($"{count} source checks passed");
sealed class OneByteStream(byte[] bytes):MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)=>base.ReadAsync(buffer[..Math.Min(1,buffer.Length)],ct);
}
sealed class NoJs:IJSRuntime
{
    public ValueTask<T> InvokeAsync<T>(string name,object?[]? args)=>ValueTask.FromResult(default(T)!);
    public ValueTask<T> InvokeAsync<T>(string name,CancellationToken ct,object?[]? args)=>ValueTask.FromResult(default(T)!);
}
sealed class FixtureNavigation:NavigationManager
{
    public FixtureNavigation()=>Initialize("https://fixture.invalid/","https://fixture.invalid/");
    protected override void NavigateToCore(string uri,bool forceLoad)=>Uri=ToAbsoluteUri(uri).ToString();
}
sealed class Fixture:HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var content=request.RequestUri!.AbsolutePath switch
        {
            "/api/auth/me"=>JsonContent.Create(new UserInfo("fixture-user","אנרי"),NexusJson.Default.UserInfo),
            "/api/settings"=>JsonContent.Create(new WorkspaceSettings(),NexusJson.Default.WorkspaceSettings),
            "/api/plugins"=>JsonContent.Create(new PluginList([]),NexusJson.Default.PluginList),
            "/api/limits"=>JsonContent.Create(new UploadLimit(8388608,100000),NexusJson.Default.UploadLimit),
            "/api/models"=>JsonContent.Create(new ModelCatalog([new("gemini-3.1-flash-image","Nano Banana 2",65536,32768,["generateContent"]),new("future-model-v1","Future Model",1000,1000,["predict"])]),NexusJson.Default.ModelCatalog),
            "/api/conversations"=>JsonContent.Create(new ConversationPage([],null),NexusJson.Default.ConversationPage),
            "/api/runs"=>JsonContent.Create(Array.Empty<Run>(),NexusJson.Default.RunArray),
            _=>throw new Exception("Unexpected fixture route")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content});
    }
}
