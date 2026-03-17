namespace MidiBridge.Models;

public class AppConfig
{
    public WindowConfig Window { get; set; } = new();
    public NetworkConfig Network { get; set; } = new();
    public List<RouteConfig> Routes { get; set; } = new();
    public List<string> InputDeviceOrder { get; set; } = new();
    public List<string> OutputDeviceOrder { get; set; } = new();
    public List<string> DisabledDevices { get; set; } = new();
}

public class WindowConfig
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 900;
    public double Height { get; set; } = 600;
    public bool IsMaximized { get; set; }
}

public class NetworkConfig
{
    public int RtpPort { get; set; } = 5004;
    public int NM2Port { get; set; } = 5506;
    public bool AutoStart { get; set; }
}

public class RouteConfig
{
    public string SourceId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
}