using MidiBridge.Models;

namespace MidiBridge.Services.Interfaces;

/// <summary>
/// 本地 MIDI 服务接口，负责本地 MIDI 设备的扫描、连接和消息传输。
/// </summary>
public interface ILocalMidiService : IDisposable
{
    /// <summary>
    /// 扫描本地 MIDI 设备。
    /// </summary>
    IEnumerable<MidiDevice> ScanDevices();

    /// <summary>
    /// 连接本地设备。
    /// </summary>
    /// <param name="device">设备。</param>
    /// <returns>连接成功返回 true。</returns>
    bool Connect(MidiDevice device);

    /// <summary>
    /// 断开本地设备连接。
    /// </summary>
    /// <param name="device">设备。</param>
    void Disconnect(MidiDevice device);

    /// <summary>
    /// 发送 MIDI 消息。
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <param name="data">MIDI 数据。</param>
    void SendMessage(MidiDevice device, byte[] data);

    /// <summary>
    /// 发送 MIDI 缓冲区（SysEx 等）。
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <param name="buffer">MIDI 数据缓冲区。</param>
    void SendBuffer(MidiDevice device, byte[] buffer);

    /// <summary>
    /// 发送 MIDI 短消息。
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <param name="message">MIDI 短消息。</param>
    void SendShortMessage(MidiDevice device, int message);

    /// <summary>
    /// MIDI 数据接收事件。
    /// </summary>
    event EventHandler<(MidiDevice Device, byte[] Data)>? MidiDataReceived;
}