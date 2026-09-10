using System.Runtime.InteropServices;

namespace HpVictusControl;

/// <summary>Reads system-wide physical RAM usage via the Win32 GlobalMemoryStatusEx API.</summary>
public static class MemoryInfo {

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static bool TryGetUsage(out double usedGb, out double totalGb) {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) {
            usedGb = 0;
            totalGb = 0;
            return false;
        }

        totalGb = status.ullTotalPhys / 1024.0 / 1024.0 / 1024.0;
        double availGb = status.ullAvailPhys / 1024.0 / 1024.0 / 1024.0;
        usedGb = totalGb - availGb;
        return true;
    }
}
