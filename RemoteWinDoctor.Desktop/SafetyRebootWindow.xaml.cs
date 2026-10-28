using System.Windows;

namespace RemoteWinDoctor.Desktop;

public partial class SafetyRebootWindow : Window
{
    public bool IsConfirmed { get; private set; }

    public SafetyRebootWindow(string ip, string machineName, bool isLocal)
    {
        InitializeComponent();

        TxtTargetIp.Text = ip;
        TxtTargetName.Text = machineName;
        LocalAlertBanner.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnValidationChanged(object sender, RoutedEventArgs e)
    {
        var isChecked = ChkConfirm.IsChecked == true;
        var textMatch = TxtRebootWord.Text.Trim().Equals("REBOOT", StringComparison.OrdinalIgnoreCase);

        BtnConfirmReboot.IsEnabled = isChecked && textMatch;
    }

    private void BtnConfirmReboot_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = true;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        DialogResult = false;
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        DialogResult = false;
        Close();
    }
}
