using FastEndpoints;
using GeminiNexus.Server.Infrastructure;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddCors(options => options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

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

app.UseCors();
app.UseFastEndpoints();
app.Run();