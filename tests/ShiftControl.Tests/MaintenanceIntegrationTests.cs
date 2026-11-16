using ShiftControl.Maintenance;

namespace ShiftControl.Tests;

/// <summary>Somente leitura. Nenhum teste instala drivers, altera registro, exporta senhas ou executa reparos.</summary>
[Trait("Category", "Integration")]
public class MaintenanceIntegrationTests
{
    [Fact]
    public void Drivers_inventory_reads_real_device_versions()
    {
        var inventory = Drivers.Inventory();
        Assert.NotEmpty(inventory);
        Assert.Contains(inventory, d => d.Version is not null);
        Assert.Contains(inventory, d => d.DeviceClass.Equals("Display", StringComparison.OrdinalIgnoreCase));
        TestContext.Current.SendDiagnosticMessage($"Inventário: {inventory.Count} dispositivos, {inventory.Count(d => d.HasProblem)} problemas.");
    }

    [Fact]
    public void Disk_health_reads_real_physical_disks()
    {
        var disks = DiskHealthReader.Read();
        Assert.NotEmpty(disks);
        Assert.All(disks, d => Assert.Contains(d.HealthStatus, new[] { "Healthy", "Warning", "Unhealthy", "Unknown" }));
        TestContext.Current.SendDiagnosticMessage($"Discos físicos: {disks.Count}.");
    }

    [Fact]
    public void Startup_and_error_history_are_read_only_and_consistent()
    {
        var startup = Startup.List();
        Assert.All(startup, i => Assert.False(string.IsNullOrWhiteSpace(i.Name)));
        var errors = ErrorHistory.Read(TimeSpan.FromDays(7), maxPerKind: 10);
        Assert.All(errors, e => Assert.True(e.At >= DateTime.Now.AddDays(-7).AddMinutes(-1)));
    }

    [Fact]
    public void Power_plan_inventory_reports_one_active_plan_without_changing_it()
    {
        var plans = BoostEngine.PowerPlans();
        Assert.NotEmpty(plans);
        Assert.Single(plans, p => p.Active);
    }
}
