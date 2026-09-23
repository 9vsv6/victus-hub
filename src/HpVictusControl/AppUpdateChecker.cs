using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HpVictusControl;

public readonly record struct AppUpdateResult(
    bool UpdateAvailable, string? LatestVersion, string? ReleaseUrl, string? Error,
    string? AssetUrl = null, string? AssetName = null, long AssetSize = 0);

/// <summary>Checks this project's own GitHub Releases for a newer build than the one running.</summary>
public static class AppUpdateChecker {

    private const string ReleasesApiUrl = "https://api.github.com/repos/9vsv6/victus-hub/releases/latest";

    public static async Task<AppUpdateResult> CheckAsync(Version currentVersion) {
        try {
            using HttpClient client = CreateClient(currentVersion);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using HttpResponseMessage response = await client.GetAsync(ReleasesApiUrl);
            if (!response.IsSuccessStatusCode) {
                string message = response.StatusCode == System.Net.HttpStatusCode.NotFound
                    // GitHub answers 404 rather than 403 for a repository the caller can't see, so a
                    // private repo and one with no releases look the same from here.
                    ? Loc.T("No release found. If the repository is private, its releases aren't readable without signing in.")
                    : Loc.F("GitHub returned {0}.", (int)response.StatusCode);
                return new AppUpdateResult(false, null, null, message);
            }

            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out JsonElement tagEl) ? tagEl.GetString() ?? "" : "";
            string htmlUrl = root.TryGetProperty("html_url", out JsonElement urlEl) ? urlEl.GetString() ?? "" : "";

            string versionPart = tag.TrimStart('v', 'V');
            if (!Version.TryParse(versionPart, out Version? latest))
                return new AppUpdateResult(false, tag, htmlUrl, Loc.T("Couldn't parse the latest release's version number."));

            (string? assetUrl, string? assetName, long assetSize) = FindExecutableAsset(root);
            return new AppUpdateResult(latest > currentVersion, tag, htmlUrl, null, assetUrl, assetName, assetSize);
        } catch (Exception ex) {
            return new AppUpdateResult(false, null, null, Loc.F("Couldn't check for updates: {0}", ex.Message));
        }
    }

    /// <summary>Downloads the release's .exe to <paramref name="destinationFolder"/>, returning its path.</summary>
    public static async Task<string> DownloadAsync(AppUpdateResult update, string destinationFolder, Version currentVersion) {
        if (update.AssetUrl == null) throw new InvalidOperationException(Loc.T("That release has no downloadable build attached."));

        Directory.CreateDirectory(destinationFolder);
        string destination = Path.Combine(destinationFolder, update.AssetName ?? "HpVictusControl.exe");

        using HttpClient client = CreateClient(currentVersion);
        client.Timeout = TimeSpan.FromMinutes(10);
        using HttpResponseMessage response = await client.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using (FileStream file = File.Create(destination)) {
            await response.Content.CopyToAsync(file);
        }

        // A truncated download would otherwise be swapped in as the new app.
        var written = new FileInfo(destination);
        if (update.AssetSize > 0 && written.Length != update.AssetSize) {
            File.Delete(destination);
            throw new IOException(Loc.F("The download stopped early ({0:N0} of {1:N0} bytes).", written.Length, update.AssetSize));
        }
        return destination;
    }

    private static HttpClient CreateClient(Version currentVersion) {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HpVictusControl", currentVersion.ToString()));
        return client;
    }

    private static (string? Url, string? Name, long Size) FindExecutableAsset(JsonElement release) {
        if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, null, 0);

        foreach (JsonElement asset in assets.EnumerateArray()) {
            string name = asset.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() ?? "" : "";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            string? url = asset.TryGetProperty("browser_download_url", out JsonElement urlEl) ? urlEl.GetString() : null;
            long size = asset.TryGetProperty("size", out JsonElement sizeEl) && sizeEl.TryGetInt64(out long parsed) ? parsed : 0;
            if (url != null) return (url, name, size);
        }
        return (null, null, 0);
    }
}
