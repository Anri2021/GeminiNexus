using System.Collections.Frozen;
using System.Text.Json.Nodes;

namespace GeminiNexus.Server.Application;

public static class TraceRedactor
{
    private static readonly FrozenSet<string> SecretFields = new[]
    {"apiKey","api_key","x-goog-api-key","authorization","password","access_token","refresh_token"}
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static string Redact(string json,string apiKey)
    {
        var root=JsonNode.Parse(json);
        Visit(root);
        var result=root?.ToJsonString()??"null";
        return apiKey.Length==0?result:result.Replace(apiKey,"[REDACTED]",StringComparison.Ordinal);
    }
    private static void Visit(JsonNode? node)
    {
        if(node is JsonObject obj)
            foreach(var key in obj.Select(p=>p.Key).ToArray())
            {
                if(SecretFields.Contains(key))obj[key]="[REDACTED]";
                else Visit(obj[key]);
            }
        else if(node is JsonArray array)foreach(var child in array)Visit(child);
    }
}
