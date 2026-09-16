using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace GeminiNexus.UI.Services;

/// <summary>Safe Markdown subset: headings, lists, quotes, emphasis and links.</summary>
public static partial class SafeMarkdown
{
    [GeneratedRegex(@"(\*\*([^*\n]+)\*\*)|(`([^`\n]+)`)|(\[([^\]\n]+)\]\((https?://[^\s)]+)\))",RegexOptions.CultureInvariant,100)]
    private static partial Regex InlinePattern();
    public static string Render(string text)
    {
        var output=new StringBuilder();var list=false;
        foreach(var raw in text.Split('\n'))
        {
            var line=raw.TrimEnd('\r');var bullet=line.StartsWith("- ")||line.StartsWith("* ");
            if(list&&!bullet){output.Append("</ul>");list=false;}
            if(bullet){if(!list){output.Append("<ul>");list=true;}output.Append("<li>").Append(Inline(line[2..])).Append("</li>");continue;}
            var heading=0;while(heading<line.Length&&heading<6&&line[heading]=='#')heading++;
            if(heading>0&&line.Length>heading&&line[heading]==' ')
                output.Append("<h").Append(heading).Append('>').Append(Inline(line[(heading+1)..])).Append("</h").Append(heading).Append('>');
            else if(line.StartsWith("> "))output.Append("<blockquote>").Append(Inline(line[2..])).Append("</blockquote>");
            else output.Append("<div class=\"markdown-line\">").Append(line.Length==0?"<br>":Inline(line)).Append("</div>");
        }
        if(list)output.Append("</ul>");return output.ToString();
    }
    private static string Inline(string text)
    {
        var result=new StringBuilder();var offset=0;
        foreach(Match match in InlinePattern().Matches(text))
        {
            result.Append(HtmlEncoder.Default.Encode(text[offset..match.Index]));offset=match.Index+match.Length;
            if(match.Groups[2].Success)result.Append("<strong>").Append(HtmlEncoder.Default.Encode(match.Groups[2].Value)).Append("</strong>");
            else if(match.Groups[4].Success)result.Append("<code>").Append(HtmlEncoder.Default.Encode(match.Groups[4].Value)).Append("</code>");
            else result.Append("<a target=\"_blank\" rel=\"noopener noreferrer\" href=\"").Append(HtmlEncoder.Default.Encode(match.Groups[7].Value)).Append("\">").Append(HtmlEncoder.Default.Encode(match.Groups[6].Value)).Append("</a>");
        }
        return result.Append(HtmlEncoder.Default.Encode(text[offset..])).ToString();
    }
}
