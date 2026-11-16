using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShiftControl.Overlay;

namespace ShiftControl.Gaming;

/// <summary>
/// Captura de frames pelo Intel PresentMon (licença MIT, redistribuído junto ao app) rodando como processo
/// filho com saída CSV em stdout. O PresentMon consome eventos ETW do DXGI, D3D9 e DxgKrnl, cobrindo
/// DirectX 9–12, Vulkan e OpenGL no Windows 10/11, e informa o PID de cada frame. Nada é injetado no jogo.
/// </summary>
public sealed class PresentMonProvider : IFrameMetricsProvider
{
    public const string SessionName = "ShiftControl";

    private readonly string _executable;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private Process? _process;
    private JobObject? _job;
    private Thread? _reader;
    private volatile bool _stopping;

    public PresentMonProvider(string executable, ILogger<PresentMonProvider>? log = null)
    {
        _executable = executable;
        _log = log ?? NullLogger<PresentMonProvider>.Instance;
    }

    public FrameCaptureState State { get; private set; } = FrameCaptureState.Stopped;
    public string? Detail { get; private set; }
    public long FramesRead { get; private set; }

    public event EventHandler<FrameCaptureState>? StateChanged;
    public event PresentHandler? Present;

    private void SetState(FrameCaptureState state, string? detail = null)
    {
        State = state;
        Detail = detail;
        try { StateChanged?.Invoke(this, state); }
        catch (Exception ex) { _log.LogError(ex, "Assinante de StateChanged falhou."); }
    }

    /// <summary>
    /// Argumentos: todos os processos (a detecção de jogo usa a atividade de cada um), sem rastrear entrada
    /// nem trabalho de GPU (não usados aqui; reduz o custo), sessão com nome próprio que substitui sobras
    /// de uma execução anterior interrompida.
    /// </summary>
    internal static string Arguments =>
        $"--output_stdout --no_console_stats --no_track_input --no_track_gpu --session_name {SessionName} --stop_existing_session";

    public void Start()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) return;
            if (!File.Exists(_executable))
            {
                SetState(FrameCaptureState.Missing, _executable);
                _log.LogWarning("PresentMon não encontrado em {Path}.", _executable);
                return;
            }

            _stopping = false;
            SetState(FrameCaptureState.Starting);
            var psi = new ProcessStartInfo(_executable, Arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            try
            {
                _process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start retornou nulo.");
                _job ??= new JobObject();
                _job.Assign(_process.Handle);
                _process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha ao iniciar o PresentMon.");
                SetState(FrameCaptureState.Faulted, ex.Message);
                return;
            }

            var process = _process;
            _reader = new Thread(() => ReadLoop(process)) { IsBackground = true, Name = "ShiftControl.PresentMon", Priority = ThreadPriority.BelowNormal };
            _reader.Start();
            _log.LogInformation("PresentMon iniciado (PID {Pid}).", process.Id);
        }
    }

    private void ReadLoop(Process process)
    {
        var csv = new PresentMonCsv();
        var errors = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (errors) if (errors.Length < 4000) errors.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();

        try
        {
            string? line;
            var stdout = process.StandardOutput;
            while ((line = stdout.ReadLine()) is not null)
            {
                if (!csv.HasHeader)
                {
                    if (csv.TryReadHeader(line))
                    {
                        SetState(FrameCaptureState.Running);
                        _log.LogInformation("Captura de frames ativa.");
                    }
                    continue;
                }
                if (PresentMonCsv.LooksLikeHeader(line)) { csv.TryReadHeader(line); continue; }
                if (!csv.TryParse(line, out var record)) continue;
                FramesRead++;
                var handler = Present;
                if (handler is null) continue;
                try { handler(in record); }
                catch (Exception ex) { _log.LogError(ex, "Falha ao processar um frame."); }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (!_stopping) _log.LogWarning(ex, "Leitura do PresentMon interrompida.");
        }

        try { process.WaitForExit(2000); } catch (InvalidOperationException) { }
        if (_stopping)
        {
            SetState(FrameCaptureState.Stopped);
            return;
        }

        string err;
        lock (errors) err = errors.ToString();
        int code = process.HasExited ? process.ExitCode : -1;
        if (err.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || err.Contains("Performance Log Users", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("PresentMon sem permissão para a sessão ETW (código {Code}).", code);
            SetState(FrameCaptureState.PermissionDenied, err.Trim());
        }
        else
        {
            _log.LogError("PresentMon terminou inesperadamente (código {Code}): {Error}", code, err.Trim());
            SetState(FrameCaptureState.Faulted, string.IsNullOrWhiteSpace(err) ? $"código {code}" : err.Trim());
        }
    }

    public void Stop()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
            _stopping = true;
        }
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill();
            process.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process.Dispose();
        TerminateSession();
        _reader?.Join(3000);
        _reader = null;
        SetState(FrameCaptureState.Stopped);
        _log.LogInformation("Captura de frames encerrada.");
    }

    /// <summary>
    /// Encerrar o processo à força deixaria a sessão ETW aberta no kernel; o próprio PresentMon a fecha.
    /// (Se isso falhar, a próxima execução usa --stop_existing_session.)
    /// </summary>
    private void TerminateSession()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(_executable, $"--terminate_existing_session --session_name {SessionName}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.LogDebug(ex, "Não foi possível encerrar a sessão ETW explicitamente.");
        }
    }

    public void Dispose()
    {
        Stop();
        _job?.Dispose();
    }
}
