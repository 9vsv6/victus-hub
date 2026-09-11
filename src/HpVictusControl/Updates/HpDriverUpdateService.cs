using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HpVictusControl.Updates;

public sealed class HpUpdateException : Exception {
    public HpUpdateException(string message) : base(message) { }
}

/// <summary>
/// Looks up and downloads real driver/BIOS updates for this exact laptop via the same
/// public backend HP's own support website (support.hp.com) uses — reverse-engineered by
/// observing the site's own network calls. No HP Support Assistant, no PowerShell module,
/// no local driver: just plain HTTPS calls to HP's public API, matching what a browser
/// visiting support.hp.com would already do for this serial number.
/// </summary>
public static class HpDriverUpdateService {

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient() {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>Local machine's BIOS serial number, used to identify the exact model with HP.</summary>
    public static string GetLocalSerialNumber() {
        using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
        foreach (ManagementBaseObject item in searcher.Get()) {
            using (item) {
                return item["SerialNumber"]?.ToString()?.Trim() ?? "";
            }
        }
        return "";
    }

    public static async Task<List<HpDriverUpdate>> GetUpdatesForSerialAsync(string serialNumber) {
        if (string.IsNullOrWhiteSpace(serialNumber))
            throw new HpUpdateException("Couldn't determine this machine's serial number.");

        // Step 1: resolve the serial number to HP's internal product identifiers.
        string searchUrl = "https://support.hp.com/wcc-services/searchresult/us-en" +
            $"?q={Uri.EscapeDataString(serialNumber)}&context=swd&navigation=false&authState=anonymous&template=SWD-LaptopLanding";
        using JsonDocument searchDoc = await GetJsonAsync(searchUrl);

        JsonElement verify = searchDoc.RootElement.GetProperty("data").GetProperty("verifyResponse").GetProperty("data");
        if (verify.ValueKind != JsonValueKind.Object)
            throw new HpUpdateException("HP's support site didn't recognize this laptop's serial number.");

        string productOid = verify.GetProperty("productNameOID").GetString()!;
        long productNumberOid = long.Parse(verify.GetProperty("productNumberOID").GetString()!);
        long productSeriesOid = long.Parse(verify.GetProperty("productSeriesOID").GetString()!);

        // Step 2: find the generic "Windows 11"/"Windows 10" entry — it covers every build,
        // so this works regardless of exact OS version (unlike HP's separate reference-catalog
        // system, which requires an exact, often-unpublished version match).
        string targetOsName = Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10";
        string osVerUrl = "https://support.hp.com/wcc-services/swd-v2/osVersionData" +
            $"?cc=us&lc=en&productOid={productOid}&authState=anonymous&template=SWD-LaptopLanding";
        using JsonDocument osDoc = await GetJsonAsync(osVerUrl);

        (string osTMSId, string platformId, string platformName) = FindGenericOsEntry(osDoc, targetOsName);

        // Step 3: fetch the real driver/BIOS list for this model + OS.
        var body = new {
            cc = "us",
            lc = "en",
            osName = platformName,
            osTMSId,
            platformId,
            productNumberOid,
            productSeriesOid
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://support.hp.com/wcc-services/swd-v2/driverDetails?authState=anonymous&template=SWDSeriesDownload") {
            Content = JsonContent.Create(body)
        };

        using HttpResponseMessage response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using JsonDocument detailsDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return ParseUpdates(detailsDoc);
    }

    public static async Task<string> DownloadUpdateAsync(HpDriverUpdate update, string destinationFolder) {
        Directory.CreateDirectory(destinationFolder);
        string destPath = Path.Combine(destinationFolder, update.FileName);

        using HttpResponseMessage response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using Stream httpStream = await response.Content.ReadAsStreamAsync();
        await using FileStream fileStream = File.Create(destPath);
        await httpStream.CopyToAsync(fileStream);

        return destPath;
    }

    /// <summary>
    /// Reduces HP's raw catalog listing to just what's actually worth showing: one entry per
    /// distinct item (HP often lists several historical releases of the same driver separately)
    /// keeping only the newest, and only when it's actually newer than what's installed.
    /// </summary>
    public static List<HpDriverUpdate> GetActionableUpdates(List<HpDriverUpdate> rawUpdates) {
        List<HpDriverUpdate> deduped = rawUpdates
            .GroupBy(u => u.Title, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(u => ParseDate(u.ReleaseDate)).First())
            .ToList();

        string? installedBios = InstalledDriverInfo.GetBiosVersion();
        List<(string DeviceName, string? DriverVersion)> installedDrivers = InstalledDriverInfo.GetSignedDrivers();

        return deduped.Where(update => !IsAlreadyUpToDate(update, installedBios, installedDrivers)).ToList();
    }

    public static DateTime ParseReleaseDate(string text) => ParseDate(text);

    private static DateTime ParseDate(string text) => DateTime.TryParse(text, out DateTime dt) ? dt : DateTime.MinValue;

    private static bool IsAlreadyUpToDate(
            HpDriverUpdate update, string? installedBios, List<(string DeviceName, string? DriverVersion)> installedDrivers) {

        if (InstalledUpdateHistory.IsMarkedInstalled(update)) return true;

        if (update.Category.Contains("BIOS", StringComparison.OrdinalIgnoreCase)) {
            // BIOS versions look like "F.08 Rev.A" — compare the "F.08" part against SMBIOSBIOSVersion.
            if (string.IsNullOrEmpty(installedBios)) return false; // can't tell — don't hide it
            string catalogBase = update.Version.Split(' ')[0].Trim();
            return CompareBiosVersions(installedBios, catalogBase) >= 0;
        }

        // HP's catalog lists wireless drivers for every radio chip variant this laptop model
        // ships with across different configurations (e.g. some units have Intel wireless,
        // this one has Realtek) — not just the part actually inside this specific unit. A
        // wireless driver whose vendor doesn't match any wireless hardware on this machine
        // can't apply here regardless of version. Scoped to wireless only: broadening this to
        // every category risks false-excluding Intel chipset/graphics items on an Intel-CPU
        // machine, where "Intel" legitimately appears for unrelated reasons.
        if (IsForUninstalledWirelessVendor(update.Title, installedDrivers)) return true;

        // NVIDIA GPU updates now come from NVIDIA's own lookup service instead (confirmed HP's
        // catalog runs behind — its "latest" NVIDIA listing translates to marketing version
        // 591.91 while NVIDIA's own site already had 616.92 for this exact GPU). Always exclude
        // HP's NVIDIA-branded graphics entries so the direct source is the only one shown.
        if (update.Category.Contains("Graphics", StringComparison.OrdinalIgnoreCase)
            && update.Title.Contains("nvidia", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (!TryExtractVersion(update.Version, out Version? catalogVersion)) return false; // can't parse — don't hide it

        // For graphics drivers specifically, the Category field HP already gives us makes a
        // single vendor-word match safe (e.g. "NVIDIA Graphics Driver" loses both "nvidia" and
        // "graphics" to noise filtering under the generic matcher, leaving only one word and no
        // confident match — but "Driver-Graphics" + "nvidia" is already unambiguous: a laptop
        // has at most one GPU per vendor).
        (string DeviceName, string? DriverVersion)? match = update.Category.Contains("Graphics", StringComparison.OrdinalIgnoreCase)
            ? FindGraphicsDriverMatch(update.Title, installedDrivers)
            : FindBestDriverMatch(update.Title, installedDrivers);

        if (match is null || !TryExtractVersion(match.Value.DriverVersion ?? "", out Version? installedVersion))
            return false; // no confident match — don't hide it

        return installedVersion! >= catalogVersion!;
    }

    private static readonly string[] GpuVendors = { "nvidia", "amd", "intel" };

    private static (string DeviceName, string? DriverVersion)? FindGraphicsDriverMatch(
            string title, List<(string DeviceName, string? DriverVersion)> installedDrivers) {
        string lowerTitle = title.ToLowerInvariant();
        string? vendor = GpuVendors.FirstOrDefault(v => lowerTitle.Contains(v));
        if (vendor is null) return null;

        foreach ((string DeviceName, string? DriverVersion) candidate in installedDrivers) {
            if (candidate.DeviceName.ToLowerInvariant().Contains(vendor)) return candidate;
        }
        return null;
    }

    private static readonly string[] WirelessVendors = { "intel", "realtek", "mediatek", "qualcomm", "broadcom" };
    private static readonly string[] WirelessKeywords = { "bluetooth", "wlan", "wireless", "wifi", "wi-fi" };

    private static bool IsForUninstalledWirelessVendor(string title, List<(string DeviceName, string? DriverVersion)> installedDrivers) {
        string lowerTitle = title.ToLowerInvariant();
        if (!WirelessKeywords.Any(k => lowerTitle.Contains(k))) return false; // not a wireless-category item

        string? titleVendor = WirelessVendors.FirstOrDefault(v => lowerTitle.Contains(v));
        if (titleVendor is null) return false; // no vendor keyword to check — don't exclude

        // Require the SAME installed device to match both the vendor and a wireless keyword —
        // matching vendor alone would false-positive on an unrelated Intel CPU/iGPU/chipset
        // entry that has nothing to do with the wireless radio.
        return !installedDrivers.Any(d => {
            string name = d.DeviceName.ToLowerInvariant();
            return name.Contains(titleVendor) && WirelessKeywords.Any(k => name.Contains(k));
        });
    }

    // Formats like "F.08" -> letter prefix + numeric suffix, compared as an integer so "F.9" < "F.10".
    private static int CompareBiosVersions(string installed, string catalogBase) {
        Match mi = Regex.Match(installed.Trim(), @"^([A-Za-z]*)\.?(\d+)$");
        Match mc = Regex.Match(catalogBase.Trim(), @"^([A-Za-z]*)\.?(\d+)$");
        if (mi.Success && mc.Success && string.Equals(mi.Groups[1].Value, mc.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
            return int.Parse(mi.Groups[2].Value).CompareTo(int.Parse(mc.Groups[2].Value));

        return string.CompareOrdinal(installed.Trim(), catalogBase.Trim());
    }

    private static bool TryExtractVersion(string raw, out Version? version) {
        Match match = Regex.Match(raw.Trim(), @"^[vV]?(\d+(?:\.\d+)+)");
        if (match.Success && Version.TryParse(match.Groups[1].Value, out Version? parsed)) {
            version = parsed;
            return true;
        }
        version = null;
        return false;
    }

    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase) {
        "driver", "drivers", "update", "software", "the", "and", "for", "hp", "consumer",
        "notebook", "pc", "graphics", "controller", "device", "adapter", "system"
    };

    private static (string DeviceName, string? DriverVersion)? FindBestDriverMatch(
            string title, List<(string DeviceName, string? DriverVersion)> installed) {

        string[] titleWords = Tokenize(title);
        if (titleWords.Length == 0) return null;

        (string DeviceName, string? DriverVersion)? best = null;
        int bestScore = 0;

        foreach ((string DeviceName, string? DriverVersion) candidate in installed) {
            string[] deviceWords = Tokenize(candidate.DeviceName);
            int score = titleWords.Count(w => deviceWords.Contains(w));
            if (score > bestScore) {
                bestScore = score;
                best = candidate;
            }
        }

        // Require at least two meaningful overlapping words (e.g. "nvidia" + "geforce") so a
        // single generic word can't cause a false match against the wrong device.
        return bestScore >= 2 ? best : null;
    }

    private static string[] Tokenize(string text) =>
        text.Split(new[] { ' ', '(', ')', '-', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !NoiseWords.Contains(w))
            .Select(w => w.ToLowerInvariant())
            .ToArray();

    private static async Task<JsonDocument> GetJsonAsync(string url) {
        using HttpResponseMessage response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static (string osTMSId, string platformId, string platformName) FindGenericOsEntry(JsonDocument osDoc, string targetName) {
        foreach (JsonElement platform in osDoc.RootElement.GetProperty("data").GetProperty("osAvailablePlatformsAnsOS")
                     .GetProperty("osPlatforms").EnumerateArray()) {
            string platformName = platform.GetProperty("name").GetString() ?? "";
            string platformId = platform.GetProperty("id").GetString() ?? "";
            foreach (JsonElement version in platform.GetProperty("osVersions").EnumerateArray()) {
                if (version.GetProperty("name").GetString() == targetName)
                    return (version.GetProperty("id").GetString()!, platformId, platformName);
            }
        }
        throw new HpUpdateException($"HP doesn't list {targetName} as a supported OS for this model.");
    }

    private static List<HpDriverUpdate> ParseUpdates(JsonDocument doc) {
        var results = new List<HpDriverUpdate>();
        if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            return results;

        foreach (JsonElement type in data.GetProperty("softwareTypes").EnumerateArray()) {
            string category = type.GetProperty("accordionName").GetString() ?? "";
            foreach (JsonElement item in type.GetProperty("softwareDriversList").EnumerateArray()) {
                JsonElement latest = item.GetProperty("latestVersionDriver");
                results.Add(new HpDriverUpdate {
                    Title = latest.GetProperty("title").GetString() ?? "",
                    Version = latest.GetProperty("version").GetString() ?? "",
                    Category = category,
                    ReleaseDate = latest.TryGetProperty("releaseDateString", out JsonElement rd) ? rd.GetString() ?? "" : "",
                    FileSize = latest.GetProperty("fileSize").GetString() ?? "",
                    DownloadUrl = latest.GetProperty("fileUrl").GetString() ?? "",
                    FileName = latest.GetProperty("detailInformation").GetProperty("fileName").GetString() ?? ""
                });
            }
        }
        return results;
    }
}
