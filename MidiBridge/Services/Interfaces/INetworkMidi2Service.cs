using System.Collections.ObjectModel;
using MidiBridge.Models;
using MidiBridge.Services.NetworkMidi2;

namespace MidiBridge.Services.Interfaces;

/// <summary>
/// Network MIDI 2.0 服务接口，负责 MIDI 2.0 网络协议的实现。
/// </summary>
public interface INetworkMidi2Service : IDisposable
{
    /// <summary>
    /// 获取输入设备集合。
    /// </summary>
    ObservableCollection<MidiDevice> InputDevices { get; }

    /// <summary>
    /// 获取输出设备集合。
    /// </summary>
    ObservableCollection<MidiDevice> OutputDevices { get; }

    /// <summary>
    /// 获取服务是否正在运行。
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// 获取当前会话字典。
    /// </summary>
    IReadOnlyDictionary<string, NetworkMidi2Protocol.SessionInfo> Sessions { get; }

    /// <summary>
    /// 获取已发现设备字典。
    /// </summary>
    IReadOnlyDictionary<string, NetworkMidi2Protocol.DiscoveredDevice> DiscoveredDevices { get; }

    /// <summary>
    /// 设置服务信息。
    /// </summary>
    /// <param name="name">服务名称。</param>
    /// <param name="productInstanceId">产品实例ID。</param>
    void SetServiceInfo(string name, string productInstanceId = "");

    /// <summary>
    /// 启动服务。
    /// </summary>
    /// <param name="port">监听端口。</param>
    /// <returns>启动成功返回 true。</returns>
    bool Start(int port = NetworkMidi2Protocol.DEFAULT_PORT);

    /// <summary>
    /// 停止服务。
    /// </summary>
    void Stop();

    /// <summary>
    /// 邀请设备连接。
    /// </summary>
    /// <param name="host">目标主机。</param>
    /// <param name="port">目标端口。</param>
    /// <param name="name">设备名称。</param>
    /// <returns>连接成功返回 true。</returns>
    Task<bool> InviteDevice(string host, int port, string? name = null);

    /// <summary>
    /// 结束会话。
    /// </summary>
    /// <param name="sessionId">会话ID。</param>
    void EndSession(string sessionId);

    /// <summary>
    /// 请求会话重置。
    /// </summary>
    /// <param name="sessionId">会话ID。</param>
    void RequestSessionReset(string sessionId);

    /// <summary>
    /// 接受待确认的邀请。
    /// </summary>
    /// <param name="sessionId">会话ID。</param>
    void AcceptPendingInvitation(string sessionId);

    /// <summary>
    /// 拒绝待确认的邀请。
    /// </summary>
    /// <param name="sessionId">会话ID。</param>
    void RejectPendingInvitation(string sessionId);

    /// <summary>
    /// 获取或设置最大会话数。
    /// </summary>
    int MaxSessions { get; set; }

    /// <summary>
    /// 发送 UMP 数据。
    /// </summary>
    /// <param name="sessionId">会话ID。</param>
    /// <param name="umpData">UMP 数据。</param>
    void SendUMP(string sessionId, byte[] umpData);

    /// <summary>
    /// 发送 MIDI 数据。
    /// </summary>
    /// <param name="sessionId">会话ID。</param>
    /// <param name="midiData">MIDI 数据。</param>
    void SendMidiData(string sessionId, byte[] midiData);

    /// <summary>
    /// 设备添加事件。
    /// </summary>
    event EventHandler<MidiDevice>? DeviceAdded;

    /// <summary>
    /// 设备移除事件。
    /// </summary>
    event EventHandler<MidiDevice>? DeviceRemoved;

    /// <summary>
    /// 设备更新事件。
    /// </summary>
    event EventHandler<MidiDevice>? DeviceUpdated;

    /// <summary>
    /// MIDI 数据接收事件。
    /// </summary>
    event EventHandler<(MidiDevice Device, byte[] Data)>? MidiDataReceived;

    /// <summary>
    /// 状态变化事件。
    /// </summary>
    event EventHandler<string>? StatusChanged;

    /// <summary>
    /// 设备发现事件。
    /// </summary>
    event EventHandler<NetworkMidi2Protocol.DiscoveredDevice>? DeviceDiscovered;

    /// <summary>
    /// 收到邀请事件（需要用户确认）。
    /// </summary>
    event EventHandler<(string SessionId, string DeviceName, string Host, int Port)>? InvitationReceived;

    /// <summary>
    /// 收到重传错误事件。
    /// </summary>
    event EventHandler<(string SessionId, int LostPackets)>? RetransmitErrorReceived;
}