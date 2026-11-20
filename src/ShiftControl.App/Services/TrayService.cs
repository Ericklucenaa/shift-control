using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ShiftControl.App.Services;

public enum TrayCommand { Open, MiniPanel, ToggleOverlay, ToggleBoost, Exit }

/// <summary>
/// Ícone na área de notificação (Shell_NotifyIcon) numa janela só de mensagens, criada na thread da interface.
/// Clique: mini-painel. Botão direito: menu. Também mostra os avisos dos alertas (balões do Windows).
/// </summary>
public sealed partial class TrayService : IDisposable
{
    private const uint WmTray = 0x8000 + 21;
    private const int IdOpen = 1, IdOverlay = 2, IdBoost = 3, IdExit = 9;

    private readonly ILogger<TrayService> _log;
    private readonly WndProc _proc;
    private nint _hwnd, _icon;
    private uint _taskbarCreated;
    private bool _added;
    public bool IsAvailable => _added;

    public TrayService(ILogger<TrayService> log)
    {
        _log = log;
        _proc = WindowProc; // referência forte: o Windows chama de volta enquanto a janela existir
    }

    /// <summary>Ação escolhida pelo usuário (na thread da interface). O ponto é a posição do cursor em pixels.</summary>
    public event Action<TrayCommand, int, int>? Command;

    /// <summary>Textos dinâmicos do menu (overlay ligado? boost ativo?).</summary>
    public Func<(bool OverlayOn, bool BoostOn)>? MenuState { get; set; }

    public void Initialize()
    {
        if (_hwnd != 0) return;
        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInstance,
            lpszClassName = "ShiftControl.Tray",
        };
        RegisterClassExW(ref wc);
        // Janela oculta de nível superior recebe TaskbarCreated após reiniciar o Explorer.
        _hwnd = CreateWindowExW(0, "ShiftControl.Tray", "", 0, 0, 0, 0, 0, 0, 0, hInstance, 0);
        if (_hwnd == 0)
        {
            _log.LogWarning("Não foi possível criar a janela da bandeja ({Error}).", Marshal.GetLastWin32Error());
            return;
        }
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "ShiftControl.ico");
        _icon = File.Exists(ico) ? LoadImageW(0, ico, 1, GetSystemMetrics(49), GetSystemMetrics(50), 0x10) : 0;
        Add();
    }

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WmTray,
        hIcon = _icon,
        szTip = "Shift Control",
        szInfo = "",
        szInfoTitle = "",
    };

    private void Add()
    {
        var d = Data(0x1 | 0x2 | 0x4 | 0x80); // MESSAGE | ICON | TIP | SHOWTIP
        _added = Shell_NotifyIconW(0 /* NIM_ADD */, ref d);
        d.uVersion = 4;
        Shell_NotifyIconW(4 /* NIM_SETVERSION */, ref d);
        if (!_added) _log.LogWarning("Não foi possível adicionar o ícone da bandeja.");
    }

    public void SetTooltip(string text)
    {
        if (!_added) return;
        var d = Data(0x4 | 0x80);
        d.szTip = text.Length > 127 ? text[..127] : text;
        Shell_NotifyIconW(1 /* NIM_MODIFY */, ref d);
    }

    /// <summary>Notificação do Windows a partir do ícone (some sozinha; clicar abre o app).</summary>
    public void Notify(string title, string text, bool critical)
    {
        if (!_added) return;
        var d = Data(0x10 /* NIF_INFO */);
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = text.Length > 255 ? text[..255] : text;
        d.dwInfoFlags = critical ? 3u : 2u; // NIIF_ERROR : NIIF_WARNING
        Shell_NotifyIconW(1, ref d);
    }

    /// <summary>
    /// Executa o comando fora do WindowProc: uma exceção dentro de um callback nativo derruba o processo
    /// sem passar por nenhum tratador (era o que fechava o app ao clicar no ícone), e criar janelas WinUI
    /// no meio do despacho de uma mensagem Win32 é frágil. Na fila da interface, o erro vira só um registro.
    /// </summary>
    private void Raise(TrayCommand cmd, int x, int y)
    {
        void Run()
        {
            try { Command?.Invoke(cmd, x, y); }
            catch (Exception ex) { _log.LogError(ex, "Falha ao executar o comando da bandeja {Command}.", cmd); }
        }
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null || !queue.TryEnqueue(Run)) Run();
    }

    private const long DoubleEventWindowMs = 250;
    private long _lastClick = -DoubleEventWindowMs;

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmTray)
        {
            int ev = (int)(lParam & 0xFFFF);
            int x = (short)(wParam & 0xFFFF), y = (short)((wParam >> 16) & 0xFFFF);
            switch (ev)
            {
                case 0x0202: // WM_LBUTTONUP
                case 0x0400: // NIN_SELECT
                    // Com NOTIFYICON_VERSION_4 um clique pode chegar como os dois eventos; contar duas vezes
                    // abria e fechava (ou fechava e reabria) o mini-painel no mesmo clique.
                    long now = Environment.TickCount64;
                    if (now - _lastClick < DoubleEventWindowMs) break;
                    _lastClick = now;
                    Raise(TrayCommand.MiniPanel, x, y);
                    break;
                case 0x007B: // WM_CONTEXTMENU
                    ShowMenu(x, y);
                    break;
                case 0x0405: // NIN_BALLOONUSERCLICK
                    Raise(TrayCommand.Open, x, y);
                    break;
            }
            return 0;
        }
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            Add(); // o Explorer reiniciou: o ícone precisa ser recriado
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var (overlayOn, boostOn) = MenuState?.Invoke() ?? (false, false);
        nint menu = CreatePopupMenu();
        AppendMenuW(menu, 0, IdOpen, L.Get("tray.open"));
        AppendMenuW(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenuW(menu, overlayOn ? 0x8u : 0u, IdOverlay, L.Get("tray.overlay"));
        AppendMenuW(menu, boostOn ? 0x8u : 0u, IdBoost, L.Get("tray.boost"));
        AppendMenuW(menu, 0x800, 0, null);
        AppendMenuW(menu, 0, IdExit, L.Get("tray.exit"));
        SetForegroundWindow(_hwnd);
        int id = TrackPopupMenuEx(menu, 0x0100 | 0x0080 | 0x0002 /* RETURNCMD | NONOTIFY | RIGHTBUTTON */, x, y, _hwnd, 0);
        DestroyMenu(menu);
        PostMessageW(_hwnd, 0, 0, 0);
        var cmd = id switch { IdOpen => TrayCommand.Open, IdOverlay => TrayCommand.ToggleOverlay, IdBoost => TrayCommand.ToggleBoost, IdExit => TrayCommand.Exit, _ => (TrayCommand?)null };
        if (cmd is { } c) Raise(c, x, y);
    }

    public void Dispose()
    {
        if (_added)
        {
            var d = Data(0);
            Shell_NotifyIconW(2 /* NIM_DELETE */, ref d);
            _added = false;
        }
        if (_hwnd != 0) DestroyWindow(_hwnd);
        if (_icon != 0) DestroyIcon(_icon);
        _hwnd = 0;
    }

    #region Win32

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(int exStyle, string cls, string name, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);

    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImageW(nint inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(nint menu, uint flags, nint id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    #endregion
}
