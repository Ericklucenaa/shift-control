using System.Diagnostics;
using System.Net.NetworkInformation;
using ShiftControl.Core;

namespace ShiftControl.Hardware;

/// <summary>Taxas de rede pelos contadores de bytes de cada adaptador físico ativo.</summary>
internal sealed class NetworkReader
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);

    private static readonly string[] VirtualMarkers =
    [
        "virtual", "hyper-v", "vmware", "virtualbox", "loopback", "pseudo", "bluetooth", "kernel debug",
        "tap-", "wan miniport", "vethernet", "npcap", "teredo", "isatap", "wi-fi direct", "parsec",
        "filter", "qos packet", "tunnel", "vpn",
    ];

    private List<NetworkInterface> _interfaces = [];
    private readonly Dictionary<string, (long Rx, long Tx, long At)> _last = [];
    private long _lastRefresh = long.MinValue;

    public NetworkState Read()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastRefresh == long.MinValue || Stopwatch.GetElapsedTime(_lastRefresh, now) >= RefreshEvery)
        {
            _interfaces = NetworkInterface.GetAllNetworkInterfaces().Where(IsPhysical).ToList();
            _lastRefresh = now;
        }

        var adapters = new List<AdapterState>(_interfaces.Count);
        bool ready = true;
        foreach (var ni in _interfaces)
        {
            long rx, tx;
            try
            {
                var stats = ni.GetIPStatistics();
                rx = stats.BytesReceived;
                tx = stats.BytesSent;
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            double down = 0, up = 0;
            if (_last.TryGetValue(ni.Id, out var prev))
            {
                double seconds = Stopwatch.GetElapsedTime(prev.At, now).TotalSeconds;
                if (seconds > 0)
                {
                    // Contadores podem zerar (adaptador reiniciado): delta negativo vira 0.
                    down = Math.Max(0, rx - prev.Rx) / seconds;
                    up = Math.Max(0, tx - prev.Tx) / seconds;
                }
            }
            else
            {
                ready = false;
            }
            _last[ni.Id] = (rx, tx, now);

            adapters.Add(new AdapterState(ni.Id, ni.Name, ni.Description, KindName(ni.NetworkInterfaceType), SafeSpeed(ni), down, up));
        }
        return new NetworkState(adapters, ready && adapters.Count > 0);
    }

    internal static bool IsPhysical(NetworkInterface ni)
    {
        if (ni.OperationalStatus != OperationalStatus.Up) return false;
        if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Unknown)
            return false;
        return !LooksVirtual(ni.Name) && !LooksVirtual(ni.Description);
    }

    internal static bool LooksVirtual(string text)
    {
        foreach (var marker in VirtualMarkers)
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static long SafeSpeed(NetworkInterface ni)
    {
        try { return ni.Speed; }
        catch (NetworkInformationException) { return 0; }
    }

    private static string KindName(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Celular",
        _ => type.ToString(),
    };
}
