using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using GeminiNexus.Client.Wasm;
using GeminiNexus.UI.Services;
var builder=WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddScoped(_=>new HttpClient{BaseAddress=new Uri(builder.HostEnvironment.BaseAddress)});
builder.Services.AddScoped<WorkspaceClient>();
await builder.Build().RunAsync();
