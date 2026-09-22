using System.Runtime.InteropServices;

namespace HpVictusControl;

/// <summary>
/// Reads and sets the primary display's refresh rate via the Win32 display settings API —
/// the same mechanism Windows' own Display Settings page uses. Used to apply the refresh rate
/// chosen for each performance mode.
/// </summary>
public static class DisplayRefreshRate {

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DM_DISPLAYFREQUENCY = 0x400000;
    private const int CDS_UPDATEREGISTRY = 0x01;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsW(ref DEVMODE devMode, int flags);

    private static DEVMODE NewDevMode() => new() {
        dmDeviceName = "",
        dmFormName = "",
        dmSize = (short)Marshal.SizeOf<DEVMODE>()
    };

    /// <summary>Highest refresh rate (Hz) available at the display's current resolution.</summary>
    public static int? GetMaxRefreshRate() {
        DEVMODE current = NewDevMode();
        if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref current)) return null;

        int max = current.dmDisplayFrequency;
        int modeNum = 0;
        DEVMODE dm = NewDevMode();
        while (EnumDisplaySettingsW(null, modeNum, ref dm)) {
            if (dm.dmPelsWidth == current.dmPelsWidth && dm.dmPelsHeight == current.dmPelsHeight
                && dm.dmDisplayFrequency > max) {
                max = dm.dmDisplayFrequency;
            }
            modeNum++;
            dm = NewDevMode();
        }
        return max > 0 ? max : null;
    }

    /// <summary>All distinct refresh rates (Hz) available at the display's current resolution, ascending.</summary>
    public static List<int> GetSupportedRefreshRates() {
        var rates = new List<int>();

        DEVMODE current = NewDevMode();
        if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref current)) return rates;

        int modeNum = 0;
        DEVMODE dm = NewDevMode();
        while (EnumDisplaySettingsW(null, modeNum, ref dm)) {
            if (dm.dmPelsWidth == current.dmPelsWidth && dm.dmPelsHeight == current.dmPelsHeight
                && dm.dmDisplayFrequency > 0 && !rates.Contains(dm.dmDisplayFrequency)) {
                rates.Add(dm.dmDisplayFrequency);
            }
            modeNum++;
            dm = NewDevMode();
        }
        rates.Sort();
        return rates;
    }

    private const int DM_PELSWIDTH = 0x80000;
    private const int DM_PELSHEIGHT = 0x100000;

    public static (int Width, int Height)? GetCurrentResolution() {
        DEVMODE current = NewDevMode();
        return EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref current) ? (current.dmPelsWidth, current.dmPelsHeight) : null;
    }

    /// <summary>Every distinct size the display reports, largest first.</summary>
    public static List<(int Width, int Height)> GetAllResolutions() {
        var modes = new List<(int Width, int Height)>();
        int modeNum = 0;
        DEVMODE dm = NewDevMode();
        while (EnumDisplaySettingsW(null, modeNum, ref dm)) {
            if (dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0 && !modes.Contains((dm.dmPelsWidth, dm.dmPelsHeight)))
                modes.Add((dm.dmPelsWidth, dm.dmPelsHeight));
            modeNum++;
            dm = NewDevMode();
        }
        return modes.OrderByDescending(m => (long)m.Width * m.Height).ToList();
    }

    /// <summary>
    /// True when the display can actually run this size at some refresh rate. Windows refuses anything
    /// else, so a typed resolution is checked against this before it's stored on a game.
    /// </summary>
    public static bool IsResolutionSupported(int width, int height) {
        int modeNum = 0;
        DEVMODE dm = NewDevMode();
        while (EnumDisplaySettingsW(null, modeNum, ref dm)) {
            if (dm.dmPelsWidth == width && dm.dmPelsHeight == height) return true;
            modeNum++;
            dm = NewDevMode();
        }
        return false;
    }

    /// <summary>
    /// Changes the resolution, keeping the current refresh rate when the new size supports it (otherwise
    /// the highest it does). Best-effort; returns false if Windows refuses.
    /// </summary>
    public static bool SetResolution(int width, int height, bool persist = false) {
        DEVMODE current = NewDevMode();
        if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref current)) return false;
        if (current.dmPelsWidth == width && current.dmPelsHeight == height) return true;

        int bestHz = 0;
        int modeNum = 0;
        DEVMODE dm = NewDevMode();
        while (EnumDisplaySettingsW(null, modeNum, ref dm)) {
            if (dm.dmPelsWidth == width && dm.dmPelsHeight == height) {
                if (dm.dmDisplayFrequency == current.dmDisplayFrequency) { bestHz = dm.dmDisplayFrequency; break; }
                bestHz = Math.Max(bestHz, dm.dmDisplayFrequency);
            }
            modeNum++;
            dm = NewDevMode();
        }
        if (bestHz == 0) return false;

        current.dmPelsWidth = width;
        current.dmPelsHeight = height;
        current.dmDisplayFrequency = bestHz;
        current.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        // A game's lowered resolution isn't saved to the registry, so it can't survive a crash or restart;
        // putting the original back is saved, in case anything wrote the lowered one there meanwhile.
        return ChangeDisplaySettingsW(ref current, persist ? CDS_UPDATEREGISTRY : 0) == DISP_CHANGE_SUCCESSFUL;
    }

    /// <summary>Sets the display's refresh rate, keeping its current resolution/color depth. Best-effort.</summary>
    public static bool SetRefreshRate(int hz) {
        DEVMODE dm = NewDevMode();
        if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref dm)) return false;
        if (dm.dmDisplayFrequency == hz) return true;

        dm.dmDisplayFrequency = hz;
        dm.dmFields = DM_DISPLAYFREQUENCY;
        return ChangeDisplaySettingsW(ref dm, CDS_UPDATEREGISTRY) == DISP_CHANGE_SUCCESSFUL;
    }
}
