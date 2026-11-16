using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;
using ShiftControl.App.Services;
using Windows.Graphics;

namespace ShiftControl.App.Views;

/// <summary>Startup gate: no application operations run while an update is applied.</summary>
public sealed class StartupUpdateWindow : Window
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _version = new() { Opacity = 0.7 };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Maximum = 100 };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 12, Visibility = Visibility.Collapsed };
    private readonly Button _retry = new();
    private readonly Button _continue = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource<bool>? _choice;
    private bool _closed;

    public StartupUpdateWindow()
    {
        Title = "Shift Control";
        AppWindow.Resize(new SizeInt32(560, 400));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        AppWindow.Move(new PointInt32(area.WorkArea.X + (area.WorkArea.Width - 560) / 2, area.WorkArea.Y + (area.WorkArea.Height - 400) / 2));
        var panel = new StackPanel { Padding = new Thickness(32), Spacing = 18 };
        panel.Children.Add(new TextBlock { Text = "Shift Control", FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(_version);
        panel.Children.Add(_status);
        panel.Children.Add(_progress);
        _continue.Content = L.Get("update.startup.continue");
        _retry.Click += (_, _) => _choice?.TrySetResult(true);
        _continue.Click += (_, _) => _choice?.TrySetResult(false);
        _actions.Children.Add(_retry);
        _actions.Children.Add(_continue);
        panel.Children.Add(_actions);
        Content = panel;
        var theme = App.Services.GetRequiredService<ThemeService>();
        var settings = App.Services.GetRequiredService<SettingsService>().Current;
        panel.RequestedTheme = theme.ActualTheme(settings.Theme);
        theme.Apply(settings);
        panel.Background = theme.Surface(panel.ActualTheme, "AppBackgroundBrush");
        _status.Text = L.Get("update.startup.checking");
        Closed += (_, _) => { _closed = true; _stop.Cancel(); };
    }

    public void Dismiss() { if (!_closed) Close(); }

    // True means exit this process (installer launched, or user closed startup).
    public async Task<bool> RunAsync(UpdateService updates, bool autoInstall)
    {
        try
        {
            while (true)
            {
                _actions.Visibility = Visibility.Collapsed;
                _progress.Visibility = Visibility.Visible;
                _progress.IsIndeterminate = true;
                _status.Text = L.Get("update.startup.checking");
                try
                {
                    var result = await updates.CheckAsync(_stop.Token);
                    _stop.Token.ThrowIfCancellationRequested();
                    if (result == UpdateCheck.Failed) throw new IOException(L.Get("update.result.Failed"));
                    if (result != UpdateCheck.Available || updates.Latest is not { } update) return false;
                    _version.Text = $"{AppPaths.Version} → {update.Version}";
                    if (update.InstallerUrl is null || update.Sha256 is null)
                        throw new InvalidDataException(L.Get("update.noVerifiedInstaller"));
                    if (!autoInstall && !await ChooseAsync(L.Get("update.availableTitle"), L.Get("update.install"))) return false;
                    _actions.Visibility = Visibility.Collapsed;
                    _status.Text = L.Get("update.downloading");
                    _progress.Visibility = Visibility.Visible;
                    var progress = new Progress<double>(value =>
                    {
                        if (_stop.IsCancellationRequested) return;
                        _progress.IsIndeterminate = false;
                        _progress.Value = value;
                        _status.Text = value >= 100 ? L.Get("update.startup.verifying") : L.F("update.startup.progress", (int)value);
                    });
                    if (await updates.InstallAsync(update, automatic: true, progress: progress, ct: _stop.Token, exitApp: false, installing: () =>
                    {
                        _status.Text = L.Get("update.startup.installing");
                        _progress.IsIndeterminate = true;
                    })) return true;
                    throw new IOException(L.Get("update.installFailed"));
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return true; }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Startup update failed.");
                    if (!await ChooseAsync(L.Get("update.installFailed") + " " + ex.Message, L.Get("update.startup.retry"))) return false;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return true; }
    }

    private async Task<bool> ChooseAsync(string message, string primary)
    {
        _status.Text = message;
        _progress.Visibility = Visibility.Collapsed;
        _retry.Content = primary;
        _choice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _actions.Visibility = Visibility.Visible;
        return await _choice.Task.WaitAsync(_stop.Token);
    }
}
