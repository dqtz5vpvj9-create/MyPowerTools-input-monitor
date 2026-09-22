namespace InputMonitor.Core;

/// <summary>
/// Fatigue accrues only while a selected source stays active. A full remind interval
/// is 100 points. Crossing the threshold reminds once; skipping or finishing a rest
/// starts a new full interval. Pausing reminders caps the score at the threshold.
/// </summary>
public sealed class FatigueEngine
{
    public const double IdleGapSeconds = 120;
    public const double DefaultThreshold = 100;

    private readonly MonitorSettings _settings;
    private readonly object _gate = new();
    private DateTimeOffset? _lastActivity;
    private (double Value, double Threshold)? _manualRestBackup;
    private bool _thresholdLatched;

    public double Value { get; private set; }
    public double Threshold { get; private set; } = DefaultThreshold;
    public bool IsResting { get; private set; }
    public bool IsPaused { get; private set; }
    public bool SanitizedPersistedValue { get; private set; }
    public int Percentage => ToPercentage(Value, Threshold);
    public Action? OnShouldRemind { get; set; }
    public Action? OnChanged { get; set; }

    public FatigueEngine(MonitorSettings settings)
    {
        _settings = settings;
        Value = Math.Max(0, settings.FatigueValue);
        Threshold = settings.FatigueThreshold <= 0 ? DefaultThreshold : settings.FatigueThreshold;
        IsPaused = settings.FatigueIsPaused;
        if (Value > Threshold + 1)
        {
            Value = 0;
            Threshold = DefaultThreshold;
            SanitizedPersistedValue = true;
            Persist();
        }
    }

    public FatigueSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new(Value, Threshold, IsResting, IsPaused, Percentage);
        }
    }

    public void NotifyActivity(FatigueActivitySource source)
    {
        var allowed = source switch
        {
            FatigueActivitySource.Keyboard => _settings.FatigueFromKeyboard,
            FatigueActivitySource.Mouse => _settings.FatigueFromMouse,
            FatigueActivitySource.App => _settings.FatigueFromApp,
            _ => false
        };
        if (!allowed)
        {
            return;
        }

        lock (_gate)
        {
            _lastActivity = DateTimeOffset.Now;
        }
    }

    public void Tick(DateTimeOffset now)
    {
        Action? remind = null;
        var changed = false;
        lock (_gate)
        {
            if (IsResting || !IsRecentlyActive(now))
            {
                return;
            }

            if (IsPaused && ReachedThreshold())
            {
                return;
            }

            Value += PointsPerSecond();
            if (IsPaused)
            {
                if (Value > Threshold)
                {
                    Value = Threshold;
                }
            }
            else if (ReachedThreshold())
            {
                if (!_thresholdLatched)
                {
                    _thresholdLatched = true;
                    remind = OnShouldRemind;
                }
            }
            else
            {
                _thresholdLatched = false;
            }

            Persist();
            changed = true;
        }

        remind?.Invoke();
        if (changed)
        {
            OnChanged?.Invoke();
        }
    }

    public void ManualRest()
    {
        var remind = false;
        lock (_gate)
        {
            if (IsResting)
            {
                return;
            }

            _manualRestBackup = (Value, Threshold);
            remind = true;
        }

        if (remind)
        {
            OnShouldRemind?.Invoke();
        }
    }

    public void BeginResting()
    {
        lock (_gate)
        {
            IsResting = true;
            _thresholdLatched = true;
        }
    }

    public void Skip()
    {
        lock (_gate)
        {
            if (_manualRestBackup is { } backup)
            {
                Value = Math.Max(0, backup.Value);
                Threshold = backup.Threshold <= 0 ? DefaultThreshold : backup.Threshold;
                _manualRestBackup = null;
                if (ReachedThreshold())
                {
                    Value = 0;
                    Threshold = DefaultThreshold;
                }
            }
            else
            {
                Value = 0;
                Threshold = DefaultThreshold;
            }

            _thresholdLatched = false;
            IsResting = false;
            Persist();
        }

        OnChanged?.Invoke();
    }

    public void RestDone()
    {
        lock (_gate)
        {
            Value = 0;
            Threshold = DefaultThreshold;
            _manualRestBackup = null;
            _thresholdLatched = false;
            IsResting = false;
            Persist();
        }

        OnChanged?.Invoke();
    }

    public void SetPaused(bool paused)
    {
        Action? remind = null;
        lock (_gate)
        {
            IsPaused = paused;
            if (paused && Value > Threshold)
            {
                Value = Threshold;
            }

            Persist();
            if (!paused && !IsResting && _settings.RemindAfterResume && ReachedThreshold() && !_thresholdLatched)
            {
                _thresholdLatched = true;
                remind = OnShouldRemind;
            }
        }

        remind?.Invoke();
        OnChanged?.Invoke();
    }

    internal static int ToPercentage(double value, double threshold)
    {
        var span = threshold <= 0 ? DefaultThreshold : threshold;
        return (int)Math.Round(Math.Clamp(value / span * 100.0, 0, 100));
    }

    private bool IsRecentlyActive(DateTimeOffset now) =>
        _lastActivity is { } last && (now - last).TotalSeconds <= IdleGapSeconds;

    private bool ReachedThreshold() => Value >= Threshold - 0.0000001;

    private double PointsPerSecond() => 100.0 / Math.Max(1, _settings.RemindIntervalMinutes * 60);

    private void Persist()
    {
        _settings.FatigueValue = Value;
        _settings.FatigueThreshold = Threshold;
        _settings.FatigueIsPaused = IsPaused;
    }
}
