using System.Reflection;
using InputMonitor.Core;
using Microsoft.Data.Sqlite;

namespace InputMonitor.Core.Tests;

public sealed class AggregationConcurrencyTests
{
    [Fact]
    public void Rotation_preserves_every_undrained_day()
    {
        var first = LocalTime(2026, 10, 16, 23, 59, 59);
        var aggregator = new MetricsAggregator(EventRepository.DayString(first));
        aggregator.Process(Event(InputEventKind.KeyDown, first));
        aggregator.ProcessAppHeartbeat(TimeSpan.FromSeconds(30), first, false);
        aggregator.Process(Event(InputEventKind.LeftClick, first.AddSeconds(1)));
        aggregator.Process(Event(InputEventKind.Scroll, first.AddDays(2)));

        var deltas = Enumerable.Range(0, 3).Select(_ => aggregator.DrainDelta()).ToArray();

        Assert.Equal(new[] { "2026-10-16", "2026-10-17", "2026-10-18" }, deltas.Select(item => item.Day));
        Assert.Equal(1, deltas[0].Delta.KeyCount);
        Assert.Equal(30, deltas[0].Delta.ActiveAppSeconds);
        Assert.Equal(1, deltas[1].Delta.ClickCount);
        Assert.Equal(1, deltas[2].Delta.ScrollCount);
        Assert.Equal(0, aggregator.DrainDelta().Delta.ScrollCount);
    }

    [Fact]
    public void Reset_discards_previous_days_as_well_as_current_pending_data()
    {
        var first = LocalTime(2026, 10, 16, 23, 59, 59);
        var aggregator = new MetricsAggregator(EventRepository.DayString(first));
        aggregator.Process(Event(InputEventKind.KeyDown, first));
        aggregator.Process(Event(InputEventKind.LeftClick, first.AddSeconds(1)));

        aggregator.Reset("2026-10-18");

        var (day, delta) = aggregator.DrainDelta();
        Assert.Equal("2026-10-18", day);
        Assert.Equal(0, delta.KeyCount);
        Assert.Equal(0, delta.ClickCount);
        Assert.Equal(0, aggregator.Snapshot().InteractionSeconds);
    }

    [Fact]
    public void Host_drain_persists_both_sides_of_midnight()
    {
        using var fixture = new HostFixture();
        var first = LocalTime(2026, 10, 16, 23, 59, 59);
        fixture.Host.Start();
        fixture.Capture.Push(Event(InputEventKind.KeyDown, first));
        fixture.Capture.Push(Event(InputEventKind.LeftClick, first.AddSeconds(1)));
        Assert.True(SpinWait.SpinUntil(() => fixture.Host.Snapshot().Metrics.ClickCount == 1, TimeSpan.FromSeconds(5)));

        fixture.Host.Stop();

        Assert.Equal(1, fixture.Host.Repository.DaySummaryFor("2026-10-16").KeyCount);
        Assert.Equal(1, fixture.Host.Repository.DaySummaryFor("2026-10-17").ClickCount);
    }

    [Fact]
    public async Task Clear_does_not_allow_an_in_flight_buffer_flush_to_restore_deleted_events()
    {
        using var fixture = new HostFixture();
        var host = fixture.Host;
        var now = DateTimeOffset.Now;
        host.Start();
        fixture.Capture.Push(Event(InputEventKind.KeyDown, now));
        Assert.True(SpinWait.SpinUntil(() => host.Snapshot().Metrics.KeyCount == 1, TimeSpan.FromSeconds(5)));

        var buffer = Field<EventBuffer>(host, "_buffer");
        Assert.True(SpinWait.SpinUntil(() =>
        {
            lock (Field<object>(buffer, "_gate"))
            {
                return Field<List<InputEventRecord>>(buffer, "_buffer").Count == 1;
            }
        }, TimeSpan.FromSeconds(5)));
        var persist = buffer.OnFlush!;
        using var flushEntered = new ManualResetEventSlim();
        using var allowFlush = new ManualResetEventSlim();
        using var clearStarted = new ManualResetEventSlim();
        buffer.OnFlush = batch =>
        {
            flushEntered.Set();
            Assert.True(allowFlush.Wait(TimeSpan.FromSeconds(10)));
            persist(batch);
        };
        var flush = Task.Run(buffer.Flush);
        Assert.True(flushEntered.Wait(TimeSpan.FromSeconds(5)));
        var clear = Task.Run(() =>
        {
            clearStarted.Set();
            host.ClearCollectedData();
        });
        try
        {
            Assert.True(clearStarted.Wait(TimeSpan.FromSeconds(5)));
            // The old implementation completes clear while an already-drained batch is
            // suspended above. With serialization, clear must wait for that batch.
            await Task.WhenAny(clear, Task.Delay(150));
        }
        finally
        {
            allowFlush.Set();
        }
        await Task.WhenAll(flush, clear).WaitAsync(TimeSpan.FromSeconds(10));
        host.Stop();

        Assert.Empty(host.Repository.KeyHeat(EventRepository.DayString(now)));
        Assert.Equal(0, host.Repository.DaySummaryFor(EventRepository.DayString(now)).KeyCount);
        Assert.Equal(0, host.Snapshot().Metrics.KeyCount);
    }

    [Fact]
    public async Task Stop_persists_all_events_already_accepted_by_the_capture_queue()
    {
        using var fixture = new HostFixture();
        var now = DateTimeOffset.Now;
        var buffer = Field<EventBuffer>(fixture.Host, "_buffer");
        var persist = buffer.OnFlush!;
        using var flushEntered = new ManualResetEventSlim();
        using var allowFlush = new ManualResetEventSlim();
        var firstFlush = 0;
        buffer.OnFlush = batch =>
        {
            if (Interlocked.Exchange(ref firstFlush, 1) == 0)
            {
                flushEntered.Set();
                Assert.True(allowFlush.Wait(TimeSpan.FromSeconds(10)));
            }
            persist(batch);
        };
        fixture.Host.Start();
        for (var i = 0; i < 2000; i++)
        {
            fixture.Capture.Push(Event(InputEventKind.KeyDown, now));
        }
        Assert.True(flushEntered.Wait(TimeSpan.FromSeconds(5)));
        var lifetime = Field<CancellationTokenSource>(fixture.Host, "_lifetime");
        var stop = Task.Run(fixture.Host.Stop);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => lifetime.IsCancellationRequested, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            allowFlush.Set();
        }
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2000, fixture.Host.Repository.DaySummaryFor(EventRepository.DayString(now)).KeyCount);
        Assert.Equal(2000, fixture.Host.Repository.KeyHeat(EventRepository.DayString(now)).Sum(item => item.Count));
    }

    [Fact]
    public void Stopped_and_paused_capture_reject_late_callbacks_but_resume_accepts_new_input()
    {
        using var fixture = new HostFixture();
        var now = DateTimeOffset.Now;
        fixture.Host.Start();
        fixture.Capture.Push(Event(InputEventKind.KeyDown, now));
        fixture.Host.Stop();
        fixture.Capture.Push(Event(InputEventKind.KeyDown, now));
        fixture.Tracker.Pulse(now);

        fixture.Host.Start();
        fixture.Host.SetCaptureRunning(false);
        fixture.Capture.Push(Event(InputEventKind.KeyDown, now));
        fixture.Tracker.Pulse(now);
        fixture.Host.SetCaptureRunning(true);
        fixture.Capture.Push(Event(InputEventKind.LeftClick, now));
        fixture.Tracker.Pulse(now);
        fixture.Host.Stop();

        var summary = fixture.Host.Repository.DaySummaryFor(EventRepository.DayString(now));
        Assert.Equal(1, summary.KeyCount);
        Assert.Equal(1, summary.ClickCount);
        Assert.Equal(30, summary.ActiveAppSeconds);
        Assert.Equal(1, fixture.Host.Repository.KeyHeat(EventRepository.DayString(now)).Sum(item => item.Count));
    }

    [Fact]
    public async Task Restart_waits_until_the_previous_stop_has_finished()
    {
        using var fixture = new HostFixture();
        using var stopEntered = new ManualResetEventSlim();
        using var allowStop = new ManualResetEventSlim();
        using var startAttempted = new ManualResetEventSlim();
        fixture.Capture.OnStop = () =>
        {
            stopEntered.Set();
            Assert.True(allowStop.Wait(TimeSpan.FromSeconds(10)));
        };
        fixture.Host.Start();
        var stop = Task.Run(fixture.Host.Stop);
        Assert.True(stopEntered.Wait(TimeSpan.FromSeconds(5)));
        var start = Task.Run(() =>
        {
            startAttempted.Set();
            fixture.Host.Start();
        });
        try
        {
            Assert.True(startAttempted.Wait(TimeSpan.FromSeconds(5)));
            await Task.WhenAny(start, Task.Delay(150));
            Assert.False(start.IsCompleted);
        }
        finally
        {
            allowStop.Set();
            fixture.Capture.OnStop = null;
        }
        await Task.WhenAll(stop, start).WaitAsync(TimeSpan.FromSeconds(10));

        var now = DateTimeOffset.Now;
        fixture.Capture.Push(Event(InputEventKind.KeyDown, now));
        fixture.Host.Stop();
        Assert.Equal(1, fixture.Host.Repository.DaySummaryFor(EventRepository.DayString(now)).KeyCount);
    }

    [Fact]
    public async Task Concurrent_capture_heartbeat_drain_and_snapshot_conserve_totals()
    {
        using var fixture = new HostFixture();
        var now = DateTimeOffset.Now;
        var drain = typeof(InputMonitorHost).GetMethod("Drain", BindingFlags.Instance | BindingFlags.NonPublic)!;
        fixture.Host.Start();
        await Task.WhenAll(
            Task.Run(() =>
            {
                for (var i = 0; i < 4000; i++) fixture.Capture.Push(Event(InputEventKind.KeyDown, now));
            }),
            Task.Run(() =>
            {
                for (var i = 0; i < 500; i++) fixture.Tracker.Pulse(now);
            }),
            Task.Run(() =>
            {
                for (var i = 0; i < 1000; i++) drain.Invoke(fixture.Host, null);
            }),
            Task.Run(() =>
            {
                for (var i = 0; i < 1000; i++) fixture.Host.Snapshot();
            })).WaitAsync(TimeSpan.FromSeconds(20));
        fixture.Host.Stop();

        var summary = fixture.Host.Repository.DaySummaryFor(EventRepository.DayString(now));
        Assert.Equal(4000, summary.KeyCount);
        Assert.Equal(15000, summary.ActiveAppSeconds);
        Assert.Equal(4000, fixture.Host.Repository.KeyHeat(EventRepository.DayString(now)).Sum(item => item.Count));
    }

    [Fact]
    public async Task Empty_backend_can_idle_stop_and_restart_without_a_polling_timeout()
    {
        using var fixture = new HostFixture(withBackends: false);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            fixture.Host.Start();
            var consumer = Field<Thread>(fixture.Host, "_consumer");
            Assert.True(SpinWait.SpinUntil(
                () => (consumer.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)));
            Assert.False(fixture.Host.CaptureRunning);
            Assert.Equal(0, fixture.Host.Snapshot().Metrics.KeyCount);
            await Task.Run(fixture.Host.Stop).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(consumer.IsAlive);
        }
    }

    [Fact]
    public async Task Failed_persistence_callback_releases_the_buffer_gate()
    {
        using var buffer = new EventBuffer(capacity: 1, flushInterval: TimeSpan.FromHours(1));
        buffer.OnFlush = _ => throw new IOException("Synthetic storage failure");
        Assert.Throws<IOException>(() => buffer.Append(Event(InputEventKind.KeyDown, DateTimeOffset.Now)));

        var persisted = 0;
        buffer.OnFlush = batch => persisted += batch.Count;
        await Task.Run(() => buffer.Append(Event(InputEventKind.KeyDown, DateTimeOffset.Now)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, persisted);
        // The failed first batch is still lost, as before this fix. This test proves
        // lock release after failure, not durable delivery or retry semantics.
    }

    internal static InputEventRecord Event(InputEventKind kind, DateTimeOffset stamp) =>
        new(kind, 0, stamp, null, null, 65, null, 0, 1, false, 0);

    internal static DateTimeOffset LocalTime(int year, int month, int day, int hour, int minute, int second)
    {
        var date = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
    }

    internal static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    internal sealed class HostFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "input-monitor-aggregation", Guid.NewGuid().ToString("N"));
        public FakeCapture Capture { get; } = new();
        public FakeTracker Tracker { get; } = new();
        public InputMonitorHost Host { get; }

        public HostFixture(bool withBackends = true) =>
            Host = new InputMonitorHost(_directory, withBackends ? Capture : null, withBackends ? Tracker : null);

        public void Dispose()
        {
            Host.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }

    internal sealed class FakeCapture : IInputCapture
    {
        public event Action<InputEventRecord>? EventReceived;
        public bool IsRunning { get; private set; }
        public Action? OnStop { get; set; }
        public void Start() => IsRunning = true;
        public void Stop()
        {
            IsRunning = false;
            OnStop?.Invoke();
        }
        public void UpdateTrackSampleDistance(double pixels) { }
        public void Dispose() => Stop();
        public void Push(InputEventRecord record) => EventReceived?.Invoke(record);
    }

    internal sealed class FakeTracker : IFrontAppTracker
    {
        public event Action<FrontAppSession>? SessionCompleted { add { } remove { } }
        public event Action<FrontAppSession, DateTimeOffset>? Heartbeat;
        public bool ScreenLocked => false;
        public FrontAppSession? CurrentSession => null;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void UpdateHeartbeatInterval(TimeSpan interval) { }
        public void Pulse(DateTimeOffset now) => Heartbeat?.Invoke(new FrontAppSession
        {
            BundleId = "synthetic",
            AppName = "Synthetic test application",
            Category = AppCategory.Other,
            Start = now
        }, now);
    }
}
