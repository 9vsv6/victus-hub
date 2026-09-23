namespace HpVictusControl;

/// <summary>
/// What changed in each version, shown once after an update. Kept in the app rather than read from
/// the GitHub release, so it works offline and in either language (each line goes through Loc.T).
/// </summary>
public static class Changelog {

    public sealed record Release(Version Version, string[] Changes);

    // Newest first.
    public static readonly Release[] Releases = {
        new(new Version(1, 5, 0), new[] {
            "Linen light and dark themes, easier on the eyes over long sessions",
            "Arabic interface with a right-to-left layout (Settings → Language)",
            "The interface scales with the window, from small to maximized",
            "Battery time for each performance mode, learned from how you use it",
            "Drive health and BIOS settings on the System page, including battery care",
            "Games are added to the list the first time you play them",
            "The Drivers page remembers the last check, hides firmware for drives you don't have, and checks weekly",
            "The app can download and install its own updates",
            "Fixed: the CPU dial drew outside its card above 50°C",
        }),
    };

    /// <summary>Changes newer than <paramref name="lastSeen"/>, up to and including <paramref name="current"/>.</summary>
    public static List<Release> Since(Version? lastSeen, Version current) =>
        Releases.Where(release => release.Version <= current && (lastSeen == null || release.Version > lastSeen)).ToList();
}
