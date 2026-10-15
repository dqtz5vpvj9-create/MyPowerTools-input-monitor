using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using InputMonitor.Surface.ViewModels;

namespace InputMonitor.Surface.Views;

public sealed partial class InputMonitorView : UserControl
{
    public InputMonitorView()
    {
        AvaloniaXamlLoader.Load(this);
        KeyDown += OnViewKeyDown;
        DetachedFromVisualTree += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Handled || DataContext is not InputMonitorViewModel viewModel)
        {
            return;
        }

        var handled = InputMonitorKeyboardShortcut.Resolve(eventArgs.Key, eventArgs.KeyModifiers) switch
        {
            InputMonitorKeyboardAction.Refresh => TryExecute(viewModel.RefreshCommand),
            InputMonitorKeyboardAction.PreviousDate => TryExecute(viewModel.PreviousDayCommand),
            InputMonitorKeyboardAction.NextDate when viewModel.CanGoForward => TryExecute(viewModel.NextDayCommand),
            InputMonitorKeyboardAction.Today when viewModel.ShowBackToToday => TryExecute(viewModel.TodayCommand),
            _ => false
        };
        eventArgs.Handled = handled;
    }

    private static bool TryExecute(ICommand command)
    {
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }
}
