using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MidiBridge.Models;
using Serilog;

namespace MidiBridge.Services.RtpMidi;

public class RtpMidiDiscoveryService : IDisposable
{
    private const string MDNS_MULTICAST_ADDRESS = "224.0.0.251";
    private const int MDNS_PORT = 5353;
    private const string RTP_MIDI_SERVICE_TYPE = "_apple-midi._udp";

    private UdpClient? _mdnsClient;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private int _servicePort;
    private string _serviceName;

    private readonly ConcurrentDictionary<string, DiscoveredRtpDevice> _discoveredDevices = new();
    private readonly ConcurrentDictionary<string, DateTime> _deviceLastSeen = new();

    public event EventHandler<DiscoveredRtpDevice>? DeviceDiscovered;
    public event EventHandler<DiscoveredRtpDevice>? DeviceLost;

    public IReadOnlyDictionary<string, DiscoveredRtpDevice> DiscoveredDevices => _discoveredDevices;
    public bool IsRunning => _isRunning;

    public class DiscoveredRtpDevice
    {
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public DateTime DiscoveredTime { get; set; }
    }

    public RtpMidiDiscoveryService()
    {
        _serviceName = "MidiBridge";
        _servicePort = 5004;
    }

    public void SetServiceInfo(string name, int port)
    {
        _serviceName = name;
        _servicePort = port;
    }

    public bool Start()
    {
        if (_isRunning) Stop();

        try
        {
            _cts = new CancellationTokenSource();

            _mdnsClient = new UdpClient();
            _mdnsClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _mdnsClient.Client.Bind(new IPEndPoint(IPAddress.Any, MDNS_PORT));

            var multicastAddr = IPAddress.Parse(MDNS_MULTICAST_ADDRESS);
            _mdnsClient.JoinMulticastGroup(multicastAddr);

            _isRunning = true;

            Task.Run(() => ReceiveLoop(_cts.Token));
            Task.Run(() => AnnounceLoop(_cts.Token));
            Task.Run(() => QueryLoop(_cts.Token));
            Task.Run(() => CleanupLoop(_cts.Token));

            Log.Information("[RTP-mDNS] 发现服务已启动");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RTP-mDNS] 启动失败");
            return false;
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;

        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }

        try
        {
            var multicastAddr = IPAddress.Parse(MDNS_MULTICAST_ADDRESS);
            _mdnsClient?.DropMulticastGroup(multicastAddr);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[RTP-mDNS] 退出多播组失败");
        }

        _mdnsClient?.Close();
        _mdnsClient?.Dispose();
        _discoveredDevices.Clear();
        _deviceLastSeen.Clear();
    }

    public void QueryServices()
    {
        if (!_isRunning || _mdnsClient == null) return;

        try
        {
            var query = CreateServiceQuery();
            var multicastEP = new IPEndPoint(IPAddress.Parse(MDNS_MULTICAST_ADDRESS), MDNS_PORT);
            _mdnsClient.Send(query, query.Length, multicastEP);
            Log.Debug("[RTP-mDNS] 已发送查询: {ServiceType}", RTP_MIDI_SERVICE_TYPE);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RTP-mDNS] 查询失败");
        }
    }

    private async void ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _mdnsClient != null)
        {
            try
            {
                var result = await _mdnsClient.ReceiveAsync();
                ProcessMDNSPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log.Debug(ex, "[RTP-mDNS] 接收错误");
                break;
            }
        }
    }

    private void ProcessMDNSPacket(byte[] data, IPEndPoint remoteEP)
    {
        try
        {
            if (data.Length < 12) return;

            ushort flags = (ushort)((data[2] << 8) | data[3]);
            bool isResponse = (flags & 0x8000) != 0;
            ushort answerCount = (ushort)((data[6] << 8) | data[7]);

            int offset = 12;

            ushort questionCount = (ushort)((data[4] << 8) | data[5]);
            for (int i = 0; i < questionCount && offset < data.Length; i++)
            {
                offset = SkipName(data, offset);
                if (offset + 4 > data.Length) return;
                offset += 4;
            }

            for (int i = 0; i < answerCount && offset < data.Length; i++)
            {
                offset = ParseResourceRecord(data, offset, remoteEP);
                if (offset < 0) return;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[RTP-mDNS] 处理数据包错误");
        }
    }

    private int ParseResourceRecord(byte[] data, int offset, IPEndPoint remoteEP)
    {
        if (offset >= data.Length) return -1;

        string name = ReadName(data, ref offset);
        if (string.IsNullOrEmpty(name) || offset + 10 > data.Length) return -1;

        ushort recordType = (ushort)((data[offset] << 8) | data[offset + 1]);
        ushort recordLength = (ushort)((data[offset + 8] << 8) | data[offset + 9]);
        offset += 10;

        if (offset + recordLength > data.Length) return -1;

        if (recordType == 33 && name.Contains(RTP_MIDI_SERVICE_TYPE))
        {
            ParseServiceRecord(data, offset, recordLength, remoteEP, name);
        }

        return offset + recordLength;
    }

    private void ParseServiceRecord(byte[] data, int offset, int length, IPEndPoint remoteEP, string name)
    {
        if (length < 7) return;

        ushort port = (ushort)((data[offset + 4] << 8) | data[offset + 5]);

        int nameOffset = offset + 6;
        string instanceName = ReadName(data, ref nameOffset);

        if (string.IsNullOrEmpty(instanceName)) return;

        if (IsLocalAddress(remoteEP.Address) && port == _servicePort)
        {
            return;
        }

        string deviceKey = $"{instanceName}@{remoteEP.Address}";

        var device = new DiscoveredRtpDevice
        {
            Name = instanceName,
            Host = remoteEP.Address.ToString(),
            Port = port,
            DiscoveredTime = DateTime.Now,
        };

        if (!_discoveredDevices.ContainsKey(deviceKey))
        {
            _discoveredDevices[deviceKey] = device;
            _deviceLastSeen[deviceKey] = DateTime.Now;
            DeviceDiscovered?.Invoke(this, device);
            Log.Information("[RTP-mDNS] 发现设备: {InstanceName} 地址 {Address}:{Port}", instanceName, remoteEP.Address, port);
        }
        else
        {
            _deviceLastSeen[deviceKey] = DateTime.Now;
        }
    }

    private bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && ip.Equals(address))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private string ReadName(byte[] data, ref int offset)
    {
        var sb = new StringBuilder();

        while (offset < data.Length)
        {
            byte len = data[offset++];

            if (len == 0) break;

            if ((len & 0xC0) == 0xC0)
            {
                if (offset >= data.Length) break;
                int pointer = ((len & 0x3F) << 8) | data[offset++];
                offset = pointer;
                continue;
            }

            if (offset + len > data.Length) break;

            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(data, offset, len));
            offset += len;
        }

        return sb.ToString();
    }

    private int SkipName(byte[] data, int offset)
    {
        while (offset < data.Length)
        {
            byte len = data[offset++];

            if (len == 0) break;

            if ((len & 0xC0) == 0xC0)
            {
                offset++;
                break;
            }

            offset += len;
        }

        return offset;
    }

    private byte[] CreateServiceQuery()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        ushort transactionId = (ushort)Random.Shared.Next();
        writer.Write((byte)((transactionId >> 8) & 0xFF));
        writer.Write((byte)(transactionId & 0xFF));

        writer.Write((byte)0x00);
        writer.Write((byte)0x00);

        writer.Write((byte)0x00);
        writer.Write((byte)0x01);

        writer.Write((byte)0x00);
        writer.Write((byte)0x00);

        writer.Write((byte)0x00);
        writer.Write((byte)0x00);

        writer.Write((byte)0x00);
        writer.Write((byte)0x00);

        WriteNameToWriter(writer, RTP_MIDI_SERVICE_TYPE);

        writer.Write((byte)0x00);
        writer.Write((byte)0x21);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);

        return ms.ToArray();
    }

    private byte[] CreateAnnouncement()
    {
        var serviceName = $"{_serviceName}.{RTP_MIDI_SERVICE_TYPE}";

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)0x00);
        writer.Write((byte)0x00);

        writer.Write((byte)0x84);
        writer.Write((byte)0x00);

        writer.Write((byte)0x00);
        writer.Write((byte)0x00);

        writer.Write((byte)0x00);
        writer.Write((byte)0x02);

        writer.Write((byte)0x00);
        writer.Write((byte)0x00);

        writer.Write((byte)0x00);
        writer.Write((byte)0x00);

        WriteNameToWriter(writer, serviceName);
        writer.Write((byte)0x00);
        writer.Write((byte)0x21);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);

        uint ttl = 4500;
        writer.Write((byte)((ttl >> 24) & 0xFF));
        writer.Write((byte)((ttl >> 16) & 0xFF));
        writer.Write((byte)((ttl >> 8) & 0xFF));
        writer.Write((byte)(ttl & 0xFF));

        var nameBytes = Encoding.UTF8.GetBytes(serviceName);
        ushort srvLen = (ushort)(2 + 2 + 2 + 1 + nameBytes.Length + 1);
        writer.Write((byte)((srvLen >> 8) & 0xFF));
        writer.Write((byte)(srvLen & 0xFF));

        writer.Write((byte)0x00);
        writer.Write((byte)0x00);

        writer.Write((byte)((_servicePort >> 8) & 0xFF));
        writer.Write((byte)(_servicePort & 0xFF));

        WriteNameToWriter(writer, serviceName);

        WriteNameToWriter(writer, serviceName);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);
        writer.Write((byte)((ttl >> 24) & 0xFF));
        writer.Write((byte)((ttl >> 16) & 0xFF));
        writer.Write((byte)((ttl >> 8) & 0xFF));
        writer.Write((byte)(ttl & 0xFF));
        writer.Write((byte)0x00);
        writer.Write((byte)0x04);

        var localIP = GetLocalIPAddress();
        var ipBytes = localIP.GetAddressBytes();
        writer.Write(ipBytes[0]);
        writer.Write(ipBytes[1]);
        writer.Write(ipBytes[2]);
        writer.Write(ipBytes[3]);

        return ms.ToArray();
    }

    private void WriteNameToWriter(BinaryWriter writer, string name)
    {
        var parts = name.Split('.');
        foreach (var part in parts)
        {
            var partBytes = Encoding.UTF8.GetBytes(part);
            writer.Write((byte)partBytes.Length);
            writer.Write(partBytes);
        }
        writer.Write((byte)0);
    }

    private async void AnnounceLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var announcement = CreateAnnouncement();
                var multicastEP = new IPEndPoint(IPAddress.Parse(MDNS_MULTICAST_ADDRESS), MDNS_PORT);
                _mdnsClient?.Send(announcement, announcement.Length, multicastEP);
                Log.Debug("[RTP-mDNS] 已发布服务: {ServiceName}", _serviceName);

                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log.Error(ex, "[RTP-mDNS] 发布错误");
                break;
            }
        }
    }

    private async void QueryLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                QueryServices();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { break; }
        }
    }

    private async void CleanupLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);

                var now = DateTime.Now;
                var expiredKeys = _deviceLastSeen
                    .Where(kvp => (now - kvp.Value).TotalSeconds > 120)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    if (_discoveredDevices.TryRemove(key, out var device))
                    {
                        _deviceLastSeen.TryRemove(key, out _);
                        DeviceLost?.Invoke(this, device);
                        Log.Information("[RTP-mDNS] 设备离线: {DeviceName}", device.Name);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { break; }
            }
    }

    private IPAddress GetLocalIPAddress()
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());
        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return ip;
            }
        }
        return IPAddress.Loopback;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}