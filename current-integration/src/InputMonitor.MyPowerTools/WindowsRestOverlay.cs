using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InputMonitor.Core;

namespace InputMonitor.MyPowerTools;

internal readonly record struct RestCompletionSound(bool Enabled, string Name, double Volume);

[SupportedOSPlatform("windows")]
internal sealed class WindowsRestOverlay : IRestOverlay
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsVisible = 0x10000000;
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const int LwaAlpha = 0x02;
    private const byte OverlayAlpha = 217;
    private const int WmDestroy = 0x0002;
    private const int WmClose = 0x0010;
    private const int WmPaint = 0x000F;
    private const int WmEraseBkgnd = 0x0014;
    private const int WmKeyDown = 0x0100;
    private const int WmLButtonUp = 0x0202;
    private const int WmSkipRest = 0x8001;
    private const int VkEscape = 0x1B;
    private const int WhKeyboardLl = 13;
    private const int CsHredraw = 0x0002;
    private const int CsVredraw = 0x0001;
    private const int DefaultCharset = 1;
    private const int ClearTypeQuality = 5;
    private const int FwLight = 300;
    private const int FwNormal = 400;
    private const int FwSemibold = 600;
    private const int NullPen = 8;
    private const int IdcArrow = 32512;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int Transparent = 1;
    private const uint DtCenter = 0x00000001;
    private const uint DtVcenter = 0x00000004;
    private const uint DtSingleLine = 0x00000020;
    // A rest reminder must not be the brightest thing on the desk. The previous palette was an
    // 85%-opaque pure white sheet with dark text, which is glaring on a large high-DPI display and
    // works against the point of resting your eyes. These are low-luminance equivalents.
    // COLORREF is 0x00BBGGRR.
    private const uint ColorBackground = 0x00161616;
    private const uint ColorIcon = 0x004880B0;
    private const uint ColorTitle = 0x00E6E6E6;
    private const uint ColorTime = 0x00F5F5F5;
    private const uint ColorSubtitle = 0x009E9E9E;
    private const uint ColorButtonFill = 0x00262626;
    private const uint ColorButtonBorder = 0x00454545;
    private const uint ColorButtonText = 0x00D6D6D6;
    private const uint SndAlias = 0x00010000;
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const int ErrorClassAlreadyExists = 1410;
    private const int GwlExstyle = -20;

    private static readonly WndProc StaticProc = StaticWindowProc;

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. The overlay can be hosted by a process that
    // declares no DPI awareness at all (MyPowerTools.Runner has no manifest), so the drawing
    // thread opts in itself. Without this the window is virtualized into the DPI-unaware
    // coordinate space and Windows bitmap-stretches it, which is what makes the rest screen
    // look wrong on a high-DPI display.
    private static readonly nint DpiAwarenessContextPerMonitorV2 = -4;

    private static WindowsRestOverlay? _active;
    private static bool _classRegistered;

    private readonly object _windowsGate = new();
    private readonly List<nint> _windows = [];
    private Func<RestCompletionSound>? _readSound;
    private Action? _skip;
    private Action? _finished;
    private Thread? _thread;
    private Timer? _timer;
    private nint _keyboardHook;
    private nint _keyboardProc;
    private HookProc? _keyboardCallback;
    private int _remaining;
    private int _closed;
    private bool _showing;
    private bool _threadDpiAware;

    public bool IsShowing => _showing;

    public void BindCompletionSound(Func<RestCompletionSound> readSound) => _readSound = readSound;

    private static bool EnablePerMonitorDpiAwareness()
    {
        try
        {
            // Returns the previous context (0 on failure); the thread keeps the new one.
            return SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorV2) != 0;
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-1607 builds lack the API. Leave the host's awareness untouched.
            return false;
        }
    }

    /// <summary>
    /// Layout scale for one overlay window. Only applied when this thread really receives
    /// physical pixels: an unaware thread is virtualized into the logical coordinate space and
    /// Windows applies the display scale on top, so scaling here as well would double it.
    /// </summary>
    private float CurrentScale(nint hwnd)
    {
        if (!_threadDpiAware)
        {
            return 1f;
        }

        var dpi = GetDpiForWindow(hwnd);
        return (dpi == 0 ? 96u : dpi) / 96f;
    }

    public void Show(int totalSeconds, Action skip, Action finished)
    {
        if (!OperatingSystem.IsWindows() || _showing)
        {
            return;
        }

        _skip = skip;
        _finished = finished;
        _remaining = Math.Max(10, totalSeconds);
        _closed = 0;
        _showing = true;
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "InputMonitor.RestOverlay"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Dismiss()
    {
        nint[] windows;
        lock (_windowsGate)
        {
            windows = _windows.ToArray();
        }

        foreach (var window in windows)
        {
            if (window != 0)
            {
                PostMessage(window, WmClose, 0, 0);
            }
        }
    }

    private void MessageLoop()
    {
        try
        {
            _active = this;
            _threadDpiAware = EnablePerMonitorDpiAwareness();
            EnsureClass();
            if (!_classRegistered || !CreateMonitorWindows())
            {
                RequestSkip();
                return;
            }

            InstallEscapeHook();
            _timer = new Timer(OnTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            _timer?.Dispose();
            _timer = null;
            if (_keyboardHook != 0)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = 0;
            }

            _keyboardCallback = null;

            // Destroy on this STA thread before clearing ownership. If GetMessage fails or an
            // exception escapes after CreateMonitorWindows(), clearing alone would orphan
            // topmost non-click-through overlays with no Esc hook left to dismiss them.
            nint[] remaining;
            lock (_windowsGate)
            {
                remaining = _windows.ToArray();
                _windows.Clear();
            }

            foreach (var window in remaining)
            {
                if (window != 0 && IsWindow(window))
                {
                    DestroyWindow(window);
                }
            }

            _showing = false;
            if (ReferenceEquals(_active, this))
            {
                _active = null;
            }
        }
    }

    private void OnTick(object? state)
    {
        if (Interlocked.Decrement(ref _remaining) <= 0)
        {
            RequestFinish();
            return;
        }

        nint[] windows;
        lock (_windowsGate)
        {
            windows = _windows.ToArray();
        }

        foreach (var window in windows)
        {
            InvalidateRect(window, 0, false);
        }
    }

    private void RequestSkip()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        try
        {
            _skip?.Invoke();
        }
        catch (Exception exception)
        {
            TraceSkipFailure(exception);
        }

        Dismiss();
    }

    private void RequestFinish()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        PlayCompletionSound();
        try
        {
            _finished?.Invoke();
        }
        catch (Exception exception)
        {
            TraceSkipFailure(exception);
        }

        Dismiss();
    }

    private void PlayCompletionSound()
    {
        RestCompletionSound sound;
        try
        {
            sound = _readSound?.Invoke() ?? default;
        }
        catch (Exception exception)
        {
            TraceSkipFailure(exception);
            return;
        }

        if (!sound.Enabled || sound.Volume <= 0)
        {
            return;
        }

        PlaySound(SystemAlias(sound.Name), 0, SndAlias | SndAsync | SndNoDefault);
    }

    private static void TraceSkipFailure(Exception exception) =>
        System.Diagnostics.Trace.TraceWarning($"InputMonitor rest overlay callback failed: {exception.Message}");

    private static string SystemAlias(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "SystemAsterisk";
        }

        var key = name.Trim();
        if (key.StartsWith("System", StringComparison.OrdinalIgnoreCase))
        {
            return key;
        }

        return key.ToLowerInvariant() switch
        {
            "exclamation" => "SystemExclamation",
            "hand" or "critical" or "error" => "SystemHand",
            "question" => "SystemQuestion",
            "beep" or "default" => "SystemDefault",
            _ => "SystemAsterisk"
        };
    }

    private bool CreateMonitorWindows()
    {
        var monitors = new List<Rect>();
        MonitorEnumProc callback = (nint _, nint _, ref Rect rect, nint _) =>
        {
            if (rect.Right > rect.Left && rect.Bottom > rect.Top)
            {
                monitors.Add(rect);
            }

            return true;
        };
        EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        if (monitors.Count == 0)
        {
            monitors.Add(new Rect
            {
                Left = 0,
                Top = 0,
                Right = GetSystemMetrics(SmCxScreen),
                Bottom = GetSystemMetrics(SmCyScreen)
            });
        }

        var instance = GetModuleHandle(null);
        foreach (var monitor in monitors)
        {
            var window = CreateWindowEx(
                WsExTopmost | WsExToolwindow | WsExLayered | WsExNoActivate,
                "InputMonitorRestOverlay",
                "休息一下",
                WsPopup | WsVisible,
                monitor.Left,
                monitor.Top,
                monitor.Right - monitor.Left,
                monitor.Bottom - monitor.Top,
                0,
                0,
                instance,
                0);
            if (window == 0)
            {
                continue;
            }

            SetLayeredWindowAttributes(window, 0, OverlayAlpha, LwaAlpha);
            lock (_windowsGate)
            {
                _windows.Add(window);
            }
        }

        return _windows.Count > 0;
    }

    private void InstallEscapeHook()
    {
        _keyboardCallback = EscapeHook;
        _keyboardProc = Marshal.GetFunctionPointerForDelegate(_keyboardCallback);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, HookModuleHandle(_keyboardProc), 0);
        if (_keyboardHook == 0)
        {
            // WS_EX_NOACTIVATE windows never take focus, so WM_KEYDOWN is unreliable without the
            // low-level hook. Fall back to an activatable overlay that can receive Esc itself.
            EnableKeyboardDismissFallback();
        }
    }

    private void EnableKeyboardDismissFallback()
    {
        nint primary;
        lock (_windowsGate)
        {
            primary = _windows.Count > 0 ? _windows[0] : 0;
        }

        if (primary == 0 || !IsWindow(primary))
        {
            return;
        }

        var exStyle = GetWindowLongPtr(primary, GwlExstyle);
        if ((exStyle & WsExNoActivate) != 0)
        {
            SetWindowLongPtr(primary, GwlExstyle, exStyle & ~(nint)WsExNoActivate);
        }

        SetForegroundWindow(primary);
        SetFocus(primary);
    }

    private nint EscapeHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && (int)wParam == WmKeyDown && _closed == 0)
        {
            var info = Marshal.PtrToStructure<KbdLlHook>(lParam);
            if (info.VkCode == VkEscape)
            {
                nint target;
                lock (_windowsGate)
                {
                    target = _windows.Count > 0 ? _windows[0] : 0;
                }

                if (target != 0)
                {
                    PostMessage(target, WmSkipRest, 0, 0);
                    return 1;
                }
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private static void EnsureClass()
    {
        if (_classRegistered)
        {
            return;
        }

        // The class brush is what Windows erases the window with, so it has to match the
        // painted background; a white class brush kept flashing a glaring sheet behind the
        // paint even after the fill colour was toned down.
        var brush = CreateSolidBrush(ColorBackground);
        var wndClass = new WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            Style = CsHredraw | CsVredraw,
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticProc),
            HInstance = GetModuleHandle(null),
            HCursor = LoadCursor(0, IdcArrow),
            HbrBackground = brush,
            LpszClassName = "InputMonitorRestOverlay"
        };
        if (RegisterClassEx(ref wndClass) != 0 || Marshal.GetLastWin32Error() == ErrorClassAlreadyExists)
        {
            _classRegistered = true;
        }
    }

    private static nint StaticWindowProc(nint hwnd, uint msg, nint wParam, nint lParam) =>
        _active?.WindowProc(hwnd, msg, wParam, lParam) ?? DefWindowProc(hwnd, msg, wParam, lParam);

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WmEraseBkgnd:
                return 1;
            case WmPaint:
                Paint(hwnd);
                return 0;
            case WmLButtonUp when HitSkipButton(hwnd, SignedLowWord(lParam), SignedHighWord(lParam)):
                RequestSkip();
                return 0;
            case WmKeyDown when (int)wParam == VkEscape:
            case WmSkipRest:
                RequestSkip();
                return 0;
            case WmClose:
                DestroyWindow(hwnd);
                return 0;
            case WmDestroy:
                var quit = false;
                lock (_windowsGate)
                {
                    _windows.Remove(hwnd);
                    quit = _windows.Count == 0;
                }

                if (quit)
                {
                    PostQuitMessage(0);
                }

                return 0;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private unsafe void Paint(nint hwnd)
    {
        var dc = BeginPaint(hwnd, out var ps);
        try
        {
            if (!GetClientRect(hwnd, out var client))
            {
                return;
            }

            var width = Math.Max(1, client.Right - client.Left);
            var height = Math.Max(1, client.Bottom - client.Top);
            var background = CreateSolidBrush(ColorBackground);
            FillRect(dc, ref client, background);
            DeleteObject(background);
            SetBkMode(dc, Transparent);

            var scale = CurrentScale(hwnd);
            var icon = Math.Max(48, (int)Math.Round(72 * scale));
            var titleSize = Math.Max(18, (int)Math.Round(28 * scale));
            var timeSize = Math.Max(48, (int)Math.Round(96 * scale));
            var subtitleSize = Math.Max(12, (int)Math.Round(15 * scale));
            var gapAfterIcon = Math.Max(12, (int)Math.Round(24 * scale));
            var gapAfterTitle = Math.Max(8, (int)Math.Round(12 * scale));
            var gapAfterTime = Math.Max(6, (int)Math.Round(8 * scale));
            var titleHeight = (int)Math.Round(titleSize * 1.35);
            var timeHeight = (int)Math.Round(timeSize * 1.05);
            var subtitleHeight = (int)Math.Round(subtitleSize * 1.5);
            var block = icon + gapAfterIcon + titleHeight + gapAfterTitle + timeHeight + gapAfterTime + subtitleHeight;
            var button = SkipButtonRect(width, height, scale);
            var top = Math.Max((int)Math.Round(24 * scale), (height - (button.Bottom - button.Top) - (int)Math.Round(64 * scale) - block) / 2);

            var iconRect = new Rect
            {
                Left = (width - icon) / 2,
                Top = top,
                Right = (width - icon) / 2 + icon,
                Bottom = top + icon
            };
            DrawIcon(dc, iconRect, scale);

            var y = iconRect.Bottom + gapAfterIcon;
            DrawTextLine(dc, "该休息了", "Segoe UI", titleSize, FwSemibold, ColorTitle, 0, y, width, titleHeight);
            y += titleHeight + gapAfterTitle;
            var remaining = Math.Max(0, Volatile.Read(ref _remaining));
            DrawTextLine(dc, $"{remaining / 60:00}:{remaining % 60:00}", "Consolas", timeSize, FwLight, ColorTime, 0, y, width, timeHeight);
            y += timeHeight + gapAfterTime;
            DrawTextLine(dc, "起身活动一下，看看远处", "Segoe UI", subtitleSize, FwNormal, ColorSubtitle, 0, y, width, subtitleHeight);
            DrawSkipButton(dc, button, scale);
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    private static void DrawIcon(nint dc, Rect rect, float scale)
    {
        var brush = CreateSolidBrush(ColorIcon);
        var previousBrush = SelectObject(dc, brush);
        var previousPen = SelectObject(dc, GetStockObject(NullPen));
        Ellipse(dc, rect.Left, rect.Top, rect.Right, rect.Bottom);
        SelectObject(dc, previousPen);
        SelectObject(dc, previousBrush);
        DeleteObject(brush);

        var glyph = Math.Max(18, (int)Math.Round(32 * scale));
        DrawTextLine(dc, "\u2615", "Segoe UI Symbol", glyph, FwNormal, ColorBackground, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static void DrawSkipButton(nint dc, Rect rect, float scale)
    {
        var brush = CreateSolidBrush(ColorButtonFill);
        var pen = CreatePen(0, Math.Max(1, (int)Math.Round(scale)), ColorButtonBorder);
        var previousBrush = SelectObject(dc, brush);
        var previousPen = SelectObject(dc, pen);
        var radius = Math.Max(8, rect.Bottom - rect.Top);
        RoundRect(dc, rect.Left, rect.Top, rect.Right, rect.Bottom, radius, radius);
        SelectObject(dc, previousPen);
        SelectObject(dc, previousBrush);
        DeleteObject(pen);
        DeleteObject(brush);
        var font = Math.Max(12, (int)Math.Round(15 * scale));
        DrawTextLine(dc, "跳过本次", "Segoe UI", font, FwNormal, ColorButtonText, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static void DrawTextLine(nint dc, string text, string face, int pixelHeight, int weight, uint color, int x, int y, int width, int height)
    {
        var font = CreateFont(-Math.Max(1, pixelHeight), 0, 0, 0, weight, 0, 0, 0, DefaultCharset, 0, 0, ClearTypeQuality, 0, face);
        var previous = SelectObject(dc, font);
        SetTextColor(dc, color);
        var rect = new Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
        DrawText(dc, text, -1, ref rect, DtCenter | DtVcenter | DtSingleLine);
        SelectObject(dc, previous);
        DeleteObject(font);
    }

    private bool HitSkipButton(nint hwnd, int x, int y)
    {
        if (!GetClientRect(hwnd, out var client))
        {
            return false;
        }

        var button = SkipButtonRect(client.Right - client.Left, client.Bottom - client.Top, CurrentScale(hwnd));
        return x >= button.Left && x < button.Right && y >= button.Top && y < button.Bottom;
    }

    private static Rect SkipButtonRect(int width, int height, float scale)
    {
        var buttonHeight = Math.Max(36, (int)Math.Round(40 * scale));
        var buttonWidth = Math.Max(128, (int)Math.Round(148 * scale));
        var margin = Math.Max(36, (int)Math.Round(64 * scale));
        var left = Math.Max(0, (width - buttonWidth) / 2);
        var top = Math.Max(0, height - margin - buttonHeight);
        return new Rect { Left = left, Top = top, Right = left + buttonWidth, Bottom = top + buttonHeight };
    }

    private static int SignedLowWord(nint value) => (short)(value & 0xFFFF);

    private static int SignedHighWord(nint value) => (short)((value >> 16) & 0xFFFF);

    private static nint HookModuleHandle(nint proc)
    {
        const int fromAddress = 0x00000004;
        const int unchangedRefCount = 0x00000002;
        if (GetModuleHandleEx(fromAddress | unchangedRefCount, proc, out var handle) && handle != 0)
        {
            return handle;
        }

        return GetModuleHandle(null);
    }

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref Rect rect, nint data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public nint LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public nint HInstance;
        public nint HIcon;
        public nint HCursor;
        public nint HbrBackground;
        public string? LpszMenuName;
        public string LpszClassName;
        public nint HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PaintStruct
    {
        public nint Hdc;
        public int Erase;
        public Rect RcPaint;
        public int Restore;
        public int IncUpdate;
        public fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHook
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hWnd);

    private static nint GetWindowLongPtr(nint hWnd, int nIndex) =>
        nint.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    private static void SetWindowLongPtr(nint hWnd, int nIndex, nint value)
    {
        if (nint.Size == 8)
        {
            SetWindowLongPtr64(hWnd, nIndex, value);
        }
        else
        {
            SetWindowLong32(hWnd, nIndex, (int)value);
        }
    }

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint hWnd, out PaintStruct lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(nint hWnd, ref PaintStruct lpPaint);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint hWnd, nint lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawText(nint hdc, string lpchText, int cchText, ref Rect lprc, uint format);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadCursor(nint hInstance, nint lpCursorName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, nint lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(nint hdc, uint color);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern nint CreatePen(int style, int width, uint color);

    [DllImport("gdi32.dll")]
    private static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int obj);

    [DllImport("gdi32.dll")]
    private static extern bool Ellipse(nint hdc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool RoundRect(nint hdc, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint hdc, ref Rect lprc, nint brush);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetModuleHandleEx(int dwFlags, nint lpModuleName, out nint phModule);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string pszSound, nint hmod, uint fdwSound);
}
