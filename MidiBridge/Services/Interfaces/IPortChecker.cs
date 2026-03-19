namespace MidiBridge.Services.Interfaces;

/// <summary>
/// 端口检查器接口，用于检测端口是否被占用。
/// </summary>
public interface IPortChecker
{
    /// <summary>
    /// 检查指定端口是否被占用。
    /// </summary>
    /// <param name="port">端口号。</param>
    /// <returns>如果端口被占用返回 true。</returns>
    bool IsPortOccupied(int port);

    /// <summary>
    /// 检查 RTP 和 NM2 端口状态。
    /// </summary>
    /// <param name="rtpPort">RTP 端口。</param>
    /// <param name="nm2Port">NM2 端口。</param>
    /// <returns>包含端口状态和错误信息的元组。</returns>
    (bool RtpAvailable, bool Nm2Available, string? RtpError, string? Nm2Error) CheckPorts(int rtpPort, int nm2Port);
}