using System.Globalization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SonarMiniMixer.Core;

namespace SonarMiniMixer.App;

public partial class MainWindow : Window
{
    private readonly MixerViewModel _viewModel;
    private readonly SettingsStore _settingsStore;
    private AppSettings _settings = AppSettings.Default;
    private bool _pinned;
    private bool _allowClose;

    public MainWindow(MixerViewModel viewModel, SettingsStore settingsStore)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsStore = settingsStore;
        DataContext = viewModel;
        Loaded += Window_Loaded;
        Closing += Window_Closing;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _settingsStore.LoadAsync();
        Width = _settings.Width;
        Height = _settings.Height;
        SetPinned(_settings.Pinned, false);
        if (_pinned && _settings.Left is double left && _settings.Top is double top)
        {
            Left = left; Top = top;
        }
        await _viewModel.StartAsync();
    }

    public void ShowFromTray()
    {
        if (!_pinned) PositionNearTaskbar();
        Show();
        Activate();
        Focus();
    }

    public void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    private void PositionNearTaskbar()
    {
        var work = SystemParameters.WorkArea;
        Left = Math.Max(work.Left + 12, work.Right - ActualWidth - 14);
        Top = Math.Max(work.Top + 12, work.Bottom - ActualHeight - 14);
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
        if (reposition && !value) PositionNearTaskbar();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_pinned && IsVisible) Hide();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_pinned && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private async void Mute_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChannelViewModel channel) await channel.ToggleMuteAsync();
    }

    private void CenterChatMix_Click(object sender, RoutedEventArgs e) => _viewModel.ChatMix = 0;
    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow { Owner = this }.ShowDialog();
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_pinned) { Hide(); e.Handled = true; }
        if (e.Key == Key.D0 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _viewModel.ChatMix = 0; e.Handled = true; }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        await SaveSettingsAsync();
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
