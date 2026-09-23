using System.Diagnostics;
using System.IO;
using HpVictusControl.Bios;
using Microsoft.Win32;

namespace HpVictusControl;

/// <summary>
/// Installs the app the way Windows expects: one copy in Program Files, a Start menu shortcut, and
/// an entry in Settings → Apps → Installed apps whose Uninstall runs this exe with --uninstall.
/// No separate setup program — the app installs, updates and removes itself.
/// </summary>
public static class Installer {

    public const string UninstallArgument = "--uninstall";

    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\HpVictusControl";

    public static string InstallDirectory => StartupManager.InstallDirectory;
    public static string InstalledExe => Path.Combine(InstallDirectory, "HpVictusControl.exe");

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Victus Hub.lnk");

    private static string SettingsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HP Victus Control");

    public static bool IsRunningInstalled => StartupManager.IsRunningFromInstallDirectory();

    /// <summary>Listed in Windows' installed apps, i.e. installed through this, not just copied.</summary>
    public static bool IsRegistered {
        get {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(UninstallKeyPath);
            return key != null;
        }
    }

    /// <summary>Major.minor.patch of the copy in Program Files, or null if there isn't one.</summary>
    public static Version? InstalledVersion {
        get {
            if (!File.Exists(InstalledExe)) return null;
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(InstalledExe);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        }
    }

    // Where the Start menu shortcut lived before the app was renamed from HP Victus Control.
    private static string OldShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "HP Victus Control.lnk");

    /// <summary>
    /// Keeps Windows' installed-apps entry and the Start menu shortcut in step with the copy in
    /// Program Files, which "Start with Windows" and self-update replace without going through
    /// <see cref="Install"/>: the version number, and the name since the rename to Victus Hub.
    /// </summary>
    public static void SyncListing() {
        try {
            Version? installed = InstalledVersion;
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(UninstallKeyPath, writable: true);
            if (key == null) return;
            if (installed != null && key.GetValue("DisplayVersion") as string != installed.ToString(3))
                key.SetValue("DisplayVersion", installed.ToString(3));
            if (key.GetValue("DisplayName") as string != "Victus Hub") {
                key.SetValue("DisplayName", "Victus Hub");
                key.SetValue("Publisher", "Victus Hub project");
            }
            if (File.Exists(OldShortcutPath)) {
                if (File.Exists(ShortcutPath)) File.Delete(OldShortcutPath);
                else File.Move(OldShortcutPath, ShortcutPath);
            }
        } catch {
            // Needs admin, which the running app has; a stale entry there is harmless anyway.
        }
    }

    /// <summary>
    /// Copies the running exe into Program Files (unless it's already running from there), adds the
    /// Start menu shortcut and the installed-apps entry. With <paramref name="startWithWindows"/> the
    /// logon task is pointed at the installed copy too. Requires admin; returns the installed exe.
    /// </summary>
    public static string Install(bool startWithWindows, Version version) {
        string exe;
        if (startWithWindows) {
            StartupManager.SetEnabled(true); // copies the app as part of registering the task
            exe = InstalledExe;
        } else {
            exe = StartupManager.InstallCopy();
        }

        CreateShortcut(exe);
        RegisterUninstallEntry(exe, version);
        return exe;
    }

    /// <summary>
    /// Removes the startup task, shortcut and installed-apps entry, and optionally the settings. The
    /// Program Files folder goes too — after this process exits when it's the copy running from it.
    /// </summary>
    public static void Uninstall(bool removeSettings) {
        try {
            StartupManager.SetEnabled(false);
        } catch {
            // Carry on: a missing task or Run entry is exactly what we want anyway.
        }

        TryDelete(ShortcutPath);
        TryDelete(OldShortcutPath);
        try {
            Registry.LocalMachine.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        } catch {
            // Without admin this fails; the elevated uninstall path always has it.
        }

        if (removeSettings) {
            try {
                if (Directory.Exists(SettingsFolder)) Directory.Delete(SettingsFolder, recursive: true);
            } catch {
                // A locked file leaves the folder behind; nothing depends on it once uninstalled.
            }
        }

        if (Directory.Exists(InstallDirectory)) {
            if (IsRunningInstalled) RunAfterExit($"rmdir /s /q \"{InstallDirectory}\"");
            else TryDeleteDirectory(InstallDirectory);
        }
    }

    /// <summary>
    /// Puts the laptop back the way HP ships it before the app goes: fans on the BIOS's own curve,
    /// Balanced mode and the matching Windows power plan. Best-effort on hardware that differs.
    /// </summary>
    public static void RestoreHardwareDefaults() {
        try {
            using var bios = new HpBiosClient();
            bios.Connect();
            if (!bios.IsAvailable) return;
            bios.SetMaxFanSpeed(false);
            bios.ReleaseManualFanControl();
            bios.SetFanMode(HpFanMode.Balanced);
        } catch {
            // Nothing left to undo if the BIOS won't answer.
        }
        try {
            WindowsPowerPlan.SetForMode(HpFanMode.Balanced);
        } catch {
            // The user's own plan choice stays as it was.
        }
    }

    /// <summary>Starts <paramref name="exe"/> once this process has had a moment to exit.</summary>
    public static void LaunchAfterExit(string exe) => RunAfterExit($"start \"\" \"{exe}\"");

    private static void RunAfterExit(string command) {
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak > nul & {command}") {
            CreateNoWindow = true,
            UseShellExecute = false,
            // Outside the folder being removed, or cmd would hold it open.
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        });
    }

    private static void CreateShortcut(string exe) {
        // Windows Script Host's shortcut object: the simplest way to write a .lnk without extra libraries.
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host isn't available to create the shortcut.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try {
            dynamic link = shell.CreateShortcut(ShortcutPath);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = "Fan, performance and driver control for HP Victus laptops";
            link.Save();
        } finally {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void RegisterUninstallEntry(string exe, Version version) {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(UninstallKeyPath, writable: true);
        key.SetValue("DisplayName", "Victus Hub");
        key.SetValue("DisplayVersion", version.ToString(3));
        key.SetValue("Publisher", "Victus Hub project");
        key.SetValue("DisplayIcon", exe);
        key.SetValue("InstallLocation", Path.GetDirectoryName(exe)!);
        key.SetValue("UninstallString", $"\"{exe}\" {UninstallArgument}");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("URLInfoAbout", "https://github.com/9vsv6/victus-hub");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
    }

    private static void TryDelete(string path) {
        try {
            if (File.Exists(path)) File.Delete(path);
        } catch {
            // A leftover shortcut is the worst case.
        }
    }

    private static void TryDeleteDirectory(string path) {
        try {
            Directory.Delete(path, recursive: true);
        } catch {
            // Files in use: they go on the next uninstall or can be deleted by hand.
        }
    }
}
