using System.Globalization;
using System.Management;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ShiftControl.Maintenance;

public sealed record DriverInfo(
    string DeviceName,
    string DeviceClass,
    string? Manufacturer,
    string? Provider,
    string? Version,
    DateTime? Date,
    string? InfName,
    bool Signed,
    string? DeviceId,
    int ErrorCode)
{
    public bool HasProblem => ErrorCode is not (0 or 22 or 45);
    public bool MissingDriver => ErrorCode == 28;
    /// <summary>Classes que mais influenciam desempenho e estabilidade: aparecem primeiro.</summary>
    public bool Important => DeviceClass.ToUpperInvariant() is "DISPLAY" or "NET" or "MEDIA" or "BLUETOOTH" or "AUDIOENDPOINT" or "HDC" or "SCSIADAPTER" or "USB"
                             || (DeviceClass.Equals("System", StringComparison.OrdinalIgnoreCase) && (Manufacturer?.Contains("AMD", StringComparison.OrdinalIgnoreCase) == true
                                                             || Manufacturer?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true));
}

public sealed record DriverUpdate(string UpdateId, string Title, string? Manufacturer, string? DeviceClass, DateTime? DriverDate, string? HardwareId, long SizeBytes);

public enum GpuVendor { Nvidia, Amd, Intel, Other }

public sealed record GpuDriverStatus(
    string GpuName,
    GpuVendor Vendor,
    string? InstalledVersion,
    DateTime? InstalledDate,
    string? LatestVersion,
    DateTime? LatestDate,
    string? DownloadUrl,
    string OfficialPage,
    bool? UpdateAvailable,
    string? LookupError);

public sealed record DriverOperationResult(bool Ok, string Summary, string Output);

/// <summary>
/// Driver Center: inventário (WMI), atualizações pelo Windows Update (API oficial do Windows), driver da placa de vídeo
/// pelo serviço público da NVIDIA, e backup/restauração com o PnPUtil. Nada de catálogos de terceiros.
/// </summary>
public static class Drivers
{
    #region Inventário

    public static IReadOnlyList<DriverInfo> Inventory()
    {
        // Erros por dispositivo (ConfigManagerErrorCode) vêm do Win32_PnPEntity; versão/data/assinatura do Win32_PnPSignedDriver.
        var errors = Shell.Wmi("SELECT DeviceID, Name, PNPClass, Manufacturer, ConfigManagerErrorCode FROM Win32_PnPEntity",
            o => (Id: o["DeviceID"] as string ?? "", Name: o["Name"] as string, Class: o["PNPClass"] as string, Mfr: o["Manufacturer"] as string,
                  Code: Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0)))
            .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var signed = Shell.Wmi(
            "SELECT DeviceName, DeviceClass, Manufacturer, DriverProviderName, DriverVersion, DriverDate, InfName, IsSigned, DeviceID FROM Win32_PnPSignedDriver",
            o => new DriverInfo(
                (o["DeviceName"] as string ?? "").Trim(),
                o["DeviceClass"] as string ?? "Other",
                (o["Manufacturer"] as string)?.Trim(),
                (o["DriverProviderName"] as string)?.Trim(),
                o["DriverVersion"] as string,
                ParseWmiDate(o["DriverDate"] as string),
                o["InfName"] as string,
                o["IsSigned"] is true,
                o["DeviceID"] as string,
                0))
            .Where(d => d.DeviceName.Length > 0)
            .Select(d => d.DeviceId is { } id && errors.TryGetValue(id, out var e) ? d with { ErrorCode = e.Code } : d)
            .ToList();

        // Dispositivos com problema sem driver assinado registrado (ex.: código 28, sem driver) também entram.
        var known = signed.Select(d => d.DeviceId).Where(i => i is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var e in errors.Values.Where(e => e.Code is not (0 or 22 or 45) && !known.Contains(e.Id)))
            signed.Add(new DriverInfo(e.Name ?? "Dispositivo desconhecido", e.Class ?? "Other", e.Mfr, null, null, null, null, false, e.Id, e.Code));

        return signed
            .OrderByDescending(d => d.HasProblem)
            .ThenByDescending(d => d.Important)
            .ThenBy(d => d.DeviceClass)
            .ThenBy(d => d.DeviceName)
            .ToList();
    }

    internal static DateTime? ParseWmiDate(string? wmi) =>
        wmi is { Length: >= 8 } && DateTime.TryParseExact(wmi[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    #endregion

    #region Windows Update

    /// <summary>Drivers oferecidos pelo Windows Update para este PC (só consulta). Pode levar de segundos a minutos.</summary>
    public static IReadOnlyList<DriverUpdate> SearchWindowsUpdate()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("Windows Update indisponível.");
        dynamic session = Activator.CreateInstance(type)!;
        session.ClientApplicationID = "Shift Control";
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.Online = true;
        dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Driver'");
        var list = new List<DriverUpdate>();
        int count = result.Updates.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic u = result.Updates.Item(i);
            DateTime? date = null;
            try { date = (DateTime)u.DriverVerDate; } catch (Exception) { }
            long size = 0;
            try { size = Convert.ToInt64(u.MaxDownloadSize); } catch (Exception) { }
            list.Add(new DriverUpdate((string)u.Identity.UpdateID, (string)u.Title, Try(() => (string)u.DriverManufacturer),
                Try(() => (string)u.DriverClass), date, Try(() => (string)u.DriverHardwareID), size));
        }
        return list;
    }

    private static string? Try(Func<string> f)
    {
        try { return f(); } catch (Exception) { return null; }
    }

    /// <summary>Baixa e instala os drivers escolhidos pelo Windows Update. Exige administrador.</summary>
    public static DriverOperationResult InstallFromWindowsUpdate(IReadOnlyCollection<string> updateIds, Action<string>? progress = null)
    {
        if (!Shell.IsElevated) return new(false, "requiresAdmin", "");
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session")!;
        dynamic session = Activator.CreateInstance(type)!;
        session.ClientApplicationID = "Shift Control";
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Driver'");
        dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
        int count = result.Updates.Count;
        var titles = new List<string>();
        for (int i = 0; i < count; i++)
        {
            dynamic u = result.Updates.Item(i);
            if (!updateIds.Contains((string)u.Identity.UpdateID)) continue;
            if (!(bool)u.EulaAccepted) u.AcceptEula();
            coll.Add(u);
            titles.Add((string)u.Title);
        }
        if ((int)coll.Count == 0) return new(false, "notFound", "");

        progress?.Invoke("download");
        dynamic downloader = session.CreateUpdateDownloader();
        downloader.Updates = coll;
        downloader.Download();
        progress?.Invoke("install");
        dynamic installer = session.CreateUpdateInstaller();
        installer.Updates = coll;
        dynamic ir = installer.Install();
        int code = ir.ResultCode; // 2 = sucesso, 3 = sucesso com erros, 4 = falha, 5 = abortado
        bool reboot = ir.RebootRequired;
        string summary = code switch { 2 => reboot ? "okReboot" : "ok", 3 => "partial", _ => "failed" };
        return new(code == 2, summary, string.Join("\n", titles));
    }

    #endregion

    #region Placa de vídeo

    public static GpuVendor VendorOf(string name) =>
        name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Nvidia
        : name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Amd
        : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Intel
        : GpuVendor.Other;

    /// <summary>
    /// Versão "comercial" da NVIDIA a partir da versão do Windows: 32.0.16.2114 → últimos 5 dígitos (62114) → 621.14.
    /// </summary>
    public static string? NvidiaVersion(string? windowsVersion)
    {
        if (string.IsNullOrWhiteSpace(windowsVersion)) return null;
        string digits = windowsVersion.Replace(".", "");
        if (digits.Length < 5 || !digits.All(char.IsDigit)) return null;
        string last = digits[^5..];
        return $"{last[..3]}.{last[3..]}";
    }

    public static string OfficialPage(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => "https://www.nvidia.com/pt-br/drivers/",
        GpuVendor.Amd => "https://www.amd.com/pt/support/download/drivers.html",
        GpuVendor.Intel => "https://www.intel.com.br/content/www/br/pt/support/detect.html",
        _ => "https://www.catalog.update.microsoft.com/",
    };

    /// <summary>
    /// Compara o driver instalado com o último Game Ready (WHQL, DCH) publicado pela NVIDIA para o modelo.
    /// Usa o mesmo serviço da página de downloads da NVIDIA. Para AMD e Intel, que não têm consulta pública
    /// equivalente, retorna só a versão instalada e o endereço oficial.
    /// </summary>
    public static async Task<IReadOnlyList<GpuDriverStatus>> CheckGpusAsync(HttpClient http, CancellationToken ct = default)
    {
        var gpus = Shell.Wmi("SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController",
                o => (Name: (o["Name"] as string ?? "").Trim(), Version: o["DriverVersion"] as string, Date: ParseWmiDate(o["DriverDate"] as string)))
            .Where(g => VendorOf(g.Name) != GpuVendor.Other)
            .ToList();
        var list = new List<GpuDriverStatus>();
        foreach (var g in gpus)
        {
            var vendor = VendorOf(g.Name);
            if (vendor != GpuVendor.Nvidia)
            {
                list.Add(new GpuDriverStatus(g.Name, vendor, g.Version, g.Date, null, null, null, OfficialPage(vendor), null, "noApi"));
                continue;
            }
            string? installed = NvidiaVersion(g.Version);
            try
            {
                var (latest, date, url) = await NvidiaLatestAsync(http, g.Name, ct);
                bool? newer = installed is not null && latest is not null ? CompareVersions(latest, installed) > 0 : null;
                list.Add(new GpuDriverStatus(g.Name, vendor, installed, g.Date, latest, date, url, OfficialPage(vendor), newer, latest is null ? "modelNotFound" : null));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or System.Xml.XmlException)
            {
                list.Add(new GpuDriverStatus(g.Name, vendor, installed, g.Date, null, null, null, OfficialPage(vendor), null, "network"));
            }
        }
        return list;
    }

    internal static int CompareVersions(string a, string b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) ? va.CompareTo(vb) : string.CompareOrdinal(a, b);

    private static async Task<(string? Version, DateTime? Date, string? Url)> NvidiaLatestAsync(HttpClient http, string gpuName, CancellationToken ct)
    {
        string xml = await http.GetStringAsync("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3", ct);
        var (psid, pfid) = MatchNvidiaProduct(xml, gpuName);
        if (pfid is null) return (null, null, null);
        string url = $"https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid={psid}&pfid={pfid}&osID=57&languageCode=1033&isWHQL=1&dch=1&upCRD=0&sort1=0&numberOfResults=1";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        var info = doc.RootElement.GetProperty("IDS")[0].GetProperty("downloadInfo");
        string version = info.GetProperty("Version").GetString()!;
        DateTime? date = DateTime.TryParseExact(info.GetProperty("ReleaseDateTime").GetString(), "ddd MMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        return (version, date, Uri.UnescapeDataString(info.GetProperty("DownloadURL").GetString() ?? ""));
    }

    /// <summary>Encontra (série, produto) no catálogo da NVIDIA pelo nome exato da placa; o nome mais longo vence ("5070 Ti" antes de "5070").</summary>
    internal static (string? Psid, string? Pfid) MatchNvidiaProduct(string xml, string gpuName)
    {
        string Norm(string s) => Regex.Replace(s.Replace("NVIDIA", "", StringComparison.OrdinalIgnoreCase), @"\s+", " ").Trim().ToLowerInvariant();
        string target = Norm(gpuName);
        var best = XDocument.Parse(xml).Descendants("LookupValue")
            .Select(e => (Parent: (string?)e.Attribute("ParentID"), Name: (string?)e.Element("Name") ?? "", Value: (string?)e.Element("Value")))
            .Where(e => Norm(e.Name) == target)
            .OrderByDescending(e => e.Name.Length)
            .FirstOrDefault();
        return best.Value is null ? (null, null) : (best.Parent, best.Value);
    }

    #endregion

    #region Backup e restauração (PnPUtil)

    /// <summary>Exporta todos os drivers de terceiros instalados (pacotes oemNN.inf) para a pasta. Exige administrador.</summary>
    public static async Task<DriverOperationResult> BackupAsync(string folder, Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (!Shell.IsElevated) return new(false, "requiresAdmin", "");
        Directory.CreateDirectory(folder);
        var r = await Shell.RunAsync(Shell.SystemTool("pnputil.exe"), $"/export-driver * \"{folder}\"", onLine, ct: ct);
        int count = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.inf", SearchOption.AllDirectories).Length : 0;
        return new(r.ExitCode == 0 && count > 0, r.ExitCode == 0 ? "ok" : "failed", $"{count}\n{r.Output}");
    }

    /// <summary>Adiciona e instala todos os drivers de uma pasta de backup. Exige administrador.</summary>
    public static async Task<DriverOperationResult> RestoreAsync(string folder, Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (!Shell.IsElevated) return new(false, "requiresAdmin", "");
        if (!Directory.Exists(folder)) return new(false, "missing", "");
        var r = await Shell.RunAsync(Shell.SystemTool("pnputil.exe"), $"/add-driver \"{Path.Combine(folder, "*.inf")}\" /subdirs /install", onLine, ct: ct);
        // 3010 = sucesso, reinício necessário; 259 = nada a fazer (drivers já presentes).
        bool ok = r.ExitCode is 0 or 3010 or 259;
        return new(ok, r.ExitCode == 3010 ? "okReboot" : ok ? "ok" : "failed", r.Output);
    }

    #endregion
}
