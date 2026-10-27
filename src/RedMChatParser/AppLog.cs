namespace RedMChatParser;

public static class AppLog
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null)
    {
        if (exception == null)
        {
            Write("ERROR", message);
            return;
        }

        Write("ERROR", message + Environment.NewLine + exception);
    }

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Directory);
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}";
            lock (Gate)
                File.AppendAllText(AppPaths.LogPath, line);
        }
        catch
        {
        }
    }
}
