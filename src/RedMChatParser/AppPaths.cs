namespace RedMChatParser;

public static class AppPaths
{
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedMChatParser");

    public static string ConfigPath => Path.Combine(Directory, "config.json");

    public static string JournalPath => Path.Combine(Directory, "current-session.txt");

    public static string LogPath => Path.Combine(Directory, "app.log");

    public static string DefaultBackupDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "RedM Chat Logs");
}
