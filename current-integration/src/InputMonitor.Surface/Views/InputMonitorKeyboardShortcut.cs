using Avalonia.Input;

namespace InputMonitor.Surface.Views;

public enum InputMonitorKeyboardAction
{
    None,
    Refresh,
    PreviousDate,
    NextDate,
    Today
}

public static class InputMonitorKeyboardShortcut
{
    public static InputMonitorKeyboardAction Resolve(Key key, KeyModifiers modifiers)
    {
        if (key == Key.F5 && modifiers == KeyModifiers.None)
        {
            return InputMonitorKeyboardAction.Refresh;
        }

        if (modifiers != KeyModifiers.Alt)
        {
            return InputMonitorKeyboardAction.None;
        }

        return key switch
        {
            Key.Left => InputMonitorKeyboardAction.PreviousDate,
            Key.Right => InputMonitorKeyboardAction.NextDate,
            Key.Home => InputMonitorKeyboardAction.Today,
            _ => InputMonitorKeyboardAction.None
        };
    }
}
