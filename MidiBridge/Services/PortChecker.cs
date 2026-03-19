using System.Net;
using System.Net.Sockets;
using MidiBridge.Services.Interfaces;
using Serilog;

namespace MidiBridge.Services;

/// <summary>
/// 端口检查器实现，用于检测端口是否被占用。
/// </summary>
public class PortChecker : IPortChecker
{
    /// <inheritdoc/>
    public bool IsPortOccupied(int port)
    {
        return !IsPortAvailable(port);
    }

    /// <inheritdoc/>
    (bool RtpAvailable, bool Nm2Available, string? RtpError, string? Nm2Error) IPortChecker.CheckPorts(int rtpPort, int nm2Port)
    {
        var result = CheckPorts(rtpPort, nm2Port);
        return (result.RtpAvailable, result.NM2Available, 
            string.IsNullOrEmpty(result.RtpError) ? null : result.RtpError,
            string.IsNullOrEmpty(result.NM2Error) ? null : result.NM2Error);
    }

    /// <summary>
    /// 检查端口是否可用（静态方法）。
    /// </summary>
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

    /// <summary>
    /// 检查 RTP 和 NM2 端口状态（静态方法）。
    /// </summary>
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