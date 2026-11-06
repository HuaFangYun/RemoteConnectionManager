using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RemoteConnectionManager.Plugins.ServerChecks;

/// <summary>
/// Lists user sessions on a remote host through the Remote Desktop Services API (wtsapi32), the
/// same calls quser and qwinsta make. Runs as the logged on user over SMB named pipes (port 445),
/// so it needs no WinRM. Read only: nothing is logged off or messaged.
/// </summary>
internal static class WtsSessionReader
{
    private const int SmbPort = 445;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);

    public static SessionReport Read(string host, CancellationToken ct)
    {
        // An unreachable host makes WTSEnumerateSessions wait out the SMB timeout (20 seconds or
        // more), so a short probe answers first.
        if (!PortOpen(host, SmbPort, ct))
            throw new PluginException($"Could not reach {host} on port {SmbPort} to read who is signed in. Check the name resolves, the host is on, and file sharing (SMB) is open from this PC.");

        ct.ThrowIfCancellationRequested();
        var server = Native.WTSOpenServerW(host);
        if (server == IntPtr.Zero) throw Failure(host, Marshal.GetLastWin32Error());
        try
        {
            if (!Native.WTSEnumerateSessionsW(server, 0, 1, out var buffer, out var count))
                throw Failure(host, Marshal.GetLastWin32Error());

            var ids = new List<int>(count);
            try
            {
                var size = Marshal.SizeOf<Native.WTS_SESSION_INFO>();
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<Native.WTS_SESSION_INFO>(buffer + i * size);
                    // Listeners and the services session have no user; skip them before asking for more.
                    if (info.State is Native.StateListen or Native.StateDown or Native.StateInit) continue;
                    ids.Add(info.SessionId);
                }
            }
            finally
            {
                Native.WTSFreeMemory(buffer);
            }

            var sessions = new List<UserSession>();
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                if (ReadSession(server, id) is { } session) sessions.Add(session);
            }
            return new SessionReport(host, DateTimeOffset.Now, sessions
                .OrderByDescending(s => s.IsActive)
                .ThenBy(s => s.Account, StringComparer.OrdinalIgnoreCase)
                .ToList());
        }
        finally
        {
            Native.WTSCloseServer(server);
        }
    }

    private static UserSession? ReadSession(IntPtr server, int id)
    {
        var user = QueryString(server, id, Native.WTSUserName);
        // A session can end between the enumerate and the query; it then has no user either.
        if (string.IsNullOrEmpty(user)) return null;
        var domain = QueryString(server, id, Native.WTSDomainName);
        var client = QueryString(server, id, Native.WTSClientName);

        if (!Native.WTSQuerySessionInformationW(server, id, Native.WTSSessionInfo, out var buffer, out _))
            return new UserSession(id, user, NullIfEmpty(domain), "Unknown", null, NullIfEmpty(client), null, null, null);
        try
        {
            var info = Marshal.PtrToStructure<Native.WTSINFOW>(buffer);
            return new UserSession(
                SessionId: id,
                UserName: user,
                Domain: NullIfEmpty(domain),
                State: StateName(info.State),
                SessionName: NullIfEmpty(info.WinStationName),
                ClientName: NullIfEmpty(client),
                LogonTime: FromFileTime(info.LogonTime),
                LastInputTime: FromFileTime(info.LastInputTime),
                DisconnectTime: FromFileTime(info.DisconnectTime));
        }
        finally
        {
            Native.WTSFreeMemory(buffer);
        }
    }

    private static string? QueryString(IntPtr server, int id, int infoClass)
    {
        if (!Native.WTSQuerySessionInformationW(server, id, infoClass, out var buffer, out _)) return null;
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Native.WTSFreeMemory(buffer);
        }
    }

    private static string StateName(int state) => state switch
    {
        Native.StateActive => "Active",
        Native.StateConnected => "Connected",
        Native.StateConnectQuery => "Connecting",
        Native.StateShadow => "Shadowing",
        Native.StateDisconnected => "Disconnected",
        Native.StateIdle => "Idle",
        Native.StateReset => "Resetting",
        _ => "Unknown",
    };

    private static DateTimeOffset? FromFileTime(long fileTime)
    {
        if (fileTime <= 0) return null;
        try
        {
            return DateTimeOffset.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static PluginException Failure(string host, int error) => error switch
    {
        5 => new PluginException($"Access denied reading sessions on {host}. Your account needs to be a local administrator there."),
        53 or 1722 or 1727 => new PluginException($"Could not read sessions on {host}: the Remote Desktop service did not answer. ({new Win32Exception(error).Message})"),
        _ => new PluginException($"Could not read sessions on {host}: {new Win32Exception(error).Message}"),
    };

    private static bool PortOpen(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            client.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static class Native
    {
        // WTS_CONNECTSTATE_CLASS
        public const int StateActive = 0, StateConnected = 1, StateConnectQuery = 2, StateShadow = 3,
            StateDisconnected = 4, StateIdle = 5, StateListen = 6, StateReset = 7, StateDown = 8, StateInit = 9;

        // WTS_INFO_CLASS
        public const int WTSUserName = 5, WTSDomainName = 7, WTSClientName = 10, WTSSessionInfo = 24;

        [StructLayout(LayoutKind.Sequential)]
        public struct WTS_SESSION_INFO
        {
            public int SessionId;
            public IntPtr pWinStationName;
            public int State;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WTSINFOW
        {
            public int State;
            public int SessionId;
            public int IncomingBytes;
            public int OutgoingBytes;
            public int IncomingFrames;
            public int OutgoingFrames;
            public int IncomingCompressedBytes;
            public int OutgoingCompressedBytes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
            public long ConnectTime;
            public long DisconnectTime;
            public long LastInputTime;
            public long LogonTime;
            public long CurrentTime;
        }

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr WTSOpenServerW(string pServerName);

        [DllImport("wtsapi32.dll")]
        public static extern void WTSCloseServer(IntPtr hServer);

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSEnumerateSessionsW(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

        [DllImport("wtsapi32.dll")]
        public static extern void WTSFreeMemory(IntPtr pMemory);
    }
}
