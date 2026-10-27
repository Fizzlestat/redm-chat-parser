using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RedMChatParser;

internal static class InspectorLocator
{
    private const int AfInet = 2;
    private const uint Loopback = 0x0100007F;

    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromMilliseconds(900)
    };

    public static async Task<int?> FindAsync(int preferred, CancellationToken cancellationToken)
    {
        List<int> ports = CandidatePorts(preferred);
        List<int> found = new List<int>();
        await Parallel.ForEachAsync(ports, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        }, async (port, token) =>
        {
            if (!await HostsRootUiAsync(port, token).ConfigureAwait(false))
                return;

            lock (found)
                found.Add(port);
        }).ConfigureAwait(false);

        if (found.Count == 0)
            return null;

        if (found.Contains(preferred))
            return preferred;

        return found[0];
    }

    internal static int PortFromNetworkOrder(uint value)
    {
        return ((int)(value & 0xFF) << 8) | (int)((value >> 8) & 0xFF);
    }

    internal static bool ContainsRootUi(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (JsonElement target in document.RootElement.EnumerateArray())
            {
                if (target.TryGetProperty("url", out JsonElement url) &&
                    string.Equals(url.GetString(), NuiChatClient.RootUiUrl, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    internal static bool IsOwnedByClient(int port)
    {
        if (port is < 1 or > 65535)
            return false;

        HashSet<int> clientIds = GameProcess.ClientProcessIds();
        if (clientIds.Count == 0)
            return false;

        foreach ((int pid, int listening, uint address) in ListeningPorts())
        {
            if (listening != port || !clientIds.Contains(pid))
                continue;
            if (address == 0 || address == Loopback)
                return true;
        }

        return false;
    }

    private static List<int> CandidatePorts(int preferred)
    {
        HashSet<int> clientIds = GameProcess.ClientProcessIds();
        if (clientIds.Count == 0)
            return [];

        List<int> ports = new List<int>();
        foreach ((int pid, int port, uint address) in ListeningPorts())
        {
            if (port is < 1 or > 65535)
                continue;
            if (address != 0 && address != Loopback)
                continue;
            if (!clientIds.Contains(pid) || ports.Contains(port))
                continue;

            ports.Add(port);
        }

        if (ports.Contains(preferred))
        {
            ports.Remove(preferred);
            ports.Insert(0, preferred);
        }

        return ports;
    }

    private static async Task<bool> HostsRootUiAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
            string json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json", timeout.Token).ConfigureAwait(false);
            return ContainsRootUi(json);
        }
        catch (Exception exception) when (exception is HttpRequestException or SocketException or TaskCanceledException or JsonException or IOException)
        {
            return false;
        }
    }

    private static List<(int Pid, int Port, uint Address)> ListeningPorts()
    {
        int size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, TcpTableClass.OwnerPidListener, 0);
        if (size <= 0)
            return [];

        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint result = GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableClass.OwnerPidListener, 0);
            if (result != 0)
                return [];

            int count = Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<TcpRow>();
            List<(int, int, uint)> rows = new List<(int, int, uint)>(count);
            int offset = 4;
            for (int i = 0; i < count; i++)
            {
                TcpRow row = Marshal.PtrToStructure<TcpRow>(buffer + offset);
                offset += rowSize;
                rows.Add((row.OwningPid, PortFromNetworkOrder(row.LocalPort), row.LocalAddr));
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private enum TcpTableClass
    {
        OwnerPidListener = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public int OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table,
        ref int size,
        bool order,
        int ipVersion,
        TcpTableClass tableClass,
        uint reserved);
}
