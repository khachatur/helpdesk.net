using DbUp.Engine.Output;

namespace Helpdesk.Api.Migrations;

/// <summary>Sends DbUp's output to the application log.</summary>
public sealed class SerilogUpgradeLog(ILogger logger) : IUpgradeLog
{
    public void LogTrace(string format, params object[] args) => logger.LogTrace(format, args);

    public void LogDebug(string format, params object[] args) => logger.LogDebug(format, args);

    public void LogInformation(string format, params object[] args) => logger.LogInformation(format, args);

    public void LogWarning(string format, params object[] args) => logger.LogWarning(format, args);

    public void LogError(string format, params object[] args) => logger.LogError(format, args);

    public void LogError(Exception ex, string format, params object[] args) => logger.LogError(ex, format, args);
}
