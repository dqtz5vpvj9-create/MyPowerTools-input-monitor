using InputMonitor.Core;

namespace MyPowerTools.Tests;

public sealed class FatigueRegressionTests
{
    [Fact]
    public void App_heartbeats_and_idle_time_do_not_trigger_reminders()
    {
        var clock = new ManualClock();
        var engine = new FatigueEngine(new MonitorSettings
        {
            RemindIntervalMinutes = 1,
            FatigueFromApp = true
        }, clock);
        var reminders = 0;
        engine.OnShouldRemind = () => reminders++;

        for (var second = 0; second < 600; second++)
        {
            if (second % 30 == 0) engine.NotifyActivity(FatigueActivitySource.App);
            clock.Advance();
            engine.Tick(clock.GetUtcNow());
        }

        Assert.Equal(0, reminders);
        Assert.Equal(0, engine.Value);
    }

    [Fact]
    public void Skip_requires_another_full_interval_of_input()
    {
        var clock = new ManualClock();
        var engine = new FatigueEngine(new MonitorSettings { RemindIntervalMinutes = 1 }, clock);
        var reminders = 0;
        engine.OnShouldRemind = () => reminders++;

        Work(engine, clock, 60);
        Assert.Equal(1, reminders);
        Assert.True(engine.IsResting);
        engine.Skip();
        Assert.Equal(0, engine.Value);
        Assert.False(engine.IsResting);

        for (var second = 0; second < 120; second++)
        {
            clock.Advance();
            engine.Tick(clock.GetUtcNow());
        }
        Assert.Equal(1, reminders);
        Work(engine, clock, 59);
        Assert.Equal(1, reminders);
        Work(engine, clock, 1);
        Assert.Equal(2, reminders);
    }

    [Fact]
    public void Crossing_the_threshold_while_idle_does_not_show_the_overlay()
    {
        var clock = new ManualClock();
        var engine = new FatigueEngine(new MonitorSettings { RemindIntervalMinutes = 1 }, clock);
        var reminders = 0;
        engine.OnShouldRemind = () => reminders++;
        Work(engine, clock, 58);

        for (var second = 0; second < 10; second++)
        {
            clock.Advance();
            engine.Tick(clock.GetUtcNow());
        }

        Assert.Equal(0, reminders);
        Assert.False(engine.IsResting);
    }

    [Fact]
    public void Natural_break_and_lock_screen_clear_idle_credit()
    {
        var clock = new ManualClock();
        var engine = new FatigueEngine(new MonitorSettings { RemindIntervalMinutes = 1, RestDurationSeconds = 90 }, clock);
        Work(engine, clock, 20);
        var beforeIdle = engine.Value;
        for (var second = 0; second < 60; second++)
        {
            clock.Advance();
            engine.Tick(clock.GetUtcNow());
        }
        Assert.Equal(beforeIdle, engine.Value, 6);
        for (var second = 0; second < 30; second++)
        {
            clock.Advance();
            engine.Tick(clock.GetUtcNow(), screenLocked: true);
        }
        Assert.Equal(0, engine.Value);
    }

    [Fact]
    public void Legacy_and_stale_persisted_fatigue_cannot_cause_immediate_reminders()
    {
        var clock = new ManualClock();
        var legacy = new MonitorSettings { FatigueValue = 120, FatigueThreshold = 120 };
        var engine = new FatigueEngine(legacy, clock);
        Assert.True(engine.SanitizedPersistedValue);
        Assert.Equal(0, engine.Value);
        Assert.Equal(100, engine.Threshold);

        var stale = new MonitorSettings
        {
            FatigueValue = 95,
            FatigueLastActivity = clock.GetUtcNow().AddMinutes(-10)
        };
        Assert.Equal(0, new FatigueEngine(stale, clock).Value);
    }

    private static void Work(FatigueEngine engine, ManualClock clock, int seconds)
    {
        engine.NotifyActivity(FatigueActivitySource.Keyboard);
        for (var second = 0; second < seconds; second++)
        {
            clock.Advance();
            engine.Tick(clock.GetUtcNow());
            engine.NotifyActivity(FatigueActivitySource.Keyboard);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now = _now.AddSeconds(1);
    }
}
