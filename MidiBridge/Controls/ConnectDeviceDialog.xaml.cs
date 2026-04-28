using MidiBridge.Utils;
using System.Windows;
using System.Windows.Input;

namespace MidiBridge.Controls;

public partial class ConnectDeviceDialog : Window
{
    public string DeviceIp => NetworkUtils.ExtractIpAddress(IpTextBox.Text.Trim());
    public int DevicePort => NetworkUtils.ExtractPort(IpTextBox.Text.Trim(), PortTextBox.Text.Trim());

    public ConnectDeviceDialog()
    {
        InitializeComponent();
        IpTextBox.Focus();
        IpTextBox.SelectAll();
    }

    public ConnectDeviceDialog(string defaultIp, int defaultPort) : this()
    {
        if (!string.IsNullOrEmpty(defaultIp))
        {
            IpTextBox.Text = defaultIp;
        }
        if (defaultPort > 0)
        {
            PortTextBox.Text = defaultPort.ToString();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var extractedIp = DeviceIp;
        if (string.IsNullOrWhiteSpace(extractedIp) || !NetworkUtils.IsValidIpAddress(extractedIp))
        {
            IpTextBox.Focus();
            return;
        }

        var extractedPort = DevicePort;
        if (extractedPort <= 0 || extractedPort > 65535)
        {
            PortTextBox.Focus();
            return;
        }

        DialogResult = true;
        Close();
    }
}