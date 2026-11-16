using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;
using ShiftControl.App.Overlay;
using ShiftControl.App.Services;
using ShiftControl.App.ViewModels;
using ShiftControl.Core;
using ShiftControl.Hardware;
using ShiftControl.Monitoring;
using ShiftControl.Storage;

namespace ShiftControl.App;

public partial class App : Application
{
    private MainWindow? _window;
    private Views.StartupUpdateWindow? _startupWindow;
    private ILogger<App>? _log;

    public static ServiceProvider Services { get; private set; } = null!;
    public static MainWindow? Window => ((App)Current)._window;

    public App()
    {
        InitializeComponent();
        ConfigureLogging();
        Services = BuildServices();
        _log = Services.GetRequiredService<ILogger<App>>();

        UnhandledException += (_, e) =>
        {
            _log.LogError(e.Exception, "Erro não tratado na interface.");
            e.Handled = true; // mantém o app aberto; o erro fica no log
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Erro fatal.");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Tarefa com erro não observado.");
            e.SetObserved();
        };

        try
        {
            var settings = Services.GetRequiredService<SettingsService>();
            L.SetLanguage(settings.Current.Language);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Falha na inicialização do aplicativo.");
            Log.CloseAndFlush();
            throw;
        }
    }

    private static void ConfigureLogging()
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(AppPaths.LogDirectory, "shiftcontrol-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                shared: true, // mais de uma instância (ex.: comum + administrador) escrevem no mesmo arquivo
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Log.Information("Shift Control {Version} iniciando.", AppPaths.Version);
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().AddSerilog(dispose: true));

        services.AddSingleton(_ => new ShiftDatabase(AppPaths.Database));
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IHistoryStore, HistoryStore>();
        services.AddSingleton<IHardwareSource, WindowsHardwareSource>();
        services.AddSingleton(sp => new MonitoringEngine(
            sp.GetRequiredService<IHardwareSource>(),
            sp.GetRequiredService<IHistoryStore>(),
            sp.GetRequiredService<ILogger<MonitoringEngine>>(),
            liveCapacity: 300));

        services.AddSingleton<SettingsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<SnapshotHub>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<SensorsViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // Gaming
        services.AddSingleton<GameSessionStore>();
        services.AddSingleton<OverlayService>();
        services.AddSingleton<OverlayPageViewModel>();
        services.AddSingleton<SessionsViewModel>();

        // Ferramentas (Format Kit, Checagem) e Central de desfazer
        services.AddSingleton(sp => new ShiftControl.Setup.WinGetClient(null, sp.GetRequiredService<ILogger<ShiftControl.Setup.WinGetClient>>()));
        services.AddSingleton<ChangeJournal>();
        services.AddSingleton<UndoService>();
        services.AddSingleton<FormatKitViewModel>();
        services.AddSingleton<ChecksViewModel>();
        services.AddSingleton<UndoViewModel>();

        // Fases B-E: drivers, migração, Game Boost, manutenção, rede, alertas, bandeja e atualizações
        services.AddSingleton(_ =>
        {
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ShiftControl/{AppPaths.Version}");
            return http;
        });
        services.AddSingleton<AlertStore>();
        services.AddSingleton<PingTestStore>();
        services.AddSingleton<TrayService>();
        services.AddSingleton<GameBoostService>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<TrayPanel>();
        services.AddSingleton<DriversViewModel>();
        services.AddSingleton<MigrationViewModel>();
        services.AddSingleton<GameBoostViewModel>();
        services.AddSingleton<DisksViewModel>();
        services.AddSingleton<CleanupViewModel>();
        services.AddSingleton<StartupViewModel>();
        services.AddSingleton<TaskManagerViewModel>();
        services.AddSingleton<RepairViewModel>();
        services.AddSingleton<NetworkViewModel>();
        services.AddSingleton<AlertsViewModel>();
        return services.BuildServiceProvider();
    }

    private static Mutex? _singleInstance;

    /// <summary>
    /// Uma instância por usuário: duas dividiriam o banco, o atalho global e a sessão de captura.
    /// Espera alguns segundos porque "Reiniciar como administrador" abre a nova antes de a antiga fechar.
    /// </summary>
    private static bool AcquireSingleInstance()
    {
        string suffix = Environment.GetCommandLineArgs().Any(a => a.StartsWith("--data-dir=", StringComparison.Ordinal))
            ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.DataDirectory.ToUpperInvariant())))[..16] : "";
        _singleInstance = new Mutex(false, @"Local\ShiftControl.SingleInstance" + suffix);
        try
        {
            if (_singleInstance.WaitOne(TimeSpan.FromSeconds(8))) return true;
        }
        catch (AbandonedMutexException)
        {
            return true; // a instância anterior terminou sem liberar: assumimos
        }
        nint existing = FindWindowW(null, "Shift Control");
        if (existing != 0)
        {
            ShowWindow(existing, 9); // SW_RESTORE
            SetForegroundWindow(existing);
        }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindowW(string? cls, string title);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int cmd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!AcquireSingleInstance())
        {
            Log.Information("Outra instância já está aberta; trazendo-a para a frente.");
            Log.CloseAndFlush();
            Exit();
            return;
        }

        // Os recursos do app só podem ser alterados a partir daqui (no construtor ainda não existem).
        var settings = Services.GetRequiredService<SettingsService>();
        if (!Environment.GetCommandLineArgs().Any(a => a.StartsWith("--data-dir=", StringComparison.Ordinal)))
        {
            try { SystemIntegration.MigrateLegacyStartup(settings.Current.StartMinimized); }
            catch (Exception ex) { Log.Warning(ex, "Could not migrate Windows startup registration."); }
        }
        var theme = Services.GetRequiredService<ThemeService>();
        theme.Install(Resources);
        theme.SetAccent(settings.Current.Accent);
        // Complete updates before monitoring, overlay, tray or the main UI can start.
        if (settings.Current.CheckUpdates && !Environment.GetCommandLineArgs().Any(a => a.StartsWith("--data-dir=", StringComparison.Ordinal)))
        {
            _startupWindow = new Views.StartupUpdateWindow();
            _startupWindow.Activate();
            if (await _startupWindow.RunAsync(Services.GetRequiredService<UpdateService>(), settings.Current.AutoInstallUpdates))
            {
                _startupWindow.Dismiss();
                _startupWindow = null;
                Shutdown();
                Exit();
                return;
            }
        }

        var engine = Services.GetRequiredService<MonitoringEngine>();
        var history = Services.GetRequiredService<IHistoryStore>();

        void ApplyRuntime(AppSettings s)
        {
            Fmt.TemperatureUnit = s.TemperatureUnit;
            engine.Interval = TimeSpan.FromMilliseconds(s.SampleIntervalMs);
            engine.RecordHistory = s.RecordHistory;
            history.RawRetention = TimeSpan.FromHours(s.RawRetentionHours);
            history.AggregatedRetention = TimeSpan.FromDays(s.AggregatedRetentionDays);
        }
        ApplyRuntime(settings.Current);
        settings.Changed += (_, s) =>
        {
            ApplyRuntime(s);
            Services.GetRequiredService<ThemeService>().Apply(s);
        };

        // A coleta começa antes da janela: o LibreHardwareMonitor leva ~1 s para abrir.
        engine.Start();

        bool minimized = Environment.GetCommandLineArgs().Contains(SystemIntegration.MinimizedArgument, StringComparer.OrdinalIgnoreCase);
        try
        {
            // --page=<overview|history|sensors|settings> abre direto numa tela (útil para atalhos e testes visuais).
            string? page = Environment.GetCommandLineArgs()
                .FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))?["--page=".Length..];
            _window = new MainWindow(minimized, page);
            _window.Closed += (_, _) => Shutdown();
            _window.Show();
            _startupWindow?.Dismiss();
            _startupWindow = null;

            // O overlay usa a thread da interface (timers e janela própria).
            Services.GetRequiredService<OverlayService>().Initialize(_window.DispatcherQueue);
            StartBackgroundServices(_window.DispatcherQueue);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Falha ao criar a janela principal.");
            Log.CloseAndFlush();
            throw;
        }
    }

    /// <summary>Bandeja, Game Boost (restaura um boost interrompido) e alertas: rodam com a janela fechada na bandeja.</summary>
    private void StartBackgroundServices(Microsoft.UI.Dispatching.DispatcherQueue ui)
    {
        try
        {
            var tray = Services.GetRequiredService<TrayService>();
            var overlay = Services.GetRequiredService<OverlayService>();
            var boost = Services.GetRequiredService<GameBoostService>();
            boost.Initialize(ui);
            Services.GetRequiredService<AlertService>().Initialize(ui);
            tray.Initialize();
            tray.MenuState = () => (overlay.Settings.Enabled, boost.IsActive);
            tray.Command += (cmd, x, y) =>
            {
                switch (cmd)
                {
                    case TrayCommand.Open: ShowMainWindow(); break;
                    case TrayCommand.MiniPanel: Services.GetRequiredService<TrayPanel>().Toggle(x, y); break;
                    case TrayCommand.ToggleOverlay: overlay.UpdateSettings(s => s.Enabled = !s.Enabled); break;
                    case TrayCommand.ToggleBoost:
                        if (boost.IsActive) boost.Deactivate(L.Get("boost.restoredManual"));
                        else boost.Activate(boost.Settings.DefaultProfile, null, null);
                        break;
                    case TrayCommand.Exit: ExitApp(); break;
                }
            };
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Falha ao iniciar os serviços em segundo plano (bandeja, Game Boost, alertas).");
        }
    }

    /// <summary>Mostra a janela principal (também quando estava escondida na bandeja).</summary>
    public static void ShowMainWindow()
    {
        if (Window is null) return;
        Window.AppWindow.Show();
        if (Window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } p) p.Restore();
        Window.Activate();
    }

    /// <summary>Encerra de verdade (o botão fechar só esconde quando "fechar para a bandeja" está ligado).</summary>
    public static void ExitApp()
    {
        if (Window is null)
        {
            Current.Exit();
            return;
        }
        Window.AllowClose = true;
        Window.Close();
    }

    private void Shutdown()
    {
        _log?.LogInformation("Encerrando.");
        try
        {
            // O contêiner libera na ordem inversa da criação: overlay (grava a sessão) → coleta → histórico → banco.
            Services.Dispose();
            _singleInstance?.ReleaseMutex();
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Falha ao encerrar.");
        }
        Log.CloseAndFlush();
    }
}
