using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FastEndpoints;
using GeminiNexus.Server.Application;
using GeminiNexus.Server.Domain;
using GeminiNexus.Server.Infrastructure;
using GeminiNexus.Server.Providers;
using GeminiNexus.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace GeminiNexus.Server.Endpoints;

public abstract class NexusEndpoint : EndpointWithoutRequest
{
    protected string Owner=>User.FindFirstValue(ClaimTypes.NameIdentifier)??throw new WorkspaceException(401,"נדרשת התחברות");
    protected string Id=>Route<string>("id")??throw new WorkspaceException(400,"חסר מזהה");
    protected Task Json<T>(T value,JsonTypeInfo<T> type,CancellationToken ct) { HttpContext.Response.ContentType="application/json; charset=utf-8"; return JsonSerializer.SerializeAsync(HttpContext.Response.Body,value,type,ct); }
    protected async Task<T> Body<T>(JsonTypeInfo<T> type,CancellationToken ct)=>await HttpContext.Request.ReadFromJsonAsync(type,ct)??throw new WorkspaceException(400,"חסר גוף בקשה");
    protected int Number(string key,int fallback)=>int.TryParse(HttpContext.Request.Query[key],out var n)?n:fallback;
    protected string? QueryText(string key)=>HttpContext.Request.Query[key].FirstOrDefault();
}
public sealed class LoginEndpoint(WorkspaceStore store) : NexusEndpoint
{
    public override void Configure(){Post("/api/auth/login");AllowAnonymous();}
    public override async Task HandleAsync(CancellationToken ct)
    {
        var request=await Body(NexusJson.Default.LoginRequest,ct);
        if(request.Name.Length>80||request.Password.Length>1024)throw new WorkspaceException(400,"פרטי התחברות אינם תקינים");
        var user=await store.Login(request.Name,request.Password,ct)??throw new WorkspaceException(401,"שם משתמש או סיסמה אינם נכונים");
        var session=await store.CreateSession(user.Id,ct);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,user.Id),new Claim(ClaimTypes.Name,user.Name),new Claim("nexus:session",session)],CookieAuthenticationDefaults.AuthenticationScheme)),new AuthenticationProperties{IsPersistent=true});await Json(user,NexusJson.Default.UserInfo,ct);
    }
}
public sealed class LogoutEndpoint(WorkspaceStore store) : NexusEndpoint
{public override void Configure()=>Post("/api/auth/logout");public override async Task HandleAsync(CancellationToken ct){await store.RevokeSessions(Owner,User.FindFirstValue("nexus:session"),ct);await HttpContext.SignOutAsync();HttpContext.Response.StatusCode=204;}}
public sealed class MeEndpoint(ServerOptions options) : NexusEndpoint
{public override void Configure()=>Get("/api/auth/me");public override Task HandleAsync(CancellationToken ct)=>Json(new UserInfo(Owner,User.Identity?.Name??"",User.Identity?.Name==options.AdminName),NexusJson.Default.UserInfo,ct);}
public sealed class UsersEndpoint(WorkspaceStore store,ServerOptions options):NexusEndpoint
{
    public override void Configure()=>Post("/api/admin/users");
    public override async Task HandleAsync(CancellationToken ct){if(User.Identity?.Name!=options.AdminName)throw new WorkspaceException(403,"נדרשת הרשאת מנהל");var r=await Body(NexusJson.Default.CreateUserRequest,ct);await Json(await store.AddUser(r.Name,r.Password,r.Email,ct),NexusJson.Default.UserInfo,ct);}
}
public sealed class RequestPasswordResetEndpoint(PasswordResetService resets):NexusEndpoint
{
    public override void Configure(){Post("/api/auth/password-reset");AllowAnonymous();}
    public override async Task HandleAsync(CancellationToken ct){await resets.Request((await Body(NexusJson.Default.PasswordResetRequest,ct)).Email,ct);await Json(new PasswordResetAccepted(),NexusJson.Default.PasswordResetAccepted,ct);}
}
public sealed class ConfirmPasswordResetEndpoint(PasswordResetService resets):NexusEndpoint
{
    public override void Configure(){Post("/api/auth/password-reset/confirm");AllowAnonymous();}
    public override async Task HandleAsync(CancellationToken ct){await resets.Confirm(await Body(NexusJson.Default.PasswordResetConfirm,ct),ct);HttpContext.Response.StatusCode=204;}
}
public sealed class ConversationsEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Get("/api/conversations");public override async Task HandleAsync(CancellationToken ct)=>await Json(await store.Conversations(Owner,QueryText("cursor"),Number("take",30),QueryText("search"),ct),NexusJson.Default.ConversationPage,ct);}
public sealed class CreateConversationEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Post("/api/conversations");public override async Task HandleAsync(CancellationToken ct){HttpContext.Response.StatusCode=201;await Json(await store.Create(Owner,await Body(NexusJson.Default.CreateConversation,ct),ct),NexusJson.Default.Conversation,ct);}}
public sealed class RenameConversationEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Patch("/api/conversations/{id}");public override async Task HandleAsync(CancellationToken ct){await store.Rename(Owner,Id,await Body(NexusJson.Default.RenameConversation,ct),ct);HttpContext.Response.StatusCode=204;}}
public sealed class ArchiveConversationEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Delete("/api/conversations/{id}");public override async Task HandleAsync(CancellationToken ct){await store.Archive(Owner,Id,ct);HttpContext.Response.StatusCode=204;}}
public sealed class ForkEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Post("/api/conversations/{id}/fork");public override async Task HandleAsync(CancellationToken ct){var r=await Body(NexusJson.Default.ForkRequest,ct);await Json(await store.Fork(Owner,Id,r.MessageId,ct,r.Before),NexusJson.Default.Conversation,ct);}}
public sealed class MessagesEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Get("/api/conversations/{id}/messages");public override async Task HandleAsync(CancellationToken ct)=>await Json(await store.Messages(Owner,Id,long.TryParse(QueryText("before"),out var n)?n:null,Number("take",30),ct,long.TryParse(QueryText("after"),out var after)?after:null),NexusJson.Default.MessagePage,ct);}
public sealed class ModelsEndpoint(IModelCatalog provider):NexusEndpoint
{public override void Configure()=>Get("/api/models");public override async Task HandleAsync(CancellationToken ct)=>await Json(await provider.Models(ct),NexusJson.Default.ModelCatalog,ct);}
public sealed class SubmitRunEndpoint(WorkspaceStore store,RunCoordinator coordinator,ServerOptions options):NexusEndpoint
{
    public override void Configure()=>Post("/api/runs");
    public override async Task HandleAsync(CancellationToken ct)
    {
        var r=await Body(NexusJson.Default.SubmitRun,ct);GeminiProvider.ValidateModel(r.Model);
        if(string.IsNullOrWhiteSpace(r.Prompt)||r.Prompt.Length>options.MaxPromptChars||r.IdempotencyKey.Length is <16 or >128||r.Settings is null)throw new WorkspaceException(400,"פרומפט או מזהה בקשה אינם תקינים");
        var s=r.Settings;
        if(s.ContextMaxTurns is <0 or >500||s.ContextTokenBudget is <1 or >2000000||s.MaxOutputTokens is <1 or >1000000||!double.IsFinite(s.Temperature)||s.Temperature is <0 or >2||s.AdvancedJson.Length>131072||s.SystemInstruction.Length>100000)throw new WorkspaceException(400,"הגדרות יצירה אינן תקינות");
        using var advanced=JsonDocument.Parse(s.AdvancedJson);if(advanced.RootElement.ValueKind!=JsonValueKind.Object)throw new WorkspaceException(400,"הגדרות מתקדמות חייבות להיות אובייקט JSON");
        if(advanced.RootElement.TryGetProperty("contents",out _))throw new WorkspaceException(400,"היסטוריית השיחה נקבעת על ידי השרת");
        long bytes=0;foreach(var attachment in r.Attachments??[]){if(attachment.Name.Length>255||attachment.MimeType.Length>120||attachment.Data.Length>options.MaxAttachmentBytes*2)throw new WorkspaceException(400,"קובץ אינו תקין");try{bytes+=Convert.FromBase64String(attachment.Data).Length;}catch(FormatException){throw new WorkspaceException(400,"קובץ אינו תקין");}}
        if(bytes>options.MaxAttachmentBytes||(r.Attachments?.Length??0)>8)throw new WorkspaceException(413,"הקבצים חורגים מהמגבלה");
        var plugins=await store.Plugins(Owner,ct);var changed=PluginEngine.Apply(r.Prompt,s.SystemInstruction,plugins.Items,"server");r=r with{Prompt=changed.Prompt,Settings=s with{SystemInstruction=changed.System,AdvancedJson=PluginEngine.AddToolDeclarations(s.AdvancedJson,plugins.Items,"server")}};
        if(r.Prompt.Length>options.MaxPromptChars||r.Settings.SystemInstruction.Length>100000)throw new WorkspaceException(400,"פלט התוספים גדול ממגבלת הפרומפט");
        var run=await store.Enqueue(Owner,r,ct);coordinator.Signal();HttpContext.Response.StatusCode=202;await Json(run,NexusJson.Default.Run,ct);
    }
}
public sealed class RunsEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Get("/api/runs");public override async Task HandleAsync(CancellationToken ct)=>await Json(await store.ListRuns(Owner,QueryText("conversation"),ct),NexusJson.Default.RunArray,ct);}
public sealed class RunEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Get("/api/runs/{id}");public override async Task HandleAsync(CancellationToken ct)=>await Json(await store.FindRun(Owner,Id,ct)??throw new WorkspaceException(404,"הריצה לא נמצאה"),NexusJson.Default.Run,ct);}
public sealed class CancelRunEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Post("/api/runs/{id}/cancel");public override async Task HandleAsync(CancellationToken ct){await store.Cancel(Owner,Id,ct);HttpContext.Response.StatusCode=204;}}
public sealed class EventsEndpoint(WorkspaceStore store):NexusEndpoint
{
    public override void Configure()=>Get("/api/runs/{id}/events");
    public override async Task HandleAsync(CancellationToken ct)
    {
        var id=Id;var owner=Owner;var session=User.FindFirstValue("nexus:session");var after=long.TryParse(QueryText("after"),out var n)?n:0;
        if(long.TryParse(HttpContext.Request.Headers["Last-Event-ID"],out var last))after=Math.Max(after,last);
        if(await store.FindRun(owner,id,ct) is null)throw new WorkspaceException(404,"הריצה לא נמצאה");
        if(QueryText("format")=="json")
        {var items=await store.Events(owner,id,after,100,ct);var next=items.LastOrDefault()?.Sequence??after;var more=items.Length>0&&(await store.Events(owner,id,next,1,ct)).Length>0;await Json(new RunEventPage(items,more),NexusJson.Default.RunEventPage,ct);return;}
        HttpContext.Response.ContentType="text/event-stream";HttpContext.Response.Headers.CacheControl="no-store";HttpContext.Response.Headers["X-Accel-Buffering"]="no";
        var idle=0;
        while(!ct.IsCancellationRequested)
        {
            var items=await store.Events(owner,id,after,100,ct);
            foreach(var item in items){if(QueryText("view")=="text"&&item.Kind is not ("delta" or "complete")){after=item.Sequence;continue;}await HttpContext.Response.WriteAsync($"id: {item.Sequence}\nevent: {item.Kind}\ndata: {JsonSerializer.Serialize(item,NexusJson.Default.RunEvent)}\n\n",ct);after=item.Sequence;}
            if(items.Length>0){await HttpContext.Response.Body.FlushAsync(ct);idle=0;}
            var run=await store.FindRun(owner,id,ct);
            if(run is null||run.Status is not ("queued" or "running")&&(after>=run.LastSequence||items.Length==0))
            {await HttpContext.Response.WriteAsync("event: closed\ndata: {}\n\n",ct);await HttpContext.Response.Body.FlushAsync(ct);break;}
            if(!await store.ValidSession(owner,session,ct))break;
            if(++idle%40==0){await HttpContext.Response.WriteAsync(": heartbeat\n\n",ct);await HttpContext.Response.Body.FlushAsync(ct);}
            await Task.Delay(250,ct);
        }
    }
}
public sealed class SettingsEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Get("/api/settings");public override async Task HandleAsync(CancellationToken ct)=>await Json(await store.Settings(Owner,ct),NexusJson.Default.WorkspaceSettings,ct);}
public sealed class SaveSettingsEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Put("/api/settings");public override async Task HandleAsync(CancellationToken ct){await store.SaveSettings(Owner,await Body(NexusJson.Default.WorkspaceSettings,ct),ct);HttpContext.Response.StatusCode=204;}}
public sealed class RetentionEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Get("/api/retention");public override async Task HandleAsync(CancellationToken ct)=>await Json(await store.Retention(Owner,ct),NexusJson.Default.RetentionPolicy,ct);}
public sealed class SaveRetentionEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Put("/api/retention");public override async Task HandleAsync(CancellationToken ct){await store.SaveRetention(Owner,await Body(NexusJson.Default.RetentionPolicy,ct),ct);HttpContext.Response.StatusCode=204;}}
public sealed class PluginsEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Get("/api/plugins");public override async Task HandleAsync(CancellationToken ct)=>await Json(await store.Plugins(Owner,ct),NexusJson.Default.PluginList,ct);}
public sealed class SavePluginEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Put("/api/plugins");public override async Task HandleAsync(CancellationToken ct){await store.SavePlugin(Owner,await Body(NexusJson.Default.PluginDefinition,ct),ct);HttpContext.Response.StatusCode=204;}}
public sealed class DeletePluginEndpoint(WorkspaceStore store):NexusEndpoint
{public override void Configure()=>Delete("/api/plugins/{id}");public override async Task HandleAsync(CancellationToken ct){await store.DeletePlugin(Owner,Id,ct);HttpContext.Response.StatusCode=204;}}
public sealed class InstallPluginPackageEndpoint(PluginPackageService packages):NexusEndpoint
{public override void Configure()=>Post("/api/plugins/packages");public override async Task HandleAsync(CancellationToken ct){HttpContext.Response.StatusCode=201;await Json(await packages.Install(Owner,await Body(NexusJson.Default.PluginPackageRequest,ct),ct),NexusJson.Default.PluginDefinition,ct);}}
public sealed class ExplorerEndpoint(IProviderExplorer provider,ServerOptions options):NexusEndpoint
{
    public override void Configure()=>Post("/api/explorer");
    public override async Task HandleAsync(CancellationToken ct){if(User.Identity?.Name!=options.AdminName)throw new WorkspaceException(403,"מעבדת ה־API זמינה למנהל בלבד");await Json(await provider.Execute(await Body(NexusJson.Default.PlaygroundRequest,ct),ct),NexusJson.Default.PlaygroundResponse,ct);}
}
public sealed class CountEndpoint(ITokenCounter provider):NexusEndpoint
{public override void Configure()=>Post("/api/chat/count-tokens");public override async Task HandleAsync(CancellationToken ct){var r=await Body(NexusJson.Default.TokenCountRequest,ct);await Json(new TokenCountResult(await provider.Count(r.Model,r.ContentsJson,ct),false),NexusJson.Default.TokenCountResult,ct);}}
public sealed class LimitsEndpoint(ServerOptions options):NexusEndpoint
{public override void Configure()=>Get("/api/limits");public override Task HandleAsync(CancellationToken ct)=>Json(new UploadLimit(options.MaxAttachmentBytes,options.MaxPromptChars),NexusJson.Default.UploadLimit,ct);}
public sealed class SimilarityEndpoint:NexusEndpoint
{public override void Configure()=>Post("/api/local/similarity");public override async Task HandleAsync(CancellationToken ct){var request=await Body(NexusJson.Default.SimilarityRequest,ct);if(request.Left.Length>2_000_000||request.Backend is not ("auto" or "simd" or "native" or "gpu"))throw new WorkspaceException(400,"בקשת חישוב אינה תקינה");var value=ComputeKernels.Dot(request.Left,request.Right,request.Backend);await Json(new SimilarityResult(value,request.Backend,ComputeKernels.OpenClAvailable(),ComputeKernels.Invocations),NexusJson.Default.SimilarityResult,ct);}}

public sealed class ProviderCapabilitiesEndpoint(IProviderOperations provider):NexusEndpoint
{public override void Configure()=>Get("/api/provider/capabilities");public override Task HandleAsync(CancellationToken ct)=>Json(provider.Capabilities,NexusJson.Default.ProviderCapabilityCatalog,ct);}

public sealed class ProviderOperationsEndpoint(GetProviderOperationsHandler handler):NexusEndpoint
{public override void Configure()=>Get("/api/provider/operations");public override async Task HandleAsync(CancellationToken ct)=>await Json(await handler.Handle(new(Owner),ct),NexusJson.Default.ProviderOperationList,ct);}

public sealed class ExecuteProviderOperationEndpoint(ExecuteProviderOperationHandler handler,ServerOptions options):NexusEndpoint
{
    public override void Configure()=>Post("/api/provider/operations");
    public override async Task HandleAsync(CancellationToken ct)
    {
        var request=await Body(NexusJson.Default.ProviderOperationRequest,ct);
        if(request.Body.Length>options.MaxAttachmentBytes*2)throw new WorkspaceException(413,"גוף הפעולה גדול מהמגבלה");
        var operation=await handler.Handle(new(Owner,request),ct);HttpContext.Response.StatusCode=operation.Status=="completed"?201:502;await Json(operation,NexusJson.Default.ProviderOperation,ct);
    }
}

public sealed class ListUsersEndpoint(WorkspaceStore store,ServerOptions options):NexusEndpoint
{
    public override void Configure()=>Get("/api/admin/users");
    public override async Task HandleAsync(CancellationToken ct){if(User.Identity?.Name!=options.AdminName)throw new WorkspaceException(403,"נדרשת הרשאת מנהל");await Json(await store.Users(ct),NexusJson.Default.UserAccountArray,ct);}
}
public sealed class RevokeSessionsEndpoint(WorkspaceStore store,ServerOptions options):NexusEndpoint
{
    public override void Configure()=>Delete("/api/admin/users/{id}/sessions");
    public override async Task HandleAsync(CancellationToken ct){if(User.Identity?.Name!=options.AdminName)throw new WorkspaceException(403,"נדרשת הרשאת מנהל");await store.RevokeSessions(Id,null,ct);HttpContext.Response.StatusCode=204;}
}
public sealed class PasswordEndpoint(WorkspaceStore store):NexusEndpoint
{
    public override void Configure()=>Put("/api/auth/password");
    public override async Task HandleAsync(CancellationToken ct){await store.UpdatePassword(Owner,await Body(NexusJson.Default.ChangePassword,ct),ct);await HttpContext.SignOutAsync();HttpContext.Response.StatusCode=204;}
}

public sealed class MetricsEndpoint(WorkspaceStore store):NexusEndpoint
{
    public override void Configure()=>Get("/api/runs/{id}/metrics");
    public override async Task HandleAsync(CancellationToken ct)=>await Json(await store.Metrics(Owner,Id,ct),NexusJson.Default.RunMetrics,ct);
}
