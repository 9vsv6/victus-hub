using Microsoft.Win32;

namespace HpVictusControl;

/// <summary>
/// Xbox Game Bar's background recording ("record what happened", Game DVR), which keeps capturing
/// gameplay while you play. These are the same two per-user values the Windows Settings page writes.
/// </summary>
public static class GameBarRecording {

    private const string GameConfigStore = @"System\GameConfigStore";
    private const string CaptureKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";

    /// <summary>On only when both switches are on; either one off stops the background capture.</summary>
    public static bool IsEnabled() {
        try {
            using RegistryKey? store = Registry.CurrentUser.OpenSubKey(GameConfigStore);
            using RegistryKey? capture = Registry.CurrentUser.OpenSubKey(CaptureKey);
            bool dvr = store?.GetValue("GameDVR_Enabled") is not int dvrValue || dvrValue != 0;
            bool appCapture = capture?.GetValue("AppCaptureEnabled") is int captureValue && captureValue != 0;
            return dvr && appCapture;
        } catch {
            return false;
        }
    }

    public static void SetEnabled(bool enabled) {
        int value = enabled ? 1 : 0;
        using (RegistryKey store = Registry.CurrentUser.CreateSubKey(GameConfigStore, writable: true))
            store.SetValue("GameDVR_Enabled", value, RegistryValueKind.DWord);
        using (RegistryKey capture = Registry.CurrentUser.CreateSubKey(CaptureKey, writable: true))
            capture.SetValue("AppCaptureEnabled", value, RegistryValueKind.DWord);
    }
}
