using InputMonitor.Core;
using Microsoft.Data.Sqlite;

namespace InputMonitor.Core.Tests;

public sealed class EventRepositoryBoundaryTests : IDisposable
{
    private const string Day = "2026-08-18";
    private const string NextDay = "2026-08-19";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mpt-input-boundaries", Guid.NewGuid().ToString("N"));
    private readonly MonitorDatabase _database;
    private readonly EventRepository _repository;
    private readonly DateTimeOffset _midnight = DateTimeOffset.FromUnixTimeSeconds((long)EventRepository.DayRange(Day).End);

    public EventRepositoryBoundaryTests()
    {
        _database = new MonitorDatabase(Path.Combine(_directory, "test.db"));
        _repository = new EventRepository(_database, new AppCategoryMap());
    }

    [Fact]
    public void Midnight_event_belongs_only_to_the_new_day()
    {
        _repository.InsertEvents([Event(InputEventKind.KeyDown, _midnight)]);

        Assert.Empty(_repository.HourlyCounts(Day, [InputEventKinds.KeyDown]));
        Assert.Empty(_repository.PerMinuteCounts(Day, [InputEventKinds.KeyDown]));
        Assert.Empty(_repository.PerDayOperationCounts(Day, Day, [InputEventKinds.KeyDown]));
        Assert.Empty(_repository.KeyHeat(Day));
        Assert.Empty(_repository.InteractionMinutes(Day));
        Assert.Empty(_repository.PerDayEventActiveSeconds(Day, Day, [InputEventKinds.KeyDown], false));
        Assert.Empty(_repository.PerDayInteractionSeconds(Day, Day));
        Assert.Equal(0, _repository.InteractionSeconds(Day));

        Assert.Equal(1, _repository.HourlyCounts(NextDay, [InputEventKinds.KeyDown])[0]);
        Assert.Equal(1, _repository.PerDayOperationCounts(NextDay, NextDay, [InputEventKinds.KeyDown])[NextDay]);
        Assert.Single(_repository.KeyHeat(NextDay));
        Assert.Equal(60, _repository.InteractionSeconds(NextDay));
    }

    [Fact]
    public void Midnight_track_point_belongs_only_to_the_new_day()
    {
        _repository.InsertTrackPoints([Event(InputEventKind.MouseMoveSample, _midnight)]);

        Assert.Empty(_repository.TrackPoints(Day));
        Assert.Empty(_repository.InteractionMinutes(Day));
        Assert.Empty(_repository.PerDayEventActiveSeconds(Day, Day, [], true));
        Assert.Empty(_repository.PerDayInteractionSeconds(Day, Day));
        Assert.Single(_repository.TrackPoints(NextDay));
        Assert.Equal(60, _repository.InteractionSeconds(NextDay));
    }

    [Fact]
    public void Session_starting_at_midnight_is_not_in_previous_days_queries()
    {
        InsertSession(_midnight, _midnight.AddSeconds(30));
        var (start, end) = EventRepository.DayRange(Day);

        Assert.Empty(_repository.HourlyAppSeconds(Day));
        Assert.Empty(_repository.PerDayAppSeconds(Day, Day));
        Assert.Empty(_repository.AppSecondsByDay(Day, Day));
        Assert.Empty(_repository.PerMinuteSessionStarts(Day));
        Assert.Empty(_repository.PerDaySessionStarts(Day, Day));
        Assert.Empty(_repository.AppUsage(start, end));
        Assert.Equal(30, _repository.HourlyAppSeconds(NextDay)[0]);
        Assert.Equal(1, _repository.PerDaySessionStarts(NextDay, NextDay)[NextDay]);
    }

    [Fact]
    public void Session_ending_at_midnight_does_not_add_a_phantom_next_day_minute()
    {
        InsertSession(_midnight.AddSeconds(-30), _midnight);

        Assert.Equal(60, _repository.InteractionSeconds(Day));
        Assert.Equal(60, Assert.Single(_repository.PerDayInteractionSeconds(Day, Day)).Value);
        Assert.Equal(Day, Assert.Single(_repository.PerDayInteractionSeconds(Day, Day)).Key);
        Assert.Empty(_repository.InteractionMinutes(NextDay));
        Assert.Empty(_repository.HourlyInteractionSeconds(NextDay));
        Assert.Empty(_repository.PerDayInteractionSeconds(NextDay, NextDay));
    }

    [Fact]
    public void Session_ending_exactly_on_a_minute_boundary_does_not_count_the_following_minute()
    {
        InsertSession(_midnight.AddHours(-1), _midnight.AddHours(-1).AddMinutes(1));

        Assert.Equal(60, _repository.InteractionSeconds(Day));
        Assert.Equal(60, _repository.HourlyInteractionSeconds(Day)[23]);
        Assert.Equal(60, _repository.PerDayInteractionSeconds(Day, Day)[Day]);
    }

    [Fact]
    public void Date_string_ranges_still_include_both_requested_days()
    {
        _repository.MergeDailyStats(Day, new DaySummary { KeyCount = 1 });
        _repository.MergeDailyStats(NextDay, new DaySummary { KeyCount = 2 });

        Assert.Equal(2, _repository.DaySummaries(Day, NextDay).Count);
        Assert.Single(_repository.DaySummaries(Day, Day));
    }

    private void InsertSession(DateTimeOffset start, DateTimeOffset end) => _repository.InsertAppSession(new FrontAppSession
    {
        BundleId = "test.app",
        AppName = "Test",
        Category = AppCategory.Other,
        Start = start,
        End = end
    });

    private static InputEventRecord Event(InputEventKind kind, DateTimeOffset stamp) =>
        new(kind, 0, stamp, 10, 20, 65, "a", 0, 0, false, 1);

    public void Dispose()
    {
        _database.Dispose();
        // Pooled native connections otherwise keep the temporary file open on Windows.
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
