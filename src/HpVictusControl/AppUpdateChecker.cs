using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HpVictusControl;

public readonly record struct AppUpdateResult(bool UpdateAvailable, string? LatestVersion, string? ReleaseUrl, string? Error);

/// <summary>Checks this project's own GitHub Releases for a newer build than the one running.</summary>
public static class AppUpdateChecker {

    private const string ReleasesApiUrl = "https://api.github.com/repos/9vsv6/hp-victus-control/releases/latest";

    public static async Task<AppUpdateResult> CheckAsync(Version currentVersion) {
        try {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HpVictusControl", currentVersion.ToString()));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using HttpResponseMessage response = await client.GetAsync(ReleasesApiUrl);
            if (!response.IsSuccessStatusCode) {
                string message = response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "No releases have been published yet."
                    : $"GitHub returned {(int)response.StatusCode}.";
                return new AppUpdateResult(false, null, null, message);
            }

            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            string htmlUrl = doc.RootElement.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? "" : "";

            string versionPart = tag.TrimStart('v', 'V');
            if (!Version.TryParse(versionPart, out Version? latest))
                return new AppUpdateResult(false, tag, htmlUrl, "Couldn't parse the latest release's version number.");

            bool isNewer = latest > currentVersion;
            return new AppUpdateResult(isNewer, tag, htmlUrl, null);
        } catch (Exception ex) {
            return new AppUpdateResult(false, null, null, $"Couldn't check for updates: {ex.Message}");
        }
    }
}
