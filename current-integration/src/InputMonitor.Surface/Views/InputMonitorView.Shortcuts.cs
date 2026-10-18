using MyPowerTools.AvaloniaSdk;
using InputMonitor.Surface.ViewModels;

namespace InputMonitor.Surface.Views;

public partial class InputMonitorView : IMptShortcutCommandSource
{
    public string ShortcutToolId => "input-monitor";
    public string ShortcutContext => DataContext is InputMonitorViewModel vm ? "overview" : "";

    public IReadOnlyList<MptShortcutCommand> GetShortcutCommands()
    {
        if (DataContext is not InputMonitorViewModel vm) return [];
        return
        [
            MptShortcutCommand.FromCommand("input-monitor.ui.refresh", vm.RefreshCommand),
            MptShortcutCommand.FromCommand("input-monitor.ui.previous-day", vm.PreviousDayCommand),
            new("input-monitor.ui.next-day", () => { vm.NextDayCommand.Execute(null); return Task.CompletedTask; }, () => vm.CanGoForward),
            new("input-monitor.ui.today", () => { vm.TodayCommand.Execute(null); return Task.CompletedTask; }, () => vm.ShowBackToToday),
            MptShortcutCommand.FromCommand("input-monitor.ui.rest", vm.RestCommand),
            MptShortcutCommand.FromCommand("input-monitor.ui.pause", vm.PauseCommand),
            MptShortcutCommand.FromCommand("input-monitor.ui.capture-toggle", vm.CaptureToggleCommand),
        ];
    }
}
