using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ShiftControl.Maintenance;

public enum ProgramScope { Machine, Machine32, User }
public enum ProgramSort { Name, Size, Installed, Publisher }
public enum UninstallResult { Removed, StillInstalled, StillRunning, UninstallerMissing, Protected, Failed }
public enum LeftoverKind { Folder, Registry }

/// <summary>Um programa como o Windows o lista em "Aplicativos instalados" (chaves Uninstall do registro).</summary>
public sealed record InstalledProgram(
    ProgramScope Scope,
    string KeyName,
    string Name,
    string? Version,
    string? Publisher,
    DateTime? InstalledOn,
    string? InstallLocation,
    long? SizeBytes,
    string UninstallString,
    string? QuietUninstallString,
    string? MsiProductCode,
    string? DisplayIcon,
    string? MsiFilePath = null,
    bool SizeMeasured = false)
{
    public string Key => $"{Scope}|{KeyName}";
    public bool IsMsi => MsiProductCode is not null;
    public bool HasQuietUninstall => IsMsi || !string.IsNullOrWhiteSpace(QuietUninstallString);
}

public sealed record UninstallCommand(string File, string Arguments);
public sealed record UninstallOutcome(UninstallResult Result, int? ExitCode);
public sealed record Leftover(LeftoverKind Kind, string Path, long Bytes, RegistryHive Hive = RegistryHive.CurrentUser,
    RegistryView View = RegistryView.Default, string? SubKey = null);
public sealed record LeftoverRemoval(int Removed, int Failed, long FreedBytes, IReadOnlyList<string> RegistryBackups);

/// <summary>Onde procurar sobras. Os testes trocam por pastas e chaves temporárias.</summary>
public sealed record LeftoverRoots(IReadOnlyList<string> Folders, IReadOnlyList<(RegistryHive Hive, RegistryView View, string Path)> Registry,
    IReadOnlyList<string> Protected)
{
    public static LeftoverRoots Default(IEnumerable<string> extraProtected)
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        string local = F(Environment.SpecialFolder.LocalApplicationData);
        var folders = new[]
        {
            F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86), F(Environment.SpecialFolder.CommonApplicationData),
            F(Environment.SpecialFolder.ApplicationData), local, Path.Combine(local, "Programs"),
        }.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var protectedPaths = folders.Concat(
        [
            F(Environment.SpecialFolder.Windows), F(Environment.SpecialFolder.System), F(Environment.SpecialFolder.UserProfile),
            F(Environment.SpecialFolder.Desktop), F(Environment.SpecialFolder.MyDocuments), F(Environment.SpecialFolder.CommonProgramFiles),
            F(Environment.SpecialFolder.CommonProgramFilesX86), Path.Combine(F(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
            Path.Combine(F(Environment.SpecialFolder.UserProfile), "Downloads"),
        ]).Concat(extraProtected).Where(p => !string.IsNullOrEmpty(p)).ToList();
        return new(folders, [(RegistryHive.CurrentUser, RegistryView.Registry64, "Software"), (RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE")], protectedPaths);
    }
}

/// <summary>
/// Lista, desinstala e limpa sobras de programas. A desinstalação sempre usa o desinstalador oficial do programa
/// (ou msiexec com o código do produto); as sobras só são apagadas depois de confirmadas, pastas vão para a Lixeira
/// e chaves do registro são exportadas antes.
/// </summary>
public static partial class InstalledPrograms
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly string[] SharedRuntimePrefixes =
    [
        "Microsoft Visual C++", "Microsoft .NET", "Microsoft Windows Desktop Runtime", "Microsoft ASP.NET", "Microsoft Edge WebView2",
        "Microsoft Edge", "DirectX", "Microsoft Update Health", "Windows Software Development Kit", "NVIDIA Graphics Driver",
        "AMD Software", "Intel(R) Graphics", "Realtek",
    ];

    // Pastas e chaves que nunca são "sobras" de um programa: são compartilhadas pelo sistema ou por vários editores.
    private static readonly HashSet<string> NeverLeftover = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Classes", "Policies", "Wow6432Node", "Clients", "RegisteredApplications", "Common Files",
        "WindowsApps", "Packages", "Temp", "Programs", "Microsoft Corporation", "Intel", "NVIDIA Corporation", "Google", "Mozilla",
    };

    public static IReadOnlyList<InstalledProgram> List()
    {
        var list = new List<InstalledProgram>();
        Read(list, RegistryHive.LocalMachine, RegistryView.Registry64, ProgramScope.Machine);
        Read(list, RegistryHive.LocalMachine, RegistryView.Registry32, ProgramScope.Machine32);
        Read(list, RegistryHive.CurrentUser, RegistryView.Registry64, ProgramScope.User);
        // O mesmo programa registrado em duas vistas aparece uma vez só.
        var result = list.DistinctBy(p => (p.Name.ToUpperInvariant(), p.Version, p.Publisher)).ToList();
        // MSI que não informa pasta, ícone nem desinstalador próprio: o Windows Installer sabe onde pôs os arquivos.
        var unknown = result.Where(p => p.IsMsi && DriveOf(p) is null).Select(p => p.MsiProductCode!).ToList();
        if (unknown.Count > 0)
        {
            var files = MsiFilePaths(unknown);
            for (int i = 0; i < result.Count; i++)
                if (result[i].MsiProductCode is { } code && files.TryGetValue(code, out var file)) result[i] = result[i] with { MsiFilePath = file };
        }
        return result;
    }

    private const string MsiUserData = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData";

    /// <summary>
    /// Um arquivo instalado por cada produto MSI pedido, lido do registro de componentes do Windows Installer
    /// (UserData\&lt;SID&gt;\Components\&lt;componente&gt;: um valor por produto, com o caminho do arquivo).
    /// Prefere arquivos fora da pasta do Windows, que dizem melhor em que disco o programa está.
    /// </summary>
    internal static Dictionary<string, string> MsiFilePaths(IEnumerable<string> productCodes)
    {
        var byPacked = productCodes.Select(c => (Code: c, Packed: PackGuid(c))).Where(x => x.Packed is not null)
            .DistinctBy(x => x.Packed!, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Packed!, x => x.Code, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // já têm um arquivo fora da pasta do Windows
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var userData = root.OpenSubKey(MsiUserData);
            if (userData is null) return found;
            foreach (string sid in userData.GetSubKeyNames())
            {
                using var components = OpenOrNull(userData, sid + @"\Components");
                if (components is null) continue;
                foreach (string component in components.GetSubKeyNames())
                {
                    if (settled.Count == byPacked.Count) return found;
                    using var key = OpenOrNull(components, component);
                    if (key is null) continue;
                    foreach (string packed in key.GetValueNames())
                    {
                        if (!byPacked.TryGetValue(packed, out var code) || settled.Contains(code)) continue;
                        if (key.GetValue(packed) is not string raw || DriveLetter(raw) is null) continue; // ex.: "02:\..." é chave do registro
                        string path = raw.Length > 3 && raw[3] == '?' ? raw.Remove(3, 1) : raw; // "C:\?WINDOWS\..." (arquivo compartilhado)
                        if (!IsStoredByWindows(path)) { found[code] = path; settled.Add(code); }
                        else found.TryAdd(code, path);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return found;
    }

    private static RegistryKey? OpenOrNull(RegistryKey parent, string name)
    {
        try { return parent.OpenSubKey(name); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>GUID no formato "compactado" do Windows Installer: {12345678-ABCD-...} vira 87654321DCBA...</summary>
    internal static string? PackGuid(string code)
    {
        if (!Guid.TryParse(code, out var guid)) return null;
        string h = guid.ToString("N").ToUpperInvariant();
        static string Rev(string x) => new(x.Reverse().ToArray());
        var sb = new System.Text.StringBuilder(32);
        sb.Append(Rev(h[..8])).Append(Rev(h[8..12])).Append(Rev(h[12..16]));
        for (int i = 16; i < 32; i += 2) sb.Append(h[i + 1]).Append(h[i]);
        return sb.ToString();
    }

    private static void Read(List<InstalledProgram> list, RegistryHive hive, RegistryView view, ProgramScope scope)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall is null) return;
            foreach (string name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key is not null && FromValues(scope, name, key.GetValue) is { } program) list.Add(program);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    /// <summary>Converte os valores de uma chave Uninstall; null para componentes, atualizações e entradas sem desinstalador.</summary>
    internal static InstalledProgram? FromValues(ProgramScope scope, string keyName, Func<string, object?> get)
    {
        string? Str(string n) => get(n) is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        string? name = Str("DisplayName");
        string? uninstall = Str("UninstallString");
        if (name is null || uninstall is null) return null;
        if (get("SystemComponent") is int sc && sc == 1) return null;
        if (Str("ParentKeyName") is not null) return null;
        if (Str("ReleaseType") is { } rt && rt is "Update" or "Hotfix" or "Security Update" or "Service Pack") return null;

        string? msi = null;
        if (get("WindowsInstaller") is int wi && wi == 1 && IsGuid(keyName)) msi = keyName.ToUpperInvariant();
        else if (MsiCodeRegex().Match(uninstall) is { Success: true } m && uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            msi = m.Value.ToUpperInvariant();

        DateTime? installed = DateTime.TryParseExact(Str("InstallDate"), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        long? size = get("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null;
        string? location = Str("InstallLocation")?.Trim('"');
        return new InstalledProgram(scope, keyName, name, Str("DisplayVersion"), Str("Publisher"), installed, location, size, uninstall,
            Str("QuietUninstallString"), msi, Str("DisplayIcon"));
    }

    private static bool IsGuid(string s) => s.Length == 38 && Guid.TryParse(s, out _);

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex MsiCodeRegex();

    /// <summary>Valor do filtro de disco para programas cujo local não dá para identificar.</summary>
    public const string UnknownDrive = "?";

    /// <param name="drive">null = todos; "C:", "D:"…; ou <see cref="UnknownDrive"/>.</param>
    public static IReadOnlyList<InstalledProgram> Query(IEnumerable<InstalledProgram> all, string? search, ProgramSort sort, string? drive = null)
    {
        string q = search?.Trim() ?? "";
        var items = all.Where(p => q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (p.Publisher?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        if (drive is not null) items = items.Where(p => string.Equals(DriveOf(p) ?? UnknownDrive, drive, StringComparison.OrdinalIgnoreCase));
        return (sort switch
        {
            ProgramSort.Size => items.OrderByDescending(p => p.SizeBytes ?? -1).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            ProgramSort.Installed => items.OrderByDescending(p => p.InstalledOn ?? DateTime.MinValue).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            ProgramSort.Publisher => items.OrderBy(p => p.Publisher ?? "￿", StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => items.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
        }).ToList();
    }

    /// <summary>
    /// Disco onde o programa está ("C:", "D:"…), ou null se não der para saber (ex.: MSI que só informa o msiexec).
    /// Usa a pasta de instalação; sem ela, o ícone (em geral o executável do programa) e depois o desinstalador.
    /// Ícones e desinstaladores guardados pelo Windows (pasta do Windows, cache de pacotes) ficam no disco do sistema
    /// mesmo quando o programa está em outro, então só valem quando não há nenhuma outra pista.
    /// </summary>
    public static string? DriveOf(InstalledProgram p)
    {
        string? stored = null;
        foreach (var path in LocationCandidates(p))
        {
            if (DriveLetter(path) is not { } d) continue;
            if (!IsStoredByWindows(path)) return d;
            stored ??= d;
        }
        return stored;
    }

    private static IEnumerable<string> LocationCandidates(InstalledProgram p)
    {
        if (Expand(p.InstallLocation) is { Length: > 0 } loc) yield return loc;
        if (Expand(p.DisplayIcon?.Split(',')[0].Trim().Trim('"')) is { Length: > 0 } icon) yield return icon;
        if (SplitCommand(p.UninstallString)?.File is { } exe && Expand(exe) is { Length: > 0 } e) yield return e;
        if (p.MsiFilePath is { Length: > 0 } msi) yield return msi;
    }

    private static string? Expand(string? path) => string.IsNullOrWhiteSpace(path) ? null : Environment.ExpandEnvironmentVariables(path.Trim());

    private static string? DriveLetter(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')
            ? char.ToUpperInvariant(path[0]) + ":" : null;

    private static bool IsStoredByWindows(string path)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache");
        return (windows.Length > 0 && path.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase))
            || path.StartsWith(cache + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pasta que pode ser medida quando o programa não informa o tamanho: a de instalação ou, sem ela, a do executável
    /// (ícone ou desinstalador). Nunca uma pasta ampla (raiz do disco, Arquivos de Programas, perfil, AppData, Windows),
    /// que somaria outros programas.
    /// </summary>
    public static string? MeasurableFolder(InstalledProgram p)
    {
        string? folder = Expand(p.InstallLocation);
        if (folder is null)
        {
            string? exe = Expand(p.DisplayIcon?.Split(',')[0].Trim().Trim('"')) is { } icon && !IsStoredByWindows(icon) && Path.IsPathFullyQualified(icon)
                ? icon
                : SplitCommand(p.UninstallString)?.File is { } u && Expand(u) is { } e && !IsStoredByWindows(e) && Path.IsPathFullyQualified(e) ? e : null;
            folder = exe is null ? null : Path.GetDirectoryName(exe);
        }
        if (folder is null || DriveLetter(folder.Length == 2 ? folder + "\\" : folder) is null || SafeFull(folder) is not { } full) return null;
        if (IsStoredByWindows(full + "\\") || IsBroadFolder(full)) return null;
        return Directory.Exists(full) ? full : null;
    }

    private static bool IsBroadFolder(string full)
    {
        if (full.Length <= 3) return true; // "C:" / "C:\"
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f).TrimEnd(Path.DirectorySeparatorChar);
        string profile = F(Environment.SpecialFolder.UserProfile);
        string[] broad =
        [
            F(Environment.SpecialFolder.Windows), F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonProgramFiles), F(Environment.SpecialFolder.CommonProgramFilesX86), F(Environment.SpecialFolder.CommonApplicationData),
            profile, Path.GetDirectoryName(profile) ?? "", F(Environment.SpecialFolder.ApplicationData), F(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(F(Environment.SpecialFolder.LocalApplicationData), "Programs"), F(Environment.SpecialFolder.Desktop),
            F(Environment.SpecialFolder.MyDocuments), Path.Combine(profile, "Downloads"),
        ];
        return broad.Any(b => b.Length > 0 && string.Equals(b, full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Soma os arquivos da pasta, sem seguir links/junções e sem entrar nas pastas de <paramref name="exclude"/>
    /// (programas instalados dentro dela, como jogos dentro da pasta da Steam, que já têm o próprio tamanho).
    /// </summary>
    public static long MeasureFolder(string folder, IReadOnlyCollection<string> exclude, CancellationToken token = default)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(folder);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var dir = new DirectoryInfo(pending.Pop());
            try
            {
                foreach (var f in dir.EnumerateFiles("*", options)) total += f.Length;
                foreach (var sub in dir.EnumerateDirectories("*", options))
                    if (!exclude.Contains(sub.FullName.TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase)) pending.Push(sub.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return total;
    }

    internal static TimeSpan PerFolderLimit = TimeSpan.FromSeconds(30);

    /// <summary>Mede, em paralelo, os programas que não informam tamanho. Devolve o tamanho medido por chave do programa.</summary>
    public static async Task<IReadOnlyDictionary<string, long>> MeasureMissingSizesAsync(IReadOnlyList<InstalledProgram> all, CancellationToken token)
    {
        var folders = all.Select(p => (Program: p, Folder: MeasurableFolder(p))).ToList();
        var known = folders.Where(x => x.Folder is not null).Select(x => x.Folder!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        var targets = folders.Where(x => x.Program.SizeBytes is null && x.Folder is not null).ToList();
        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = token }, (x, ct) =>
        {
            // Limite por pasta: uma pasta enorme ou um disco lento não deixam a tela "medindo" para sempre;
            // quem passa do limite continua sem tamanho, em vez de mostrar um valor parcial.
            using var perFolder = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perFolder.CancelAfter(PerFolderLimit);
            var nested = known.Where(k => IsUnder(k, x.Folder!)).ToList();
            try
            {
                long size = MeasureFolder(x.Folder!, nested, perFolder.Token);
                if (size > 0) result[x.Program.Key] = size;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            return ValueTask.CompletedTask;
        });
        return result;
    }

    /// <summary>A própria Shift Control não é removida por esta tela: use o desinstalador dela no Windows.</summary>
    public static bool IsProtected(InstalledProgram p) =>
        p.KeyName.Equals("ShiftControl", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("Shift Control", StringComparison.OrdinalIgnoreCase);

    /// <summary>Componentes que outros programas usam: a remoção pede confirmação reforçada.</summary>
    public static bool IsSharedRuntime(InstalledProgram p) =>
        SharedRuntimePrefixes.Any(prefix => p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    #region Comando de desinstalação

    /// <summary>Separa executável e argumentos, com ou sem aspas, inclusive caminhos com espaços sem aspas.</summary>
    internal static UninstallCommand? SplitCommand(string commandLine)
    {
        string cmd = commandLine.Trim();
        if (cmd.Length == 0) return null;
        if (cmd[0] == '"')
        {
            int end = cmd.IndexOf('"', 1);
            if (end < 0) return null;
            return new(Environment.ExpandEnvironmentVariables(cmd[1..end]), cmd[(end + 1)..].Trim());
        }
        var m = ExeRegex().Match(cmd);
        return m.Success
            ? new(Environment.ExpandEnvironmentVariables(m.Groups[1].Value.Trim()), m.Groups[2].Value.Trim())
            : new(Environment.ExpandEnvironmentVariables(cmd), "");
    }

    [GeneratedRegex(@"^(.+?\.exe)(?=\s|$)(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExeRegex();

    /// <summary>Comando a executar; null quando o desinstalador não existe mais (entrada órfã).</summary>
    public static UninstallCommand? Command(InstalledProgram p, bool quiet)
    {
        if (p.IsMsi)
        {
            string msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            return new(msiexec, quiet ? $"/x {p.MsiProductCode} /passive /norestart" : $"/x {p.MsiProductCode}");
        }
        var command = SplitCommand(quiet && !string.IsNullOrWhiteSpace(p.QuietUninstallString) ? p.QuietUninstallString! : p.UninstallString);
        if (command is null) return null;
        bool rooted = Path.IsPathRooted(command.File);
        if (rooted && !File.Exists(command.File)) return null;
        if (!rooted && !File.Exists(Path.Combine(Environment.SystemDirectory, command.File))
            && !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), command.File))) return null;
        return command;
    }

    private static (RegistryHive, RegistryView) HiveOf(ProgramScope scope) => scope switch
    {
        ProgramScope.Machine32 => (RegistryHive.LocalMachine, RegistryView.Registry32),
        ProgramScope.User => (RegistryHive.CurrentUser, RegistryView.Registry64),
        _ => (RegistryHive.LocalMachine, RegistryView.Registry64),
    };

    /// <summary>A própria entrada da lista de programas, para remover quando o desinstalador não existe mais (entrada órfã).</summary>
    public static Leftover OrphanEntry(InstalledProgram p)
    {
        var (hive, view) = HiveOf(p.Scope);
        string sub = UninstallPath + "\\" + p.KeyName;
        return new(LeftoverKind.Registry, $"{(hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")}\\{sub}", 0, hive, view, sub);
    }

    public static bool StillInstalled(InstalledProgram p)
    {
        var (hive, view) = HiveOf(p.Scope);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(UninstallPath + "\\" + p.KeyName);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return true; }
    }

    /// <summary>
    /// Roda o desinstalador e espera ele terminar. O processo vai para um Job Object: muitos desinstaladores (Inno Setup,
    /// NSIS) se copiam para a pasta temporária e encerram o processo original na hora; o Job acompanha também essas cópias.
    /// Cancelar só para de esperar; nunca encerra o desinstalador no meio.
    /// </summary>
    public static async Task<UninstallOutcome> UninstallAsync(InstalledProgram p, bool quiet, CancellationToken ct = default)
    {
        if (IsProtected(p)) return new(UninstallResult.Protected, null);
        var command = Command(p, quiet);
        if (command is null) return new(UninstallResult.UninstallerMissing, null);

        Process? process;
        nint job = 0;
        try
        {
            process = Process.Start(new ProcessStartInfo(command.File, command.Arguments) { UseShellExecute = false });
            if (process is not null)
            {
                job = CreateJobObjectW(0, null);
                if (job != 0 && !AssignProcessToJobObject(job, process.Handle)) { CloseHandle(job); job = 0; }
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            // O desinstalador exige elevação (app rodando sem administrador): pede pelo UAC e acompanha só o processo.
            process = Process.Start(new ProcessStartInfo(command.File, command.Arguments) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception) { return new(UninstallResult.Failed, null); }
        if (process is null) return new(UninstallResult.Failed, null);

        using (process)
        {
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    bool running = job != 0 ? ActiveProcesses(job) > 0 : !process.HasExited;
                    if (!running) break;
                    await Task.Delay(500, ct);
                }
            }
            catch (OperationCanceledException) { return new(UninstallResult.StillRunning, null); }
            finally { if (job != 0) CloseHandle(job); }  // fechar o Job não encerra nada: não há KILL_ON_JOB_CLOSE
            int? exit = process.HasExited ? process.ExitCode : null;
            // Alguns desinstaladores apagam a chave do registro logo depois de o processo terminar.
            for (int i = 0; i < 6 && StillInstalled(p); i++) await Task.Delay(500, CancellationToken.None);
            return new(StillInstalled(p) ? UninstallResult.StillInstalled : UninstallResult.Removed, exit);
        }
    }

    private static uint ActiveProcesses(nint job)
    {
        var info = new JobBasicAccounting();
        return QueryInformationJobObject(job, 1, ref info, Marshal.SizeOf<JobBasicAccounting>(), out _) ? info.ActiveProcesses : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(nint job, int infoClass, ref JobBasicAccounting info, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);

    #endregion

    #region Sobras

    /// <summary>Nomes pelos quais as pastas e chaves do programa costumam ser criadas (com e sem versão/arquitetura).</summary>
    internal static IReadOnlyList<string> CandidateNames(InstalledProgram p)
    {
        var names = new List<string> { p.Name };
        string stripped = VersionSuffixRegex().Replace(ArchSuffixRegex().Replace(p.Name, ""), "").Trim();
        if (stripped.Length > 0) names.Add(stripped);
        if (!IsGuid(p.KeyName) && !p.KeyName.Contains('_')) names.Add(p.KeyName);
        return names.Select(n => n.Trim()).Where(n => n.Length >= 3 && !NeverLeftover.Contains(n) && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [GeneratedRegex(@"\s*[\(\[]?\b(x64|x86|64-bit|32-bit|64 bits|32 bits)\b[\)\]]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchSuffixRegex();

    [GeneratedRegex(@"\s+v?\d+(\.\d+)*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffixRegex();

    /// <summary>
    /// Sobras de um programa já desinstalado. Só nomes exatos, nunca pastas usadas por outro programa instalado,
    /// nunca as pastas do sistema e nenhuma pasta com link/junção no caminho.
    /// </summary>
    public static IReadOnlyList<Leftover> ScanLeftovers(InstalledProgram p, IReadOnlyList<InstalledProgram> stillInstalled, LeftoverRoots roots)
    {
        var names = CandidateNames(p);
        var publisher = p.Publisher is { Length: >= 2 } pub && !NeverLeftover.Contains(pub) && pub.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? pub : null;
        var folders = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.InstallLocation)) folders.Add(p.InstallLocation!);
        foreach (var root in roots.Folders)
            foreach (var n in names)
            {
                folders.Add(Path.Combine(root, n));
                if (publisher is not null) folders.Add(Path.Combine(root, publisher, n));
            }

        var others = stillInstalled.Where(o => o.Key != p.Key).ToList();
        var result = new List<Leftover>();
        foreach (var folder in folders.Select(SafeFull).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            if (SafeLeftoverFolder(folder, roots, others)) result.Add(new(LeftoverKind.Folder, folder, FolderSize(folder)));

        foreach (var (hive, view, basePath) in roots.Registry)
            foreach (var n in names)
            {
                AddKey(result, hive, view, basePath + "\\" + n, others);
                if (publisher is not null) AddKey(result, hive, view, basePath + "\\" + publisher + "\\" + n, others);
            }
        return result;
    }

    private static string? SafeFull(string path)
    {
        try { return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    internal static bool SafeLeftoverFolder(string folder, LeftoverRoots roots, IReadOnlyList<InstalledProgram> others)
    {
        if (!Directory.Exists(folder)) return false;
        string root = Path.GetPathRoot(folder) ?? "";
        if (folder.Length <= root.Length || folder[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length < 2) return false;
        if (NeverLeftover.Contains(Path.GetFileName(folder))) return false;
        // Nem uma pasta protegida, nem uma pasta que contenha uma protegida (ex.: InstallLocation = perfil do usuário).
        foreach (var prot in roots.Protected.Select(SafeFull).OfType<string>())
            if (SamePath(folder, prot) || IsUnder(prot, folder)) return false;
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        // Compartilhada: outro programa instalado mora nela, dentro dela ou acima dela.
        foreach (var o in others)
            foreach (var used in UsedPaths(o))
                if (SamePath(used, folder) || IsUnder(used, folder) || IsUnder(folder, used)) return false;
        return true;
    }

    private static IEnumerable<string> UsedPaths(InstalledProgram o)
    {
        if (SafeFull(o.InstallLocation ?? "") is { Length: > 3 } loc) yield return loc;
        if (SplitCommand(o.UninstallString)?.File is { } exe && Path.IsPathRooted(exe) && SafeFull(Path.GetDirectoryName(exe) ?? "") is { Length: > 3 } dir)
            yield return dir;
        if (o.DisplayIcon?.Split(',')[0].Trim('"') is { Length: > 3 } icon && Path.IsPathRooted(icon) && SafeFull(Path.GetDirectoryName(icon) ?? "") is { Length: > 3 } idir)
            yield return idir;
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool IsUnder(string path, string parent) =>
        path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static long FolderSize(string folder)
    {
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions
                         { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                total += f.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    private static void AddKey(List<Leftover> result, RegistryHive hive, RegistryView view, string subKey, IReadOnlyList<InstalledProgram> others)
    {
        string last = subKey[(subKey.LastIndexOf('\\') + 1)..];
        if (NeverLeftover.Contains(last)) return;
        if (others.Any(o => CandidateNames(o).Contains(last, StringComparer.OrdinalIgnoreCase))) return; // ainda em uso por outro programa
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(subKey);
            if (key is null) return;
            string display = $"{(hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")}\\{subKey}{(view == RegistryView.Registry32 && hive == RegistryHive.LocalMachine ? " (32 bits)" : "")}";
            if (result.Any(r => r.Kind == LeftoverKind.Registry && r.Hive == hive && r.View == view
                                && string.Equals(r.SubKey, subKey, StringComparison.OrdinalIgnoreCase))) return;
            result.Add(new(LeftoverKind.Registry, display, 0, hive, view, subKey));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    /// <summary>
    /// Remove as sobras escolhidas. Pastas vão para a Lixeira (recuperáveis); chaves do registro são exportadas para
    /// <paramref name="backupFolder"/> antes de serem apagadas e, se o backup falhar, não são apagadas.
    /// </summary>
    public static async Task<LeftoverRemoval> RemoveLeftoversAsync(IReadOnlyList<Leftover> items, string backupFolder, CancellationToken ct = default)
    {
        int removed = 0, failed = 0;
        long freed = 0;
        var backups = new List<string>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Kind == LeftoverKind.Folder)
            {
                // Rechecagem no momento da remoção: um link criado depois da análise não é seguido.
                bool linked = false;
                for (var dir = new DirectoryInfo(item.Path); dir is not null; dir = dir.Parent)
                    if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) { linked = true; break; }
                if (!linked && Directory.Exists(item.Path) && RecycleFolder(item.Path) && !Directory.Exists(item.Path)) { removed++; freed += item.Bytes; }
                else failed++;
                continue;
            }
            Directory.CreateDirectory(backupFolder);
            // A vista vai no nome do arquivo: a restauração precisa importar na mesma vista (32/64 bits).
            string viewTag = item.View == RegistryView.Registry32 ? "32" : "64";
            string file = Path.Combine(backupFolder, $"{backups.Count + 1:00}-{SafeFileName(item.SubKey!)}.{viewTag}.reg");
            string regPath = (item.Hive == RegistryHive.CurrentUser ? "HKCU\\" : "HKLM\\") + item.SubKey;
            string viewArg = item.View == RegistryView.Registry32 ? " /reg:32" : " /reg:64";
            var export = await Shell.RunAsync(Path.Combine(Environment.SystemDirectory, "reg.exe"), $"export \"{regPath}\" \"{file}\" /y{viewArg}", ct: ct);
            if (export.ExitCode != 0 || !File.Exists(file)) { failed++; continue; }
            try
            {
                using var root = RegistryKey.OpenBaseKey(item.Hive, item.View);
                root.DeleteSubKeyTree(item.SubKey!, throwOnMissingSubKey: false);
                backups.Add(file);
                removed++;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { failed++; }
        }
        return new(removed, failed, freed, backups);
    }

    /// <summary>
    /// Pasta dos backups do registro: %ProgramData%\ShiftControl\uninstall-backups, gravável só por Administradores e SYSTEM.
    /// O app roda elevado e a restauração importa estes arquivos: numa pasta gravável pelo usuário, um processo sem privilégio
    /// poderia trocar um backup e fazer o app importar chaves arbitrárias em HKLM.
    /// </summary>
    public static string BackupRootPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ShiftControl", "uninstall-backups");

    /// <summary>Cria (ou corrige) a pasta de backups com as permissões restritas. Exige o app elevado.</summary>
    public static string ProtectedBackupRoot()
    {
        string root = BackupRootPath;
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
        var none = System.Security.AccessControl.PropagationFlags.None;
        var allow = System.Security.AccessControl.AccessControlType.Allow;
        var full = System.Security.AccessControl.FileSystemRights.FullControl;
        security.AddAccessRule(new(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null), full, inherit, none, allow));
        security.AddAccessRule(new(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null), full, inherit, none, allow));
        security.AddAccessRule(new(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
            System.Security.AccessControl.FileSystemRights.ReadAndExecute, inherit, none, allow));
        var dir = new DirectoryInfo(root);
        if (!dir.Exists) dir.Create(security);
        else dir.SetAccessControl(security);
        return root;
    }

    /// <summary>Só arquivos criados pelo app elevado (dono Administradores ou SYSTEM) são confiáveis para importar.</summary>
    public static bool OwnedByAdministrators(string file)
    {
        try
        {
            var owner = new FileInfo(file).GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
            return owner is not null && (owner.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid)
                                         || owner.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Reimporta backups do registro feitos por <see cref="RemoveLeftoversAsync"/>. Cada arquivo precisa estar dentro de
    /// <paramref name="root"/> e passar em <paramref name="trusted"/> (por padrão, dono Administradores/SYSTEM).
    /// </summary>
    public static async Task<(int Restored, int Refused)> RestoreRegistryAsync(IReadOnlyList<string> files, string root, Func<string, bool>? trusted = null,
        CancellationToken ct = default)
    {
        trusted ??= OwnedByAdministrators;
        string rootFull = SafeFull(root) ?? "";
        int restored = 0, refused = 0;
        foreach (var f in files)
        {
            string? full = SafeFull(f);
            if (full is null || !IsUnder(full, rootFull) || !full.EndsWith(".reg", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)
                || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 || !trusted(full)) { refused++; continue; }
            string view = full.EndsWith(".32.reg", StringComparison.OrdinalIgnoreCase) ? " /reg:32" : " /reg:64";
            var r = await Shell.RunAsync(Path.Combine(Environment.SystemDirectory, "reg.exe"), $"import \"{full}\"{view}", ct: ct);
            if (r.ExitCode == 0) restored++; else refused++;
        }
        return (restored, refused);
    }

    private static string SafeFileName(string text)
    {
        var chars = text.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        string s = new(chars);
        return s.Length > 80 ? s[..80] : s;
    }

    private static bool RecycleFolder(string path)
    {
        var op = new ShFileOp
        {
            wFunc = 3, // FO_DELETE
            pFrom = path + "\0\0",
            fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400, // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI
        };
        return SHFileOperationW(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperationW(ref ShFileOp op);

    #endregion
}
