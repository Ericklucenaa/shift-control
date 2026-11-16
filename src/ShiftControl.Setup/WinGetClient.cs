using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ShiftControl.Setup;

public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>Executa um processo e entrega cada linha (inclusive as de progresso, separadas por \r) enquanto roda.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, Action<string>? onLine, CancellationToken ct);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar {file}.");
        var all = new StringBuilder();
        var lineLock = new Lock();

        async Task Pump(StreamReader reader)
        {
            var buffer = new char[1024];
            var line = new StringBuilder();
            int n;
            while ((n = await reader.ReadAsync(buffer, CancellationToken.None)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    char c = buffer[i];
                    if (c is '\r' or '\n')
                    {
                        if (line.Length > 0) Emit(line.ToString());
                        line.Clear();
                    }
                    else line.Append(c);
                }
            }
            if (line.Length > 0) Emit(line.ToString());
        }

        void Emit(string text)
        {
            lock (lineLock) all.AppendLine(text);
            onLine?.Invoke(text);
        }

        var pumps = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        await pumps;
        return new ProcessResult(process.ExitCode, all.ToString());
    }
}

/// <summary>
/// Cliente do Windows Package Manager (winget.exe). Todas as instalações vêm das fontes oficiais do WinGet ou da
/// Microsoft Store, com o hash do instalador conferido pelo próprio WinGet.
/// </summary>
public sealed class WinGetClient
{
    private static readonly string[] Common = ["--accept-source-agreements", "--disable-interactivity"];
    private readonly IProcessRunner _runner;
    private readonly ILogger _log;

    public WinGetClient(IProcessRunner? runner = null, ILogger<WinGetClient>? log = null)
    {
        _runner = runner ?? new ProcessRunner();
        _log = log ?? NullLogger<WinGetClient>.Instance;
    }

    public string Executable { get; init; } = "winget";

    /// <summary>Versão do WinGet, ou nulo se não estiver disponível neste Windows.</summary>
    public async Task<string?> VersionAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _runner.RunAsync(Executable, ["--version"], null, ct);
            return r.ExitCode == 0 ? r.Output.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // winget.exe não existe (App Installer ausente)
        }
    }

    public async Task<IReadOnlyList<InstalledEntry>> ListInstalledAsync(CancellationToken ct = default)
    {
        var r = await _runner.RunAsync(Executable, ["list", .. Common], null, ct);
        if (r.ExitCode != 0) _log.LogWarning("winget list terminou com {Code}.", WinGetCodes.Hex(r.ExitCode));
        return WinGetListParser.Parse(r.Output.Split('\n'));
    }

    internal static IReadOnlyList<string> InstallArgs(CatalogPackage p) =>
        ["install", "--id", p.Id, "--exact", "--source", p.Source, "--silent",
         "--accept-package-agreements", .. Common];

    internal static IReadOnlyList<string> UpgradeArgs(string id, string source) =>
        ["upgrade", "--id", id, "--exact", "--source", source, "--silent", "--accept-package-agreements", .. Common];

    internal static IReadOnlyList<string> UninstallArgs(string id) =>
        ["uninstall", "--id", id, "--exact", "--silent", .. Common];

    public Task<(WinGetOutcome Outcome, ProcessResult Result)> InstallAsync(CatalogPackage p, Action<string>? onLine, CancellationToken ct) =>
        RunMapped(InstallArgs(p), onLine, ct);

    public Task<(WinGetOutcome Outcome, ProcessResult Result)> UpgradeAsync(string id, string source, Action<string>? onLine, CancellationToken ct) =>
        RunMapped(UpgradeArgs(id, source), onLine, ct);

    public Task<(WinGetOutcome Outcome, ProcessResult Result)> UninstallAsync(string id, Action<string>? onLine, CancellationToken ct) =>
        RunMapped(UninstallArgs(id), onLine, ct);

    private async Task<(WinGetOutcome, ProcessResult)> RunMapped(IReadOnlyList<string> args, Action<string>? onLine, CancellationToken ct)
    {
        var r = await _runner.RunAsync(Executable, args, onLine, ct);
        var outcome = WinGetCodes.Map(r.ExitCode);
        _log.LogInformation("winget {Verb} {Id}: {Outcome} ({Code}).", args[0], args[2], outcome, WinGetCodes.Hex(r.ExitCode));
        return (outcome, r);
    }

    /// <summary>
    /// Linha de progresso do WinGet em texto legível (ex.: "45%" ou "Instalando…"), descartando as barras animadas.
    /// Retorna nulo para linhas que não interessam ao usuário.
    /// </summary>
    public static string? Progress(string line)
    {
        var t = line.Trim();
        if (t.Length == 0) return null;
        // Spinner: "-", "\", "|", "/" sozinhos.
        if (t.Length == 1 && "-\\|/".Contains(t[0])) return null;
        // Barras: "██████▒▒▒▒  12.3 MB / 40.1 MB" → mantém os números.
        int lastBlock = t.LastIndexOfAny(['█', '▒']);
        if (lastBlock >= 0) t = t[(lastBlock + 1)..].Trim();
        return t.Length == 0 ? null : t;
    }
}
