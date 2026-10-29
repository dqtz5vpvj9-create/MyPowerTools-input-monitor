using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace InputMonitor.Core;

public sealed class InputMonitorHost : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _settingsPath;
    private readonly MonitorDatabase _database;
    private readonly EventRepository _repository;
    private readonly MetricsAggregator _aggregator;
    private readonly EventBuffer _buffer;
    private readonly FatigueEngine _fatigue;
    private readonly Channel<InputEventRecord> _events = Channel.CreateUnbounded<InputEventRecord>();
    private readonly IInputCapture? _capture;
    private readonly IFrontAppTracker? _frontApp;
    private readonly IRestOverlay? _overlay;
    private readonly Timer _drainTimer;
    private readonly Timer _fatigueTimer;
    private readonly object _settingsGate = new();
    private readonly object _lifecycleGate = new();
    // Admission never takes the collection/database gate on native hook threads.
    private readonly object _admissionGate = new();
    private bool _acceptingEvents;
    // This gate covers collection, pending batch delivery, aggregate persistence,
    // and clearing. Never call capture/tracker lifecycle methods or getters under it.
    private readonly object _collectionGate = new();
    private CancellationTokenSource _lifetime = new();
    private int _fatigueStateDirty;
    private Thread? _consumer;
    private volatile bool _running;
    private bool _disposed;
    private bool _collectionClosed;

    public MonitorSettings Settings { get; }
    public AppCategoryMap Categories { get; }

    public InputMonitorHost(
        string dataDirectory,
        IInputCapture? capture = null,
        IFrontAppTracker? frontApp = null,
        IRestOverlay? overlay = null,
        AppCategoryMap? categories = null)
    {
        Directory.CreateDirectory(dataDirectory);
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
        Settings = LoadSettings(_settingsPath);
        Categories = categories ?? new AppCategoryMap();
        Categories.ReplaceOverrides(Settings.CategoryOverrides);
        Categories.Changed += PersistSettings;
        _database = new MonitorDatabase(Path.Combine(dataDirectory, "input-monitor.db"));
        _repository = new EventRepository(_database, Categories);
        _aggregator = new MetricsAggregator();
        _aggregator.RestoreInteractionBaseline(_repository.InteractionMinutes(EventRepository.DayString(DateTimeOffset.Now)));
        _buffer = new EventBuffer(synchronizationGate: _collectionGate);
        _buffer.OnFlush = FlushBuffer;
        _fatigue = new FatigueEngine(Settings)
        {
            OnShouldRemind = ShowRest,
            OnChanged = MarkFatigueStateDirty
        };
        if (_fatigue.SanitizedPersistedValue)
        {
            PersistSettings();
        }
        _capture = capture;
        _frontApp = frontApp;
        _overlay = overlay;
        if (_capture is not null)
        {
            _capture.EventReceived += Enqueue;
        }

        if (_frontApp is not null)
        {
            _frontApp.SessionCompleted += session =>
            {
                lock (_collectionGate)
                {
                    if (_collectionClosed || !Volatile.Read(ref _acceptingEvents))
                    {
                        return;
                    }

                    if (Settings.PrivacyMode)
                    {
                        session.WindowTitle = null;
                    }

                    _repository.InsertAppSession(session);
                }
            };
            _frontApp.Heartbeat += (_, now) =>
            {
                var screenLocked = _frontApp.ScreenLocked;
                lock (_collectionGate)
                {
                    if (_collectionClosed || !Volatile.Read(ref _acceptingEvents))
                    {
                        return;
                    }

                    _aggregator.ProcessAppHeartbeat(
                        TimeSpan.FromSeconds(Settings.AppHeartbeatSeconds),
                        now,
                        screenLocked);
                }
            };
        }

        _drainTimer = new Timer(_ => Drain(), null, Timeout.Infinite, Timeout.Infinite);
        _fatigueTimer = new Timer(_ => _fatigue.Tick(DateTimeOffset.Now, _frontApp?.ScreenLocked ?? false), null, Timeout.Infinite, Timeout.Infinite);
        _repository.PurgeExpiredData(Settings.DataRetentionDays);
    }

    public bool CaptureRunning => _running && (_capture?.IsRunning ?? false);

    public void Start()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return;
            }

            if (_lifetime.IsCancellationRequested)
            {
                _lifetime.Dispose();
                _lifetime = new CancellationTokenSource();
            }

            _running = true;
            lock (_admissionGate)
            {
                _acceptingEvents = true;
            }
            _consumer = new Thread(Consume)
            {
                IsBackground = true,
                Name = "InputMonitor.Consumer"
            };
            _consumer.Start();
            _capture?.UpdateTrackSampleDistance(Settings.TrackSampleDistance);
            _capture?.Start();
            _frontApp?.UpdateHeartbeatInterval(TimeSpan.FromSeconds(Settings.AppHeartbeatSeconds));
            _frontApp?.Start();
            _drainTimer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _fatigueTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    public void Stop()
    {
        lock (_lifecycleGate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            _capture?.Stop();
            _frontApp?.Stop();
            lock (_admissionGate)
            {
                _acceptingEvents = false;
            }
            _drainTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _fatigueTimer.Change(Timeout.Infinite, Timeout.Infinite);
            if (!_lifetime.IsCancellationRequested)
            {
                _lifetime.Cancel();
            }

            // Cancellation wakes an idle reader; it still consumes accepted records
            // before exiting. Join before the final flush, including an in-flight append.
            _consumer?.Join();
            _consumer = null;
            _buffer.Flush();
            Drain();
        }
    }

    public void SkipRest()
    {
        _fatigue.Skip();
        _overlay?.Dismiss();
    }

    /// <summary>
    /// Stops or resumes raw input and foreground-app collection while keeping the host
    /// (reminders, drain timer, aggregates) alive. Used by the surface's "停止采集" toggle.
    /// </summary>
    public void SetCaptureRunning(bool running)
    {
        lock (_lifecycleGate)
        {
            if (!_running)
            {
                if (running)
                {
                    Start();
                }
                return;
            }

            if (running)
            {
                lock (_admissionGate)
                {
                    _acceptingEvents = true;
                }
                _capture?.UpdateTrackSampleDistance(Settings.TrackSampleDistance);
                _capture?.Start();
                _frontApp?.Start();
            }
            else
            {
                _capture?.Stop();
                _frontApp?.Stop();
                lock (_admissionGate)
                {
                    _acceptingEvents = false;
                }
                _buffer.Flush();
                Drain();
            }
        }
    }

    public LiveSnapshot Snapshot()
    {
        MetricsSnapshot metrics;
        lock (_collectionGate)
        {
            metrics = _aggregator.Snapshot();
        }

        return new LiveSnapshot(
            metrics,
            _fatigue.Snapshot(),
            _frontApp?.CurrentSession?.AppName,
            CaptureRunning,
            _frontApp?.ScreenLocked ?? false);
    }

    public EventRepository Repository => _repository;
    public FatigueEngine Fatigue => _fatigue;

    public object BuildStatsPayload(string? day, string grain) =>
        BuildStatsPayload(new StatsQuery { Day = day, Grain = grain });

    public object BuildStatsPayload(StatsQuery query) =>
        StatsPayloadBuilder.Build(_repository, Snapshot(), Settings.Clone(), ToMetrics, query);

    public void ApplySettings(MonitorSettings next)
    {
        next.Clamp();
        lock (_settingsGate)
        {
            Settings.RemindIntervalMinutes = next.RemindIntervalMinutes;
            Settings.RestDurationSeconds = next.RestDurationSeconds;
            Settings.FatigueFromKeyboard = next.FatigueFromKeyboard;
            Settings.FatigueFromMouse = next.FatigueFromMouse;
            Settings.FatigueFromApp = next.FatigueFromApp;
            Settings.SoundEnabled = next.SoundEnabled;
            Settings.SoundVolume = next.SoundVolume;
            Settings.SoundName = next.SoundName;
            Settings.PrivacyMode = next.PrivacyMode;
            Settings.TrackSampleDistance = next.TrackSampleDistance;
            Settings.AppHeartbeatSeconds = next.AppHeartbeatSeconds;
            Settings.RemindAfterResume = next.RemindAfterResume;
            Settings.LaunchAtLogin = next.LaunchAtLogin;
            Settings.DataRetentionDays = next.DataRetentionDays;
        }

        _capture?.UpdateTrackSampleDistance(Settings.TrackSampleDistance);
        _frontApp?.UpdateHeartbeatInterval(TimeSpan.FromSeconds(Settings.AppHeartbeatSeconds));
        _repository.PurgeExpiredData(Settings.DataRetentionDays);
        PersistSettings();
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                Stop();
            }
            catch
            {
                // Host shutdown must not throw from Dispose.
            }

            _overlay?.Dismiss();
            _capture?.Dispose();
            _frontApp?.Dispose();
            Categories.Changed -= PersistSettings;
            lock (_collectionGate)
            {
                _events.Writer.TryComplete();
                _buffer.Dispose();
                _drainTimer.Dispose();
                _fatigueTimer.Dispose();
                _collectionClosed = true;
                _database.Dispose();
            }
            _lifetime.Dispose();
        }
    }

    private void Enqueue(InputEventRecord record)
    {
        if (Settings.PrivacyMode && record.Kind is InputEventKind.KeyDown or InputEventKind.KeyUp)
        {
            // The raw code stays in memory so key hold durations still pair up, but the
            // repository persists the coarse bucket instead of the code itself.
            record = record with { Characters = null, KeyBucket = KeyBuckets.For(record.KeyCode) };
        }

        lock (_admissionGate)
        {
            if (_acceptingEvents)
            {
                _events.Writer.TryWrite(record);
            }
        }
    }

    private void Consume()
    {
        while (true)
        {
            InputEventRecord? record;
            lock (_collectionGate)
            {
                if (_events.Reader.TryRead(out record))
                {
                    _aggregator.Process(record);
                    _buffer.Append(record);
                }
                else if (_lifetime.IsCancellationRequested)
                {
                    return;
                }
            }

            if (record is null)
            {
                try
                {
                    if (!_events.Reader.WaitToReadAsync(_lifetime.Token).AsTask().GetAwaiter().GetResult())
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Check the queue once more so Stop does not drop accepted input.
                }
                continue;
            }

            var source = record.Kind switch
            {
                InputEventKind.KeyDown or InputEventKind.KeyUp or InputEventKind.FlagsChanged => FatigueActivitySource.Keyboard,
                _ => FatigueActivitySource.Mouse
            };
            _fatigue.NotifyActivity(source);
        }
    }

    private void FlushBuffer(IReadOnlyList<InputEventRecord> batch)
    {
        lock (_collectionGate)
        {
            if (!_collectionClosed)
            {
                _repository.InsertEvents(batch);
                _repository.InsertTrackPoints(batch);
            }
        }
    }

    private void Drain()
    {
        FlushFatigueState();
        lock (_collectionGate)
        {
            if (_collectionClosed)
            {
                return;
            }

            foreach (var delta in _aggregator.DrainDeltas())
            {
                if (delta.KeyCount == 0 &&
                    delta.ClickCount == 0 &&
                    delta.ScrollCount == 0 &&
                    delta.KeyDurationMs == 0 &&
                    delta.MoveDistance == 0 &&
                    delta.ActiveInputSeconds == 0 &&
                    delta.ActiveAppSeconds == 0)
                {
                    continue;
                }

                _repository.MergeDailyStats(delta.Day, delta);
            }
        }
    }

    private void ShowRest()
    {
        if (_overlay is { IsShowing: true })
        {
            return;
        }

        _fatigue.BeginResting();
        _overlay?.Show(Settings.RestDurationSeconds, _fatigue.Skip, _fatigue.RestDone);
        if (_overlay is null)
        {
            _fatigue.RestDone();
        }
    }

    /// <summary>Drops every collected row and resets the in-memory counters.</summary>
    public void ClearCollectedData()
    {
        lock (_collectionGate)
        {
            _buffer.Flush();
            // Stop admission only while discarding the queue, never during SQLite
            // work. This also bounds clear if producers are continuously active.
            lock (_admissionGate)
            {
                while (_events.Reader.TryRead(out _))
                {
                }
            }
            _repository.ClearAllData();
            _aggregator.Reset(EventRepository.DayString(DateTimeOffset.Now));
        }
    }

    private void PersistSettings()
    {
        Volatile.Write(ref _fatigueStateDirty, 0);
        WriteSettings();
    }

    private void MarkFatigueStateDirty() => Volatile.Write(ref _fatigueStateDirty, 1);

    private void FlushFatigueState()
    {
        if (Interlocked.Exchange(ref _fatigueStateDirty, 0) == 1)
        {
            WriteSettings();
        }
    }

    private void WriteSettings()
    {
        lock (_settingsGate)
        {
            Settings.CategoryOverrides = new Dictionary<string, AppCategory>(Categories.Overrides, StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            var temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
    }

    private static MonitorSettings LoadSettings(string path)
    {
        if (!File.Exists(path))
        {
            return new MonitorSettings();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(path), JsonOptions) ?? new MonitorSettings();
            loaded.Clamp();
            return loaded;
        }
        catch (JsonException exception)
        {
            var corruptPath = path + ".corrupt";
            File.Move(path, corruptPath, overwrite: true);
            Trace.TraceError(
                $"InputMonitor: settings file '{path}' could not be parsed ({exception.Message}). " +
                $"It was kept as '{corruptPath}'; privacy, retention and category overrides fall back to defaults until it is restored.");
            return new MonitorSettings();
        }
    }

    private static MetricsSnapshot ToMetrics(DaySummary summary, double interactionSeconds) =>
        new(
            summary.Day,
            summary.KeyCount,
            summary.ClickCount,
            summary.ScrollCount,
            summary.MoveDistance,
            summary.KeyDurationMs,
            summary.ActiveInputSeconds,
            summary.ActiveAppSeconds,
            interactionSeconds);
}
