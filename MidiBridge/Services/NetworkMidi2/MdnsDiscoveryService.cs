using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MidiBridge.Services.Interfaces;
using Serilog;

namespace MidiBridge.Services.NetworkMidi2;

public class MdnsDiscoveryService : IMdnsDiscoveryService
{
    private const string MDNS_MULTICAST_ADDRESS = "224.0.0.251";
    private const int MDNS_PORT = 5353;

    private UdpClient? _mdnsClient;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private string _serviceName;
    private int _servicePort;
    private string _productInstanceId;
    private string _umpEndpointName;

    public event EventHandler<NetworkMidi2Protocol.DiscoveredDevice>? DeviceDiscovered;
    public event EventHandler<NetworkMidi2Protocol.DiscoveredDevice>? DeviceLost;

    public IReadOnlyDictionary<string, NetworkMidi2Protocol.DiscoveredDevice> DiscoveredDevices => _discoveredDevices;
    private readonly ConcurrentDictionary<string, NetworkMidi2Protocol.DiscoveredDevice> _discoveredDevices = new();
    private readonly ConcurrentDictionary<string, DateTime> _deviceLastSeen = new();

    public bool IsRunning => _isRunning;

    public MdnsDiscoveryService()
    {
        _serviceName = NetworkMidi2Protocol.DEFAULT_SERVICE_NAME;
        _servicePort = NetworkMidi2Protocol.DEFAULT_PORT;
        _productInstanceId = Guid.NewGuid().ToString("N").Substring(0, 16);
        _umpEndpointName = NetworkMidi2Protocol.DEFAULT_SERVICE_NAME;
    }

    public void SetServiceInfo(string name, int port, string productInstanceId)
    {
        _serviceName = name;
        _servicePort = port;
        _productInstanceId = productInstanceId;
        _umpEndpointName = name;
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

            Log.Information("[mDNS] 发现服务已启动: {ServiceType}", NetworkMidi2Protocol.MDNS_SERVICE_TYPE);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[mDNS] 启动失败");
            return false;
        }
    }

    void IMdnsDiscoveryService.Start() => Start();

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
            Log.Debug(ex, "[mDNS] 退出多播组失败");
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
            Log.Debug("[mDNS] 已发送查询: {ServiceType}", NetworkMidi2Protocol.MDNS_SERVICE_TYPE);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[mDNS] 查询失败");
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
                    Log.Debug(ex, "[mDNS] 接收错误");
                break;
            }
        }
    }

    private void ProcessMDNSPacket(byte[] data, IPEndPoint remoteEP)
    {
        try
        {
            if (data.Length < 12) return;

            int offset = 0;
            ushort flags = (ushort)((data[2] << 8) | data[3]);

            bool isResponse = (flags & 0x8000) != 0;
            ushort questionCount = (ushort)((data[4] << 8) | data[5]);
            ushort answerCount = (ushort)((data[6] << 8) | data[7]);
            ushort additionalCount = (ushort)((data[10] << 8) | data[11]);

            offset = 12;

            for (int i = 0; i < questionCount && offset < data.Length; i++)
            {
                offset = SkipName(data, offset);
                if (offset + 4 > data.Length) return;
                offset += 4;
            }

            for (int i = 0; i < answerCount && offset < data.Length; i++)
            {
                offset = ParseResourceRecord(data, offset, remoteEP, isResponse);
                if (offset < 0) return;
            }

            for (int i = 0; i < additionalCount && offset < data.Length; i++)
            {
                offset = ParseResourceRecord(data, offset, remoteEP, isResponse);
                if (offset < 0) return;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[mDNS] 处理数据包错误");
        }
    }

    private int ParseResourceRecord(byte[] data, int offset, IPEndPoint remoteEP, bool isResponse)
    {
        if (offset >= data.Length) return -1;

        string name = ReadName(data, ref offset);
        if (string.IsNullOrEmpty(name) || offset + 10 > data.Length) return -1;

        ushort recordType = (ushort)((data[offset] << 8) | data[offset + 1]);
        ushort recordClass = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
        uint ttl = (uint)((data[offset + 4] << 24) | (data[offset + 5] << 16) | (data[offset + 6] << 8) | data[offset + 7]);
        ushort recordLength = (ushort)((data[offset + 8] << 8) | data[offset + 9]);
        offset += 10;

        if (offset + recordLength > data.Length) return -1;

        string serviceType = NetworkMidi2Protocol.MDNS_SERVICE_TYPE;

        if (recordType == 33 && name.Contains(serviceType.Replace(".local", "")))
        {
            ParseServiceRecord(data, offset, recordLength, remoteEP, name, ttl);
        }
        else if (recordType == 16)
        {
            ParseTXTRecord(data, offset, recordLength, remoteEP, name);
        }
        else if (recordType == 1)
        {
            ParseARecord(data, offset, recordLength, remoteEP, name);
        }

        return offset + recordLength;
    }

    private void ParseServiceRecord(byte[] data, int offset, int length, IPEndPoint remoteEP, string name, uint ttl)
    {
        if (length < 7) return;

        ushort priority = (ushort)((data[offset] << 8) | data[offset + 1]);
        ushort weight = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
        ushort port = (ushort)((data[offset + 4] << 8) | data[offset + 5]);

        int nameOffset = offset + 6;
        string targetHost = ReadName(data, ref nameOffset);

        if (string.IsNullOrEmpty(targetHost)) return;

        if (IsLocalAddress(remoteEP.Address) && port == _servicePort)
        {
            Log.Debug("[mDNS] 忽略本机: {TargetHost}:{Port}", targetHost, port);
            return;
        }

        string serviceInstanceName = ExtractServiceInstanceName(name);
        string deviceKey = $"{serviceInstanceName}@{remoteEP.Address}";

        var device = new NetworkMidi2Protocol.DiscoveredDevice
        {
            ServiceInstanceName = serviceInstanceName,
            UMPEndpointName = serviceInstanceName,
            Host = remoteEP.Address.ToString(),
            Port = port,
            ProductInstanceId = "",
            DiscoveredTime = DateTime.Now,
        };

        if (!_discoveredDevices.ContainsKey(deviceKey))
        {
            _discoveredDevices[deviceKey] = device;
            _deviceLastSeen[deviceKey] = DateTime.Now;
            DeviceDiscovered?.Invoke(this, device);
            Log.Information("[mDNS] 发现设备: {Name} 地址 {Address}:{Port}", serviceInstanceName, remoteEP.Address, port);
        }
        else
        {
            _deviceLastSeen[deviceKey] = DateTime.Now;
        }
    }

    private string ExtractServiceInstanceName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return "";

        int idx = fullName.IndexOf("._midi2");
        if (idx > 0)
        {
            return fullName.Substring(0, idx);
        }

        return fullName.Split('.')[0];
    }

    private void ParseTXTRecord(byte[] data, int offset, int length, IPEndPoint remoteEP, string name)
    {
        int end = offset + length;
        string umpEndpointName = "";
        string productInstanceId = "";

        while (offset < end)
        {
            byte txtLen = data[offset++];
            if (offset + txtLen > end) break;

            string txt = Encoding.UTF8.GetString(data, offset, txtLen);
            offset += txtLen;

            if (txt.StartsWith("UMPEndpointName="))
            {
                umpEndpointName = txt.Substring(16);
            }
            else if (txt.StartsWith("ProductInstanceId="))
            {
                productInstanceId = txt.Substring(17);
            }
        }

        if (!string.IsNullOrEmpty(umpEndpointName) || !string.IsNullOrEmpty(productInstanceId))
        {
            Log.Debug("[mDNS] TXT: UMPEndpointName={Name}, ProductInstanceId={Id}", umpEndpointName, productInstanceId);

            foreach (var device in _discoveredDevices.Values)
            {
                if (device.Host == remoteEP.Address.ToString())
                {
                    if (!string.IsNullOrEmpty(umpEndpointName))
                        device.UMPEndpointName = umpEndpointName;
                    if (!string.IsNullOrEmpty(productInstanceId))
                        device.ProductInstanceId = productInstanceId;
                }
            }
        }
    }

    private void ParseARecord(byte[] data, int offset, int length, IPEndPoint remoteEP, string name)
    {
        if (length != 4) return;

        try
        {
            var ip = new IPAddress(data, offset);
            Log.Debug("[mDNS] A记录: {Name} -> {IP}", name, ip);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[mDNS] A记录解析错误");
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
        catch (Exception ex)
        {
            Log.Debug(ex, "[mDNS] 获取本地主机信息失败");
        }

        return false;
    }

    private string ReadName(byte[] data, ref int offset)
    {
        var sb = new StringBuilder();
        int originalOffset = offset;

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
        var serviceType = NetworkMidi2Protocol.MDNS_SERVICE_TYPE;

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

        WriteNameToWriter(writer, serviceType);

        writer.Write((byte)0x00);
        writer.Write((byte)0x21);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);

        return ms.ToArray();
    }

    private byte[] CreateAnnouncement()
    {
        var serviceInstanceName = $"{_productInstanceId.Substring(0, 8)}-{_serviceName}";
        var fullServiceName = $"{serviceInstanceName}.{NetworkMidi2Protocol.MDNS_SERVICE_TYPE}";

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)0x00);
        writer.Write((byte)0x00);

        writer.Write((byte)0x84);
        writer.Write((byte)0x00);

        writer.Write((byte)0x00);
        writer.Write((byte)0x03);

        writer.Write((byte)0x00);
        writer.Write((byte)0x00);

        writer.Write((byte)0x00);
        writer.Write((byte)0x00);

        uint ttl = 4500;

        WriteNameToWriter(writer, fullServiceName);
        writer.Write((byte)0x00);
        writer.Write((byte)0x21);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);
        writer.Write((byte)((ttl >> 24) & 0xFF));
        writer.Write((byte)((ttl >> 16) & 0xFF));
        writer.Write((byte)((ttl >> 8) & 0xFF));
        writer.Write((byte)(ttl & 0xFF));

        var targetBytes = Encoding.UTF8.GetBytes($"{Environment.MachineName.ToLower()}.local.");
        ushort srvLen = (ushort)(2 + 2 + 2 + targetBytes.Length);
        writer.Write((byte)((srvLen >> 8) & 0xFF));
        writer.Write((byte)(srvLen & 0xFF));

        writer.Write((byte)0x00);
        writer.Write((byte)0x00);

        writer.Write((byte)((_servicePort >> 8) & 0xFF));
        writer.Write((byte)(_servicePort & 0xFF));

        WriteNameToWriter(writer, $"{Environment.MachineName.ToLower()}.local.");

        WriteNameToWriter(writer, fullServiceName);
        writer.Write((byte)0x00);
        writer.Write((byte)0x10);
        writer.Write((byte)0x00);
        writer.Write((byte)0x01);
        writer.Write((byte)((ttl >> 24) & 0xFF));
        writer.Write((byte)((ttl >> 16) & 0xFF));
        writer.Write((byte)((ttl >> 8) & 0xFF));
        writer.Write((byte)(ttl & 0xFF));

        var txtEntries = new List<string>
        {
            $"UMPEndpointName={_umpEndpointName}",
            $"ProductInstanceId={_productInstanceId}"
        };

        int txtLen = txtEntries.Sum(e => 1 + Encoding.UTF8.GetByteCount(e));
        writer.Write((byte)((txtLen >> 8) & 0xFF));
        writer.Write((byte)(txtLen & 0xFF));

        foreach (var entry in txtEntries)
        {
            var entryBytes = Encoding.UTF8.GetBytes(entry);
            writer.Write((byte)entryBytes.Length);
            writer.Write(entryBytes);
        }

        WriteNameToWriter(writer, $"{Environment.MachineName.ToLower()}.local.");
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
            if (string.IsNullOrEmpty(part)) continue;
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
                Log.Debug("[mDNS] 已发布服务: {ServiceName}", _serviceName);

                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log.Error(ex, "[mDNS] 发布错误");
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
                        Log.Information("[mDNS] 设备离线: {DeviceName}", device.UMPEndpointName);
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