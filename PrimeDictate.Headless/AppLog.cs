namespace PrimeDictate;

internal enum AppLogLevel
{
    Info,
    Error
}

internal static class AppLog
{
    public static void Info(string message, Guid? threadId = null) =>
        Write(AppLogLevel.Info, message, threadId);

    public static void Error(string message, Guid? threadId = null) =>
        Write(AppLogLevel.Error, message, threadId);

    private static void Write(AppLogLevel level, string message, Guid? threadId)
    {
        var prefix = level == AppLogLevel.Error ? "ERROR" : "INFO ";
        var thread = threadId is null ? string.Empty : $" [{threadId}]";
        Console.Error.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {prefix}{thread} {message}");
    }
}
