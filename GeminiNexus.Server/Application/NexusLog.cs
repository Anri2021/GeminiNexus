namespace GeminiNexus.Server.Application;

public static partial class NexusLog
{
    [LoggerMessage(Level=LogLevel.Error, Message="Request {TraceId} failed")]
    public static partial void RequestFailed(ILogger logger, Exception exception, string traceId);
}
