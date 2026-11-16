namespace ShiftControl.Overlay;

public sealed record TargetContext(
    int? ForegroundPid,
    IReadOnlyList<PresentingProcess> Presenting,
    string? ManualExecutable,
    bool AutoDetect,
    int? CurrentPid,
    bool CurrentAlive,
    IReadOnlyCollection<string> UserExclusions);

public enum TargetReason { None, Manual, Foreground, Kept }

public sealed record TargetDecision(PresentingProcess? Target, TargetReason Reason);

/// <summary>
/// Decide qual processo é "o jogo" usando só informações públicas do sistema: a janela em primeiro plano
/// e quem está apresentando frames (via ETW). Não abre handles nos processos nem lê seus módulos,
/// o que poderia incomodar sistemas anticheat.
/// </summary>
public static class GameTargetPolicy
{
    /// <summary>Taxa mínima para considerar que o processo está renderizando continuamente (um jogo, não um app ocioso).</summary>
    public const double MinimumFps = 10;

    /// <summary>Launchers, navegadores, ferramentas e o próprio sistema: apresentam frames, mas não são jogos.</summary>
    public static IReadOnlySet<string> DefaultExclusions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "dwm.exe", "explorer.exe", "ShiftControl.exe", "shift-desktop.exe", "PresentMon.exe",
        "steam.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe", "EpicWebHelper.exe", "Battle.net.exe",
        "Agent.exe", "Origin.exe", "EADesktop.exe", "upc.exe", "UbisoftConnect.exe", "GalaxyClient.exe",
        "RiotClientServices.exe", "RiotClientUx.exe", "RiotClientUxRender.exe", "XboxPcApp.exe", "GameBar.exe",
        "chrome.exe", "msedge.exe", "msedgewebview2.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe",
        "Discord.exe", "obs64.exe", "Spotify.exe", "Telegram.exe", "WhatsApp.exe", "ms-teams.exe", "slack.exe",
        "Code.exe", "devenv.exe", "ApplicationFrameHost.exe", "SearchHost.exe", "StartMenuExperienceHost.exe",
        "ShellExperienceHost.exe", "TextInputHost.exe", "LockApp.exe", "NVIDIA Overlay.exe", "NVIDIA App.exe",
        "RadeonSoftware.exe", "parsecd.exe", "AnyDesk.exe", "mstsc.exe", "vlc.exe", "mpc-hc64.exe",
    };

    public static bool IsExcluded(string executable, IReadOnlyCollection<string> userExclusions)
    {
        string exe = Normalize(executable);
        return DefaultExclusions.Contains(exe) || userExclusions.Any(x => string.Equals(Normalize(x), exe, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"Game" e "game.exe" são o mesmo executável.</summary>
    public static string Normalize(string name)
    {
        name = Path.GetFileName(name.Trim());
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    public static TargetDecision Decide(TargetContext ctx)
    {
        PresentingProcess? Current() =>
            ctx.CurrentPid is { } pid && ctx.CurrentAlive ? ctx.Presenting.FirstOrDefault(p => p.ProcessId == pid) : null;

        // 1) Executável escolhido pelo usuário: só ele.
        if (!string.IsNullOrWhiteSpace(ctx.ManualExecutable))
        {
            string manual = Normalize(ctx.ManualExecutable);
            var match = ctx.Presenting
                .Where(p => string.Equals(Normalize(p.Executable), manual, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.ProcessId == ctx.ForegroundPid)
                .ThenByDescending(p => p.Fps)
                .FirstOrDefault();
            if (match is not null) return new(match, TargetReason.Manual);
            return Current() is { } keep && string.Equals(Normalize(keep.Executable), manual, StringComparison.OrdinalIgnoreCase)
                ? new(keep, TargetReason.Kept)
                : new(null, TargetReason.None);
        }

        // 2) Detecção desligada: mantém o atual enquanto existir.
        if (!ctx.AutoDetect)
            return Current() is { } kept ? new(kept, TargetReason.Kept) : new(null, TargetReason.None);

        // 3) Automático: o processo em primeiro plano que renderiza continuamente e não é launcher/ferramenta.
        if (ctx.ForegroundPid is { } fg
            && ctx.Presenting.FirstOrDefault(p => p.ProcessId == fg) is { } candidate
            && candidate.Fps >= MinimumFps
            && !IsExcluded(candidate.Executable, ctx.UserExclusions))
            return new(candidate, TargetReason.Foreground);

        // 4) Alt+Tab para outra janela: o jogo continua sendo o alvo enquanto existir e apresentar frames.
        return Current() is { } current && !IsExcluded(current.Executable, ctx.UserExclusions)
            ? new(current, TargetReason.Kept)
            : new(null, TargetReason.None);
    }
}
