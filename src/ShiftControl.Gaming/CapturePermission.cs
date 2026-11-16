using System.Diagnostics;
using System.Security.Principal;

namespace ShiftControl.Gaming;

public enum CaptureAccess
{
    /// <summary>Processo elevado: pode criar sessões ETW.</summary>
    Administrator,
    /// <summary>Usuário no grupo "Performance Log Users": captura sem administrador.</summary>
    PerformanceLogUsers,
    /// <summary>O usuário foi adicionado ao grupo, mas o token atual ainda não reflete (precisa sair e entrar no Windows).</summary>
    PendingSignOut,
    /// <summary>Sem permissão para sessões ETW.</summary>
    None,
}

/// <summary>
/// Sessões ETW em tempo real (base da medição de FPS) exigem administrador ou o grupo interno
/// "Performance Log Users" (SID S-1-5-32-559, o nome varia com o idioma do Windows).
/// </summary>
public static class CapturePermission
{
    public const string PerformanceLogUsersSid = "S-1-5-32-559";

    public static CaptureAccess Current()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return CaptureAccess.Administrator;
        if (identity.Groups?.Any(g => g.Value == PerformanceLogUsersSid) == true) return CaptureAccess.PerformanceLogUsers;
        return IsMemberInDirectory(identity.Name) ? CaptureAccess.PendingSignOut : CaptureAccess.None;
    }

    /// <summary>Consulta a associação ao grupo no banco de contas local (vale mesmo antes de refazer o login).</summary>
    private static bool IsMemberInDirectory(string account)
    {
        try
        {
            var group = new SecurityIdentifier(PerformanceLogUsersSid).Translate(typeof(NTAccount)).Value;
            var psi = new ProcessStartInfo("net.exe", $"localgroup \"{group.Split('\\').Last()}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            string user = account.Split('\\').Last();
            return output.Split('\n').Any(l => string.Equals(l.Trim().Split('\\').Last(), user, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IdentityNotMappedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Adiciona o usuário atual ao grupo "Performance Log Users" (pede UAC uma vez).
    /// Depois de sair e entrar de novo no Windows, a medição de FPS funciona sem administrador.
    /// Retorna falso se o usuário recusar o UAC ou o comando falhar.
    /// </summary>
    public static bool GrantToCurrentUser()
    {
        string account = WindowsIdentity.GetCurrent().Name;
        string script = $"Add-LocalGroupMember -SID '{PerformanceLogUsersSid}' -Member '{account.Replace("'", "''")}' -ErrorAction Stop";
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit(30_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC recusado
        }
    }
}
