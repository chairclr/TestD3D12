using System.Diagnostics;

namespace TestD3D12.Logging;

public interface ILogger
{
    void Log(LogLevel level, string? message);

    void Log(LogLevel level, Exception? exception);

    void LogInformation(string? message)
    {
        Log(LogLevel.Information, message);
    }

    void LogInformation(Exception? exception)
    {
        Log(LogLevel.Information, exception);
    }

    void LogWarning(string? message)
    {
        Log(LogLevel.Warning, message);
    }

    void LogWarning(Exception? exception)
    {
        Log(LogLevel.Warning, exception);
    }

    void LogCritical(string? message)
    {
        Log(LogLevel.Critical, message);
    }

    void LogCritical(string? message, StackTrace stackTrace)
    {
        Log(LogLevel.Critical, message);
        Log(LogLevel.Critical, $"Stack trace:\n{stackTrace}");
    }

    void LogCritical(Exception? exception)
    {
        Log(LogLevel.Critical, exception);
    }
}
