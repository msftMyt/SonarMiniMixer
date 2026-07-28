using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using SonarMiniMixer.Core;

namespace SonarMiniMixer.App;

public partial class MainWindow : Window
{
    private readonly MixerViewModel _viewModel;
    private readonly SettingsStore _settingsStore;
    private AppSettings _settings = AppSettings.Default;
    private bool _pinned;
    private bool _allowClose;
    private bool _loaded;

    public MainWindow(MixerViewModel viewModel, SettingsStore settingsStore)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsStore = settingsStore;
        DataContext = viewModel;
        Loaded += Window_Loaded;
        Closing += Window_Closing;
        // Single hook for every show/hide path (tray toggle, Esc, deactivate, close-to-tray)
        // so a hidden popup stops polling Sonar and resyncs the moment it returns.
        IsVisibleChanged += async (_, e) =>
        {
            if (!_loaded) return;
            await _viewModel.SetSurfaceVisibleAsync(e.NewValue is true);
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        // Launching with --background never shows the window, so seed the real state
        // rather than assuming visible and polling for a surface nobody is looking at.
        _viewModel.SetSurfaceVisible(IsVisible);
        _settings = await _settingsStore.LoadAsync();
        Width = _settings.Width;
        Height = _settings.Height;
        SetPinned(_settings.Pinned, false);
        if (_pinned && _settings.Left is double left && _settings.Top is double top)
        {
            Left = left;
            Top = top;
            ClampToVisibleWorkArea();
        }
        else PositionNearTaskbar();
        await _viewModel.StartAsync();
    }

    public void ShowFromTray()
    {
        Topmost = true;
        Show();
        if (!_loaded) return;
        if (!_pinned) PositionNearTaskbar();
        else ClampToVisibleWorkArea();
        Activate();
        Focus();
    }

    public void ToggleFromTray()
    {
        if (IsVisible && !_pinned) Hide();
        else ShowFromTray();
    }

    public void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    private void PositionNearTaskbar()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var fromPixels = GetTransformFromPixels();
        var topLeft = fromPixels.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = fromPixels.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        Left = Math.Clamp(bottomRight.X - width - 14, topLeft.X + 12, Math.Max(topLeft.X + 12, bottomRight.X - width - 12));
        Top = Math.Clamp(bottomRight.Y - height - 14, topLeft.Y + 12, Math.Max(topLeft.Y + 12, bottomRight.Y - height - 12));
    }

    private Matrix GetTransformFromPixels()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null) return source.CompositionTarget.TransformFromDevice;
        using var graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return new Matrix(96.0 / graphics.DpiX, 0, 0, 96.0 / graphics.DpiY, 0, 0);
    }

    private void ClampToVisibleWorkArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var screen = hwnd != IntPtr.Zero
            ? System.Windows.Forms.Screen.FromHandle(hwnd)
            : System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var fromPixels = GetTransformFromPixels();
        var topLeft = fromPixels.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = fromPixels.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        Left = Math.Clamp(Left, topLeft.X + 12, Math.Max(topLeft.X + 12, bottomRight.X - width - 12));
        Top = Math.Clamp(Top, topLeft.Y + 12, Math.Max(topLeft.Y + 12, bottomRight.Y - height - 12));
    }

    private async void Pin_Click(object sender, RoutedEventArgs e) => await SetPinnedAsync(!_pinned);

    private async Task SetPinnedAsync(bool value)
    {
        SetPinned(value, true);
        await SaveSettingsAsync();
    }

    private void SetPinned(bool value, bool reposition)
    {
        _pinned = value;
        Topmost = value;
        ResizeMode = value ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;
        ShowInTaskbar = value;
        PinButton.Content = value ? "◆" : "◇";
        PinButton.ToolTip = value ? "Unpin and auto-hide" : "Pin and keep open";
        System.Windows.Automation.AutomationProperties.SetName(PinButton, value ? "Unpin mixer" : "Pin mixer");
        if (reposition && !value) PositionNearTaskbar();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_pinned && IsVisible)
        {
            Hide();
            Topmost = false;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_pinned && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private async void Mute_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChannelViewModel channel) await channel.ToggleMuteAsync();
    }

    private async void Preset_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox { DataContext: ChannelViewModel channel, SelectedValue: Guid presetId }) return;
        if (channel.SelectedPresetId != presetId) await _viewModel.SelectPresetAsync(channel, presetId);
    }

    private async void Device_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox { DataContext: ChannelViewModel channel, SelectedValue: string deviceId }) return;
        if (string.Equals(channel.SelectedDeviceId, deviceId, StringComparison.OrdinalIgnoreCase)) return;
        if (channel.IsMaster) await _viewModel.SelectMasterOutputAsync(channel, deviceId);
        else await _viewModel.SelectDeviceAsync(channel, deviceId);
    }

    private void CenterChatMix_Click(object sender, RoutedEventArgs e) => _viewModel.ChatMix = 0;

    private void Slider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.Slider slider || !slider.IsEnabled || e.Delta == 0) return;
        slider.Value = Math.Clamp(
            slider.Value + (Math.Sign(e.Delta) * slider.SmallChange),
            slider.Minimum,
            slider.Maximum);
        e.Handled = true;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow { Owner = this }.ShowDialog();
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_pinned) { Hide(); e.Handled = true; }
        if (e.Key == Key.D0 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _viewModel.CanControlChatMix) { _viewModel.ChatMix = 0; e.Handled = true; }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        if (_loaded) await SaveSettingsAsync();
        _viewModel.Dispose();
    }

    private Task SaveSettingsAsync() => _settingsStore.SaveAsync(_settings with
    {
        StartWithWindows = StartupRegistration.IsEnabled(),
        Pinned = _pinned,
        Width = Width,
        Height = Height,
        Left = _pinned ? Left : null,
        Top = _pinned ? Top : null
    });
}

public sealed class ConnectionBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new SolidColorBrush(value is true ? System.Windows.Media.Color.FromRgb(80, 250, 123) : System.Windows.Media.Color.FromRgb(255, 184, 108));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

public sealed class LevelFillHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4 ||
            values[0] is not double value || values[1] is not double minimum ||
            values[2] is not double maximum || values[3] is not double trackHeight ||
            maximum <= minimum || double.IsNaN(trackHeight)) return 0d;
        var fraction = Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
        return Math.Max(0, trackHeight * fraction);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Scales a metric with the window so extra room is actually used and a shrunk
/// window stays legible. Parameter is "atMin,atReference,atMax".
/// </summary>
public sealed class ResponsiveMetricConverter : IValueConverter
{
    public const double MinHeight = 372;
    public const double ReferenceHeight = 424;
    public const double MaxHeight = 650;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double actual || double.IsNaN(actual) ||
            parameter is not string spec) return DependencyProperty.UnsetValue;
        var parts = spec.Split(',');
        if (parts.Length != 3 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var atMin) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var atRef) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var atMax))
            return DependencyProperty.UnsetValue;

        var height = Math.Clamp(actual, MinHeight, MaxHeight);
        return height <= ReferenceHeight
            ? Lerp(atMin, atRef, Fraction(height, MinHeight, ReferenceHeight))
            : Lerp(atRef, atMax, Fraction(height, ReferenceHeight, MaxHeight));
    }

    private static double Fraction(double value, double from, double to) =>
        to <= from ? 1 : Math.Clamp((value - from) / (to - from), 0, 1);

    private static double Lerp(double from, double to, double t) => from + ((to - from) * t);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
