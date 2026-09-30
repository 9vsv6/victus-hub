using System.Runtime.InteropServices;

namespace HpVictusControl;

/// <summary>
/// Empties Windows' standby memory list: file data kept in RAM in case it's needed again. Windows
/// normally gives it up on demand, but in some games the hand-over causes stutter when memory is
/// nearly full. This is the same call RAMMap's "Empty Standby List" makes. It needs admin rights and
/// the "profile single process" privilege, which an admin token has but doesn't switch on by itself.
/// </summary>
public static class StandbyMemory {

    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;
    private const string ProfileSingleProcessPrivilege = "SeProfileSingleProcessPrivilege";

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Returns true when Windows emptied the list.</summary>
    public static bool Purge() {
        try {
            if (!EnablePrivilege(ProfileSingleProcessPrivilege)) return false;
            int command = MemoryPurgeStandbyList;
            return NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int)) == 0;
        } catch {
            return false;
        }
    }

    private static bool EnablePrivilege(string name) {
        const uint AdjustPrivileges = 0x20, Query = 0x8, Enabled = 0x2;
        if (!OpenProcessToken(GetCurrentProcess(), AdjustPrivileges | Query, out IntPtr token)) return false;
        try {
            if (!LookupPrivilegeValue(null, name, out Luid luid)) return false;
            var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = Enabled };
            return AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero)
                && Marshal.GetLastWin32Error() == 0; // ERROR_NOT_ALL_ASSIGNED means the token lacks it
        } finally {
            CloseHandle(token);
        }
    }
}
