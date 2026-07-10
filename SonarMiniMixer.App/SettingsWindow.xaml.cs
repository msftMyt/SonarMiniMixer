using System.Windows;

namespace SonarMiniMixer.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        StartupCheck.IsChecked = StartupRegistration.IsEnabled();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        StartupRegistration.SetEnabled(StartupCheck.IsChecked == true);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
