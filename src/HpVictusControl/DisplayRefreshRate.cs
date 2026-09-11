using System.Runtime.InteropServices;

namespace HpVictusControl;

/// <summary>
/// Reads and sets the primary display's refresh rate via the Win32 display settings API —
/// the same mechanism Windows' own Display Settings page uses. Used to bump the panel to its
/// highest supported refresh rate in Performance mode, and back down for Balanced/Cool.
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
