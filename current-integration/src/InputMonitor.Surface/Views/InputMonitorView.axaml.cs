using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using InputMonitor.Surface.ViewModels;

namespace InputMonitor.Surface.Views;

public sealed partial class InputMonitorView : UserControl
{
    private InputMonitorViewModel? _boundViewModel;
    private bool _isAttachedToVisualTree;

    public InputMonitorView()
    {
        AvaloniaXamlLoader.Load(this);
        AttachedToVisualTree += (_, _) =>
        {
            _isAttachedToVisualTree = true;
            SyncViewModelActivity();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _isAttachedToVisualTree = false;
            SyncViewModelActivity();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataContextProperty || change.Property == IsVisibleProperty)
        {
            SyncViewModelActivity();
        }
    }

    private void SyncViewModelActivity()
    {
        var current = DataContext as InputMonitorViewModel;
        if (!ReferenceEquals(_boundViewModel, current))
        {
            _boundViewModel?.SetRefreshActive(false);
            _boundViewModel = current;
        }

        var isActive = _isAttachedToVisualTree && IsVisible;
        _boundViewModel?.SetRefreshActive(isActive);
    }
}
