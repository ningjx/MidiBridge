using System.Net;
using System.Net.Sockets;
using Serilog;

namespace MidiBridge.Services;

public static class PortChecker
{
    public static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static (bool RtpAvailable, bool NM2Available, string RtpError, string NM2Error) CheckPorts(int rtpPort, int nm2Port)
    {
        bool rtpAvailable = IsPortAvailable(rtpPort);
        bool nm2Available = IsPortAvailable(nm2Port);

        string rtpError = "";
        string nm2Error = "";

        if (!rtpAvailable)
        {
            rtpError = $"端口 {rtpPort}-{rtpPort + 1} 已被占用";
            Log.Warning("[Port] RTP 端口 {Port}-{Port1} 已被占用", rtpPort, rtpPort + 1);
        }

        if (!nm2Available)
        {
            nm2Error = $"端口 {nm2Port} 已被占用";
            Log.Warning("[Port] NM2 端口 {Port} 已被占用", nm2Port);
        }

        return (rtpAvailable, nm2Available, rtpError, nm2Error);
    }
}