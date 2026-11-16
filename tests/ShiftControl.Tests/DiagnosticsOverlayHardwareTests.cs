using ShiftControl.Core;
using ShiftControl.Diagnostics;
using ShiftControl.Hardware;
using ShiftControl.Overlay;

namespace ShiftControl.Tests;

public class HealthAnalyzerTests
{
    private static readonly RecentAverages NoHistory = new(null, null, 0);

    [Fact]
    public void Healthy_system_is_good_with_cpu_sensor_note_as_info_only()
    {
        var r = HealthAnalyzer.Analyze(Snapshots.Basic(drives: [Snapshots.Drive()]), Snapshots.Info(), NoHistory, DateTimeOffset.Now);
        Assert.Equal(HealthLevel.Good, r.Level);
        var f = Assert.Single(r.Findings);
        Assert.Equal("cpu.sensors", f.Code);
        Assert.Equal(FindingSeverity.Info, f.Severity);
    }

    [Theory]
    [InlineData(80, HealthLevel.Attention)] // 8% livre
    [InlineData(30, HealthLevel.Critical)]  // 3% livre
    public void Low_disk_space(ulong freeGb, HealthLevel expected)
    {
        var snap = Snapshots.Basic(cpuTemp: 50, drives: [Snapshots.Drive(free: freeGb << 30, total: 1000UL << 30)]);
        var r = HealthAnalyzer.Analyze(snap, Snapshots.Info(), NoHistory, DateTimeOffset.Now);
        Assert.Equal(expected, r.Level);
        var f = r.Findings.Single(x => x.Code == "volume.low");
        Assert.Equal("C:\\", f.Subject);
        Assert.True(f.Evidence.ContainsKey("freePercent"));
    }

    [Fact]
    public void Drive_temperature_uses_the_drive_own_thresholds()
    {
        // 75 °C com limite do fabricante 80/85: sem alerta. Com limite 70: atenção.
        var cool = Snapshots.Basic(cpuTemp: 50, drives: [Snapshots.Drive(temp: 75, warn: 80, crit: 85)]);
        Assert.Equal(HealthLevel.Good, HealthAnalyzer.Analyze(cool, Snapshots.Info(), NoHistory, DateTimeOffset.Now).Level);

        var warm = Snapshots.Basic(cpuTemp: 50, drives: [Snapshots.Drive(temp: 75, warn: 70, crit: 85)]);
        var r = HealthAnalyzer.Analyze(warm, Snapshots.Info(), NoHistory, DateTimeOffset.Now);
        Assert.Equal(HealthLevel.Attention, r.Level);
        Assert.Equal(70, r.Findings.Single(f => f.Code == "drive.temp").Evidence["threshold"]);
    }

    [Fact]
    public void Without_device_thresholds_there_is_no_temperature_alert()
    {
        var snap = Snapshots.Basic(cpuTemp: 50, drives: [Snapshots.Drive(temp: 95, warn: null, crit: null)]);
        Assert.Equal(HealthLevel.Good, HealthAnalyzer.Analyze(snap, Snapshots.Info(), NoHistory, DateTimeOffset.Now).Level);
    }

    [Fact]
    public void Smart_health_and_wear()
    {
        var snap = Snapshots.Basic(cpuTemp: 50, drives: [Snapshots.Drive(health: DriveHealth.Unhealthy, life: 5)]);
        var r = HealthAnalyzer.Analyze(snap, Snapshots.Info(), NoHistory, DateTimeOffset.Now);
        Assert.Equal(HealthLevel.Critical, r.Level);
        Assert.Contains(r.Findings, f => f.Code == "drive.health");
        Assert.Contains(r.Findings, f => f.Code == "drive.life");
        Assert.Equal(FindingSeverity.Critical, r.Findings[0].Severity); // mais grave primeiro
    }

    [Fact]
    public void Sustained_load_requires_enough_samples()
    {
        var snap = Snapshots.Basic(cpuTemp: 50);
        var few = HealthAnalyzer.Analyze(snap, Snapshots.Info(), new RecentAverages(99, 99, 5), DateTimeOffset.Now);
        Assert.DoesNotContain(few.Findings, f => f.Code is "cpu.high" or "memory.high");

        var many = HealthAnalyzer.Analyze(snap, Snapshots.Info(), new RecentAverages(95, 92, 60), DateTimeOffset.Now);
        Assert.Contains(many.Findings, f => f.Code == "cpu.high");
        Assert.Contains(many.Findings, f => f.Code == "memory.high");
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+O", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 'O')]
    [InlineData("alt + f10", HotkeyModifiers.Alt, 0x79)]
    [InlineData("F12", HotkeyModifiers.None, 0x7B)]
    public void Parses_valid_hotkeys(string text, HotkeyModifiers mods, uint vk)
    {
        Assert.True(Hotkeys.TryParse(text, out var h));
        Assert.Equal(mods, h.Modifiers);
        Assert.Equal(vk, h.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("O")]            // tecla comum sem modificador sequestraria a digitação
    [InlineData("Ctrl+Ctrl+O")]
    [InlineData("Hyper+O")]
    [InlineData("Ctrl+Shift+Ç")]
    public void Rejects_invalid_hotkeys(string text) => Assert.False(Hotkeys.TryParse(text, out _));

    [Fact]
    public void Format_round_trips()
    {
        Assert.True(Hotkeys.TryParse("shift+ctrl+o", out var h));
        Assert.Equal("Ctrl+Shift+O", h.ToString());
    }
}

public class OverlayModelTests
{
    [Fact]
    public void Frame_metrics_are_never_fabricated_from_sensors()
    {
        var snap = Snapshots.Basic();
        Assert.False(OverlayCatalog.Value(OverlayMetric.Fps, snap).HasValue);
        Assert.False(OverlayCatalog.Value(OverlayMetric.OnePercentLow, snap).HasValue);
        Assert.Equal(OverlaySource.FramePresentation, OverlayCatalog.SourceOf(OverlayMetric.Frametime));
        Assert.Equal(30, OverlayCatalog.Value(OverlayMetric.GpuLoad, snap).Value);
    }

    [Fact]
    public void Settings_normalize_and_pick_game_preset()
    {
        var s = new OverlaySettings
        {
            Hotkey = "O",
            ActivePreset = "nao-existe",
            Presets =
            [
                new OverlayPreset { Name = "Geral", FontSize = 200, BackgroundOpacity = 3, TextColor = "x", UpdateIntervalMs = 7 },
                new OverlayPreset { Name = "Geral" },
                new OverlayPreset { Name = "Jogo" },
            ],
            GamePresets = new() { ["Game"] = "Jogo", ["Outro.exe"] = "nao-existe" },
            ManualExecutable = "  ",
            Exclusions = ["launcher", "Launcher.exe", ""],
        }.Normalize();

        Assert.Equal("Ctrl+Shift+O", s.Hotkey);
        Assert.Equal(2, s.Presets.Count);
        Assert.Equal("Geral", s.ActivePreset);
        Assert.Equal(32, s.Presets[0].FontSize);
        Assert.Equal(1, s.Presets[0].BackgroundOpacity);
        Assert.Equal(500, s.Presets[0].UpdateIntervalMs);
        Assert.Equal("#ECEDEE", s.Presets[0].TextColor);
        Assert.Equal("Jogo", s.ForGame("game.exe").Name);  // "Game" normalizado para "Game.exe"
        Assert.Equal("Geral", s.ForGame("outro.exe").Name); // associação a preset inexistente é descartada
        Assert.Null(s.ManualExecutable);
        Assert.Equal(["launcher.exe"], s.Exclusions);
    }

    [Fact]
    public void Preset_always_contains_every_metric_once()
    {
        var p = new OverlayPreset
        {
            Items = [new() { Metric = OverlayMetric.GpuLoad, Visible = true, Label = "  Placa  " }, new() { Metric = OverlayMetric.GpuLoad }],
        }.Normalize();
        Assert.Equal(OverlayCatalog.All.Count, p.Items.Count);
        Assert.Equal(OverlayMetric.GpuLoad, p.Items[0].Metric); // a ordem escolhida é mantida
        Assert.Equal("Placa", p.Items[0].Label);
        Assert.Single(p.VisibleItems);
    }

    [Fact]
    public void Built_in_presets_match_the_specification()
    {
        var minimal = OverlayPreset.Minimal().VisibleItems.Select(i => i.Metric).ToList();
        Assert.Equal([OverlayMetric.Fps, OverlayMetric.OnePercentLow, OverlayMetric.CpuLoad, OverlayMetric.CpuTemperature,
            OverlayMetric.GpuLoad, OverlayMetric.GpuTemperature], minimal);
        var competitive = OverlayPreset.Competitive().VisibleItems.Select(i => i.Metric).ToList();
        Assert.Contains(OverlayMetric.Frametime, competitive);
        Assert.Contains(OverlayMetric.Ping, competitive);
        Assert.True(OverlayPreset.Complete().VisibleItems.Count() > 15);
    }
}

public class HardwareParsingTests
{
    [Fact]
    public void Disk_temperature_descriptor_is_parsed()
    {
        // Formato real lido de um NVMe nesta máquina: 41 °C, alerta 80, crítico 85.
        var data = new byte[40];
        BitConverter.GetBytes((short)85).CopyTo(data, 8);
        BitConverter.GetBytes((short)80).CopyTo(data, 10);
        BitConverter.GetBytes((ushort)1).CopyTo(data, 12);
        BitConverter.GetBytes((short)41).CopyTo(data, 26);
        var t = Native.ParseTemperatureDescriptor(data);
        Assert.Equal(new DiskTemperature(41, 80, 85), t);
    }

    [Fact]
    public void Disk_temperature_sentinels_mean_not_reported()
    {
        var data = new byte[40];
        BitConverter.GetBytes(short.MinValue).CopyTo(data, 8); // HDD sem limite crítico
        BitConverter.GetBytes((short)60).CopyTo(data, 10);
        BitConverter.GetBytes((ushort)1).CopyTo(data, 12);
        BitConverter.GetBytes((short)35).CopyTo(data, 26);
        Assert.Equal(new DiskTemperature(35, 60, null), Native.ParseTemperatureDescriptor(data));

        BitConverter.GetBytes((ushort)0).CopyTo(data, 12);
        Assert.Null(Native.ParseTemperatureDescriptor(data));
        Assert.Null(Native.ParseTemperatureDescriptor(new byte[10]));
    }

    [Theory]
    [InlineData("/nvme/2", 2)]
    [InlineData("/hdd/0", 0)]
    [InlineData("/ssd/1/", 1)]
    [InlineData("/storage", -1)]
    public void Lhm_storage_index(string id, int expected) => Assert.Equal(expected, StorageReader.LhmIndex(id));

    [Theory]
    [InlineData("vEthernet (WSL)", true)]
    [InlineData("Conexão de Rede Bluetooth", true)]
    [InlineData("Ethernet-QoS Packet Scheduler-0000", true)]
    [InlineData("Ethernet", false)]
    [InlineData("Intel(R) Wi-Fi 6 AX201", false)]
    public void Virtual_adapters_are_filtered(string name, bool isVirtual) => Assert.Equal(isVirtual, NetworkReader.LooksVirtual(name));

    [Theory]
    [InlineData(26, "DDR4")]
    [InlineData(34, "DDR5")]
    [InlineData(0, null)]
    public void Memory_type_names(int smbios, string? expected) => Assert.Equal(expected, MemoryReader.MemoryTypeName(smbios));
}

/// <summary>
/// Integração com o hardware real desta máquina (Windows). Não verifica valores específicos,
/// só que a coleta funciona, não lança e nunca devolve leituras inválidas como valores.
/// </summary>
[Trait("Category", "Integration")]
public class WindowsHardwareIntegrationTests
{
    [Fact]
    public void Real_hardware_samples_are_consistent()
    {
        using var source = new WindowsHardwareSource();
        var info = source.Initialize();
        Assert.False(string.IsNullOrWhiteSpace(info.ComputerName));
        Assert.StartsWith("Windows", info.OsName);

        source.Sample(1);
        Thread.Sleep(1100);
        var s = source.Sample(2);

        Assert.False(string.IsNullOrWhiteSpace(s.Cpu.Name));
        Assert.True(s.Cpu.Threads > 0);
        Assert.True(s.Memory.TotalBytes > 0);
        Assert.InRange(s.Memory.LoadPercent, 0, 100);
        Assert.True(s.Cpu.Load.HasValue, "Uso da CPU deveria estar disponível sem administrador.");
        Assert.NotEmpty(s.Drives);
        Assert.True(s.Network.Ready || s.Network.Adapters.Count == 0);

        foreach (var sensor in s.Sensors)
            if (sensor.Value.Value is { } v)
                Assert.True(double.IsFinite(v), $"{sensor.Id} = {v}");

        // Sem admin/driver, sensores de baixo nível da CPU não podem aparecer como 0 "real".
        if (!info.CpuSensorsAccessible)
        {
            Assert.False(s.Cpu.Temperature.HasValue);
            Assert.All(s.Sensors.Where(x => x.Category == HardwareCategory.Cpu && x.Kind == SensorKind.Temperature),
                x => Assert.False(x.Value.HasValue));
        }
    }
}
