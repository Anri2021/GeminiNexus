using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using System.Net;
using System.Threading.RateLimiting;
using FastEndpoints;
using GeminiNexus.Server;
using GeminiNexus.Server.Application;
using GeminiNexus.Server.Domain;
using GeminiNexus.Server.Infrastructure;
using GeminiNexus.Server.Providers;
using GeminiNexus.Shared;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;

var publishedWebRoot=Path.Combine(AppContext.BaseDirectory,"wwwroot");
var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args=args,
    WebRootPath=Directory.Exists(publishedWebRoot)?publishedWebRoot:"wwwroot"
});
var configuredSettings=Environment.GetEnvironmentVariable("NEXUS_CONFIG");
var settingsCandidates=new[]
{
    configuredSettings,
    Path.Combine(builder.Environment.ContentRootPath,"nexus.settings.json"),
    Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath,"..","nexus.settings.json"))
};
var settingsPath=settingsCandidates.FirstOrDefault(path=>!string.IsNullOrWhiteSpace(path)&&File.Exists(path));
if(settingsPath is not null)builder.Configuration.AddJsonStream(new MemoryStream(File.ReadAllBytes(settingsPath)));
// The local file is convenient, while environment variables and CLI switches retain highest precedence.
builder.Configuration.AddEnvironmentVariables();builder.Configuration.AddCommandLine(args);
var options=new ServerOptions(builder.Configuration);
builder.Services.AddSingleton(options);
builder.WebHost.UseKestrelHttpsConfiguration();
builder.WebHost.ConfigureKestrel(k=>k.Limits.MaxRequestBodySize=(long)options.MaxAttachmentBytes+2*1024*1024);
builder.Services.Configure<FormOptions>(o=>{o.MultipartBodyLengthLimit=(long)options.MaxAttachmentBytes+1024*1024;o.MemoryBufferThreshold=1024*1024;});
builder.Services.ConfigureHttpJsonOptions(o=>o.SerializerOptions.TypeInfoResolverChain.Insert(0,NexusJson.Default));
if(options.AllowInsecureLocal&&!builder.Environment.IsDevelopment()&&!builder.Environment.IsEnvironment("Testing"))
    throw new InvalidOperationException("Auth:AllowInsecureLocal is limited to Development and Testing.");
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["Auth:KeyPath"]??"data/keys")).SetApplicationName("GeminiNexus");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o=>
{
    o.Cookie.Name="Nexus.Session";o.Cookie.HttpOnly=true;o.Cookie.SameSite=SameSiteMode.Strict;
    o.Cookie.SecurePolicy=options.AllowInsecureLocal?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;
    o.ExpireTimeSpan=TimeSpan.FromDays(7);o.SlidingExpiration=false;
    o.Events.OnValidatePrincipal=async c=>
    {
        var store=c.HttpContext.RequestServices.GetRequiredService<WorkspaceStore>();
        if(!await store.ValidSession(c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier),c.Principal?.FindFirstValue("nexus:session"),c.HttpContext.RequestAborted))
        {c.RejectPrincipal();await c.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);}
    };
    o.Events.OnRedirectToLogin=c=>{c.Response.StatusCode=401;return Task.CompletedTask;};
    o.Events.OnRedirectToAccessDenied=c=>{c.Response.StatusCode=403;return Task.CompletedTask;};
});
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(o=>
{
    o.ForwardedHeaders=ForwardedHeaders.XForwardedFor|ForwardedHeaders.XForwardedProto;
    foreach(var proxy in builder.Configuration.GetSection("Proxy:KnownProxies").GetChildren())
        if(IPAddress.TryParse(proxy.Value,out var ip))o.KnownProxies.Add(ip);
});
builder.Services.AddRateLimiter(o=>
{
    o.RejectionStatusCode=429;
    o.GlobalLimiter=PartitionedRateLimiter.Create<HttpContext,string>(context=>
        RateLimitPartition.GetTokenBucketLimiter((context.Request.Path=="/api/auth/login"?"login:":"api:")+(context.User.Identity?.Name??context.Connection.RemoteIpAddress?.ToString()??"unknown"),_=>new TokenBucketRateLimiterOptions
        {TokenLimit=context.Request.Path=="/api/auth/login"?5:options.ApiRequestsPerMinute,TokensPerPeriod=context.Request.Path=="/api/auth/login"?5:options.ApiRequestsPerMinute,ReplenishmentPeriod=TimeSpan.FromMinutes(1),QueueLimit=0,AutoReplenishment=true}));
});
var baseUri=new Uri(options.ApiBaseUrl);
if(baseUri.Scheme!="https"&&!(builder.Environment.IsEnvironment("Testing")&&baseUri.IsLoopback))throw new InvalidOperationException("Gemini:BaseUrl requires HTTPS");
builder.Services.AddHttpClient("Gemini",c=>{c.BaseAddress=baseUri;c.Timeout=TimeSpan.FromSeconds(options.RunTimeoutSeconds);})
    .ConfigurePrimaryHttpMessageHandler(()=>new SocketsHttpHandler{AllowAutoRedirect=false,PooledConnectionLifetime=TimeSpan.FromMinutes(5),MaxConnectionsPerServer=options.Workers+8});
builder.Services.AddSingleton<WorkspaceStore>();builder.Services.AddSingleton<IWorkspaceStore>(s=>s.GetRequiredService<WorkspaceStore>());
builder.Services.AddSingleton<GeminiProvider>();builder.Services.AddSingleton<IChatProvider>(s=>s.GetRequiredService<GeminiProvider>());
builder.Services.AddSingleton<IModelCatalog>(s=>s.GetRequiredService<GeminiProvider>());builder.Services.AddSingleton<ITokenCounter>(s=>s.GetRequiredService<GeminiProvider>());builder.Services.AddSingleton<IProviderExplorer>(s=>s.GetRequiredService<GeminiProvider>());
builder.Services.AddSingleton<IProviderOperations>(s=>s.GetRequiredService<GeminiProvider>());
builder.Services.AddSingleton<ToolExecutor>();
builder.Services.AddSingleton<PluginPackageService>();
builder.Services.AddSingleton<PasswordResetService>();
builder.Services.AddSingleton<ExecuteProviderOperationHandler>();builder.Services.AddSingleton<GetProviderOperationsHandler>();
builder.Services.AddSingleton<RunCoordinator>();builder.Services.AddHostedService(s=>s.GetRequiredService<RunCoordinator>());
builder.Services.AddFastEndpoints(DiscoveredTypes.All);
builder.Services.AddHsts(o=>{o.MaxAge=TimeSpan.FromDays(365);o.IncludeSubDomains=false;});
var app=builder.Build();
await app.Services.GetRequiredService<WorkspaceStore>().Initialize(CancellationToken.None);
app.UseForwardedHeaders();
#if !NEXUS_NATIVE_AOT
app.UseBlazorFrameworkFiles();
#endif
var staticContentTypes=new FileExtensionContentTypeProvider();
staticContentTypes.Mappings[".dat"]="application/octet-stream";
app.UseStaticFiles(new StaticFileOptions{ContentTypeProvider=staticContentTypes});
if(!options.AllowInsecureLocal)app.UseHsts();
app.Use(async(context,next)=>
{
    context.Response.Headers["X-Content-Type-Options"]="nosniff";
    context.Response.Headers["Cache-Control"]="no-store";
    try
    {
        if(context.Request.Path.StartsWithSegments("/api")&&!options.AllowInsecureLocal&&!context.Request.IsHttps)
            throw new WorkspaceException(400,"נדרש חיבור HTTPS");
        if(context.Request.Path.StartsWithSegments("/api")&&context.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
        {
            if(context.Request.Headers["X-Nexus-Request"]!="1")throw new WorkspaceException(403,"חסרה כותרת אימות בקשה");
            if(context.Request.Headers.Origin.FirstOrDefault() is {Length:>0} origin&&origin!=$"{context.Request.Scheme}://{context.Request.Host}")throw new WorkspaceException(403,"מקור הבקשה אינו מורשה");
        }
        await next(context);
    }
    catch(OperationCanceledException)when(context.RequestAborted.IsCancellationRequested){}
    catch(Exception ex)
    {
        if(context.Response.HasStarted){context.Abort();return;}
        context.Response.StatusCode=ex switch{WorkspaceException w=>w.Status,System.Text.Json.JsonException=>400,_=>500};
        var message=ex switch{WorkspaceException w=>w.Message,System.Text.Json.JsonException=>"JSON אינו תקין",_=>"אירעה שגיאה בשרת"};
        if(context.Response.StatusCode==500)NexusLog.RequestFailed(app.Logger,ex,context.TraceIdentifier);
        context.Response.ContentType="application/json; charset=utf-8";
        await System.Text.Json.JsonSerializer.SerializeAsync(context.Response.Body,new ApiError(message,context.TraceIdentifier),NexusJson.Default.ApiError,context.RequestAborted);
    }
});
app.UseAuthentication();app.UseRateLimiter();app.UseAuthorization();
app.UseWebSockets(new WebSocketOptions{KeepAliveInterval=TimeSpan.FromSeconds(20),AllowedOrigins={}});
LiveGateway.Map(app,options);
app.MapGet("/health/live",()=>TypedResults.Json(new HealthStatus("ok"),NexusJson.Default.HealthStatus));
app.MapGet("/health/ready",async(WorkspaceStore store,CancellationToken ct)=>{await using var db=await store.Open(ct);await using var command=db.CreateCommand();command.CommandText="SELECT COALESCE(MAX(Version),0) FROM SchemaMigrations";var schema=Convert.ToInt32(await command.ExecuteScalarAsync(ct));if(schema!=DatabaseMigrations.LatestVersion)throw new InvalidOperationException("Database schema is not current");var depth=await store.QueueDepth(ct);return TypedResults.Json(new ReadinessStatus("ready",schema,options.DatabaseProvider,depth.Queued,depth.Running),NexusJson.Default.ReadinessStatus);});
app.UseFastEndpoints(c=>c.Serializer.Options.TypeInfoResolverChain.Insert(0,NexusJson.Default));
app.MapGet("/web",()=>Results.Redirect("/"));
app.MapGet("/wasm",()=>Results.Redirect("/"));
app.MapFallbackToFile("index.html");
app.Run();
