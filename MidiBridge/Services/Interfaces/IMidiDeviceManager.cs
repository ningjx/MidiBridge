using System.Collections.ObjectModel;
using MidiBridge.Models;
using MidiBridge.Services.NetworkMidi2;

namespace MidiBridge.Services.Interfaces;

/// <summary>
/// MIDI 设备管理器接口，负责设备发现、连接、断开和消息传输。
/// </summary>
public interface IMidiDeviceManager : IDisposable
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
    /// 获取已发现的 Network MIDI 2.0 设备集合。
    /// </summary>
    ObservableCollection<NetworkMidi2Protocol.DiscoveredDevice> DiscoveredNM2Devices { get; }

    /// <summary>
    /// 获取路由器实例。
    /// </summary>
    IMidiRouter Router { get; }

    /// <summary>
    /// 获取服务是否正在运行。
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// 获取或设置 RTP 端口。
    /// </summary>
    int RtpPort { get; set; }

    /// <summary>
    /// 获取或设置 Network MIDI 2.0 端口。
    /// </summary>
    int NM2Port { get; set; }

    /// <summary>
    /// 扫描本地 MIDI 设备。
    /// </summary>
    void ScanLocalDevices();

    /// <summary>
    /// 启动网络服务。
    /// </summary>
    /// <param name="rtpPort">RTP-MIDI 端口。</param>
    /// <param name="nm2Port">Network MIDI 2.0 端口。</param>
    /// <returns>启动成功返回 true。</returns>
    bool Start(int rtpPort, int nm2Port);

    /// <summary>
    /// 停止网络服务。
    /// </summary>
    void Stop();

    /// <summary>
    /// 连接设备。
    /// </summary>
    /// <param name="deviceId">设备ID。</param>
    /// <returns>连接成功返回 true。</returns>
    bool ConnectDevice(string deviceId);

    /// <summary>
    /// 断开设备连接。
    /// </summary>
    /// <param name="deviceId">设备ID。</param>
    void DisconnectDevice(string deviceId);

    /// <summary>
    /// 发送 MIDI 消息到目标设备。
    /// </summary>
    /// <param name="target">目标设备。</param>
    /// <param name="data">MIDI 数据。</param>
    void SendMidiMessage(MidiDevice target, byte[] data);

    /// <summary>
    /// 发送 MIDI 缓冲区到目标设备（用于 SysEx 等变长消息）。
    /// </summary>
    /// <param name="target">目标设备。</param>
    /// <param name="buffer">MIDI 数据缓冲区。</param>
    void SendMidiBuffer(MidiDevice target, byte[] buffer);

    /// <summary>
    /// 发送 MIDI 短消息到目标设备。
    /// </summary>
    /// <param name="target">目标设备。</param>
    /// <param name="message">MIDI 短消息。</param>
    void SendMidiShortMessage(MidiDevice target, int message);

    /// <summary>
    /// 邀请 Network MIDI 2.0 设备连接。
    /// </summary>
    /// <param name="host">目标主机。</param>
    /// <param name="port">目标端口。</param>
    /// <param name="name">设备名称。</param>
    /// <returns>连接成功返回 true。</returns>
    Task<bool> InviteNM2Device(string host, int port, string? name = null);

    /// <summary>
    /// 结束 Network MIDI 2.0 会话。
    /// </summary>
    /// <param name="sessionId">会话ID。</param>
    void EndNM2Session(string sessionId);

    /// <summary>
    /// 结束 RTP-MIDI 会话。
    /// </summary>
    /// <param name="sessionId">会话ID。</param>
    void EndRtpSession(string sessionId);

    /// <summary>
    /// 刷新 Network MIDI 2.0 设备发现。
    /// </summary>
    void RefreshNM2Discovery();

    /// <summary>
    /// 移动设备在列表中的位置。
    /// </summary>
    /// <param name="deviceId">设备ID。</param>
    /// <param name="newIndex">新索引。</param>
    /// <param name="isInput">是否为输入设备。</param>
    void MoveDevice(string deviceId, int newIndex, bool isInput);

    /// <summary>
    /// 保存设备排序顺序。
    /// </summary>
    void SaveDeviceOrder();

    /// <summary>
    /// 设置设备启用状态。
    /// </summary>
    /// <param name="deviceId">设备ID。</param>
    /// <param name="enabled">是否启用。</param>
    void SetDeviceEnabled(string deviceId, bool enabled);

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
    /// 状态变化事件。
    /// </summary>
    event EventHandler<string>? StatusChanged;

    /// <summary>
    /// MIDI 数据接收事件。
    /// </summary>
    event EventHandler<(MidiDevice Device, byte[] Data)>? MidiDataReceived;
}