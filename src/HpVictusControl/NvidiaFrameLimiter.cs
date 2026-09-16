using System.IO;
using System.Runtime.InteropServices;

namespace HpVictusControl;

/// <summary>
/// Per-game FPS cap, written straight into the NVIDIA driver's own profile database — the
/// same "Max Frame Rate" setting NVIDIA Control Panel writes, so the limiter is the driver's
/// and no overlay or extra process is needed. Talks to nvapi64.dll (ships with the display
/// driver) through its undocumented nvapi_QueryInterface entry point, the way every other
/// third-party NVIDIA tool does; the setting id used here (0x10835002, "Frame Rate Limiter")
/// was read back from the driver itself via NvAPI_DRS_EnumAvailableSettingIds rather than
/// guessed. Only applies to games actually rendering on the NVIDIA GPU.
/// </summary>
public static class NvidiaFrameLimiter {

    // Setting id and value encoding: the value is simply the FPS cap as a DWORD, 0 = no cap.
    private const uint FrameRateLimiterSettingId = 0x10835002;
    public const int MinFps = 20;
    public const int MaxFps = 1000;

    // NvAPI has no export table: every function is looked up by a fixed id through
    // nvapi_QueryInterface. These ids are stable across drivers and publicly known.
    private const uint IdInitialize = 0x0150E828;
    private const uint IdCreateSession = 0x0694D52E;
    private const uint IdDestroySession = 0xDAD9CFF8;
    private const uint IdLoadSettings = 0x375DBD6B;
    private const uint IdSaveSettings = 0xFCBC7E14;
    private const uint IdFindApplicationByName = 0xEEE566B2;
    private const uint IdCreateProfile = 0xCC176068;
    private const uint IdCreateApplication = 0x4347A9DE;
    private const uint IdSetSetting = 0x577DD202;
    private const uint IdGetSetting = 0x73BF8338;
    private const uint IdDeleteProfileSetting = 0xE4A26362;

    private const int NvApiOk = 0;

    /// <summary>What a unit of work did inside a driver session.</summary>
    private enum SessionResult { Failed, Ok, SaveNeeded }

    // Struct versions are "sizeof(struct) | (version << 16)" — the values below are the sizes
    // of NVDRS_PROFILE_V1, NVDRS_APPLICATION_V4 and NVDRS_SETTING_V1 on x64.
    private const int ProfileSize = 4116;
    private const int ProfileVersion = ProfileSize | (1 << 16);
    private const int ApplicationSize = 20492;
    private const int ApplicationVersion = ApplicationSize | (4 << 16);
    private const int SettingSize = 12320;
    private const int SettingVersion = SettingSize | (1 << 16);

    // NVDRS_SETTING_V1 field offsets.
    private const int SettingIdOffset = 4100;
    private const int SettingTypeOffset = 4104;
    private const int SettingCurrentValueOffset = 8220;

    // NvAPI_UnicodeString is a fixed NvU16[2048] buffer.
    private const int UnicodeStringChars = 2048;
    private const int UnicodeStringBytes = UnicodeStringChars * 2;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateSessionFn(out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SessionFn(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindApplicationByNameFn(IntPtr session, IntPtr appName, out IntPtr profile, IntPtr application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateProfileFn(IntPtr session, IntPtr profileInfo, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateApplicationFn(IntPtr session, IntPtr profile, IntPtr application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SettingFn(IntPtr session, IntPtr profile, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSettingFn(IntPtr session, IntPtr profile, uint settingId, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeleteSettingFn(IntPtr session, IntPtr profile, uint settingId);

    private static readonly object Gate = new();
    private static bool _initialized;
    private static bool _initializeFailed;

    /// <summary>True when an NVIDIA driver with a usable profile interface is present.</summary>
    public static bool IsAvailable => Initialize();

    private static bool Initialize() {
        lock (Gate) {
            if (_initialized) return true;
            if (_initializeFailed) return false;

            try {
                InitializeFn? init = Lookup<InitializeFn>(IdInitialize);
                _initialized = init != null && init() == NvApiOk;
            } catch {
                _initialized = false; // nvapi64.dll missing (no NVIDIA driver) or refused to load.
            }
            _initializeFailed = !_initialized;
            return _initialized;
        }
    }

    private static T? Lookup<T>(uint id) where T : Delegate {
        IntPtr address = QueryInterface(id);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    /// <summary>
    /// Current FPS cap for each executable, as the driver has it stored. Missing keys mean
    /// "no cap". Reads every game in one driver session, since opening one isn't free.
    /// </summary>
    public static Dictionary<string, int> GetLimits(IEnumerable<string> executablePaths) {
        var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        List<string> paths = executablePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0 || !Initialize()) return limits;

        WithSession((session, buffers) => {
            GetSettingFn? getSetting = Lookup<GetSettingFn>(IdGetSetting);
            FindApplicationByNameFn? find = Lookup<FindApplicationByNameFn>(IdFindApplicationByName);
            if (getSetting == null || find == null) return SessionResult.Failed;

            foreach (string path in paths) {
                IntPtr profile = FindProfile(find, session, buffers, path);
                if (profile == IntPtr.Zero) continue;

                PrepareSettingBuffer(buffers.Setting);
                if (getSetting(session, profile, FrameRateLimiterSettingId, buffers.Setting) != NvApiOk) continue;

                int fps = Marshal.ReadInt32(buffers.Setting, SettingCurrentValueOffset);
                if (fps > 0) limits[path] = fps;
            }
            return SessionResult.Ok; // nothing written, so nothing to save
        });

        return limits;
    }

    /// <summary>
    /// Caps <paramref name="executablePath"/> at <paramref name="fps"/> frames per second,
    /// or removes the cap when <paramref name="fps"/> is 0. Returns false if the driver
    /// rejected the change (including when it's simply not available).
    /// </summary>
    public static bool SetLimit(string executablePath, int fps) {
        if (string.IsNullOrWhiteSpace(executablePath) || !Initialize()) return false;
        if (fps != 0 && (fps < MinFps || fps > MaxFps)) return false;

        return WithSession((session, buffers) => {
            FindApplicationByNameFn? find = Lookup<FindApplicationByNameFn>(IdFindApplicationByName);
            SettingFn? setSetting = Lookup<SettingFn>(IdSetSetting);
            DeleteSettingFn? deleteSetting = Lookup<DeleteSettingFn>(IdDeleteProfileSetting);
            if (find == null || setSetting == null || deleteSetting == null) return SessionResult.Failed;

            IntPtr profile = FindProfile(find, session, buffers, executablePath);

            if (fps == 0) {
                // No profile at all means there's nothing to clear, which is already the goal.
                if (profile == IntPtr.Zero) return SessionResult.Ok;
                return deleteSetting(session, profile, FrameRateLimiterSettingId) == NvApiOk
                    ? SessionResult.SaveNeeded
                    : SessionResult.Failed;
            }

            if (profile == IntPtr.Zero) {
                profile = CreateProfileForApplication(session, buffers, executablePath);
                if (profile == IntPtr.Zero) return SessionResult.Failed;
            }

            PrepareSettingBuffer(buffers.Setting);
            Marshal.WriteInt32(buffers.Setting, SettingIdOffset, unchecked((int)FrameRateLimiterSettingId));
            Marshal.WriteInt32(buffers.Setting, SettingTypeOffset, 0); // NVDRS_DWORD_TYPE
            Marshal.WriteInt32(buffers.Setting, SettingCurrentValueOffset, fps);
            return setSetting(session, profile, buffers.Setting) == NvApiOk
                ? SessionResult.SaveNeeded
                : SessionResult.Failed;
        });
    }

    private static IntPtr FindProfile(FindApplicationByNameFn find, IntPtr session, Buffers buffers, string executablePath) {
        string fileName = Path.GetFileName(executablePath);
        if (fileName.Length == 0) return IntPtr.Zero;

        WriteUnicodeString(buffers.Name, fileName);
        PrepareApplicationBuffer(buffers.Application);

        int status = find(session, buffers.Name, out IntPtr profile, buffers.Application);
        return status == NvApiOk ? profile : IntPtr.Zero;
    }

    private static IntPtr CreateProfileForApplication(IntPtr session, Buffers buffers, string executablePath) {
        CreateProfileFn? createProfile = Lookup<CreateProfileFn>(IdCreateProfile);
        CreateApplicationFn? createApplication = Lookup<CreateApplicationFn>(IdCreateApplication);
        if (createProfile == null || createApplication == null) return IntPtr.Zero;

        string fileName = Path.GetFileName(executablePath);

        Clear(buffers.Profile, ProfileSize);
        Marshal.WriteInt32(buffers.Profile, 0, ProfileVersion);
        WriteUnicodeString(buffers.Profile + 4, fileName);
        Marshal.WriteInt32(buffers.Profile, 4100, 1); // gpuSupport.geforce

        if (createProfile(session, buffers.Profile, out IntPtr profile) != NvApiOk || profile == IntPtr.Zero)
            return IntPtr.Zero;

        PrepareApplicationBuffer(buffers.Application);
        WriteUnicodeString(buffers.Application + 8, fileName);            // appName
        WriteUnicodeString(buffers.Application + 4104, fileName);         // userFriendlyName
        return createApplication(session, profile, buffers.Application) == NvApiOk ? profile : IntPtr.Zero;
    }

    private sealed class Buffers : IDisposable {
        public readonly IntPtr Name = Marshal.AllocHGlobal(UnicodeStringBytes);
        public readonly IntPtr Application = Marshal.AllocHGlobal(ApplicationSize);
        public readonly IntPtr Profile = Marshal.AllocHGlobal(ProfileSize);
        public readonly IntPtr Setting = Marshal.AllocHGlobal(SettingSize);

        public void Dispose() {
            Marshal.FreeHGlobal(Name);
            Marshal.FreeHGlobal(Application);
            Marshal.FreeHGlobal(Profile);
            Marshal.FreeHGlobal(Setting);
        }
    }

    // Opens a driver session, hands it to <paramref name="work"/>, and saves only when the
    // work reports it changed something. Serialized: the driver database is machine-wide state.
    private static bool WithSession(Func<IntPtr, Buffers, SessionResult> work) {
        lock (Gate) {
            CreateSessionFn? createSession = Lookup<CreateSessionFn>(IdCreateSession);
            SessionFn? loadSettings = Lookup<SessionFn>(IdLoadSettings);
            SessionFn? saveSettings = Lookup<SessionFn>(IdSaveSettings);
            SessionFn? destroySession = Lookup<SessionFn>(IdDestroySession);
            if (createSession == null || loadSettings == null || destroySession == null) return false;

            IntPtr session = IntPtr.Zero;
            using var buffers = new Buffers();
            try {
                if (createSession(out session) != NvApiOk) return false;
                if (loadSettings(session) != NvApiOk) return false;

                SessionResult result = work(session, buffers);
                if (result != SessionResult.SaveNeeded) return result == SessionResult.Ok;

                return saveSettings != null && saveSettings(session) == NvApiOk;
            } catch {
                return false; // A driver update mid-call, or an interface that moved on.
            } finally {
                if (session != IntPtr.Zero) {
                    try { destroySession(session); } catch { /* nothing left to do */ }
                }
            }
        }
    }

    private static void PrepareApplicationBuffer(IntPtr buffer) {
        Clear(buffer, ApplicationSize);
        Marshal.WriteInt32(buffer, 0, ApplicationVersion);
    }

    private static void PrepareSettingBuffer(IntPtr buffer) {
        Clear(buffer, SettingSize);
        Marshal.WriteInt32(buffer, 0, SettingVersion);
    }

    private static void Clear(IntPtr buffer, int size) {
        int whole = size - size % 8;
        for (int offset = 0; offset < whole; offset += 8) Marshal.WriteInt64(buffer, offset, 0);
        for (int offset = whole; offset < size; offset++) Marshal.WriteByte(buffer, offset, 0);
    }

    private static void WriteUnicodeString(IntPtr buffer, string value) {
        for (int i = 0; i < UnicodeStringChars; i++)
            Marshal.WriteInt16(buffer, i * 2, i < value.Length ? (short)value[i] : (short)0);
    }
}
