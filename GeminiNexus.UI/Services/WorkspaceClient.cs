using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using GeminiNexus.Shared;
namespace GeminiNexus.UI.Services;

public sealed class WorkspaceClient(HttpClient http)
{
    public async IAsyncEnumerable<RunEvent> Observe(string runId,long after,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,$"api/runs/{Uri.EscapeDataString(runId)}/events?view=text&after={after}");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        await Check(response,ct);
        await using var stream=await response.Content.ReadAsStreamAsync(ct);
        using var reader=new StreamReader(stream);
        while(await reader.ReadLineAsync(ct) is { } line)
        {
            if(line=="event: closed")yield break;
            if(!line.StartsWith("data: ",StringComparison.Ordinal))continue;
            var item=JsonSerializer.Deserialize(line.AsSpan(6),NexusJson.Default.RunEvent);
            if(item is not null)yield return item;
        }
    }
    public async Task<T> Get<T>(string path,JsonTypeInfo<T> type,CancellationToken ct=default)
    {using var response=await http.GetAsync("api/"+path,ct);await Check(response,ct);return (await response.Content.ReadFromJsonAsync(type,ct))!;}
    public async Task<TOut> Send<TIn,TOut>(HttpMethod method,string path,TIn value,JsonTypeInfo<TIn> input,JsonTypeInfo<TOut> output,CancellationToken ct=default)
    {using var request=new HttpRequestMessage(method,"api/"+path){Content=JsonContent.Create(value,input)};request.Headers.Add("X-Nexus-Request","1");using var response=await http.SendAsync(request,ct);await Check(response,ct);return (await response.Content.ReadFromJsonAsync(output,ct))!;}
    public async Task Save<T>(HttpMethod method,string path,T value,JsonTypeInfo<T> type,CancellationToken ct=default)
    {using var request=new HttpRequestMessage(method,"api/"+path){Content=JsonContent.Create(value,type)};request.Headers.Add("X-Nexus-Request","1");using var response=await http.SendAsync(request,ct);await Check(response,ct);}
    public async Task Action(HttpMethod method,string path,CancellationToken ct=default)
    {using var request=new HttpRequestMessage(method,"api/"+path);request.Headers.Add("X-Nexus-Request","1");using var response=await http.SendAsync(request,ct);await Check(response,ct);}
    private static async Task Check(HttpResponseMessage response,CancellationToken ct)
    {
        if(response.IsSuccessStatusCode)return;
        string? error=null;try{error=(await response.Content.ReadFromJsonAsync(NexusJson.Default.ApiError,ct))?.Error;}catch(JsonException){}
        throw new HttpRequestException(error??$"הבקשה נכשלה ({(int)response.StatusCode})",null,response.StatusCode);
    }
}
