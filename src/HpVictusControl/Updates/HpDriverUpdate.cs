namespace HpVictusControl.Updates;

/// <summary>One driver/BIOS/software update as reported by HP's public support site.</summary>
public sealed class HpDriverUpdate {
    public required string Title { get; init; }
    public required string Version { get; init; }
    public required string Category { get; init; }
    public required string ReleaseDate { get; init; }
    public required string FileSize { get; init; }
    public required string DownloadUrl { get; init; }
    public required string FileName { get; init; }
}
