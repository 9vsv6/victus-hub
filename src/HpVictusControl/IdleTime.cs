using System.Runtime.InteropServices;

namespace HpVictusControl;

/// <summary>How long it's been since the last keyboard or mouse input, system-wide.</summary>
public static class IdleTime {

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>Time since the last input; zero if Windows can't say, so nothing treats the user as away.</summary>
    public static TimeSpan Current {
        get {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
            // Both counters are milliseconds since boot and wrap together every 49.7 days, so the
            // unsigned difference stays right across the wrap.
            uint elapsed = unchecked((uint)Environment.TickCount - info.Time);
            return TimeSpan.FromMilliseconds(elapsed);
        }
    }
}
