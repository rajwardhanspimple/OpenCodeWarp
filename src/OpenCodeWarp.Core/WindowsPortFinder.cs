using System.Net;
using System.Runtime.InteropServices;

namespace OpenCodeWarp;

/// <summary>
/// Finds TCP listeners owned by an OpenCode process tree. The standalone TUI starts
/// its private v2 server with port 0, so the port is assigned by Windows and is not
/// present in the command line.
/// </summary>
internal static class WindowsPortFinder
{
    private const uint Th32CsSnapProcess = 0x00000002;
    private const int ErrorInsufficientBuffer = 122;
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint Loopback = 0x0100007F;
    private const uint AnyAddress = 0x00000000;

    public static IReadOnlyList<int> GetLoopbackListenerPorts(int rootPid)
    {
        var pids = GetDescendantProcessIds(rootPid);
        pids.Add(rootPid);

        var ports = new List<int>();
        foreach (var row in EnumerateTcpListeners())
        {
            if (!pids.Contains((int)row.OwningPid))
                continue;
            if (row.LocalAddress != Loopback && row.LocalAddress != AnyAddress)
                continue;

            var port = (ushort)IPAddress.NetworkToHostOrder((short)(row.LocalPort & 0xFFFF));
            if (port > 0 && !ports.Contains(port))
                ports.Add(port);
        }

        return ports;
    }

    private static HashSet<int> GetDescendantProcessIds(int rootPid)
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return new HashSet<int>();

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                }
                while (Process32Next(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var result = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var pair in parents)
            {
                if (pair.Value != current || !result.Add(pair.Key))
                    continue;
                queue.Enqueue(pair.Key);
            }
        }

        return result;
    }

    private static List<MIB_TCPROW_OWNER_PID> EnumerateTcpListeners()
    {
        var size = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (result != ErrorInsufficientBuffer || size <= 0)
            return new List<MIB_TCPROW_OWNER_PID>();

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(
                buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            if (result != 0)
                return new List<MIB_TCPROW_OWNER_PID>();

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            var rows = new List<MIB_TCPROW_OWNER_PID>(count);
            var offset = 4;
            for (var i = 0; i < count; i++)
            {
                rows.Add(Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(
                    IntPtr.Add(buffer, offset + i * rowSize)));
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table,
        ref int size,
        bool order,
        int addressFamily,
        int tableClass,
        uint reserved);
}
