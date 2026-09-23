using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace HpVictusControl;

/// <summary>
/// Starts the app at sign-in through a logon-triggered scheduled task with highest privileges, so
/// it gets admin without a UAC prompt. Explorer can silently skip Run entries, so the Run entry
/// launches nothing: it exists only so the app is listed, and switchable, in Task Manager's
/// Startup apps, and the task-started copy exits when it's switched off there.
/// </summary>
public static class StartupManager {

    public const string RunEntryArgument = "--startup-entry";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "HpVictusControl";
    private const string TaskName = "HpVictusControlStartup";

    // A highest-privilege task pointing at a user-writable exe would let any program replace that
    // exe and get admin without a prompt, so the task only ever runs a copy in Program Files.
    internal static readonly string InstallDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HP Victus Control");

    private static readonly string SchTasksPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    /// <summary>Requires admin. Throws if the app can't be copied or the task can't be registered.</summary>
    public static void SetEnabled(bool enabled) {
        if (enabled) {
            string installedExe = InstallCopy();
            // Re-registering replaces the task, which can stop the copy it is currently running, so the
            // task-started copy leaves an existing registration alone.
            if (!IsRunningFromInstallDirectory() || !TaskExists()) RegisterTask(installedExe);
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(ValueName, $"\"{installedExe}\" {RunEntryArgument}");
            return;
        }

        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)) {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        RunSchTasks($"/Delete /TN \"{TaskName}\" /F");
        if (!IsRunningFromInstallDirectory()) {
            try {
                if (Directory.Exists(InstallDirectory)) Directory.Delete(InstallDirectory, recursive: true);
            } catch {
                // Leftover files are harmless; the task and Run entry are already gone.
            }
        }
    }

    /// <summary>True when the app is switched off in Task Manager's (or Settings') Startup apps.</summary>
    public static bool IsDisabledInTaskManager() {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupApprovedPath);
        // The first byte is even when switched on (02, 06) and odd when switched off (03, 07).
        return key?.GetValue(ValueName) is byte[] { Length: > 0 } data && data[0] % 2 == 1;
    }

    /// <summary>Starts the elevated copy without a UAC prompt. False if the task isn't registered or won't run.</summary>
    public static bool TryRunStartupTask() => RunSchTasks($"/Run /TN \"{TaskName}\"") == 0;

    private static bool TaskExists() => RunSchTasks($"/Query /TN \"{TaskName}\"") == 0;

    internal static string InstallCopy() {
        if (!Path.IsPathRooted(InstallDirectory)) throw new InvalidOperationException("Couldn't locate the Program Files folder.");

        string exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Couldn't locate the running executable.");
        if (IsRunningFromInstallDirectory()) return exePath;

        // A single-file release is just the exe; a regular build needs everything beside it.
        // Assembly.Location is empty only when running as a single-file app.
        bool singleFile = string.IsNullOrEmpty(typeof(StartupManager).Assembly.Location);
        string installedExe = Path.Combine(InstallDirectory, singleFile ? "HpVictusControl.exe" : Path.GetFileName(exePath));

        // Clearing first keeps files from a different build type from lingering beside the new exe.
        if (Directory.Exists(InstallDirectory)) Directory.Delete(InstallDirectory, recursive: true);
        Directory.CreateDirectory(InstallDirectory);

        if (singleFile) File.Copy(exePath, installedExe);
        else CopyDirectory(Path.GetDirectoryName(exePath)!, InstallDirectory);

        return installedExe;
    }

    internal static bool IsRunningFromInstallDirectory() {
        string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        return exeDir != null && string.Equals(
            Path.GetFullPath(exeDir).TrimEnd('\\'), Path.GetFullPath(InstallDirectory).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string source, string destination) {
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static void RegisterTask(string exePath) {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);

        // PT0S and priority 4 stop Task Scheduler killing the app after 72 hours or running it below
        // normal priority. Parallel lets an on-demand run reach an already-running instance.
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Victus Hub with administrator rights at sign-in.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exePath)}</Command>
                  <Arguments>--minimized</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        // Written inside Program Files, not %TEMP%, so a normal-user program can't swap the file
        // between writing it and schtasks reading it.
        string xmlPath = Path.Combine(InstallDirectory, "startup-task.xml");
        try {
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);
            int exitCode = RunSchTasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (exitCode != 0) throw new InvalidOperationException($"Couldn't register the startup task (schtasks exit code {exitCode}).");
        } finally {
            File.Delete(xmlPath);
        }
    }

    private static int RunSchTasks(string arguments) {
        try {
            using Process? process = Process.Start(new ProcessStartInfo(SchTasksPath, arguments) {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process == null) return -1;
            return process.WaitForExit(10_000) ? process.ExitCode : -1;
        } catch {
            return -1;
        }
    }
}
