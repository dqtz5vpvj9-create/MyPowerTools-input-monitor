using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using InputMonitor.Core;

namespace InputMonitor.MyPowerTools;

[SupportedOSPlatform("windows")]
internal sealed class WindowsFrontAppTracker : IFrontAppTracker
{
    private readonly AppCategoryMap _categories;
    private readonly object _gate = new();
    private readonly StringBuilder _titleBuffer = new(1024);
    private Timer? _heartbeat;
    private TimeSpan _interval;
    private FrontAppSession? _current;
    private bool _screenLocked;
    private bool _running;

    public WindowsFrontAppTracker(AppCategoryMap categories, TimeSpan? heartbeatInterval = null)
    {
        _categories = categories;
        _interval = heartbeatInterval ?? TimeSpan.FromSeconds(30);
    }

    public event Action<FrontAppSession>? SessionCompleted;
    public event Action<FrontAppSession, DateTimeOffset>? Heartbeat;
    public bool ScreenLocked => _screenLocked;

    public FrontAppSession? CurrentSession
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _running)
        {
            return;
        }

        _running = true;
        SwitchToForeground(DateTimeOffset.Now);
        _heartbeat = new Timer(_ => Pulse(), null, _interval, _interval);
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _heartbeat?.Dispose();
        _heartbeat = null;
        lock (_gate)
        {
            EndCurrentLocked(DateTimeOffset.Now);
        }
    }

    public void UpdateHeartbeatInterval(TimeSpan interval)
    {
        _interval = interval;
        if (_heartbeat is not null)
        {
            _heartbeat.Change(interval, interval);
        }
    }

    public void Dispose() => Stop();

    private void Pulse()
    {
        var locked = IsWorkstationLocked();
        lock (_gate)
        {
            if (locked && !_screenLocked)
            {
                _screenLocked = true;
                EndCurrentLocked(DateTimeOffset.Now);
                return;
            }

            if (!locked && _screenLocked)
            {
                _screenLocked = false;
            }
            else if (_screenLocked)
            {
                return;
            }
        }

        if (locked)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var probe = ProbeForeground();
        lock (_gate)
        {
            if (_current is { } session)
            {
                // Protected-process / probe failures resolve as "unknown". Do not keep accruing
                // usage on the previous resolved app — switch to the explicit unknown session.
                if (ForegroundAppIdentity.MustLeaveResolvedSession(session.BundleId, probe.BundleId))
                {
                    EndCurrentLocked(now);
                    _current = Begin(probe, now);
                    return;
                }

                if (!string.Equals(session.BundleId, probe.BundleId, StringComparison.OrdinalIgnoreCase))
                {
                    EndCurrentLocked(now);
                    _current = Begin(probe, now);
                    return;
                }

                if (!string.Equals(session.WindowTitle, probe.Title, StringComparison.Ordinal) &&
                    probe.Title is not null)
                {
                    EndCurrentLocked(now);
                    _current = Begin(probe with { }, now);
                    _current.WindowTitle = probe.Title;
                    return;
                }

                Heartbeat?.Invoke(session, now);
                return;
            }
        }

        SwitchToForeground(now);
    }

    private void SwitchToForeground(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_screenLocked)
            {
                return;
            }

            EndCurrentLocked(now);
            var probe = ProbeForeground();
            _current = Begin(probe, now);
        }
    }

    private FrontAppSession Begin((string BundleId, string AppName, string? Title) probe, DateTimeOffset now) =>
        new()
        {
            BundleId = probe.BundleId,
            AppName = probe.AppName,
            WindowTitle = probe.Title,
            Start = now,
            Category = _categories.CategoryFor(probe.BundleId)
        };

    private void EndCurrentLocked(DateTimeOffset now)
    {
        if (_current is not { } session)
        {
            return;
        }

        session.End = now;
        _current = null;
        if ((session.DurationSeconds ?? 0) >= 1)
        {
            SessionCompleted?.Invoke(session);
        }
    }

    private (string BundleId, string AppName, string? Title) ProbeForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0)
        {
            return ("unknown", "Unknown", null);
        }

        var title = ReadTitle(hwnd);
        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            var unresolved = ForegroundAppIdentity.Resolve(null, null, null, title);
            return (unresolved.BundleId, unresolved.AppName, title);
        }

        var path = TryImagePath(processId);
        if (path is not null && IsApplicationFrameHost(path))
        {
            var coreWindow = FindCoreWindow(hwnd);
            if (coreWindow != 0)
            {
                GetWindowThreadProcessId(coreWindow, out var coreProcessId);
                var corePath = coreProcessId == 0 ? null : TryImagePath(coreProcessId);
                if (corePath is not null && !IsApplicationFrameHost(corePath))
                {
                    path = corePath;
                }
            }
        }

        string? description = null;
        if (path is not null)
        {
            description = IsApplicationFrameHost(path) && !string.IsNullOrWhiteSpace(title)
                ? title
                : TryFileDescription(path);
        }

        var processName = path is null ? TryProcessName(processId) : null;
        var identity = ForegroundAppIdentity.Resolve(path, description, processName, title);
        return (identity.BundleId, identity.AppName, title);
    }

    private string? ReadTitle(nint hwnd)
    {
        if (_titleBuffer.Capacity < 1024)
        {
            _titleBuffer.Capacity = 1024;
        }

        _titleBuffer.Clear();
        var length = GetWindowText(hwnd, _titleBuffer, _titleBuffer.Capacity);
        return length > 0 ? _titleBuffer.ToString() : null;
    }

    private static bool IsApplicationFrameHost(string path) =>
        string.Equals(Path.GetFileName(path), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

    private static string? TryImagePath(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size) || size <= 0)
            {
                return null;
            }

            var path = buffer.ToString();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? TryProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? TryFileDescription(string path)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            return string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static nint FindCoreWindow(nint parent)
    {
        nint found = 0;
        EnumChildProc callback = (child, _) =>
        {
            var className = new StringBuilder(256);
            if (GetClassName(child, className, className.Capacity) > 0 &&
                string.Equals(className.ToString(), "Windows.UI.Core.CoreWindow", StringComparison.Ordinal))
            {
                found = child;
                return false;
            }

            return true;
        };
        EnumChildWindows(parent, callback, 0);
        GC.KeepAlive(callback);
        return found;
    }

    private static bool IsWorkstationLocked()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == 0)
        {
            return true;
        }

        CloseDesktop(desktop);
        return false;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    private delegate bool EnumChildProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hWndParent, EnumChildProc lpEnumFunc, nint lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern nint OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(nint hDesktop);
}
