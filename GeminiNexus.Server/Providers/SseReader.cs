using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using GeminiNexus.Server.Domain;

namespace GeminiNexus.Server.Providers;

/// <summary>Limits bytes before materializing an untrusted provider line.</summary>
public static class SseReader
{
    public static async IAsyncEnumerable<string> Read(Stream stream, int maxBytes,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var reader=PipeReader.Create(stream,new StreamPipeReaderOptions(leaveOpen:true));
        var data=new StringBuilder();var bytes=0;
        try
        {
            while(true)
            {
                var result=await reader.ReadAsync(ct);var buffer=result.Buffer;
                while(buffer.PositionOf((byte)'\n') is { } end)
                {
                    var line=buffer.Slice(0,end);
                    if(line.Length>maxBytes)throw new WorkspaceException(502,"אירוע ספק גדול מדי");
                    var value=Encoding.UTF8.GetString(line).TrimEnd('\r');
                    buffer=buffer.Slice(buffer.GetPosition(1,end));
                    if(value.StartsWith("data:",StringComparison.Ordinal))
                    {
                        var payload=value.AsSpan(5);if(payload.StartsWith(" "))payload=payload[1..];
                        bytes+=Encoding.UTF8.GetByteCount(payload)+1;
                        if(bytes>maxBytes)throw new WorkspaceException(502,"אירוע ספק גדול מדי");
                        if(data.Length>0)data.Append('\n');data.Append(payload);
                    }
                    else if(value.Length==0&&data.Length>0)
                    {
                        var json=data.ToString();data.Clear();bytes=0;
                        if(json=="[DONE]")yield break;
                        yield return json;
                    }
                }
                if(buffer.Length>maxBytes)throw new WorkspaceException(502,"שורת ספק גדולה מדי");
                reader.AdvanceTo(buffer.Start,buffer.End);
                if(result.IsCompleted)
                {
                    // An SSE event is committed only by its terminating blank line.
                    // A partial event on EOF is not a valid completed response.
                    break;
                }
            }
        }
        finally{await reader.CompleteAsync();}
    }
}
