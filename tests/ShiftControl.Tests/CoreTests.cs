using ShiftControl.Core;

namespace ShiftControl.Tests;

public class RingBufferTests
{
    [Fact]
    public void Keeps_the_newest_items_in_order()
    {
        var rb = new RingBuffer<int>(3);
        for (int i = 1; i <= 5; i++) rb.Add(i);
        Assert.Equal(3, rb.Count);
        Assert.Equal([3, 4, 5], rb.ToArray());
        Assert.Equal(5, rb.Last);
    }

    [Fact]
    public void CopyTo_smaller_destination_takes_the_most_recent()
    {
        var rb = new RingBuffer<int>(5);
        for (int i = 1; i <= 5; i++) rb.Add(i);
        var dest = new int[2];
        Assert.Equal(2, rb.CopyTo(dest));
        Assert.Equal([4, 5], dest);
    }

    [Fact]
    public void Clear_and_bounds()
    {
        var rb = new RingBuffer<int>(2);
        rb.Add(1);
        rb.Clear();
        Assert.Equal(0, rb.Count);
        Assert.Throws<InvalidOperationException>(() => rb.Last);
        Assert.Throws<ArgumentOutOfRangeException>(() => rb[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<int>(0));
    }
}

public class StatsTests
{
    [Fact]
    public void Running_stats_min_max_mean()
    {
        var s = new RunningStats();
        foreach (var v in new[] { 10.0, 20, 30, double.NaN, double.PositiveInfinity }) s.Add(v);
        Assert.Equal(3, s.Count);
        Assert.Equal(10, s.Min);
        Assert.Equal(30, s.Max);
        Assert.Equal(20, s.Mean, 9);
    }

    [Fact]
    public void Tracker_ignores_missing_readings_and_resets()
    {
        var t = new SensorStatsTracker();
        t.Update([
            new SensorReading("a", "A", "h", "H", HardwareCategory.Cpu, SensorKind.Load, Reading.Of(50)),
            new SensorReading("b", "B", "h", "H", HardwareCategory.Cpu, SensorKind.Temperature, Reading.Missing(Unavailable.RequiresAdmin)),
        ]);
        Assert.True(t.Get("a").HasData);
        Assert.False(t.Get("b").HasData);
        t.Reset();
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void Tracker_is_safe_under_concurrent_updates_and_reads()
    {
        var t = new SensorStatsTracker();
        var reading = new SensorReading("x", "X", "h", "H", HardwareCategory.Cpu, SensorKind.Load, Reading.Of(1));
        Parallel.For(0, 2000, i =>
        {
            if (i % 2 == 0) t.Update([reading]);
            else _ = t.Get("x");
        });
        Assert.Equal(1000, t.Get("x").Count);
    }
}

public class ReadingAndValidationTests
{
    [Fact]
    public void NaN_is_never_a_value()
    {
        var r = Reading.Of(double.NaN);
        Assert.False(r.HasValue);
        Assert.Equal(Unavailable.Invalid, r.Reason);
    }

    [Theory]
    [InlineData(SensorKind.Temperature, 200.0)]
    [InlineData(SensorKind.Temperature, -60.0)]
    [InlineData(SensorKind.Load, 150.0)]
    [InlineData(SensorKind.Fan, -5.0)]
    [InlineData(SensorKind.Power, 10_000.0)]
    public void Impossible_values_are_discarded(SensorKind kind, double value)
    {
        var r = SensorValidation.Validate(kind, value);
        Assert.False(r.HasValue);
        Assert.Equal(Unavailable.Invalid, r.Reason);
    }

    [Fact]
    public void Missing_sensor_is_reported_as_no_sensor()
    {
        Assert.Equal(Unavailable.NoSensor, SensorValidation.Validate(SensorKind.Temperature, null).Reason);
    }

    [Fact]
    public void Load_is_clamped_to_0_100()
    {
        Assert.Equal(100, SensorValidation.Validate(SensorKind.Load, 100.3).Value);
        Assert.Equal(0, SensorValidation.Validate(SensorKind.Load, -0.2).Value);
    }

    [Fact]
    public void Units()
    {
        Assert.Equal(212, Core.Units.ToFahrenheit(100), 6);
        Assert.Equal((1.5, "KB"), Core.Units.Bytes(1536));
        Assert.Equal((0.0, "B"), Core.Units.Bytes(0));
    }
}

public class SettingsNormalizeTests
{
    [Fact]
    public void Out_of_range_values_are_corrected()
    {
        var s = new AppSettings
        {
            SampleIntervalMs = 700,
            RawRetentionHours = 999,
            AggregatedRetentionDays = 0,
            Accent = "red",
            Theme = (ThemeMode)42,
            FavoriteSensors = ["a", "a", " ", "b"],
        }.Normalize();

        Assert.Contains(s.SampleIntervalMs, AppSettings.AllowedIntervalsMs);
        Assert.Equal(48, s.RawRetentionHours);
        Assert.Equal(1, s.AggregatedRetentionDays);
        Assert.Equal(AccentPalette.Default, s.Accent);
        Assert.Equal(ThemeMode.Dark, s.Theme);
        Assert.Equal(["a", "b"], s.FavoriteSensors);
    }

    [Fact]
    public void Clone_is_independent()
    {
        var a = new AppSettings { FavoriteSensors = ["x"] };
        var b = a.Clone();
        b.FavoriteSensors.Add("y");
        Assert.Single(a.FavoriteSensors);
    }
}

public class MetricExtractorTests
{
    [Fact]
    public void Unavailable_metrics_are_skipped_not_zeroed()
    {
        var snap = Snapshots.Basic(cpuTemp: null);
        var values = MetricExtractor.Extract(snap).ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.False(values.ContainsKey(MetricKeys.CpuTemp));
        Assert.Equal(20, values[MetricKeys.CpuLoad]);
        Assert.False(values.ContainsKey(MetricKeys.NetDown)); // rede ainda sem 2ª leitura
    }

    [Fact]
    public void Primary_gpu_prefers_discrete()
    {
        var snap = Snapshots.Basic(gpus: [Snapshots.Gpu(5, GpuVendor.Intel, "iGPU"), Snapshots.Gpu(70, GpuVendor.Nvidia, "dGPU")]);
        Assert.Equal("dGPU", snap.PrimaryGpu!.Name);
        var values = MetricExtractor.Extract(snap).ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(70, values[MetricKeys.GpuLoad]);
        Assert.Equal(25, values[MetricKeys.GpuVram], 6);
    }

    [Fact]
    public void Computer_without_gpu_has_no_gpu_metrics()
    {
        var values = MetricExtractor.Extract(Snapshots.Basic(gpus: [])).Select(kv => kv.Key).ToList();
        Assert.DoesNotContain(MetricKeys.GpuLoad, values);
    }
}

public class PresentationTests
{
    [Theory]
    [InlineData(1200, 264, 16, 4, 4, 4)]
    [InlineData(1000, 264, 16, 4, 4, 2)] // 3 colunas deixariam 1 sozinho
    [InlineData(500, 264, 16, 4, 4, 1)]
    [InlineData(1200, 264, 16, 4, 2, 2)]
    [InlineData(1200, 264, 16, 4, 6, 4)] // 6 itens em 4 colunas: 4+2 é aceitável
    [InlineData(1200, 264, 16, 4, 5, 3)] // 5 itens: 4+1 deixaria um sozinho, então 3+2
    public void Columns_avoid_orphans(double width, double min, double spacing, int max, int count, int expected) =>
        Assert.Equal(expected, Presentation.Columns(width, min, spacing, max, count));

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(83.0, 100.0)]
    [InlineData(21.0, 25.0)]
    [InlineData(1400.0, 2000.0)]
    public void Nice_ceiling(double v, double expected) => Assert.Equal(expected, Presentation.NiceCeiling(v), 9);

    [Fact]
    public void Nice_binary_ceiling_is_round_in_megabytes()
    {
        double mb = 1024 * 1024;
        Assert.Equal(20 * mb, Presentation.NiceBinaryCeiling(15 * mb), 3);
        Assert.Equal(1024, Presentation.NiceBinaryCeiling(0));
    }
}
