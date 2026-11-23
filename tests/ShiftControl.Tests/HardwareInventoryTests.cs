using ShiftControl.Core;
using ShiftControl.Hardware;

namespace ShiftControl.Tests;

public class HardwareInventoryTests
{
    private static string Bytes(double b) => $"{b / (1024d * 1024 * 1024):0} GB";

    private static HardwareInventory Sample(params MemoryModuleSpec[] modules) => new(
        [new ProcessorSpec("AMD Ryzen 7 5800X3D 8-Core Processor            ", 8, 16, 3401, "AM4", 4096, 98304)],
        [new GpuSpec("NVIDIA GeForce RTX 5070", 12UL << 30, "32.0.16.2124", new DateTime(2026, 11, 7))],
        modules.Length > 0 ? modules :
        [
            new MemoryModuleSpec("DIMM_A1", 8UL << 30, 3000, 3000, "DDR4", "Kingston", "KHX3000C15D4/8GX"),
            new MemoryModuleSpec("DIMM_B1", 8UL << 30, 3000, 3000, "DDR4", "Kingston", "KHX3000C15D4/8GX"),
        ],
        4,
        new BoardSpec("ASUSTeK", "TUF GAMING B550M-PLUS", "American Megatrends Inc.", "3621", new DateTime(2025, 1, 13)),
        [new DriveSpec("WDC WDS500G2B0C", 500UL << 30, DriveKind.NvmeSsd, "NVMe")],
        [new MonitorSpec("AOC", "24G2W1G4")],
        [new NetworkAdapterSpec("Realtek Gaming 2.5GbE Family Controller", 1_000_000_000)],
        ["Realtek High Definition Audio"]);

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2F04&SUBSYS_1437196E", true)]
    [InlineData(@"USB\VID_1BCF&PID_2281&MI_02\7&2C8C", true)]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0B00", true)]
    [InlineData(@"ROOT\DISPLAY\0000", false)]          // Parsec Virtual Display Adapter
    [InlineData(@"ROOT\NET\0000", false)]              // ZeroTier
    [InlineData(@"SWD\WINTUN\{37217669}", false)]      // Tailscale
    [InlineData(@"ROOT\UNNAMED_DEVICE\0000", false)]   // áudio virtual da NVIDIA
    [InlineData(null, false)]
    public void Only_physical_devices_count_as_parts(string? pnp, bool physical) =>
        Assert.Equal(physical, HardwareInventory.IsPhysicalDevice(pnp));

    [Fact]
    public void Codes_from_windows_become_names_people_know()
    {
        Assert.Equal("DDR4", HardwareInventory.MemoryTypeName(26));
        Assert.Equal("DDR5", HardwareInventory.MemoryTypeName(34));
        Assert.Null(HardwareInventory.MemoryTypeName(0));
        Assert.Equal(DriveKind.Ssd, HardwareInventory.DriveKindFrom(4));
        Assert.Equal(DriveKind.Hdd, HardwareInventory.DriveKindFrom(3));
        Assert.Equal(DriveKind.Unknown, HardwareInventory.DriveKindFrom(0));
        Assert.Equal(DriveKind.NvmeSsd, HardwareInventory.DriveKindFrom(4, 17));
        Assert.Equal(DriveKind.Removable, HardwareInventory.DriveKindFrom(4, 7));
        Assert.Equal("NVMe", HardwareInventory.BusName(17));
        Assert.Equal("SATA", HardwareInventory.BusName(11));
        Assert.Equal("LG", HardwareInventory.MonitorVendor("GSM"));
        Assert.Equal("VLK", HardwareInventory.MonitorVendor("VLK"));
        Assert.Equal("ASUSTeK", HardwareInventory.CleanVendor("ASUSTeK COMPUTER INC."));
        Assert.Equal("Micro-Star International", HardwareInventory.CleanVendor("Micro-Star International Co., Ltd."));
        Assert.Equal("24G2W1G4", HardwareInventory.EdidText(new ushort[] { 50, 52, 71, 50, 87, 49, 71, 52, 0, 0, 0 }));
        Assert.Null(HardwareInventory.EdidText(new ushort[] { 0, 0 }));
    }

    [Theory]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor            ", "Ryzen 7 5800X3D")]
    [InlineData("AMD Ryzen 5 7600 6-Core Processor", "Ryzen 5 7600")]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700K", "13th Gen Core i7-13700K")]
    [InlineData("Intel(R) Core(TM) i5-4590 CPU @ 3.30GHz", "Core i5-4590")]
    public void Cpu_names_are_shortened_for_the_headline(string full, string shortName) =>
        Assert.Equal(shortName, HardwareInventory.ShortCpu(full));

    [Fact]
    public void Headline_and_copy_text_summarize_the_computer()
    {
        var inv = Sample();
        Assert.Equal(16UL << 30, inv.TotalMemoryBytes);
        Assert.Equal("DDR4", inv.MemoryType);
        Assert.Equal("Ryzen 7 5800X3D · RTX 5070 12 GB · 16 GB DDR4 3000 MT/s · TUF GAMING B550M-PLUS", inv.Headline(Bytes));

        string text = inv.ToText(Bytes, key => key.Replace("components.", ""));
        Assert.Contains("cpu: AMD Ryzen 7 5800X3D 8-Core Processor (8C/16T)", text);
        Assert.Contains("gpu: NVIDIA GeForce RTX 5070 12 GB", text);
        Assert.Contains("memory: 16 GB DDR4 (8 GB + 8 GB) 3000 MT/s", text);
        Assert.Contains("motherboard: ASUSTeK TUF GAMING B550M-PLUS", text);
        Assert.Contains("storage: WDC WDS500G2B0C 500 GB NVMe SSD", text);
        Assert.Contains("monitors: AOC 24G2W1G4", text);
    }

    [Fact]
    public void Memory_below_rated_speed_and_mixed_types_are_detected()
    {
        var slow = new MemoryModuleSpec("A1", 8UL << 30, 3600, 2133, "DDR4", "G.Skill", "F4-3600");
        Assert.True(slow.BelowRated);
        Assert.False(slow with { ConfiguredMts = 3600 } is { BelowRated: true });
        Assert.False(slow with { RatedMts = 0 } is { BelowRated: true }); // velocidade nominal desconhecida: sem aviso

        var mixed = Sample(slow, slow with { Type = "DDR5" });
        Assert.Null(mixed.MemoryType);
        Assert.Equal("Ryzen 7 5800X3D · RTX 5070 12 GB · 16 GB 2133 MT/s · TUF GAMING B550M-PLUS", mixed.Headline(Bytes));
    }
}

[Trait("Category", "Integration")]
public class HardwareInventoryIntegrationTests
{
    [Fact]
    public void Reading_this_computer_finds_a_processor_memory_and_no_virtual_adapters()
    {
        var inv = HardwareInventory.Read();
        Assert.NotEmpty(inv.Processors);
        Assert.True(inv.TotalMemoryBytes > 0);
        Assert.DoesNotContain(inv.Gpus, g => g.Name.Contains("Parsec", StringComparison.OrdinalIgnoreCase) || g.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(inv.Network, n => n.Name.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) || n.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase));
    }
}
