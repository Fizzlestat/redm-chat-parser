using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RedMChatParser;

public readonly record struct GamePresence(bool ClientRunning, bool GameRunning);

public static class GameProcess
{
    public static bool IsClientRunning() => Query().ClientRunning;

    public static bool IsGameRunning() => Query().GameRunning;

    public static GamePresence Query()
    {
        GamePresence presence = new GamePresence(false, false);
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return presence;
        }

        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    string name = process.ProcessName;
                    bool client = IsClientProcessName(name);
                    bool game = IsGameProcessName(name);
                    if (client || game)
                        presence = new GamePresence(presence.ClientRunning || client, presence.GameRunning || game);
                }
                catch
                {
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }

        return presence;
    }

    public static bool IsRedMFocused()
    {
        nint handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
            return false;

        _ = GetWindowThreadProcessId(handle, out uint processId);
        if (processId == 0)
            return false;

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string name = process.ProcessName;
            return IsClientProcessName(name);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsClientProcessName(string name)
    {
        return name.Equals("RedM", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("RedM_", StringComparison.OrdinalIgnoreCase);
    }

    internal static HashSet<int> ClientProcessIds()
    {
        HashSet<int> ids = new HashSet<int>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return ids;
        }

        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    if (IsClientProcessName(process.ProcessName))
                        ids.Add(process.Id);
                }
                catch
                {
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }

        return ids;
    }

    private static bool IsGameProcessName(string name)
    {
        return name.StartsWith("RedM", StringComparison.OrdinalIgnoreCase)
            && name.Contains("GTAProcess", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
