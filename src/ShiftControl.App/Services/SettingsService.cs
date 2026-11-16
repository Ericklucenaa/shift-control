using Microsoft.Extensions.Logging;
using ShiftControl.Core;

namespace ShiftControl.App.Services;

/// <summary>Preferências em memória + gravação no banco. Toda alteração passa por <see cref="Update"/>.</summary>
public sealed class SettingsService
{
    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsService> _log;

    public SettingsService(ISettingsStore store, ILogger<SettingsService> log)
    {
        _store = store;
        _log = log;
        Current = store.Load();
    }

    public AppSettings Current { get; private set; }

    /// <summary>Disparado na thread que chamou <see cref="Update"/> (a da interface).</summary>
    public event EventHandler<AppSettings>? Changed;

    public void Update(Action<AppSettings> change)
    {
        var next = Current.Clone();
        change(next);
        Replace(next);
    }

    /// <summary>Substitui todas as preferências (restauração do pacote de migração).</summary>
    public void Replace(AppSettings next)
    {
        next.Normalize();
        Current = next;
        try
        {
            _store.Save(next);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Não foi possível gravar as preferências.");
        }
        Changed?.Invoke(this, next);
    }
}
