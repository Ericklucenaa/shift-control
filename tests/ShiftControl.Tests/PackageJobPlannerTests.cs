using ShiftControl.Setup;

namespace ShiftControl.Tests;

public class PackageJobPlannerTests
{
    private static CatalogPackage Package(string id) => new() { Id = id, Name = id, Category = "test" };

    [Fact]
    public void Only_selected_installed_packages_with_updates_enter_the_queue()
    {
        var jobs = PackageJobPlanner.Upgrades([
            new(Package("selected-update"), true, true, true),
            new(Package("excluded-update"), false, true, true),
            new(Package("selected-current"), true, true, false),
            new(Package("selected-missing"), true, false, true),
        ]);

        var job = Assert.Single(jobs);
        Assert.Equal("selected-update", job.Package.Id);
        Assert.Equal(JobKind.Upgrade, job.Kind);
        Assert.False(job.RecordInstallation);
    }

    [Fact]
    public void Clearing_selection_never_falls_back_to_updating_all_installed_packages()
    {
        Assert.Empty(PackageJobPlanner.Upgrades([
            new(Package("Chrome"), false, true, true),
            new(Package("Firefox"), false, true, true),
        ]));
        Assert.Empty(PackageJobPlanner.Upgrades([]));
    }

    [Fact]
    public void Multiple_selected_updates_keep_order_and_source_in_a_snapshot()
    {
        var store = Package("StoreApp") with { Source = "msstore" };
        List<PackageSelection> selection = [new(Package("First"), true, true, true), new(store, true, true, true)];
        var jobs = PackageJobPlanner.Upgrades(selection);
        selection.Clear();

        Assert.Equal(new[] { "First", "StoreApp" }, jobs.Select(j => j.Package.Id));
        Assert.Equal("msstore", jobs[1].Package.Source);
        Assert.All(jobs, j => Assert.Equal(JobKind.Upgrade, j.Kind));
    }
}
