using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace MidiBridge.Utils;

public static class NetworkUtils
{
    private static readonly Regex IpRegex = new(
        @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})",
        RegexOptions.Compiled);

    private static readonly Regex PortRegex = new(
        @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})[:\s](\d{1,5})",
        RegexOptions.Compiled);

    public static string ExtractIpAddress(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var match = IpRegex.Match(input);
        return match.Success ? match.Groups[1].Value : input;
    }

    public static int ExtractPort(string ipInput, string portInput, int defaultPort = 5506)
    {
        if (!string.IsNullOrWhiteSpace(portInput) && int.TryParse(portInput, out int port))
            return port;

        if (!string.IsNullOrWhiteSpace(ipInput))
        {
            var match = PortRegex.Match(ipInput);
            if (match.Success && int.TryParse(match.Groups[2].Value, out int urlPort))
                return urlPort;
        }

        return defaultPort;
    }

    public static bool IsValidIpAddress(string ip)
    {
        if (!IpRegex.IsMatch(ip)) return false;
        var parts = ip.Split('.');
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out int num) || num < 0 || num > 255)
                return false;
        }
        return true;
    }

    public static IPAddress GetLocalIPAddress()
    {
        var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
                return ip;
        }
        return IPAddress.Loopback;
    }

    public static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && ip.Equals(address))
                    return true;
            }
        }
        catch { }

        return false;
    }
}
