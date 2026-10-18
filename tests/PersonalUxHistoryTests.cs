using Avalonia.Headless.XUnit;
using InputMonitor.Surface.ViewModels;
using MyPowerTools.AvaloniaSdk;
using Xunit;

namespace PersonalUx.Tests;

public sealed class PersonalUxHistoryTests
{
    [Theory]
    [InlineData("day", "2024-03-01", -1, "2024-02-29")]
    [InlineData("month", "2026-03-31", -1, "2026-02-01")]
    [InlineData("quarter", "2026-01-31", -1, "2025-10-01")]
    [InlineData("year", "2024-02-29", 1, "2025-01-01")]
    public void History_moves_by_calendar_period_including_leap_days(string grain, string date, int delta, string expected)
    {
        Assert.Equal(DateTime.Parse(expected), HistoryPeriod.Shift(DateTime.Parse(date), grain, delta));
    }

    [AvaloniaFact]
    public void Month_navigation_keeps_the_correct_anchor_and_cannot_advance_into_a_future_period()
    {
        var context = new MptAvaloniaSurfaceContext("input-monitor", "dashboard", Path.GetTempPath(), "light",
            (_, _, _) => throw new OperationCanceledException(), (_, _, _) => Task.CompletedTask, null!, _ => { });
        using var vm = new InputMonitorViewModel(context, () => new DateTime(2026, 9, 5));
        vm.SelectGrainCommand.Execute("month");
        Assert.False(vm.CanGoForward);
        vm.PreviousDayCommand.Execute(null);
        Assert.Equal(new DateTime(2026, 8, 1), vm.SelectedDate);
        Assert.Equal("2026年8月", vm.SelectedDateText);
        Assert.True(vm.ShowBackToToday);
        vm.NextDayCommand.Execute(null);
        Assert.False(vm.CanGoForward);
        Assert.False(vm.ShowBackToToday);
        vm.TodayCommand.Execute(null);
        Assert.Equal(new DateTime(2026, 9, 5), vm.SelectedDate);
    }
}
