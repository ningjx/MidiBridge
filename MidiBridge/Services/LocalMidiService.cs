using System.Collections.Concurrent;
using MidiBridge.Models;
using MidiBridge.Services.Interfaces;
using NAudio.Midi;
using Serilog;

namespace MidiBridge.Services;

/// <summary>
/// 本地 MIDI 服务实现，负责本地 MIDI 设备的扫描、连接和消息传输。
/// </summary>
public class LocalMidiService : ILocalMidiService
{
    private readonly ConcurrentDictionary<int, MidiIn> _localInputs = new();
    private readonly ConcurrentDictionary<int, MidiOut> _localOutputs = new();

    public event EventHandler<(MidiDevice Device, byte[] Data)>? MidiDataReceived;

    /// <summary>
    /// 扫描本地 MIDI 设备。
    /// </summary>
    public IEnumerable<MidiDevice> ScanDevices()
    {
        var devices = new List<MidiDevice>();

        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
        {
            devices.Add(new MidiDevice
            {
                Id = $"local-in-{i}",
                Name = MidiIn.DeviceInfo(i).ProductName,
                Type = MidiDeviceType.LocalInput,
                Protocol = "MIDI 1.0",
                LocalDeviceId = i,
                Status = MidiDeviceStatus.Disconnected
            });
        }

        for (int i = 0; i < MidiOut.NumberOfDevices; i++)
        {
            devices.Add(new MidiDevice
            {
                Id = $"local-out-{i}",
                Name = MidiOut.DeviceInfo(i).ProductName,
                Type = MidiDeviceType.LocalOutput,
                Protocol = "MIDI 1.0",
                LocalDeviceId = i,
                Status = MidiDeviceStatus.Disconnected
            });
        }

        Log.Information("[LocalMidi] 扫描完成: {InputCount} 输入, {OutputCount} 输出",
            devices.Count(d => d.Type == MidiDeviceType.LocalInput),
            devices.Count(d => d.Type == MidiDeviceType.LocalOutput));

        return devices;
    }

    /// <summary>
    /// 连接本地设备。
    /// </summary>
    public bool Connect(MidiDevice device)
    {
        if (!device.LocalDeviceId.HasValue) return false;

        try
        {
            if (device.Type == MidiDeviceType.LocalInput)
            {
                var midiIn = new MidiIn(device.LocalDeviceId.Value);
                midiIn.MessageReceived += (s, e) => OnMidiMessageReceived(device, e);
                midiIn.Start();
                _localInputs[device.LocalDeviceId.Value] = midiIn;
                device.Status = MidiDeviceStatus.Connected;
                device.ConnectedTime = DateTime.Now;
                return true;
            }
            else if (device.Type == MidiDeviceType.LocalOutput)
            {
                var midiOut = new MidiOut(device.LocalDeviceId.Value);
                _localOutputs[device.LocalDeviceId.Value] = midiOut;
                device.Status = MidiDeviceStatus.Connected;
                device.ConnectedTime = DateTime.Now;
                return true;
            }
        }
        catch (Exception ex)
        {
            device.Status = MidiDeviceStatus.Error;
            device.ErrorMessage = ex.Message;
            Log.Error(ex, "[LocalMidi] 连接设备失败: {DeviceName}", device.Name);
        }

        return false;
    }

    /// <summary>
    /// 断开本地设备连接。
    /// </summary>
    public void Disconnect(MidiDevice device)
    {
        if (!device.LocalDeviceId.HasValue) return;

        if (device.Type == MidiDeviceType.LocalInput)
        {
            if (_localInputs.TryRemove(device.LocalDeviceId.Value, out var midiIn))
            {
                midiIn.Stop();
                midiIn.Dispose();
            }
        }
        else if (device.Type == MidiDeviceType.LocalOutput)
        {
            if (_localOutputs.TryRemove(device.LocalDeviceId.Value, out var midiOut))
            {
                midiOut.Dispose();
            }
        }

        device.Status = MidiDeviceStatus.Disconnected;
    }

    /// <summary>
    /// 发送 MIDI 消息。
    /// </summary>
    public void SendMessage(MidiDevice device, byte[] data)
    {
        if (!device.LocalDeviceId.HasValue || device.Type != MidiDeviceType.LocalOutput) return;
        if (!_localOutputs.TryGetValue(device.LocalDeviceId.Value, out var midiOut)) return;

        try
        {
            if (data.Length >= 3)
            {
                int message = (data[2] << 16) | (data[1] << 8) | data[0];
                midiOut.Send(message);
                UpdateDeviceStats(device);
            }
            else if (data.Length == 2)
            {
                int message = (data[1] << 8) | data[0];
                midiOut.Send(message);
                UpdateDeviceStats(device);
            }
            else if (data.Length == 1)
            {
                midiOut.Send(data[0]);
                UpdateDeviceStats(device);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[LocalMidi] 发送消息失败: {DeviceName}", device.Name);
        }
    }

    /// <summary>
    /// 发送 MIDI 缓冲区。
    /// </summary>
    public void SendBuffer(MidiDevice device, byte[] buffer)
    {
        if (!device.LocalDeviceId.HasValue || device.Type != MidiDeviceType.LocalOutput) return;
        if (!_localOutputs.TryGetValue(device.LocalDeviceId.Value, out var midiOut)) return;

        try
        {
            midiOut.SendBuffer(buffer);
            UpdateDeviceStats(device);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[LocalMidi] 发送缓冲区失败: {DeviceName}", device.Name);
        }
    }

    /// <summary>
    /// 发送 MIDI 短消息。
    /// </summary>
    public void SendShortMessage(MidiDevice device, int message)
    {
        if (!device.LocalDeviceId.HasValue || device.Type != MidiDeviceType.LocalOutput) return;
        if (!_localOutputs.TryGetValue(device.LocalDeviceId.Value, out var midiOut)) return;

        try
        {
            midiOut.Send(message);
            UpdateDeviceStats(device);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[LocalMidi] 发送短消息失败: {DeviceName}", device.Name);
        }
    }

    private void OnMidiMessageReceived(MidiDevice device, MidiInMessageEventArgs e)
    {
        device.ReceivedMessages++;
        device.LastActivity = DateTime.Now;
        device.Status = MidiDeviceStatus.Active;
        device.PulseTransmit();

        if (e.MidiEvent == null) return;

        byte[] data;

        if (e.MidiEvent is SysexEvent)
        {
            data = Array.Empty<byte>();
        }
        else
        {
            int msg = e.MidiEvent.GetAsShortMessage();
            if (msg == 0) return;

            byte status = (byte)(msg & 0xFF);
            byte data1 = (byte)((msg >> 8) & 0xFF);
            byte data2 = (byte)((msg >> 16) & 0xFF);

            if (status >= 0xF8)
            {
                data = new byte[] { status };
            }
            else if (status >= 0xF0 || (status & 0xF0) == 0xC0 || (status & 0xF0) == 0xD0)
            {
                data = new byte[] { status, data1 };
            }
            else
            {
                data = new byte[] { status, data1, data2 };
            }
        }

        if (data.Length > 0)
        {
            MidiDataReceived?.Invoke(this, (device, data));
        }
    }

    private void UpdateDeviceStats(MidiDevice device)
    {
        device.SentMessages++;
        device.LastActivity = DateTime.Now;
        device.PulseTransmit();
    }

    public void Dispose()
    {
        foreach (var midiIn in _localInputs.Values)
        {
            try
            {
                midiIn.Stop();
                midiIn.Dispose();
            }
            catch { }
        }

        foreach (var midiOut in _localOutputs.Values)
        {
            try { midiOut.Dispose(); } catch { }
        }

        _localInputs.Clear();
        _localOutputs.Clear();
    }
}