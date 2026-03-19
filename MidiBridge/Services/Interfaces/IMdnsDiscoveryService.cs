using MidiBridge.Services.NetworkMidi2;

namespace MidiBridge.Services.Interfaces;

/// <summary>
/// mDNS 发现服务接口，负责 Network MIDI 2.0 设备的发现。
/// </summary>
public interface IMdnsDiscoveryService : IDisposable
{
    /// <summary>
    /// 获取服务是否正在运行。
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// 设置服务信息。
    /// </summary>
    /// <param name="name">服务名称。</param>
    /// <param name="port">服务端口。</param>
    /// <param name="productInstanceId">产品实例ID。</param>
    void SetServiceInfo(string name, int port, string productInstanceId = "");

    /// <summary>
    /// 启动发现服务。
    /// </summary>
    void Start();

    /// <summary>
    /// 停止发现服务。
    /// </summary>
    void Stop();

    /// <summary>
    /// 查询网络中的服务。
    /// </summary>
    void QueryServices();

    /// <summary>
    /// 设备发现事件。
    /// </summary>
    event EventHandler<NetworkMidi2Protocol.DiscoveredDevice>? DeviceDiscovered;

    /// <summary>
    /// 设备丢失事件。
    /// </summary>
    event EventHandler<NetworkMidi2Protocol.DiscoveredDevice>? DeviceLost;
}