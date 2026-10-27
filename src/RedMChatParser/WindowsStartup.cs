using Microsoft.Win32;

namespace RedMChatParser;

internal static class WindowsStartup
{
    private const string ValueName = "RedMChatParser";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled, bool minimized)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Windows startup settings could not be opened.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("The parser path could not be found.");

        key.SetValue(ValueName, minimized ? $"\"{path}\" --tray" : $"\"{path}\"");
    }
}
