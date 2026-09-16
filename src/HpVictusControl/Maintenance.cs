using System.IO;

namespace HpVictusControl;

/// <summary>Size in bytes plus a file count, for "1.0 GB in 30 files" style summaries.</summary>
public readonly record struct FolderUsage(long Bytes, int Files) {
    public static FolderUsage operator +(FolderUsage a, FolderUsage b) => new(a.Bytes + b.Bytes, a.Files + b.Files);
    public override string ToString() => Maintenance.FormatBytes(Bytes);
}

/// <summary>
/// Disk cleanups: graphics shader caches and the driver installers this app downloads. Only ever deletes
/// files inside the specific folders listed here — never the folders themselves, never anything else —
/// and skips any file that's in use (a running game holds its shader cache open).
/// </summary>
public static class Maintenance {

    // Where the graphics drivers keep compiled shaders. Deleting them is safe: games rebuild what they need,
    // so the first launch afterwards loads a little slower — and a stale cache is a common cause of stutter
    // after a driver update.
    public static IReadOnlyList<string> ShaderCacheFolders { get; } = new[] {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "DXCache"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "GLCache"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA Corporation", "NV_Cache"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Intel", "ShaderCache"),
    };

    public static string DriverDownloadsFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HP Victus Control", "Downloads");

    public static FolderUsage MeasureShaderCaches() =>
        ShaderCacheFolders.Aggregate(new FolderUsage(0, 0), (total, folder) => total + Measure(folder));

    /// <summary>Deletes shader cache files; returns what was freed and how many files were in use.</summary>
    public static (FolderUsage Freed, int Skipped) ClearShaderCaches() {
        var freed = new FolderUsage(0, 0);
        int skipped = 0;
        foreach (string folder in ShaderCacheFolders) {
            (FolderUsage f, int s) = DeleteFiles(folder, recursive: true, _ => true);
            freed += f;
            skipped += s;
        }
        return (freed, skipped);
    }

    public static FolderUsage MeasureDriverDownloads() => Measure(DriverDownloadsFolder, recursive: false);

    /// <summary>Deletes downloaded installers, optionally only ones older than <paramref name="olderThan"/>.</summary>
    public static (FolderUsage Freed, int Skipped) CleanDriverDownloads(TimeSpan? olderThan = null) {
        DateTime cutoff = olderThan.HasValue ? DateTime.Now - olderThan.Value : DateTime.MaxValue;
        return DeleteFiles(DriverDownloadsFolder, recursive: false, file => file.LastWriteTime < cutoff);
    }

    /// <summary>Deletes one downloaded installer once it has installed successfully.</summary>
    public static bool DeleteDownloadedInstaller(string path) {
        try {
            // Only files the app itself put in its downloads folder.
            string folder = Path.GetFullPath(DriverDownloadsFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
            File.Delete(path);
            return true;
        } catch {
            return false; // Still in use by the installer's own cleanup — the 30-day sweep gets it later.
        }
    }

    public static string FormatBytes(long bytes) => bytes switch {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} bytes"
    };

    private static FolderUsage Measure(string folder, bool recursive = true) {
        long bytes = 0;
        int files = 0;
        foreach (FileInfo file in Files(folder, recursive)) {
            try {
                bytes += file.Length;
                files++;
            } catch {
                // Vanished between listing and reading.
            }
        }
        return new FolderUsage(bytes, files);
    }

    private static (FolderUsage Freed, int Skipped) DeleteFiles(string folder, bool recursive, Func<FileInfo, bool> shouldDelete) {
        long bytes = 0;
        int files = 0, skipped = 0;
        foreach (FileInfo file in Files(folder, recursive).ToList()) {
            try {
                if (!shouldDelete(file)) continue;
                long length = file.Length;
                file.Delete();
                bytes += length;
                files++;
            } catch {
                skipped++;
            }
        }
        return (new FolderUsage(bytes, files), skipped);
    }

    private static IEnumerable<FileInfo> Files(string folder, bool recursive) {
        if (!Directory.Exists(folder)) return Enumerable.Empty<FileInfo>();
        try {
            return new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions {
                RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
            });
        } catch {
            return Enumerable.Empty<FileInfo>();
        }
    }
}
