using System.Globalization;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HpVictusControl.Updates;

/// <summary>
/// Looks up the real latest GPU driver directly from NVIDIA's own public lookup service,
/// bypassing HP's catalog — which was confirmed stale (its "latest" NVIDIA entries translate
/// to marketing versions like 591.91, while NVIDIA's own site already shows 616.92 for this
/// exact GPU). Found by watching NVIDIA's manual driver search page make this exact request;
/// no API key or auth needed.
/// </summary>
public static class NvidiaDriverUpdateService {

    // Product/OS IDs specific to "GeForce RTX 3050 Laptop GPU" on Windows 10/11 64-bit —
    // captured from NVIDIA's own driver search for this exact model.
    private const string LookupUrl =
        "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
        "?func=DriverManualLookup&psid=123&pfid=963&osID=57&languageCode=1033&isWHQL=0&dch=1&sort1=0&numberOfResults=3";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient() {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
        return client;
    }

    /// <summary>Returns the latest NVIDIA driver, or null if the installed one is already current.</summary>
    public static async Task<HpDriverUpdate?> GetUpdateIfNewerAsync() {
        try {
            string? installedVersion = GetInstalledDriverVersion();

            using HttpResponseMessage response = await Http.GetAsync(LookupUrl);
            response.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            if (!doc.RootElement.TryGetProperty("IDS", out JsonElement ids)) return null;

            JsonElement? chosen = null;
            foreach (JsonElement item in ids.EnumerateArray()) {
                JsonElement info = item.GetProperty("downloadInfo");
                string name = Uri.UnescapeDataString(info.GetProperty("Name").GetString() ?? "");
                if (name.Contains("Game Ready", StringComparison.OrdinalIgnoreCase)) { chosen = info; break; }
                chosen ??= info;
            }
            if (chosen is null) return null;

            JsonElement entry = chosen.Value;
            string latestMarketing = entry.GetProperty("Version").GetString() ?? "";

            if (installedVersion != null
                && TryConvertToMarketingVersion(installedVersion, out double installedValue)
                && double.TryParse(latestMarketing, NumberStyles.Any, CultureInfo.InvariantCulture, out double latestValue)
                && installedValue >= latestValue) {
                return null; // already up to date
            }

            string downloadUrl = entry.GetProperty("DownloadURL").GetString() ?? "";
            string name2 = Uri.UnescapeDataString(entry.GetProperty("Name").GetString() ?? "NVIDIA Driver");

            return new HpDriverUpdate {
                Title = $"{name2} (from NVIDIA)",
                Version = latestMarketing,
                Category = "Driver-Graphics",
                ReleaseDate = entry.GetProperty("ReleaseDateTime").GetString() ?? "",
                FileSize = entry.GetProperty("DownloadURLFileSize").GetString() ?? "",
                DownloadUrl = downloadUrl,
                FileName = downloadUrl.Length > 0 ? Path.GetFileName(new Uri(downloadUrl).AbsolutePath) : "nvidia-driver.exe"
            };
        } catch {
            return null;
        }
    }

    private static string? GetInstalledDriverVersion() {
        try {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
            foreach (ManagementBaseObject item in searcher.Get()) {
                using (item) {
                    string name = item["Name"]?.ToString() ?? "";
                    if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                        return item["DriverVersion"]?.ToString();
                }
            }
        } catch {
            // Fall through to null below.
        }
        return null;
    }

    // NVIDIA's Windows driver version (e.g. "32.0.16.1692") encodes its own marketing version
    // (e.g. "616.92") in the last two segments: concatenate them, drop the leading digit, then
    // place a decimal point two digits from the end. Verified against two known pairs before
    // trusting it (32.0.16.1692 -> 616.92, 32.0.15.9191 -> 591.91).
    private static bool TryConvertToMarketingVersion(string windowsDriverVersion, out double marketing) {
        marketing = 0;
        string[] parts = windowsDriverVersion.Split('.');
        if (parts.Length != 4) return false;

        string combined = parts[2] + parts[3];
        if (combined.Length < 4) return false;

        string trimmed = combined[1..];
        string formatted = trimmed.Insert(trimmed.Length - 2, ".");
        return double.TryParse(formatted, NumberStyles.Any, CultureInfo.InvariantCulture, out marketing);
    }
}
