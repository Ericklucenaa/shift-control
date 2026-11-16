using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace ShiftControl.App.Overlay;

/// <summary>
/// Janela do overlay: sem moldura, sempre no topo, fora do Alt+Tab e da barra de tarefas, fundo transparente
/// por pixel e, no modo de jogo, "click-through" (o mouse atravessa até o jogo). É uma janela comum composta
/// pelo DWM: não injeta nada no processo do jogo, não lê a memória dele e não altera arquivos.
/// </summary>
public sealed class OverlayHost
{
    private const uint WM_NCCALCSIZE = 0x0083, WM_NCPAINT = 0x0085, WM_NCACTIVATE = 0x0086, WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_NCHITTEST = 0x0084;
    private const nint MA_NOACTIVATE = 3, HTCAPTION = 2;

    private readonly Window _window;
    private readonly Win32.SubclassProc _subclass; // mantido em campo: o delegate não pode ser coletado
    private bool _clickThrough = true;
    private bool _closed;

    public OverlayHost(UIElement content)
    {
        // O overlay nunca recebe foco: sem isso o WinUI desenharia o retângulo de foco do teclado.
        var root = new Microsoft.UI.Xaml.Controls.Grid
        {
            IsTabStop = false,
            AllowFocusOnInteraction = false,
            UseSystemFocusVisuals = false,
            Children = { content },
        };
        _window = new Window
        {
            Content = root,
            SystemBackdrop = new TransparentBackdrop(),
            Title = "Shift Control Overlay",
        };
        var presenter = OverlappedPresenter.CreateForToolWindow();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        _window.AppWindow.SetPresenter(presenter);
        _window.AppWindow.IsShownInSwitchers = false;
        _window.Closed += (_, _) => _closed = true;

        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _subclass = WndProc;
        Win32.SetWindowSubclass(Hwnd, _subclass, 1, 0);
        ApplyStyles();
    }

    public nint Hwnd { get; }
    public AppWindow AppWindow => _window.AppWindow;
    public bool IsVisible => !_closed && _window.AppWindow.IsVisible;
    public bool IsClosed => _closed;

    /// <summary>Escala de DPI do monitor em que a janela está (1,0 = 96 DPI).</summary>
    public double Scale => Win32.GetDpiForWindow(Hwnd) is var dpi and > 0 ? dpi / 96.0 : 1.0;

    /// <summary>Disparado quando o usuário termina de arrastar a janela no modo de edição.</summary>
    public event EventHandler<PointInt32>? Moved;

    /// <summary>Ligado: o mouse atravessa o overlay. Desligado: modo de edição, o overlay pode ser arrastado.</summary>
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value) return;
            _clickThrough = value;
            ApplyStyles();
        }
    }

    private void ApplyStyles()
    {
        long style = Win32.GetWindowLongPtr(Hwnd, Win32.GWL_STYLE);
        style &= ~(Win32.WS_BORDER | Win32.WS_DLGFRAME | Win32.WS_THICKFRAME);
        style |= Win32.WS_POPUP;
        Win32.SetWindowLongPtr(Hwnd, Win32.GWL_STYLE, (nint)style);

        long ex = Win32.GetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE);
        ex &= ~(Win32.WS_EX_DLGMODALFRAME | Win32.WS_EX_WINDOWEDGE | Win32.WS_EX_CLIENTEDGE | Win32.WS_EX_STATICEDGE);
        ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOPMOST;
        if (_clickThrough) ex |= Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED;
        else ex &= ~(Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED);
        Win32.SetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE, (nint)ex);
        // Janela "layered" só é exibida depois de receber atributos; alfa 255 = a opacidade vem do próprio conteúdo.
        if (_clickThrough) Win32.SetLayeredWindowAttributes(Hwnd, 0, 255, Win32.LWA_ALPHA);

        Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);

        // O DWM compõe a janela inteira com alfa (transparência por pixel), sem borda nem cantos do sistema.
        var margins = new Win32.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(Hwnd, ref margins);
        int none = Win32.DWMWA_COLOR_NONE, square = Win32.DWMWCP_DONOTROUND;
        Win32.DwmSetWindowAttribute(Hwnd, Win32.DWMWA_BORDER_COLOR, ref none, sizeof(int));
        Win32.DwmSetWindowAttribute(Hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int));
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        switch (msg)
        {
            case WM_NCCALCSIZE when wParam != 0:
                return 0; // área cliente = janela inteira: nenhuma moldura, mesmo que o presenter reaplique estilos
            case WM_NCPAINT:
                return 0;
            case WM_NCACTIVATE:
                return 1;
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE; // clicar no overlay não tira o foco do jogo
            case WM_NCHITTEST when !_clickThrough:
                return HTCAPTION; // modo de edição: arrastar em qualquer ponto move a janela
            case 0x0232: // WM_EXITSIZEMOVE
                Moved?.Invoke(this, _window.AppWindow.Position);
                break;
            case (uint)Win32.WM_HOTKEY when wParam == HotkeyId:
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
                return 0;
        }
        return Win32.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private const int HotkeyId = 0x5C01;
    private bool _hotkeyRegistered;

    public event EventHandler? HotkeyPressed;

    /// <summary>Atalho global (funciona com o jogo em foco). Falha se outro programa já usa a combinação.</summary>
    public bool RegisterHotkey(ShiftControl.Overlay.Hotkey hotkey)
    {
        UnregisterHotkey();
        _hotkeyRegistered = Win32.RegisterHotKey(Hwnd, HotkeyId, (uint)hotkey.Modifiers | Win32.MOD_NOREPEAT, hotkey.VirtualKey);
        return _hotkeyRegistered;
    }

    public void UnregisterHotkey()
    {
        if (!_hotkeyRegistered) return;
        Win32.UnregisterHotKey(Hwnd, HotkeyId);
        _hotkeyRegistered = false;
    }

    private RectInt32 _lastRect;

    public void MoveAndResize(RectInt32 rect)
    {
        if (rect.X == _lastRect.X && rect.Y == _lastRect.Y && rect.Width == _lastRect.Width && rect.Height == _lastRect.Height) return;
        _lastRect = rect;
        _window.AppWindow.MoveAndResize(rect);
    }

    public void Show()
    {
        if (_closed) return;
        _window.AppWindow.Show(activateWindow: false);
        ApplyStyles(); // reafirma estilos e topmost depois de exibir
    }

    public void Hide()
    {
        if (!_closed) _window.AppWindow.Hide();
    }

    public void Close()
    {
        if (_closed) return;
        UnregisterHotkey();
        Win32.RemoveWindowSubclass(Hwnd, _subclass, 1);
        _window.Close();
    }
}
