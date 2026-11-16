using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShiftControl.Overlay;

namespace ShiftControl.Gaming;

/// <summary>
/// Ping ICMP de 32 bytes, uma vez por segundo, só enquanto alguma métrica de rede estiver visível.
/// O destino é escolhido pelo usuário; nenhum dado do computador é enviado.
/// </summary>
public sealed class LatencyProbe : IDisposable
{
    private readonly ILogger _log;
    private readonly byte[] _payload = new byte[32];
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public LatencyProbe(ILogger<LatencyProbe>? log = null) => _log = log ?? NullLogger<LatencyProbe>.Instance;

    public LatencyStats Stats { get; } = new(60);
    public string Host { get; private set; } = "";
    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start(string host)
    {
        if (IsRunning && host == Host) return;
        Stop();
        Host = host;
        Stats.Clear();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(host, token), token);
    }

    private async Task RunAsync(string host, CancellationToken token)
    {
        using var ping = new Ping();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        bool loggedError = false;
        do
        {
            try
            {
                var reply = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(1000), _payload, cancellationToken: token);
                Stats.Add(reply.Status == IPStatus.Success ? reply.RoundtripTime : null);
            }
            catch (OperationCanceledException) { break; }
            catch (PingException ex)
            {
                Stats.Add(null);
                if (!loggedError) { _log.LogWarning(ex, "Ping para {Host} falhou.", host); loggedError = true; }
            }
        }
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(2000); } catch (AggregateException) { }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose() => Stop();
}
