using System.Management;

namespace ShiftControl.Hardware;

public enum RestorePointResult { Created, RequiresAdmin, ProtectionDisabled, TooFrequent, Failed }

/// <summary>
/// Pontos de restauração do Windows (classe WMI SystemRestore). Exige administrador e a Proteção do Sistema ligada
/// na unidade do Windows. O Windows aceita no máximo um ponto a cada 24 h por padrão; nesse caso ele ignora o pedido.
/// </summary>
public static class RestorePoints
{
    private const int ApplicationInstall = 0, ModifySettings = 12, BeginSystemChange = 100;

    public static RestorePointResult Create(string description)
    {
        if (!Elevation.IsElevated) return RestorePointResult.RequiresAdmin;
        try
        {
            int before = Count();
            using var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            using var input = cls.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description.Length > 64 ? description[..64] : description;
            input["RestorePointType"] = ModifySettings;
            input["EventType"] = BeginSystemChange;
            using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
            uint code = Convert.ToUInt32(output?["ReturnValue"] ?? 1u);
            if (code == 0x80070422 || code == 1058) return RestorePointResult.ProtectionDisabled;
            if (code != 0) return RestorePointResult.Failed;
            // Sucesso sem ponto novo = o Windows aplicou o limite de frequência.
            return Count() > before ? RestorePointResult.Created : RestorePointResult.TooFrequent;
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return RestorePointResult.RequiresAdmin;
        }
        catch (ManagementException)
        {
            return RestorePointResult.ProtectionDisabled;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return RestorePointResult.Failed;
        }
    }

    private static int Count() => Wmi.All("SELECT SequenceNumber FROM SystemRestore", o => 1, @"root\default").Count;
}
