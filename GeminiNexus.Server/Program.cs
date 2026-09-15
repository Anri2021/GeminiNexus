using FastEndpoints;
using GeminiNexus.Server.Infrastructure;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddHttpClient("GeminiClient", c =>
{
    c.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddFastEndpoints();

var app = builder.Build();

// אתחול DB
var db = app.Services.GetRequiredService<DatabaseService>();
await db.InitializeAsync();

app.UseFastEndpoints();
app.Run();