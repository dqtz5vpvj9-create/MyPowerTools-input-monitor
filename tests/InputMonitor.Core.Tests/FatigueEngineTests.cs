using InputMonitor.Core;

namespace InputMonitor.Core.Tests;

/// <summary>
/// Covers active-time accrual and starting a full reminder interval after skipping or resting.
/// </summary>
public sealed class FatigueEngineTests
{
    private static FatigueEngine Started(double intervalMinutes = 1)
    {
        var engine = new FatigueEngine(new MonitorSettings { RemindIntervalMinutes = intervalMinutes });
        engine.NotifyActivity(FatigueActivitySource.Keyboard);
        return engine;
    }

    private static int ReachThreshold(FatigueEngine engine, int tickCount = 60)
    {
        var reminded = 0;
        engine.OnShouldRemind = () => reminded++;
        var now = DateTimeOffset.Now;
        for (var tick = 0; tick < tickCount; tick++)
        {
            engine.Tick(now);
        }

        return reminded;
    }

    [Fact]
    public void Activity_accrues_a_full_interval_and_reminds_once()
    {
        var engine = Started();

        var reminded = ReachThreshold(engine);

        Assert.True(engine.Value >= FatigueEngine.DefaultThreshold);
        Assert.Equal(1, reminded);

        // The threshold is latched: more ticks in the same interval must not remind again.
        var now = DateTimeOffset.Now;
        for (var tick = 0; tick < 5; tick++)
        {
            engine.Tick(now);
        }

        Assert.Equal(1, reminded);
    }

    [Fact]
    public void Tick_without_recent_activity_does_not_accrue()
    {
        var engine = new FatigueEngine(new MonitorSettings { RemindIntervalMinutes = 1 });

        engine.Tick(DateTimeOffset.Now);
        Assert.Equal(0, engine.Value);

        engine.NotifyActivity(FatigueActivitySource.Keyboard);
        engine.Tick(DateTimeOffset.Now.AddSeconds(FatigueEngine.IdleGapSeconds + 1));
        Assert.Equal(0, engine.Value);
    }

    [Fact]
    public void Skip_starts_a_new_full_interval()
    {
        var engine = Started();
        ReachThreshold(engine);

        engine.Skip();

        Assert.Equal(0, engine.Value);
        Assert.Equal(FatigueEngine.DefaultThreshold, engine.Threshold);
        Assert.False(engine.IsResting);
    }

    [Fact]
    public void Rest_done_resets_the_interval()
    {
        var engine = Started();
        ReachThreshold(engine);

        engine.BeginResting();
        Assert.True(engine.IsResting);

        engine.RestDone();

        Assert.Equal(0, engine.Value);
        Assert.Equal(FatigueEngine.DefaultThreshold, engine.Threshold);
        Assert.False(engine.IsResting);
    }

    [Fact]
    public void Manual_rest_skip_restores_the_backed_up_progress()
    {
        var engine = new FatigueEngine(new MonitorSettings { FatigueValue = 30, FatigueThreshold = 100 });
        var reminded = 0;
        engine.OnShouldRemind = () => reminded++;

        engine.ManualRest();
        Assert.Equal(1, reminded);

        engine.Skip();

        Assert.Equal(30, engine.Value);
        Assert.Equal(100, engine.Threshold);
    }

    [Fact]
    public void Pausing_caps_the_score_at_the_threshold()
    {
        var engine = Started();
        ReachThreshold(engine);

        engine.SetPaused(true);

        Assert.True(engine.IsPaused);
        Assert.Equal(engine.Threshold, engine.Value);

        var before = engine.Value;
        engine.Tick(DateTimeOffset.Now);
        Assert.Equal(before, engine.Value);
    }

    [Fact]
    public void Persisted_value_above_the_threshold_is_sanitized()
    {
        var sanitized = new FatigueEngine(new MonitorSettings { FatigueValue = 500, FatigueThreshold = 100 });

        Assert.True(sanitized.SanitizedPersistedValue);
        Assert.Equal(0, sanitized.Value);
        Assert.Equal(FatigueEngine.DefaultThreshold, sanitized.Threshold);

        var kept = new FatigueEngine(new MonitorSettings { FatigueValue = 40, FatigueThreshold = 100 });

        Assert.False(kept.SanitizedPersistedValue);
        Assert.Equal(40, kept.Value);
        Assert.Equal(100, kept.Threshold);
    }

    [Fact]
    public void Percentage_is_relative_to_the_current_threshold()
    {
        var engine = new FatigueEngine(new MonitorSettings { FatigueValue = 100, FatigueThreshold = 200 });

        Assert.Equal(50, engine.Percentage);
    }
}
