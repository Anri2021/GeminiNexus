using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GeminiNexus.Server.Domain;
using GeminiNexus.Server.Infrastructure;
using GeminiNexus.Shared;

namespace GeminiNexus.Server.Application;

public sealed partial class PluginPackageService(ServerOptions options,WorkspaceStore store)
{
    private static readonly FrozenSet<string> AllowedPermissions=new[]{"clock","random"}.ToFrozenSet(StringComparer.Ordinal);
    [GeneratedRegex("^[a-z][a-z0-9_-]{1,63}$",RegexOptions.CultureInvariant)]private static partial Regex IdPattern();
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$",RegexOptions.CultureInvariant)]private static partial Regex VersionPattern();
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{1,63}$",RegexOptions.CultureInvariant)]private static partial Regex ToolPattern();

    public async Task<PluginDefinition> Install(string owner,PluginPackageRequest request,CancellationToken ct)
    {
        Validate(request.Manifest);
        byte[] bytes;try{bytes=Convert.FromBase64String(request.WasmBase64);}catch(FormatException){throw new WorkspaceException(400,"חבילת WASM אינה תקינה");}
        if(bytes.Length is <8 or >16_777_216||bytes[0]!=0||bytes[1]!=(byte)'a'||bytes[2]!=(byte)'s'||bytes[3]!=(byte)'m')throw new WorkspaceException(400,"חבילת WASM אינה תקינה");
        var hash=Convert.ToHexString(SHA256.HashData(bytes));
        var directory=Path.GetFullPath(Path.Combine(options.PluginDirectory,owner,request.Manifest.Id,request.Manifest.Version));
        var root=Path.GetFullPath(options.PluginDirectory)+Path.DirectorySeparatorChar;
        if(!directory.StartsWith(root,StringComparison.Ordinal))throw new WorkspaceException(400,"נתיב חבילה אינו תקין");
        Directory.CreateDirectory(directory);var target=Path.Combine(directory,"module.wasm");var temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        await File.WriteAllBytesAsync(temporary,bytes,ct);
        try{File.Move(temporary,target,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
        var manifest=request.Manifest with{EntryPoint="module.wasm"};
        var definition=new PluginDefinition(manifest.Id,manifest.Name,"wasm",manifest.Version,request.Execution,request.Enabled,"{}",manifest,target,hash);
        await store.SavePluginPackage(owner,definition,ct);return definition;
    }

    public async Task<JsonNode> Execute(string runId,string name,JsonObject arguments,CancellationToken ct)
    {
        var plugin=await store.FindPluginForTool(runId,name,ct)??throw new InvalidOperationException("הכלי אינו מורשה או אינו מותקן");
        var configuredRoot=Path.GetFullPath(options.PluginDirectory)+Path.DirectorySeparatorChar;var module=Path.GetFullPath(plugin.PackageReference);
        if(!module.StartsWith(configuredRoot,StringComparison.Ordinal)||!File.Exists(module))throw new InvalidOperationException("חבילת התוסף אינה זמינה");
        await using var moduleStream=File.OpenRead(module);var actual=Convert.ToHexString(await SHA256.HashDataAsync(moduleStream,ct));
        if(!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual),Convert.FromHexString(plugin.PackageHash)))throw new InvalidOperationException("חתימת חבילת התוסף אינה תואמת");
        var start=new ProcessStartInfo(options.WasmtimePath){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        start.ArgumentList.Add("run");start.ArgumentList.Add("--");start.ArgumentList.Add(module);
        using var process=Process.Start(start)??throw new InvalidOperationException("לא ניתן להפעיל את מנוע ה־WASM");
        await process.StandardInput.WriteAsync(arguments.ToJsonString());process.StandardInput.Close();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(options.PluginTimeoutSeconds));
        string output;
        try{output=await ReadBounded(process.StandardOutput.BaseStream,options.MaxEventBytes,timeout.Token);await process.WaitForExitAsync(timeout.Token);}
        catch(OperationCanceledException){try{process.Kill(true);}catch(InvalidOperationException){}throw new InvalidOperationException("הרצת התוסף חרגה ממגבלת הזמן");}
        if(process.ExitCode!=0)throw new InvalidOperationException("התוסף הסתיים בשגיאה");
        try{return JsonNode.Parse(output)??new JsonObject();}catch(JsonException){throw new InvalidOperationException("התוסף החזיר JSON לא תקין");}
    }

    private static void Validate(PluginManifest manifest)
    {
        if(!IdPattern().IsMatch(manifest.Id)||!VersionPattern().IsMatch(manifest.Version)||manifest.Name.Length is <1 or >120||manifest.Runtime!="wasi"||manifest.Tools.Length is <1 or >32||manifest.Permissions.Any(x=>!AllowedPermissions.Contains(x))||!manifest.Environments.Contains("server",StringComparer.Ordinal))throw new WorkspaceException(400,"Manifest התוסף אינו תקין");
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var tool in manifest.Tools){if(!ToolPattern().IsMatch(tool.Name)||!names.Add(tool.Name)||tool.Description.Length>1000||tool.InputSchemaJson.Length>32768)throw new WorkspaceException(400,"הגדרת כלי בתוסף אינה תקינה");using var schema=JsonDocument.Parse(tool.InputSchemaJson);if(schema.RootElement.ValueKind!=JsonValueKind.Object)throw new WorkspaceException(400,"סכמת כלי חייבת להיות אובייקט");}
    }

    private static async Task<string> ReadBounded(Stream stream,int limit,CancellationToken ct)
    {
        using var memory=new MemoryStream();var rented=ArrayPool<byte>.Shared.Rent(8192);
        try{int read;while((read=await stream.ReadAsync(rented.AsMemory(0,8192),ct))>0){if(memory.Length+read>limit)throw new InvalidOperationException("פלט התוסף גדול מהמגבלה");memory.Write(rented,0,read);}return Encoding.UTF8.GetString(memory.GetBuffer(),0,(int)memory.Length);}
        finally{ArrayPool<byte>.Shared.Return(rented,true);}
    }
}
