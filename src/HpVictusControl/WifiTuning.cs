using System.Runtime.InteropServices;

namespace HpVictusControl;

/// <summary>
/// Game-time Wi-Fi tuning. Windows' WLAN service scans for other networks in the background every
/// minute or so, even while connected, and each scan briefly takes the radio off the connected channel —
/// the classic cause of a lag spike that repeats like clockwork in online games. This turns background
/// scanning off, and turns on "media streaming mode" (which asks the driver for steady low latency), on
/// every connected Wi-Fi interface for as long as a game runs.
///
/// Windows only keeps these two settings while the handle that set them stays open, so a handle is held
/// for the whole game. That's a useful safety net: if this app exits or crashes, Windows closes the handle
/// and puts both settings back by itself — nothing can be left switched off.
/// </summary>
public sealed class WifiTuning : IDisposable {

    private const int WlanApiVersion = 2;
    private const int OpcodeBackgroundScanEnabled = 2;
    private const int OpcodeMediaStreamingMode = 3;
    private const int WlanInterfaceStateConnected = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strInterfaceDescription;
        public int isState;
    }

    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(int clientVersion, IntPtr reserved, out int negotiatedVersion, out IntPtr clientHandle);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    [DllImport("wlanapi.dll")] private static extern int WlanSetInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, int dataSize, ref int data, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved, out int dataSize, out IntPtr data, IntPtr opcodeValueType);

    // Open only while tuning is active; closing it is what hands the settings back to Windows.
    private IntPtr _client = IntPtr.Zero;

    public bool IsActive => _client != IntPtr.Zero;

    /// <summary>Connected Wi-Fi adapters and their current scan / streaming settings, as Windows sees them.</summary>
    public static List<(string Adapter, bool? BackgroundScan, bool? MediaStreaming)> Describe() {
        var result = new List<(string, bool?, bool?)>();
        IntPtr client = Open();
        if (client == IntPtr.Zero) return result;
        try {
            foreach ((Guid guid, string description) in ConnectedInterfaces(client))
                result.Add((description, Query(client, guid, OpcodeBackgroundScanEnabled), Query(client, guid, OpcodeMediaStreamingMode)));
        } finally {
            WlanCloseHandle(client, IntPtr.Zero);
        }
        return result;
    }

    /// <summary>Tunes every connected Wi-Fi adapter. Returns how many took the change (0 = no Wi-Fi in use).</summary>
    public int Start() {
        if (IsActive) return 0;
        IntPtr client = Open();
        if (client == IntPtr.Zero) return 0;

        int tuned = 0;
        try {
            foreach ((Guid guid, _) in ConnectedInterfaces(client)) {
                Guid id = guid;
                int off = 0, on = 1;
                bool scanOff = WlanSetInterface(client, ref id, OpcodeBackgroundScanEnabled, sizeof(int), ref off, IntPtr.Zero) == 0;
                bool streamingOn = WlanSetInterface(client, ref id, OpcodeMediaStreamingMode, sizeof(int), ref on, IntPtr.Zero) == 0;
                if (scanOff || streamingOn) tuned++;
            }
        } catch {
            tuned = 0;
        }

        if (tuned == 0) {
            WlanCloseHandle(client, IntPtr.Zero);
            return 0;
        }
        _client = client;
        return tuned;
    }

    public void Stop() {
        IntPtr client = _client;
        if (client == IntPtr.Zero) return;
        _client = IntPtr.Zero;
        try {
            // Put both back explicitly as well, rather than relying only on the handle closing.
            foreach ((Guid guid, _) in ConnectedInterfaces(client)) {
                Guid id = guid;
                int on = 1, off = 0;
                WlanSetInterface(client, ref id, OpcodeBackgroundScanEnabled, sizeof(int), ref on, IntPtr.Zero);
                WlanSetInterface(client, ref id, OpcodeMediaStreamingMode, sizeof(int), ref off, IntPtr.Zero);
            }
        } catch {
            // Closing the handle below restores them anyway.
        } finally {
            WlanCloseHandle(client, IntPtr.Zero);
        }
    }

    public void Dispose() => Stop();

    private static IntPtr Open() {
        try {
            return WlanOpenHandle(WlanApiVersion, IntPtr.Zero, out _, out IntPtr client) == 0 ? client : IntPtr.Zero;
        } catch {
            return IntPtr.Zero; // No WLAN service on this PC.
        }
    }

    private static List<(Guid Guid, string Description)> ConnectedInterfaces(IntPtr client) {
        var interfaces = new List<(Guid, string)>();
        if (WlanEnumInterfaces(client, IntPtr.Zero, out IntPtr list) != 0) return interfaces;
        try {
            int count = Marshal.ReadInt32(list);
            int itemSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
            for (int i = 0; i < count; i++) {
                // WLAN_INTERFACE_INFO_LIST: dwNumberOfItems, dwIndex, then the items.
                var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(list + 8 + i * itemSize);
                if (info.isState == WlanInterfaceStateConnected) interfaces.Add((info.InterfaceGuid, info.strInterfaceDescription));
            }
        } finally {
            WlanFreeMemory(list);
        }
        return interfaces;
    }

    private static bool? Query(IntPtr client, Guid guid, int opcode) {
        if (WlanQueryInterface(client, ref guid, opcode, IntPtr.Zero, out int size, out IntPtr data, IntPtr.Zero) != 0 || data == IntPtr.Zero) return null;
        try {
            return size >= sizeof(int) ? Marshal.ReadInt32(data) != 0 : null;
        } finally {
            WlanFreeMemory(data);
        }
    }
}
