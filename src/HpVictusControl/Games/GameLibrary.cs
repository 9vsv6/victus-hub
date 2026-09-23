using System.IO;

namespace HpVictusControl.Games;

/// <summary>
/// Recognises an executable that lives inside a game library, so a game can be added to the list
/// the first time it's actually played rather than only by scanning.
/// </summary>
public static class GameLibrary {

    // The folder that holds one game per subfolder, per store.
    private static readonly string[] LibraryMarkers = {
        @"\steamapps\common\", @"\Epic Games\", @"\XboxGames\", @"\GOG Galaxy\Games\",
        @"\Riot Games\", @"\Battle.net\", @"\Origin Games\", @"\EA Games\", @"\Ubisoft\Ubisoft Game Launcher\games\",
    };

    // Storefronts, overlays, crash handlers and anti-cheat services all run from inside those same
    // folders; none of them is the game.
    private static readonly string[] IgnoredNames = {
        "steam", "steamwebhelper", "steamservice", "gameoverlayui", "steamerrorreporter",
        "epicgameslauncher", "epicwebhelper", "unrealcefsubprocess", "eoshelper",
        "crashhandler", "crashreportclient", "crashpad_handler", "unitycrashhandler64", "unitycrashhandler32",
        "easyanticheat", "easyanticheat_eos_setup", "battleye", "beservice", "be_service",
        "vc_redist", "dxsetup", "dotnetfx", "oalinst", "uninstall", "installscript",
        "launcher", "bootstrapper", "setup", "installer", "helper", "handler", "service",
    };

    // Redistributables and tooling shipped alongside games.
    private static readonly string[] IgnoredPathParts = {
        @"\_commonredist\", @"\commonredist\", @"\directx\", @"\redist\", @"\easyanticheat\",
        @"\battleye\", @"\tools\", @"\support\",
    };

    /// <summary>True when this path looks like the game itself rather than something around it.</summary>
    public static bool LooksLikeGame(string exePath) {
        if (exePath.Length == 0 || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (!LibraryMarkers.Any(marker => exePath.Contains(marker, StringComparison.OrdinalIgnoreCase))) return false;

        string lowerPath = exePath.ToLowerInvariant();
        if (IgnoredPathParts.Any(part => lowerPath.Contains(part))) return false;

        string name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        return !IgnoredNames.Contains(name);
    }

    /// <summary>
    /// The game's own folder inside the library ("...\steamapps\common\Overwatch"), which identifies
    /// a game better than the executable: launchers often start a second exe from the same folder.
    /// </summary>
    public static string? GameFolder(string exePath) {
        foreach (string marker in LibraryMarkers) {
            int index = exePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;

            int start = index + marker.Length;
            int end = exePath.IndexOf('\\', start);
            if (end < 0) return null; // the exe sits directly in the library folder — no game folder
            return exePath[..end];
        }
        return null;
    }

    /// <summary>A display name: the game's folder name, else the executable's.</summary>
    public static string NameFor(string exePath) {
        string? folder = GameFolder(exePath);
        string name = folder != null ? Path.GetFileName(folder) : Path.GetFileNameWithoutExtension(exePath);
        return name.Length > 0 ? name : Path.GetFileNameWithoutExtension(exePath);
    }
}
