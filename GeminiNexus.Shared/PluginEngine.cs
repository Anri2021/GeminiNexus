using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GeminiNexus.Shared;

/// <summary>Portable, deterministic prompt extensions. No reflection or arbitrary code execution.</summary>
public static class PluginEngine
{
    public static readonly FrozenSet<string> Kinds = new[] { "prefix", "suffix", "replace", "system", "wasm" }.ToFrozenSet();
    public static (string Prompt, string System) Apply(string prompt, string system, IEnumerable<PluginDefinition> plugins, string execution)
    {
        foreach (var plugin in plugins.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            if (!plugin.Enabled || plugin.Execution != execution) continue;
            using var config = JsonDocument.Parse(plugin.ConfigurationJson);
            var root = config.RootElement;
            var text = root.TryGetProperty("text", out var value) ? value.GetString() ?? "" : "";
            switch (plugin.Kind)
            {
                case "prefix": prompt = text + "\n" + prompt; break;
                case "suffix": prompt += "\n" + text; break;
                case "system": system += "\n" + text; break;
                case "replace":
                    var find = root.TryGetProperty("find", out var f) ? f.GetString() : null;
                    if (!string.IsNullOrEmpty(find)) prompt = prompt.Replace(find, text, StringComparison.Ordinal);
                    break;
                case "wasm": break;
                default: throw new InvalidOperationException("סוג תוסף אינו נתמך");
            }
            if (prompt.Length + system.Length > 200000) throw new InvalidOperationException("פלט התוסף גדול מהמגבלה");
        }
        return (prompt, system);
    }

    public static string AddToolDeclarations(string advancedJson,IEnumerable<PluginDefinition> plugins,string execution)
    {
        var root=JsonNode.Parse(advancedJson)!.AsObject();
        var tools=root["tools"] as JsonArray;
        if(tools is null){tools=[];root["tools"]=tools;}
        foreach(var plugin in plugins.OrderBy(x=>x.Id,StringComparer.Ordinal))
        {
            if(!plugin.Enabled||plugin.Kind!="wasm"||plugin.Manifest is null||plugin.Execution is not ("auto")&&plugin.Execution!=execution)continue;
            foreach(var tool in plugin.Manifest.Tools)
            {
                var schema=System.Text.Json.Nodes.JsonNode.Parse(tool.InputSchemaJson)??new System.Text.Json.Nodes.JsonObject();
                tools.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject{{"functionDeclarations",new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject{{"name",tool.Name},{"description",tool.Description},{"parameters",schema}})}});
            }
        }
        return root.ToJsonString();
    }

    public static void AddBuiltInToolDeclarations(JsonObject root)
    {
        var tools=root["tools"] as JsonArray;
        if(tools is null){tools=[];root["tools"]=tools;}
        var names=tools.SelectMany(static tool=>tool?["functionDeclarations"]?.AsArray()??[])
            .Select(static declaration=>declaration?["name"]?.GetValue<string>()).Where(static name=>name is not null).ToHashSet(StringComparer.Ordinal);
        var missing=BuiltInToolDeclarations()[0]!["functionDeclarations"]!.AsArray().Where(declaration=>!names.Contains(declaration!["name"]!.GetValue<string>()));
        var declarations=new JsonArray();foreach(var declaration in missing)declarations.Add(declaration!.DeepClone());
        if(declarations.Count>0)tools.Add((JsonNode)new JsonObject{{"functionDeclarations",declarations}});
    }

    public static JsonArray BuiltInToolDeclarations() =>new(
        new JsonObject{{"functionDeclarations",new JsonArray(
            new JsonObject{{"name","nexus_utc_time"},{"description","Returns the current UTC timestamp."},{"parameters",new JsonObject{{"type","OBJECT"},{"properties",new JsonObject()}}}},
            new JsonObject{{"name","nexus_calculate"},{"description","Evaluates a finite arithmetic expression using +, -, *, / and parentheses."},{"parameters",new JsonObject{{"type","OBJECT"},{"properties",new JsonObject{{"expression",new JsonObject{{"type","STRING"}}}}},{"required",new JsonArray("expression")}}}}
        )}});
}
