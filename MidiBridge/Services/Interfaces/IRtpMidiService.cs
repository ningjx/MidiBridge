using System.Collections.ObjectModel;
using MidiBridge.Models;

namespace MidiBridge.Services.Interfaces;

/// <summary>
/// RTP-MIDI 服务接口，负责 RTP-MIDI 协议的实现。
/// </summary>
public interface IRtpMidiService : IDisposable
{
    /// <summary>
    /// 获取 RTP-MIDI 输入设备集合。
    /// </summary>
    ObservableCollection<MidiDevice> InputDevices { get; }

    /// <summary>
    /// 获取服务是否正在运行。
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// 获取或设置控制端口。
    /// </summary>
    int ControlPort { get; set; }

    /// <summary>
    /// 启动服务。
    /// </summary>
    /// <param name="controlPort">控制端口。</param>
    /// <returns>启动成功返回 true。</returns>
    bool Start(int controlPort = 5004);

    /// <summary>
    /// 停止服务。
    /// </summary>
    void Stop();

    /// <summary>
    /// 发送 MIDI 消息到指定设备。
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <param name="data">MIDI 数据。</param>
    void SendMessage(MidiDevice device, byte[] data);

    /// <summary>
    /// 主动连接到远程设备。
    /// </summary>
    /// <param name="host">目标主机地址。</param>
    /// <param name="port">控制端口。</param>
    /// <param name="name">本地名称。</param>
    /// <returns>连接成功返回 true。</returns>
    Task<bool> ConnectAsync(string host, int port, string name = "MidiBridge");

    /// <summary>
    /// 断开与远程设备的连接。
    /// </summary>
    /// <param name="sessionId">会话ID。</param>
    void Disconnect(string sessionId);

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
}