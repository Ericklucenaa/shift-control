using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using System.Text;

namespace ShiftControl.Maintenance;

/// <summary>Utilitários comuns: elevação, WMI e execução de ferramentas do Windows com saída capturada.</summary>
public static class Shell
{
    private static readonly Lazy<bool> Elevated = new(() =>
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    });

    public static bool IsElevated => Elevated.Value;

    public static List<T> Wmi<T>(string query, Func<ManagementBaseObject, T> map, string scope = @"root\cimv2")
    {
        var list = new List<T>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var results = searcher.Get();
            foreach (var o in results)
                using (o)
                    list.Add(map(o));
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        return list;
    }

    public sealed record ToolResult(int ExitCode, string Output);

    /// <summary>
    /// Executa uma ferramenta do sistema (pnputil, sfc, DISM, netsh…). <paramref name="unicode"/> para ferramentas que
    /// escrevem UTF-16 quando redirecionadas (sfc). Cada linha é entregue a <paramref name="onLine"/> enquanto roda.
    /// </summary>
    public static async Task<ToolResult> RunAsync(string file, string arguments, Action<string>? onLine = null, bool unicode = false,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = unicode ? Encoding.Unicode : Encoding.GetEncoding(OemCodePage()),
            StandardErrorEncoding = unicode ? Encoding.Unicode : Encoding.GetEncoding(OemCodePage()),
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar {file}.");
        var sb = new StringBuilder();
        var gate = new Lock();
        void Line(string? l)
        {
            if (l is null) return;
            l = l.Replace("\0", "").TrimEnd();
            if (l.Length == 0) return;
            lock (gate) sb.AppendLine(l);
            onLine?.Invoke(l);
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        p.WaitForExit();
        return new ToolResult(p.ExitCode, sb.ToString());
    }

    /// <summary>Ferramentas de console do Windows escrevem na página de código OEM (850 em pt-BR).</summary>
    private static int OemCodePage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
    }

    public static string SystemTool(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    public static string Size(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return v.ToString(i == 0 ? "0" : "0.0", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")) + " " + u[i];
    }
}
