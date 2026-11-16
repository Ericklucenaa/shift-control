namespace ShiftControl.Overlay;

public enum FrameCaptureState
{
    Stopped,
    Starting,
    Running,
    /// <summary>O Windows recusou a sessão ETW: falta administrador ou o grupo "Performance Log Users".</summary>
    PermissionDenied,
    /// <summary>O componente de captura não foi encontrado na instalação.</summary>
    Missing,
    Faulted,
}

/// <summary>
/// Fonte de eventos de apresentação de frames. A implementação atual usa o PresentMon (ETW);
/// a abstração permite trocar por outra fonte sem mexer no overlay nem nas estatísticas.
/// </summary>
public interface IFrameMetricsProvider : IDisposable
{
    FrameCaptureState State { get; }

    /// <summary>Detalhe técnico do último erro (para o log e a tela de configuração).</summary>
    string? Detail { get; }

    event EventHandler<FrameCaptureState>? StateChanged;

    /// <summary>Chamado na thread de leitura, uma vez por Present() observado. Não deve bloquear.</summary>
    event PresentHandler? Present;

    void Start();
    void Stop();
}

public delegate void PresentHandler(in PresentRecord record);
