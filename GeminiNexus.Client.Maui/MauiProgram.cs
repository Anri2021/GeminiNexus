using Microsoft.Extensions.Logging;
using GeminiNexus.UI.Services;

namespace GeminiNexus.Client.Maui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddScoped(_ => new HttpClient(new HttpClientHandler { UseCookies=true, CookieContainer=new System.Net.CookieContainer(), AllowAutoRedirect=false })
        { BaseAddress=new Uri(Preferences.Default.Get("NexusServerUrl","https://localhost/")), Timeout=Timeout.InfiniteTimeSpan });
        builder.Services.AddScoped<WorkspaceClient>();
#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
