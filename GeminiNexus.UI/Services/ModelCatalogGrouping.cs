using GeminiNexus.Shared;

namespace GeminiNexus.UI.Services;

public static class ModelCatalogGrouping
{
    public const string General = "טקסט ורב־מודלי";
    public const string Image = "תמונה";
    public const string Video = "וידאו";
    public const string Audio = "אודיו ודיבור";
    public const string Live = "Live";
    public const string Embedding = "Embedding";
    public const string Unclassified = "לא סווג";

    public static string Category(ModelInfo item)
    {
        var id=item.Id.ToLowerInvariant();var name=item.Name.ToLowerInvariant();
        if(id.Contains("image")||id.StartsWith("imagen-")||name.Contains("nano banana")||name.Contains("image"))return Image;
        if(id.StartsWith("veo-")||id.Contains("video")||name.Contains("veo")||name.Contains("video"))return Video;
        if(id.Contains("embedding")||id.Contains("embed")||name.Contains("embedding"))return Embedding;
        if(id.Contains("live")||item.Methods.Any(x=>x.Contains("bidi",StringComparison.OrdinalIgnoreCase)))return Live;
        if(id.Contains("tts")||id.Contains("audio")||id.Contains("speech")||id.Contains("transcribe")||name.Contains("audio")||name.Contains("speech"))return Audio;
        if(id.StartsWith("gemini-")&&SupportsChat(item))return General;
        return Unclassified;
    }

    public static string Family(ModelInfo item)
    {
        var id=item.Id.ToLowerInvariant();var category=Category(item);
        if(category==Image)
        {
            if(id.StartsWith("imagen-"))return "Imagen";
            if(id.Contains("gemini")||item.Name.Contains("Nano Banana",StringComparison.OrdinalIgnoreCase))return "Nano Banana";
            return "משפחת תמונה אחרת";
        }
        if(category==Video)return id.StartsWith("veo-")?"Veo":id.Contains("omni")?"Gemini Omni":"משפחת וידאו אחרת";
        if(category==Embedding)return id.StartsWith("gemini-")?"Gemini Embedding":"Embedding אחר";
        if(category==Live)return id.Contains("gemini")?"Gemini Live":"Live אחר";
        if(category==Audio)
        {
            if(id.Contains("transcribe"))return "Gemini Transcribe";
            if(id.Contains("tts"))return "Gemini TTS";
            return id.Contains("gemini")?"Gemini Audio":"משפחת אודיו אחרת";
        }
        if(category==General)
        {
            if(id.Contains("flash-lite"))return "Gemini Flash-Lite";
            if(id.Contains("pro"))return "Gemini Pro";
            if(id.Contains("flash"))return "Gemini Flash";
            return "Gemini";
        }
        return item.Name.Split(' ',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ??item.Id.Split('-',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ??Unclassified;
    }

    public static bool SupportsChat(ModelInfo item)=>item.Methods.Any(x=>x.Equals("generateContent",StringComparison.OrdinalIgnoreCase));

    public static int CategoryOrder(string category)=>category switch
    {
        General=>0,Image=>1,Video=>2,Audio=>3,Live=>4,Embedding=>5,Unclassified=>int.MaxValue,_=>int.MaxValue-1
    };

    public static string ModelLabel(ModelInfo item)
    {
        var stage=item.Id.Contains("preview",StringComparison.OrdinalIgnoreCase)?"Preview":item.Id.Contains("exp",StringComparison.OrdinalIgnoreCase)?"Experimental":item.Id.Contains("latest",StringComparison.OrdinalIgnoreCase)?"Latest":"Stable";
        return $"{item.Name} · {stage}";
    }
}
