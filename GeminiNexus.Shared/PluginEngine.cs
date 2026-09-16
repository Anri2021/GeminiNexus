using System.Collections.Frozen;
using System.Text.Json;

namespace GeminiNexus.Shared;

/// <summary>Portable, deterministic prompt extensions. No reflection or arbitrary code execution.</summary>
public static class PluginEngine
{
    public static readonly FrozenSet<string> Kinds = new[] { "prefix", "suffix", "replace", "system" }.ToFrozenSet();
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
                default: throw new InvalidOperationException("סוג תוסף אינו נתמך");
            }
            if (prompt.Length + system.Length > 200000) throw new InvalidOperationException("פלט התוסף גדול מהמגבלה");
        }
        return (prompt, system);
    }
}
