namespace ShiftControl.Setup;

public sealed record PackageSelection(CatalogPackage Package, bool Selected, bool Installed, bool HasUpdate);

/// <summary>Snapshots the explicit selection before an update queue starts.</summary>
public static class PackageJobPlanner
{
    public static List<InstallJob> Upgrades(IEnumerable<PackageSelection> packages) => packages
        .Where(p => p.Selected && p.Installed && p.HasUpdate)
        .Select(p => new InstallJob { Package = p.Package, Kind = JobKind.Upgrade, RecordInstallation = false })
        .ToList();
}
