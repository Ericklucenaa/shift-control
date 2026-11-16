using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShiftControl.App.Services;
using ShiftControl.Core;
using ShiftControl.Monitoring;

namespace ShiftControl.App.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record AccentChoice(string Hex, string Label, Windows.UI.Color Color);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IHistoryStore _history;
    private readonly MonitoringEngine _engine;
    private readonly ILogger<SettingsViewModel> _log;
    private bool _loading;

    public SettingsViewModel(SettingsService settings, IHistoryStore history, MonitoringEngine engine, ILogger<SettingsViewModel> log)
    {
        _settings = settings;
        _history = history;
        _engine = engine;
        _log = log;

        Themes = [new(ThemeMode.Dark, L.Get("settings.theme.dark")), new(ThemeMode.Light, L.Get("settings.theme.light")), new(ThemeMode.System, L.Get("settings.theme.system"))];
        VisualThemes = Appearance.Themes.Select(v => new Choice<VisualTheme>(v, L.Get("appearance.palette." + v))).ToList();
        ButtonLooks = Appearance.Buttons.Select(v => new Choice<ButtonLook>(v, L.Get("appearance.button." + v))).ToList();
        Languages = [
            new("pt-BR", "Português (Brasil)"),
            new("en-US", "English (US)"),
            new("es-ES", "Español"),
        ];
        Units = [new(TemperatureUnit.Celsius, "°C"), new(TemperatureUnit.Fahrenheit, "°F")];
        Intervals = AppSettings.AllowedIntervalsMs.Select(ms => new Choice<int>(ms, L.F("settings.interval.option", Fmt.Number(ms / 1000.0, ms < 1000 ? 1 : 0)))).ToList();
        RawRetentions = new[] { 1, 2, 6, 12, 24, 48 }.Select(h => new Choice<int>(h, L.F("settings.hours", h))).ToList();
        AggregatedRetentions = new[] { 7, 30, 90, 180, 365 }.Select(d => new Choice<int>(d, L.F("settings.days", d))).ToList();
        Accents = AccentPalette.Options.Select(o => new AccentChoice(o.Hex, L.Get($"accent.{o.Key}"), ThemeService.Parse(o.Hex))).ToList();
        Load();
    }

    public IReadOnlyList<Choice<ThemeMode>> Themes { get; }
    public IReadOnlyList<Choice<VisualTheme>> VisualThemes { get; }
    public IReadOnlyList<Choice<ButtonLook>> ButtonLooks { get; }
    [ObservableProperty] public partial Choice<VisualTheme>? VisualTheme { get; set; }
    [ObservableProperty] public partial Choice<ButtonLook>? ButtonLook { get; set; }
    partial void OnVisualThemeChanged(Choice<VisualTheme>? value) { if (value is not null) Save(s => s.VisualTheme = value.Value); }
    partial void OnButtonLookChanged(Choice<ButtonLook>? value) { if (value is not null) Save(s => s.ButtonLook = value.Value); }
    public IReadOnlyList<Choice<string>> Languages { get; }
    public IReadOnlyList<Choice<TemperatureUnit>> Units { get; }
    public IReadOnlyList<Choice<int>> Intervals { get; }
    public IReadOnlyList<Choice<int>> RawRetentions { get; }
    public IReadOnlyList<Choice<int>> AggregatedRetentions { get; }
    public IReadOnlyList<AccentChoice> Accents { get; }

    [ObservableProperty] public partial Choice<ThemeMode>? Theme { get; set; }
    [ObservableProperty] public partial Choice<string>? Language { get; set; }
    [ObservableProperty] public partial Choice<TemperatureUnit>? Unit { get; set; }
    [ObservableProperty] public partial Choice<int>? Interval { get; set; }
    [ObservableProperty] public partial Choice<int>? RawRetention { get; set; }
    [ObservableProperty] public partial Choice<int>? AggregatedRetention { get; set; }
    [ObservableProperty] public partial AccentChoice? Accent { get; set; }
    [ObservableProperty] public partial bool RecordHistory { get; set; }
    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial bool StartMinimized { get; set; }
    [ObservableProperty] public partial bool CloseToTray { get; set; }
    [ObservableProperty] public partial bool CheckUpdates { get; set; }
    [ObservableProperty] public partial bool AutoInstallUpdates { get; set; }
    [ObservableProperty] public partial bool CheckingUpdate { get; set; }
    [ObservableProperty] public partial bool CanInstallUpdate { get; set; }
    [ObservableProperty] public partial string UpdateStatus { get; set; } = "";
    [ObservableProperty] public partial double UpdateProgress { get; set; }
    [ObservableProperty] public partial string DatabaseSize { get; set; } = "—";
    [ObservableProperty] public partial string? StatusMessage { get; set; }
    [ObservableProperty] public partial bool StatusIsError { get; set; }

    public string DataFolder => AppPaths.DataDirectory;
    public string Version => AppPaths.Version;
    public string ModeText => L.Get(_engine.Info?.IsElevated == true ? "settings.mode.admin" : "settings.mode.user");
    public string DriverText => L.Get(_engine.Info?.SensorDriverInstalled == true ? "settings.driver.installed" : "settings.driver.missing");

    private void Load()
    {
        _loading = true;
        var s = _settings.Current;
        Theme = Themes.First(t => t.Value == s.Theme);
        VisualTheme = VisualThemes.First(t => t.Value == s.VisualTheme);
        ButtonLook = ButtonLooks.First(t => t.Value == s.ButtonLook);
        Language = Languages.FirstOrDefault(l => string.Equals(l.Value, s.Language, StringComparison.OrdinalIgnoreCase)) ?? Languages[0];
        Unit = Units.First(u => u.Value == s.TemperatureUnit);
        Interval = Intervals.First(i => i.Value == s.SampleIntervalMs);
        RawRetention = RawRetentions.FirstOrDefault(r => r.Value == s.RawRetentionHours) ?? RawRetentions[1];
        AggregatedRetention = AggregatedRetentions.FirstOrDefault(r => r.Value == s.AggregatedRetentionDays) ?? AggregatedRetentions[1];
        Accent = Accents.FirstOrDefault(a => string.Equals(a.Hex, s.Accent, StringComparison.OrdinalIgnoreCase)) ?? Accents[0];
        RecordHistory = s.RecordHistory;
        // A chave Run é a fonte da verdade: o usuário pode tê-la removido pelo Gerenciador de Tarefas.
        StartWithWindows = SystemIntegration.IsStartWithWindowsRegistered();
        StartMinimized = s.StartMinimized;
        CloseToTray = s.CloseToTray;
        CheckUpdates = s.CheckUpdates;
        AutoInstallUpdates = s.AutoInstallUpdates;
        _loading = false;
    }

    public void RefreshFacts()
    {
        Load();
        _ = UpdateDatabaseSizeAsync();
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(DriverText));
    }

    private async Task UpdateDatabaseSizeAsync()
    {
        try
        {
            long bytes = await Task.Run(() => _history.DatabaseSizeBytes);
            DatabaseSize = Fmt.Bytes(bytes);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Não foi possível medir o banco de dados.");
            DatabaseSize = Fmt.Unavailable;
        }
    }

    partial void OnThemeChanged(Choice<ThemeMode>? value) { if (value is not null) Save(s => s.Theme = value.Value); }
    partial void OnLanguageChanged(Choice<string>? value)
    {
        if (value is not null)
        {
            Save(s => s.Language = value.Value);
            L.SetLanguage(value.Value);
            ShowStatus(L.Get("settings.languageChanged"), isError: false);
        }
    }
    partial void OnUnitChanged(Choice<TemperatureUnit>? value) { if (value is not null) Save(s => s.TemperatureUnit = value.Value); }
    partial void OnIntervalChanged(Choice<int>? value) { if (value is not null) Save(s => s.SampleIntervalMs = value.Value); }
    partial void OnRawRetentionChanged(Choice<int>? value) { if (value is not null) Save(s => s.RawRetentionHours = value.Value); }
    partial void OnAggregatedRetentionChanged(Choice<int>? value) { if (value is not null) Save(s => s.AggregatedRetentionDays = value.Value); }
    partial void OnAccentChanged(AccentChoice? value) { if (value is not null) Save(s => s.Accent = value.Hex); }
    partial void OnRecordHistoryChanged(bool value) => Save(s => s.RecordHistory = value);
    partial void OnCloseToTrayChanged(bool value) => Save(s => s.CloseToTray = value);
    partial void OnAutoInstallUpdatesChanged(bool value) => Save(s => s.AutoInstallUpdates = value);
    partial void OnCheckUpdatesChanged(bool value) => Save(s => s.CheckUpdates = value);

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (CheckingUpdate) return;
        CheckingUpdate = true;
        CanInstallUpdate = false;
        try
        {
            var updates = (UpdateService)App.Services.GetService(typeof(UpdateService))!;
            var status = await updates.CheckAsync();
            UpdateStatus = L.Get("update.result." + status);
            CanInstallUpdate = status == UpdateCheck.Available && updates.Latest is { InstallerUrl: not null, Sha256: not null };
            if (status == UpdateCheck.Available && !CanInstallUpdate) UpdateStatus += " " + L.Get("update.noVerifiedInstaller");
        }
        finally { CheckingUpdate = false; }
    }

    public async Task InstallUpdateAsync()
    {
        if (CheckingUpdate || !CanInstallUpdate) return;
        CheckingUpdate = true;
        CanInstallUpdate = false;
        UpdateProgress = 0;
        try
        {
            var updates = (UpdateService)App.Services.GetService(typeof(UpdateService))!;
            UpdateStatus = L.Get("update.downloading");
            if (updates.Latest is { } release)
                await updates.InstallAsync(release, progress: new Progress<double>(v => UpdateProgress = v));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao preparar atualização.");
            UpdateStatus = L.Get("update.installFailed") + " " + ex.Message;
            CanInstallUpdate = true;
        }
        finally { CheckingUpdate = false; }
    }
    partial void OnStartMinimizedChanged(bool value)
    {
        Save(s => s.StartMinimized = value);
        if (!_loading && StartWithWindows) ApplyStartup(true);
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        if (ApplyStartup(value)) Save(s => s.StartWithWindows = value);
    }

    private bool ApplyStartup(bool enabled)
    {
        try
        {
            SystemIntegration.SetStartWithWindows(enabled, StartMinimized);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Não foi possível alterar a inicialização com o Windows.");
            ShowStatus(L.Get("settings.startup.error"), isError: true);
            return false;
        }
    }

    /// <summary>
    /// Grava só se algo mudou de fato: controles de seleção reenviam o valor atual ao carregar,
    /// e isso não deve gerar gravação nem aviso.
    /// </summary>
    private void Save(Action<AppSettings> change)
    {
        if (_loading) return;
        var probe = _settings.Current.Clone();
        change(probe);
        probe.Normalize();
        if (System.Text.Json.JsonSerializer.Serialize(probe) == System.Text.Json.JsonSerializer.Serialize(_settings.Current)) return;
        _settings.Update(change);
        ShowStatus(L.Get("settings.saved"), isError: false);
    }

    private void ShowStatus(string text, bool isError)
    {
        StatusIsError = isError;
        StatusMessage = null; // reabre a barra mesmo que o texto seja igual ao anterior
        StatusMessage = text;
    }

    /// <summary>Chamado pela tela depois que o usuário confirma no diálogo.</summary>
    public async Task ClearHistoryAsync()
    {
        try
        {
            await Task.Run(_history.Clear);
            ShowStatus(L.Get("settings.history.cleared"), isError: false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao apagar o histórico.");
            ShowStatus(L.Get("settings.history.clearError"), isError: true);
        }
        await UpdateDatabaseSizeAsync();
    }

    [RelayCommand]
    private void OpenDataFolder() => SystemIntegration.OpenFolder(AppPaths.DataDirectory);

    [RelayCommand]
    private void OpenLogs() => SystemIntegration.OpenFolder(AppPaths.LogDirectory);

    [RelayCommand]
    private void OpenDriverSite() => SystemIntegration.OpenUrl(SystemIntegration.PawnIoUrl);
}
