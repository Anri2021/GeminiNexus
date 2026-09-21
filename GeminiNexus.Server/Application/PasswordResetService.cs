using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using GeminiNexus.Server.Domain;
using GeminiNexus.Server.Infrastructure;
using GeminiNexus.Shared;

namespace GeminiNexus.Server.Application;

public sealed class PasswordResetService(ServerOptions options,WorkspaceStore store,IHostEnvironment environment)
{
    public async Task Request(string email,CancellationToken ct)
    {
        var started=Stopwatch.GetTimestamp();
        if(string.IsNullOrWhiteSpace(options.PasswordResetPublicBaseUrl))throw new WorkspaceException(503,"שירות איפוס הסיסמה אינו מוגדר");
        if(!Uri.TryCreate(options.PasswordResetPublicBaseUrl,UriKind.Absolute,out var publicBase)||publicBase.Scheme!="https"&&!(environment.IsEnvironment("Testing")&&publicBase.IsLoopback))throw new WorkspaceException(503,"כתובת שירות האיפוס אינה תקינה");
        var pickup=!string.IsNullOrWhiteSpace(options.PasswordResetPickupDirectory);
        if(pickup&&!environment.IsDevelopment()&&!environment.IsEnvironment("Testing"))throw new WorkspaceException(503,"Pickup של איפוס סיסמה מותר רק בסביבת פיתוח או בדיקות");
        if(!pickup&&(string.IsNullOrWhiteSpace(options.SmtpHost)||string.IsNullOrWhiteSpace(options.SmtpFrom)))throw new WorkspaceException(503,"שירות הדוא״ל לאיפוס סיסמה אינו מוגדר");
        var reset=await store.CreatePasswordReset(email,ct);
        if(reset is not null)
        {
            var link=new Uri(publicBase,"?resetToken="+Uri.EscapeDataString(reset.Value.Token)).ToString();
            if(pickup)
            {
                var directory=Path.GetFullPath(options.PasswordResetPickupDirectory);Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory,Guid.NewGuid().ToString("N")+".txt"),$"To: {reset.Value.Email}\nSubject: Gemini Nexus password reset\n\n{link}\n",ct);
            }
            else
            {
                using var message=new MailMessage(options.SmtpFrom,reset.Value.Email,"Gemini Nexus password reset",$"A password reset was requested for your Gemini Nexus account. The link expires in 30 minutes:\n\n{link}\n\nIf you did not request this, ignore this message.");
                using var smtp=new SmtpClient(options.SmtpHost,options.SmtpPort){EnableSsl=true};
                if(options.SmtpUser.Length>0)smtp.Credentials=new NetworkCredential(options.SmtpUser,options.SmtpPassword);
                await smtp.SendMailAsync(message,ct);
            }
        }
        var remaining=TimeSpan.FromMilliseconds(250)-Stopwatch.GetElapsedTime(started);if(remaining>TimeSpan.Zero)await Task.Delay(remaining,ct);
    }

    public Task Confirm(PasswordResetConfirm request,CancellationToken ct)=>store.ResetPassword(request.Token,request.NewPassword,ct);
}
