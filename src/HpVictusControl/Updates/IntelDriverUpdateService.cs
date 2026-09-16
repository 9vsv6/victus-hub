using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HpVictusControl.Updates;

public sealed record IntelDriverMatch(string DeviceName, string InstalledVersion, HpDriverUpdate Latest, bool IsNewer);

/// <summary>
/// Checks Intel's own driver catalog - the file Intel Driver &amp; Support Assistant downloads from
/// dsadata.intel.com - so Intel updates come straight from Intel without that app. Intel's download web
/// pages block automated requests, but this catalog and downloadmirror.intel.com don't. Only the JSON
/// catalog is read; the rule DLLs Intel bundles beside it are never loaded.
/// </summary>
public static class IntelDriverUpdateService {

    private const string CatalogUrl = "https://dsadata.intel.com/data/en";
    private const string CatalogFile = "software-configurations.json";

    private static readonly HttpClient Http = CreateClient();

    private sealed record InstalledDevice(string Name, string DriverVersion);

    private static HttpClient CreateClient() {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        string version = typeof(IntelDriverUpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HpVictusControl", version));
        return client;
    }

    /// <summary>Intel catalog packages matching this PC's devices. Empty if the catalog can't be read.</summary>
    public static async Task<List<IntelDriverMatch>> CheckAsync() {
        var matches = new List<IntelDriverMatch>();
        try {
            Dictionary<string, InstalledDevice> devices = await Task.Run(GetInstalledDevices).ConfigureAwait(false);
            if (devices.Count == 0) return matches;

            byte[] zipBytes = await Http.GetByteArrayAsync(CatalogUrl).ConfigureAwait(false);
            using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            ZipArchiveEntry? catalog = archive.GetEntry(CatalogFile);
            if (catalog == null) return matches;

            await using Stream json = catalog.Open();
            using JsonDocument doc = await JsonDocument.ParseAsync(json).ConfigureAwait(false);
            string osPrefix = Environment.OSVersion.Version.Build >= 22000 ? "windows-11" : "windows-10";

            foreach (JsonElement package in doc.RootElement.EnumerateArray()) {
                if (IsTrue(package, "IsBeta") || IsTrue(package, "IntelSystemOnly")
                    || Items(package, "BoardNumbers").Any() || Items(package, "ModelNumbers").Any()) continue;

                foreach (JsonElement component in Items(package, "Components")) {
                    // Other detection types depend on Intel's rule DLLs, which this app deliberately doesn't run.
                    if (Str(component, "DetectionType") != "Default") continue;

                    InstalledDevice? device = Items(component, "DetectionValues")
                        .Select(value => (value.GetString() ?? "").TrimEnd('*'))
                        .Where(devices.ContainsKey)
                        .Select(key => devices[key])
                        .FirstOrDefault();
                    if (device == null) continue;

                    HpDriverUpdate? update = ToUpdate(package, component, osPrefix);
                    if (update != null) {
                        bool isNewer = Version.TryParse(Str(component, "Version") ?? update.Version, out Version? latest)
                            && Version.TryParse(device.DriverVersion, out Version? installed)
                            && latest > installed;
                        matches.Add(new IntelDriverMatch(device.Name, device.DriverVersion, update, isNewer));
                    }
                    break;
                }
            }
        } catch {
            // Catalog unreachable or its format changed - no Intel results this time.
        }
        return matches;
    }

    private static HpDriverUpdate? ToUpdate(JsonElement package, JsonElement component, string osPrefix) {
        List<JsonElement> files = Items(package, "Files").ToList();
        JsonElement? file = files.Cast<JsonElement?>().FirstOrDefault(f => Items(f!.Value, "OperatingSystems")
                .Select(os => os.GetString() ?? "")
                .Any(os => os.StartsWith(osPrefix, StringComparison.Ordinal) && os.EndsWith("-64", StringComparison.Ordinal)))
            ?? files.Cast<JsonElement?>().FirstOrDefault();
        if (file == null) return null;

        // The installer runs with admin rights, so only accept HTTPS downloads hosted by Intel.
        if (!Uri.TryCreate(Str(file.Value, "Url"), UriKind.Absolute, out Uri? url)
            || url.Scheme != Uri.UriSchemeHttps
            || !(url.Host.Equals("intel.com", StringComparison.OrdinalIgnoreCase)
                 || url.Host.EndsWith(".intel.com", StringComparison.OrdinalIgnoreCase))) return null;

        long size = file.Value.TryGetProperty("Size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long bytes) ? bytes : 0;
        string released = DateTime.TryParse(Str(package, "DisplayReleaseDate"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal, out DateTime date) ? date.ToString("yyyy-MM-dd") : "";

        return new HpDriverUpdate {
            Title = $"{Str(package, "Name")} (from Intel)",
            Version = Str(package, "Version") ?? "",
            Category = $"Driver-{Str(component, "Category") ?? "Intel"}",
            ReleaseDate = released,
            FileSize = size > 0 ? $"{size / 1024d / 1024d:0.#} MB" : "",
            DownloadUrl = url.ToString(),
            FileName = Path.GetFileName(url.AbsolutePath)
        };
    }

    // Catalog detection values look like "VEN_8086&DEV_A7A8" or "VEN_8086&DEV_A7A8&SUBSYS_...", so each
    // installed hardware ID is indexed under its full form and every shorter "&"-separated prefix.
    private static Dictionary<string, InstalledDevice> GetInstalledDevices() {
        var devices = new Dictionary<string, InstalledDevice>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher("SELECT DeviceName, DriverVersion, HardWareID FROM Win32_PnPSignedDriver");
        foreach (ManagementBaseObject driver in searcher.Get()) {
            using (driver) {
                string hardwareId = driver["HardWareID"] as string ?? "";
                string version = driver["DriverVersion"] as string ?? "";
                if (hardwareId.Length == 0 || version.Length == 0) continue;

                var device = new InstalledDevice(driver["DeviceName"] as string ?? hardwareId, version);
                string body = hardwareId.Contains('\\') ? hardwareId[(hardwareId.IndexOf('\\') + 1)..] : hardwareId;
                string[] parts = body.Split('&');

                devices.TryAdd(hardwareId, device);
                for (int count = parts.Length; count >= 1; count--)
                    devices.TryAdd(string.Join('&', parts, 0, count), device);
            }
        }
        return devices;
    }

    private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
