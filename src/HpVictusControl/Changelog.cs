namespace HpVictusControl;

/// <summary>
/// What changed in each version, shown once after an update. Kept in the app rather than read from
/// the GitHub release, so it works offline and in either language (titles and details go through Loc.T).
/// </summary>
public static class Changelog {

    /// <summary>Picks the icon a change is shown with.</summary>
    public enum Kind { Gpu, Cooling, Keyboard, Theme, Language, Battery, Games, Drivers, System, Update, Fix }

    public sealed record Change(Kind Kind, string Title, string Detail);

    public sealed record Release(Version Version, Change[] Changes);

    // Newest first.
    public static readonly Release[] Releases = {
        new(new Version(1, 6, 0), new Change[] {
            new(Kind.Gpu, "CPU power", "Raise the CPU's sustained power limit in Performance mode, with a heat safety net"),
            new(Kind.Games, "Max GPU performance per game", "Keeps the NVIDIA GPU at full clocks while a chosen game runs"),
            new(Kind.Gpu, "Performance boosts", "A tuned power plan in Performance mode, standby memory cleared when a game starts, and a 2-minute fan cool-down after gaming"),
            new(Kind.Games, "Smoother gaming", "Background apps on Efficiency mode, USB and Wi-Fi power saving off on the charger, and a Game Bar recording switch on the Games page"),
            new(Kind.System, "HP fan software check", "Finds HP's Omen software that can undo fan and mode changes, and switches it off on the System page"),
            new(Kind.System, "Startup apps", "See what starts with Windows on the System page, and switch any of it off"),
            new(Kind.Keyboard, "Camera switch", "Turn the webcam off in Windows itself from Settings → Privacy"),
            new(Kind.Drivers, "Restore point before updates", "An option on the Drivers page to make a Windows restore point before installing"),
            new(Kind.Fix, "Easier navigation", "The side buttons can be clicked anywhere, the app opens on the Performance page, and it has a new icon"),
        }),
        new(new Version(1, 5, 0), new Change[] {
            new(Kind.Theme, "Linen themes", "Linen light and dark themes, easier on the eyes over long sessions"),
            new(Kind.Language, "Arabic interface", "Arabic interface with a right-to-left layout (Settings → Language)"),
            new(Kind.Gpu, "GPU power", "A higher power limit and Dynamic Boost for the NVIDIA GPU"),
            new(Kind.Cooling, "Cool mode when idle", "The fans quiet down while you're away and come back when you return"),
            new(Kind.Keyboard, "Keyboard backlight", "A switch for it, and it can turn itself off while the laptop sits idle"),
            new(Kind.Battery, "Battery time per mode", "Battery time for each performance mode, learned from how you use it"),
            new(Kind.System, "Drive health and BIOS settings", "Drive health and BIOS settings on the System page, including battery care"),
            new(Kind.Games, "Games add themselves", "Games are added to the list the first time you play them"),
            new(Kind.Drivers, "Smarter driver checks", "The Drivers page remembers the last check, hides firmware for drives you don't have, and checks weekly"),
            new(Kind.Theme, "Sized to the window", "The interface scales with the window, from small to maximized"),
            new(Kind.System, "New Settings layout", "Settings are grouped in a side list, one section at a time"),
            new(Kind.Update, "Self-update", "The app can download and install its own updates, checked against the release's checksum"),
            new(Kind.Fix, "Fixes", "The CPU dial no longer draws outside its card above 50°C, and the Drivers toolbar is no longer cut off"),
        }),
    };

    /// <summary>Changes newer than <paramref name="lastSeen"/>, up to and including <paramref name="current"/>.</summary>
    public static List<Release> Since(Version? lastSeen, Version current) =>
        Releases.Where(release => release.Version <= current && (lastSeen == null || release.Version > lastSeen)).ToList();

    /// <summary>A Segoe Fluent Icons / MDL2 glyph for each kind of change.</summary>
    public static string Glyph(Kind kind) => kind switch {
        Kind.Gpu => "",       // Processor
        Kind.Cooling => "",   // Frigid
        Kind.Keyboard => "",  // KeyboardClassic
        Kind.Theme => "",     // Color
        Kind.Language => "",  // Globe
        Kind.Battery => "",   // Battery
        Kind.Games => "",     // Game
        Kind.Drivers => "",   // Download
        Kind.System => "",    // Laptop
        Kind.Update => "",    // Sync
        _ => "",              // Repair
    };
}
