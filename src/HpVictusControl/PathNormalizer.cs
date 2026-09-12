using System.IO;

namespace HpVictusControl;

/// <summary>
/// Canonical spelling for comparing executable paths. Steam and Epic hand back paths like
/// "c:/program files (x86)/steam", which point at the same file as "C:\Program Files (x86)\Steam".
/// Compare the results case-insensitively.
/// </summary>
public static class PathNormalizer {

    public static string Normalize(string path) {
        try {
            return Path.GetFullPath(path);
        } catch {
            return path;
        }
    }
}
