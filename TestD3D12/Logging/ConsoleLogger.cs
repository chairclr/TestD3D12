namespace TestD3D12.Logging;

internal class ConsoleLogger : ILogger
{
    private static readonly Lock _consoleLogLock = new();

    public void Log(LogLevel level, string? message)
    {
        ConsoleColor color = GetColorForLogLevel(level);

        string formattedMessage = $"[{DateTime.Now:HH:mm:ss}.{DateTime.Now:fff}] [{level}]{new string(' ', 11 - level.ToString().Length)}: {message}";

        using (_consoleLogLock.EnterScope())
        {
            Console.ForegroundColor = color;
            Console.WriteLine(formattedMessage);
            Console.ResetColor();
        }
    }

    public void Log(LogLevel level, Exception? exception)
    {
        ConsoleColor color = GetColorForLogLevel(level);

        string formattedMessage = $"[{DateTime.Now:HH:mm:ss}.{DateTime.Now:fff}] [{level}]{new string(' ', 11 - level.ToString().Length)}  {exception}";

        using (_consoleLogLock.EnterScope())
        {
            Console.ForegroundColor = color;
            Console.WriteLine(formattedMessage);
            Console.ResetColor();
        }
    }

    private static ConsoleColor GetColorForLogLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Information => ConsoleColor.Gray,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Critical => ConsoleColor.Red,
            _ => ConsoleColor.Gray
        };
    }
}
