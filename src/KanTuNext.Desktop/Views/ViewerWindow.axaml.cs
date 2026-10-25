using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KanTuNext.Desktop.Services;
using KanTuNext.Desktop.ViewModels;

namespace KanTuNext.Desktop.Views;

public partial class ViewerWindow : Window
{
    private readonly DispatcherTimer _hideToolbar;

    public ViewerWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindows()) WindowsResizeFrame.Attach(this);
        _hideToolbar = new DispatcherTimer(TimeSpan.FromSeconds(2.5), DispatcherPriority.Normal, (_, _) =>
        {
            _hideToolbar!.Stop();
            if (!ToolbarDock.IsPointerOver) ToolbarDock.Opacity = 0;
        });
        PointerMoved += (_, _) => ShowToolbar();
        Opened += (_, _) => { ShowToolbar(); Viewer.Focus(); };
        Closed += (_, _) => Model?.Close();
        DataContextChanged += (_, _) =>
        {
            if (Model is not { } vm) return;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewerViewModel.Index)) Viewer.ResetOnNextSource = true;
                if (e.PropertyName is nameof(ViewerViewModel.Tag) or nameof(ViewerViewModel.Index)) UpdateTagButtons();
            };
            UpdateTagButtons();
        };
    }

    private void UpdateTagButtons()
    {
        var tag = Model?.Tag ?? KanTuNext.Core.Library.PhotoTag.None;
        TagGood.Classes.Set("on", tag == KanTuNext.Core.Library.PhotoTag.Good);
        TagNeutral.Classes.Set("on", tag == KanTuNext.Core.Library.PhotoTag.Neutral);
        TagBad.Classes.Set("on", tag == KanTuNext.Core.Library.PhotoTag.Bad);
    }

    private ViewerViewModel? Model => DataContext as ViewerViewModel;

    /// <summary>Opens a viewer on <paramref name="index"/> of <paramref name="items"/>, sized to the screen.</summary>
    public static void Open(Window owner, IReadOnlyList<KanTuNext.Core.Library.MediaFile> items, int index)
    {
        var model = new ViewerViewModel(items, index);
        var window = new ViewerWindow { DataContext = model };
        if (owner.Screens.ScreenFromWindow(owner) is { } screen)
        {
            var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
            window.Width = Math.Round(area.Width * 0.8);
            window.Height = Math.Round(area.Height * 0.85);
        }
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        _ = model.ShowAsync(model.Index);
    }

    private void ShowToolbar()
    {
        ToolbarDock.Opacity = 1;
        _hideToolbar.Stop();
        _hideToolbar.Start();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Model is not { } vm) return;
        switch (e.Key)
        {
            case Key.Left or Key.Up or Key.PageUp: _ = vm.GoBackAsync(); break;
            case Key.Right or Key.Down or Key.PageDown or Key.Space: _ = vm.GoForwardAsync(); break;
            case Key.Home: _ = vm.ShowAsync(0); break;
            case Key.OemPlus or Key.Add: Viewer.ZoomBy(1.25); break;
            case Key.OemMinus or Key.Subtract: Viewer.ZoomBy(0.8); break;
            case Key.D0 or Key.NumPad0: Viewer.FitToView(); break;
            case Key.D1 or Key.NumPad1: Viewer.ActualSize(); break;
            case Key.R: Viewer.Rotate(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : -1); break;
            case Key.Escape: Close(); break;
            case Key.Delete or Key.Back: _ = DeleteAsync(); break;
            case Key.T when vm.OcrAvailable: vm.ToggleOcr(); break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control):
                if (vm.HasOcrText && vm.ShowOcr) _ = CopyTextAsync(); else return;
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnPrevious(object? sender, RoutedEventArgs e) => _ = Model?.GoBackAsync();
    private void OnNext(object? sender, RoutedEventArgs e) => _ = Model?.GoForwardAsync();
    private void OnZoomIn(object? sender, RoutedEventArgs e) => Viewer.ZoomBy(1.25);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => Viewer.ZoomBy(0.8);
    private void OnActualSize(object? sender, RoutedEventArgs e) => Viewer.ActualSize();
    private void OnFit(object? sender, RoutedEventArgs e) => Viewer.FitToView();
    private void OnRotate(object? sender, RoutedEventArgs e) => Viewer.Rotate(-1);
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private void OnDelete(object? sender, RoutedEventArgs e) => _ = DeleteAsync();
    private void OnOcr(object? sender, RoutedEventArgs e) => Model?.ToggleOcr();
    private void OnCopyText(object? sender, RoutedEventArgs e) => _ = CopyTextAsync();

    private async Task CopyTextAsync()
    {
        if (Model is { HasOcrText: true } vm && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.OcrText);
    }

    private void OnTag(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && Enum.TryParse<KanTuNext.Core.Library.PhotoTag>(name, out var tag))
            Model?.ToggleTag(tag);
    }

    private async Task DeleteAsync()
    {
        if (Model is not { } vm) return;
        await vm.DeleteCurrentAsync();
        if (vm.IsEmpty) Close();
    }
}
