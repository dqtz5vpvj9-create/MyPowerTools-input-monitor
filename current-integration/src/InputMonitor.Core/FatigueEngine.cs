namespace InputMonitor.Core;

/// <summary>Input-driven work timer with automatic credit for natural breaks.</summary>
public sealed class FatigueEngine
{
    public const double IdleGapSeconds = 60;
    public const double DefaultThreshold = 100;
    private readonly MonitorSettings _settings;
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private DateTimeOffset? _lastActivity;
    private DateTimeOffset _lastTick;
    private double _pendingIdlePoints;
    private bool _thresholdLatched;
    private bool _awaitingInput;
    private bool _inputSinceTick;
    public double Value { get; private set; }
    public double Threshold { get; private set; } = DefaultThreshold;
    public bool IsResting { get; private set; }
    public bool IsPaused { get; private set; }
    public bool SanitizedPersistedValue { get; private set; }
    public int Percentage => ToPercentage(Value, Threshold);
    public Action? OnShouldRemind { get; set; }
    public Action? OnChanged { get; set; }

    public FatigueEngine(MonitorSettings settings, TimeProvider? clock = null)
    {
        _settings = settings;
        _clock = clock ?? TimeProvider.System;
        _lastTick = _clock.GetUtcNow();
        _lastActivity = settings.FatigueLastActivity;
        Value = Math.Max(0, settings.FatigueValue);
        IsPaused = settings.FatigueIsPaused;
        // Old versions have no activity timestamp and may have accumulated while unattended.
        if (_lastActivity is null || _lastActivity > _lastTick ||
            (_lastTick - _lastActivity.Value).TotalSeconds >= settings.RestDurationSeconds ||
            !double.IsFinite(Value) || Value > DefaultThreshold + 1)
        {
            Value = 0;
            _lastActivity = null;
            SanitizedPersistedValue = true;
        }
        else
        {
            // Provisional seconds after the last input never survive a restart.
            Value = Math.Max(0, Value - settings.FatiguePendingIdlePoints);
        }
        Persist();
    }

    public FatigueSnapshot Snapshot()
    {
        lock (_gate) return new(Value, Threshold, IsResting, IsPaused, Percentage);
    }

    public void NotifyActivity(FatigueActivitySource source)
    {
        if (!(source == FatigueActivitySource.Keyboard && _settings.FatigueFromKeyboard ||
              source == FatigueActivitySource.Mouse && _settings.FatigueFromMouse)) return;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Advance(now, false);
            _lastActivity = now;
            _awaitingInput = false;
            _pendingIdlePoints = 0;
            _inputSinceTick = true;
            Persist();
        }
        OnChanged?.Invoke();
    }

    public void Tick(DateTimeOffset now, bool screenLocked = false)
    {
        Action? remind = null;
        lock (_gate)
        {
            var receivedInput = _inputSinceTick;
            _inputSinceTick = false;
            Advance(now, screenLocked);
            if (receivedInput && !screenLocked && !_awaitingInput && !IsPaused && !IsResting && _lastActivity is { } last &&
                (now - last).TotalSeconds < IdleGapSeconds && Value >= Threshold - 0.0000001 &&
                !_thresholdLatched)
            {
                _thresholdLatched = true;
                IsResting = true;
                remind = OnShouldRemind;
            }
            Persist();
        }
        remind?.Invoke();
        OnChanged?.Invoke();
    }

    private void Advance(DateTimeOffset now, bool screenLocked)
    {
        var elapsed = Math.Max(0, (now - _lastTick).TotalSeconds);
        _lastTick = now;
        if (_lastActivity is not { } last || IsResting) return;
        var idle = Math.Max(0, (now - last).TotalSeconds);
        if (idle >= _settings.RestDurationSeconds)
        {
            Reset();
            return;
        }
        if (screenLocked || idle >= IdleGapSeconds || elapsed > 5)
        {
            _awaitingInput = true;
            Value = Math.Max(0, Value - _pendingIdlePoints);
            _pendingIdlePoints = 0;
            return;
        }
        if (IsPaused || _awaitingInput) return;
        var points = elapsed * 100.0 / Math.Max(1, _settings.RemindIntervalMinutes * 60);
        var added = Math.Min(points, Math.Max(0, Threshold - Value));
        Value += added;
        _pendingIdlePoints += added;
    }

    public void ManualRest()
    {
        lock (_gate)
        {
            if (IsResting) return;
            IsResting = true;
            _thresholdLatched = true;
        }
        OnShouldRemind?.Invoke();
    }

    public void BeginResting()
    {
        lock (_gate) { IsResting = true; _thresholdLatched = true; }
    }

    public void Skip() => RestDone();

    public void RestDone()
    {
        lock (_gate)
        {
            Reset();
            IsResting = false;
            _lastTick = _clock.GetUtcNow();
            Persist();
        }
        OnChanged?.Invoke();
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (IsPaused == paused) return;
            IsPaused = paused;
            Reset();
            _lastTick = _clock.GetUtcNow();
            Persist();
        }
        OnChanged?.Invoke();
    }

    private void Reset()
    {
        Value = 0;
        Threshold = DefaultThreshold;
        _lastActivity = null;
        _pendingIdlePoints = 0;
        _thresholdLatched = false;
        _inputSinceTick = false;
    }

    internal static int ToPercentage(double value, double threshold) =>
        (int)Math.Round(Math.Clamp(value / (threshold <= 0 ? DefaultThreshold : threshold) * 100, 0, 100));

    private void Persist()
    {
        _settings.FatigueValue = Value;
        _settings.FatigueThreshold = Threshold;
        _settings.FatigueIsPaused = IsPaused;
        _settings.FatigueLastActivity = _lastActivity;
        _settings.FatiguePendingIdlePoints = _pendingIdlePoints;
    }
}
