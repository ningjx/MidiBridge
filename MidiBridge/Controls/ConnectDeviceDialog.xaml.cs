using System.Windows;
using System.Windows.Input;

namespace MidiBridge.Controls;

public partial class ConnectDeviceDialog : Window
{
    public string DeviceIp => IpTextBox.Text.Trim();
    public int DevicePort => int.TryParse(PortTextBox.Text.Trim(), out int port) ? port : 5506;

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
        if (string.IsNullOrWhiteSpace(DeviceIp))
        {
            IpTextBox.Focus();
            return;
        }

        if (DevicePort <= 0 || DevicePort > 65535)
        {
            PortTextBox.Focus();
            return;
        }

        DialogResult = true;
        Close();
    }
}