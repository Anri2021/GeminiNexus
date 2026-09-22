using GeminiNexus.UI.Pages;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace GeminiNexus.Client.Maui;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
        ServerAddress.Text=Preferences.Default.Get("NexusServerUrl","https://localhost:5000/");
    }
    private void Connect(object? sender,EventArgs e)
    {
        if(!Uri.TryCreate(ServerAddress.Text?.Trim(),UriKind.Absolute,out var uri)||uri.Scheme!="https"||!string.IsNullOrEmpty(uri.UserInfo)||!string.IsNullOrEmpty(uri.Query)||!string.IsNullOrEmpty(uri.Fragment)||uri.AbsolutePath!="/")
        { ValidationError.Text="יש להזין כתובת HTTPS של השרת, ללא נתיב, פרטי התחברות או פרמטרים.";return; }
        Preferences.Default.Set("NexusServerUrl",uri.AbsoluteUri);
        var view=new BlazorWebView { HostPage="wwwroot/index.html" };
        view.RootComponents.Add(new RootComponent { Selector="#app",ComponentType=typeof(Workspace) });
        Content=view;
    }
}
