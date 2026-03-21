# MidiBridge 性能优化方案

> 基于代码分析，针对热路径的深度性能优化方案

---

## 一、性能瓶颈分析

### 1. 热路径识别

MIDI 消息处理流程中，以下路径为高频调用（每秒可能数千次）：

```
MIDI In → LocalMidiService.OnMidiMessageReceived 
        → MidiRouter.RouteMessage 
        → MidiDeviceManager.SendMidiMessage 
        → RtpMidiService.SendMessage / LocalMidiService.SendMessage
        → UI Update (PulseTransmit)
```

### 2. 主要瓶颈点

| 位置 | 问题 | 影响 |
|------|------|------|
| `LocalMidiService.OnMidiMessageReceived:204-231` | 每条消息分配新 `byte[]` | GC 压力 |
| `RtpMidiService.ProcessMidiData:689` | 每包分配 `new byte[midiLength]` | GC 压力 |
| `RtpMidiService.CreateRtpMidiPacket:801-829` | 每包分配新数组 | GC 压力 |
| `RtpMidiService.SendSyncPacket:706-726` | `BitConverter.GetBytes` + `Array.Reverse` | 分配 + 计算 |
| `MidiRouter.RouteMessage:175-218` | LINQ `.Where().ToList()` | 分配 + 迭代 |
| `MidiDevice.PulseTransmit:91-96` | 每消息启动 Timer | Timer 开销 |
| `MidiRoute.PulseTransmit:95-100` | 每消息启动 Timer | Timer 开销 |
| `MidiDevice`/`MidiRoute` 构造函数 | 每实例创建 Timer | 内存占用 |
| 多处 `DateTime.Now` | 系统调用 | 开销 |

---

## 二、内存分配优化

### 1. 使用 ArrayPool<byte> 减少分配

**问题**: MIDI 消息处理频繁分配 `byte[]`

**优化**:

```csharp
// Services/LocalMidiService.cs
using System.Buffers;

private void OnMidiMessageReceived(MidiDevice device, MidiInMessageEventArgs e)
{
    device.ReceivedMessages++;
    device.LastActivity = DateTime.Now;
    device.Status = MidiDeviceStatus.Active;
    device.PulseTransmit();

    if (e.MidiEvent == null) return;

    int msg = e.MidiEvent.GetAsShortMessage();
    if (msg == 0) return;

    byte status = (byte)(msg & 0xFF);
    byte data1 = (byte)((msg >> 8) & 0xFF);
    byte data2 = (byte)((msg >> 16) & 0xFF);

    int length = GetMidiMessageLength(status);
    if (length == 0) return;

    // 从池中租用缓冲区
    byte[] buffer = ArrayPool<byte>.Shared.Rent(3);
    try
    {
        buffer[0] = status;
        if (length >= 2) buffer[1] = data1;
        if (length >= 3) buffer[2] = data2;

        // 使用 Span 避免复制
        ReadOnlySpan<byte> data = buffer.AsSpan(0, length);
        MidiDataReceived?.Invoke(this, (device, data.ToArray())); // 或修改事件签名
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }
}

private static int GetMidiMessageLength(byte status)
{
    if (status >= 0xF8) return 1;           // Real-time
    if (status >= 0xF0) return 2;           // System common (simplified)
    int command = status & 0xF0;
    return command switch
    {
        0xC0 or 0xD0 => 2,                   // Program Change, Channel Pressure
        _ => 3                               // Note On/Off, CC, Pitch Bend, etc.
    };
}
```

### 2. 预分配网络包缓冲区

**问题**: `CreateRtpMidiPacket` 每次分配新数组

**优化**:

```csharp
// Services/RtpMidi/RtpMidiService.cs
private readonly ArrayPool<byte> _packetPool = ArrayPool<byte>.Shared;
private const int MaxPacketSize = 1500; // MTU

public void SendMessage(MidiDevice device, byte[] data)
{
    // ... validation ...

    byte[] packet = _packetPool.Rent(MaxPacketSize);
    try
    {
        int packetLength = WriteRtpMidiPacket(packet, seq, timestamp, _localSSRC, data, recoveryJournalData);
        _dataServer.Send(packet, packetLength, endpoint);
    }
    finally
    {
        _packetPool.Return(packet);
    }
}

private int WriteRtpMidiPacket(byte[] buffer, ushort sequenceNumber, uint timestamp, uint ssrc, byte[] midiData, byte[]? recoveryJournal)
{
    int offset = 0;
    
    // Write header directly to buffer
    buffer[offset++] = 0x80;
    buffer[offset++] = 0x61;
    buffer[offset++] = (byte)(sequenceNumber >> 8);
    buffer[offset++] = (byte)sequenceNumber;
    
    // ... write remaining fields ...
    
    return offset + midiData.Length + (recoveryJournal?.Length ?? 0);
}
```

### 3. 缓存静态字节数组

**问题**: `Encoding.ASCII.GetBytes("MidiBridge")` 重复调用

**优化**:

```csharp
// Services/RtpMidi/RtpMidiService.cs
private static readonly byte[] s_nameBytes = Encoding.ASCII.GetBytes("MidiBridge");
private static readonly byte[] s_invitationTemplate = CreateInvitationTemplate();

private static byte[] CreateInvitationTemplate()
{
    var packet = new byte[16 + s_nameBytes.Length];
    packet[0] = 0xFF; packet[1] = 0xFF;
    packet[2] = (byte)'I'; packet[3] = (byte)'N';
    packet[4] = 0x00; packet[5] = 0x02;
    packet[10] = 0x00; packet[11] = 0x00;
    packet[12] = (byte)(s_nameBytes.Length >> 8);
    packet[13] = (byte)s_nameBytes.Length;
    packet[14] = 0x00; packet[15] = 0x00;
    Buffer.BlockCopy(s_nameBytes, 0, packet, 16, s_nameBytes.Length);
    return packet;
}
```

---

## 三、热路径代码优化

### 1. 消除 LINQ 在路由查找中

**问题**: `MidiRouter.RouteMessage` 使用 LINQ

**优化**:

```csharp
// Services/MidiRouter.cs
public void RouteMessage(MidiDevice source, byte[] data)
{
    if (data.Length < 1) return;
    if (!source.IsEnabled) return;

    byte status = data[0];
    int command = status & 0xF0;
    bool isSysEx = status == 0xF0;
    bool isSystemMessage = status >= 0xF0 && status <= 0xF7;
    bool isRealtimeMessage = status >= 0xF8;

    // 直接遍历，避免 LINQ 分配
    foreach (var kvp in _routes)
    {
        var route = kvp.Value;
        if (route.Source.Id != source.Id) continue;
        if (!route.IsEffectivelyEnabled) continue;

        // SysEx、系统消息、实时消息直接转发
        if (isSysEx || isSystemMessage || isRealtimeMessage)
        {
            _deviceManager.SendMidiMessage(route.Target, data);
        }
        else
        {
            if (!ShouldForward(route, command)) continue;
            _deviceManager.SendMidiMessage(route.Target, data);
        }

        route.TransferredMessages++;
        route.PulseTransmit();
    }
}
```

### 2. 优化路由索引

**问题**: 每次路由查找需遍历所有路由

**优化**: 添加按源设备索引

```csharp
// Services/MidiRouter.cs
private readonly ConcurrentDictionary<string, MidiRoute> _routes = new();
private readonly ConcurrentDictionary<string, List<MidiRoute>> _routesBySource = new();

public MidiRoute? CreateRoute(MidiDevice source, MidiDevice target, bool skipSave = false, bool isEnabled = true)
{
    // ... existing code ...

    // 更新索引
    var sourceRoutes = _routesBySource.GetOrAdd(source.Id, _ => new List<MidiRoute>());
    lock (sourceRoutes)
    {
        sourceRoutes.Add(route);
    }

    return route;
}

public void RouteMessage(MidiDevice source, byte[] data)
{
    if (data.Length < 1) return;
    if (!source.IsEnabled) return;
    if (!_routesBySource.TryGetValue(source.Id, out var routes)) return;

    byte status = data[0];
    int command = status & 0xF0;
    bool isSysEx = status == 0xF0;
    bool isSystemMessage = status >= 0xF0 && status <= 0xF7;
    bool isRealtimeMessage = status >= 0xF8;

    // 直接使用索引列表
    List<MidiRoute> routesSnapshot;
    lock (routes)
    {
        routesSnapshot = routes.ToList(); // 或使用不可变集合
    }

    foreach (var route in routesSnapshot)
    {
        if (!route.IsEffectivelyEnabled) continue;
        // ... routing logic ...
    }
}
```

### 3. 批量处理减少 UI 更新

**问题**: 每条消息触发 `PulseTransmit` 启动 Timer

**优化**: 使用节流/批处理

```csharp
// Models/MidiDevice.cs
// 方案 A: 使用静态共享 Timer + 时间戳检查
private static readonly Timer s_sharedTransmitTimer;
private static readonly ConcurrentDictionary<string, DateTime> s_lastPulse = new();

static MidiDevice()
{
    s_sharedTransmitTimer = new Timer(50);
    s_sharedTransmitTimer.AutoReset = true;
    s_sharedTransmitTimer.Elapsed += OnSharedTimerElapsed;
    s_sharedTransmitTimer.Start();
}

public void PulseTransmit()
{
    s_lastPulse[Id] = DateTime.UtcNow;
}

private static void OnSharedTimerElapsed(object? sender, ElapsedEventArgs e)
{
    var now = DateTime.UtcNow;
    foreach (var kvp in s_lastPulse)
    {
        if ((now - kvp.Value).TotalMilliseconds < 100)
        {
            // UI 线程更新 IsTransmitting
        }
    }
}
```

**或方案 B: 移除脉冲指示器，使用计数器更新**

```csharp
// Models/MidiDevice.cs
// 仅在计数器变化时更新 UI，使用节流
private long _receivedMessages;
private long _lastReportedMessages;
private readonly object _statsLock = new();

public long ReceivedMessages
{
    get => Interlocked.Read(ref _receivedMessages);
    set
    {
        Interlocked.Exchange(ref _receivedMessages, value);
        // 不立即触发 PropertyChanged，由后台定时器批量更新
    }
}

// ViewModel 中添加定时器，每 100ms 更新一次 UI
```

---

## 四、时间戳优化

### 1. 缓存时间戳计算

**问题**: 多处调用 `DateTime.Now`/`DateTimeOffset.UtcNow`

**优化**:

```csharp
// Services/TimestampProvider.cs
public static class TimestampProvider
{
    private static long _startTicks = DateTimeOffset.UtcNow.Ticks;
    private static long _startTimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    
    // 高精度时间戳 (用于 RTP)
    public static uint GetRtpTimestamp(int sampleRate = 44100)
    {
        long elapsed = DateTimeOffset.UtcNow.Ticks - _startTicks;
        double seconds = elapsed / (double)TimeSpan.TicksPerSecond;
        return (uint)(seconds * sampleRate);
    }
    
    // 毫秒时间戳 (用于心跳)
    public static long GetUnixTimeMilliseconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
    
    // 低精度时间戳 (用于活动检测，减少系统调用)
    private static long _cachedTicks;
    private static DateTime _cachedDateTime;
    private static readonly object _cacheLock = new();
    
    public static DateTime GetCachedNow()
    {
        long ticks = DateTimeOffset.UtcNow.Ticks;
        long cached = Interlocked.Read(ref _cachedTicks);
        
        if (ticks - cached > TimeSpan.TicksPerSecond) // 1秒刷新一次
        {
            lock (_cacheLock)
            {
                _cachedTicks = ticks;
                _cachedDateTime = DateTime.UtcNow;
            }
        }
        
        return _cachedDateTime;
    }
}
```

### 2. 避免时间戳编码中的分配

**问题**: `BitConverter.GetBytes` + `Array.Reverse`

**优化**:

```csharp
// Services/RtpMidi/RtpMidiService.cs
private void SendSyncPacket(IPEndPoint endpoint)
{
    Span<byte> packet = stackalloc byte[12];
    
    packet[0] = 0xFF;
    packet[1] = 0xFF;
    packet[2] = (byte)'C';
    packet[3] = (byte)'K';
    
    WriteBigEndianUInt32(packet.Slice(4), _localSSRC);
    WriteBigEndianInt64(packet.Slice(8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    
    _controlServer?.Send(packet.ToArray(), 12, controlEP);
}

private static void WriteBigEndianUInt32(Span<byte> buffer, uint value)
{
    buffer[0] = (byte)(value >> 24);
    buffer[1] = (byte)(value >> 16);
    buffer[2] = (byte)(value >> 8);
    buffer[3] = (byte)value;
}

private static void WriteBigEndianInt64(Span<byte> buffer, long value)
{
    buffer[0] = (byte)(value >> 56);
    buffer[1] = (byte)(value >> 48);
    buffer[2] = (byte)(value >> 40);
    buffer[3] = (byte)(value >> 32);
    buffer[4] = (byte)(value >> 24);
    buffer[5] = (byte)(value >> 16);
    buffer[6] = (byte)(value >> 8);
    buffer[7] = (byte)value;
}
```

---

## 五、UI 更新优化

### 1. 批量更新计数器

**问题**: `device.ReceivedMessages++` 每次触发 `OnPropertyChanged`

**优化**:

```csharp
// Models/MidiDevice.cs
private long _receivedMessages;
private long _uiDisplayedMessages;
private long _sentMessages;
private long _uiDisplayedSentMessages;

public long ReceivedMessages
{
    get => Interlocked.Read(ref _receivedMessages);
    set => Interlocked.Exchange(ref _receivedMessages, value);
}

public void IncrementReceived()
{
    Interlocked.Increment(ref _receivedMessages);
}

// 由 UI 定时器调用 (例如每 200ms)
public void RefreshUiProperties()
{
    long currentReceived = Interlocked.Read(ref _receivedMessages);
    long currentSent = Interlocked.Read(ref _sentMessages);
    
    if (currentReceived != _uiDisplayedMessages)
    {
        _uiDisplayedMessages = currentReceived;
        OnPropertyChanged(nameof(ReceivedMessages));
    }
    
    if (currentSent != _uiDisplayedSentMessages)
    {
        _uiDisplayedSentMessages = currentSent;
        OnPropertyChanged(nameof(SentMessages));
    }
}
```

### 2. 移除每实例 Timer

**问题**: `MidiDevice` 和 `MidiRoute` 每实例一个 Timer

**优化**:

```csharp
// 使用静态 Timer + WeakReference 管理
public static class TransmitIndicatorManager
{
    private static readonly Timer s_timer;
    private static readonly ConcurrentDictionary<object, byte> s_activeDevices = new();
    private static readonly Action<object?> s_updateAction;

    static TransmitIndicatorManager()
    {
        s_timer = new Timer(100);
        s_timer.AutoReset = true;
        s_timer.Elapsed += OnTimerElapsed;
        s_updateAction = UpdateTransmitting;
    }

    public static void Pulse(object target)
    {
        s_activeDevices.TryAdd(target, 1);
        // 在 UI 线程设置 IsTransmitting = true
        Application.Current?.Dispatcher.InvokeAsync(() => SetTransmitting(target, true), DispatcherPriority.Background);
    }

    private static void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        foreach (var kvp in s_activeDevices)
        {
            Application.Current?.Dispatcher.InvokeAsync(() => SetTransmitting(kvp.Key, false), DispatcherPriority.Background);
        }
        s_activeDevices.Clear();
    }

    private static void SetTransmitting(object target, bool value)
    {
        if (target is MidiDevice device)
            device.IsTransmitting = value;
        else if (target is MidiRoute route)
            route.IsTransmitting = value;
    }
}
```

---

## 六、网络层优化

### 1. 使用 SocketAsyncEventArgs 避免异步状态机分配

**问题**: `UdpClient.ReceiveAsync` 每次分配状态机

**优化**:

```csharp
// Services/RtpMidi/RtpMidiService.cs
private SocketAsyncEventArgs? _receiveEventArgs;
private byte[] _receiveBuffer = new byte[65536];

private void DataLoopOptimized(CancellationToken ct)
{
    _receiveEventArgs = new SocketAsyncEventArgs();
    _receiveEventArgs.SetBuffer(_receiveBuffer, 0, _receiveBuffer.Length);
    _receiveEventArgs.Completed += OnReceiveCompleted;

    StartReceive();
}

private void StartReceive()
{
    if (_cts?.IsCancellationRequested ?? true) return;
    
    if (!_dataServer!.Client.ReceiveAsync(_receiveEventArgs))
    {
        OnReceiveCompleted(null, _receiveEventArgs);
    }
}

private void OnReceiveCompleted(object? sender, SocketAsyncEventArgs e)
{
    if (e.BytesTransferred > 0 && e.RemoteEndPoint != null)
    {
        ProcessDataPacket(new ReadOnlySpan<byte>(_receiveBuffer, 0, e.BytesTransferred).ToArray(), 
            (IPEndPoint)e.RemoteEndPoint);
    }
    
    StartReceive();
}
```

### 2. 批量发送

**问题**: 高频 MIDI 消息导致大量小包发送

**优化**: 消息聚合 (可选，适用于高吞吐场景)

```csharp
// Services/MessageBatcher.cs
public class MessageBatcher
{
    private readonly List<byte[]> _pendingMessages = new();
    private readonly object _lock = new();
    private readonly int _maxBatchSize;
    private readonly int _maxBatchDelayMs;
    private Timer? _flushTimer;

    public MessageBatcher(int maxBatchSize = 10, int maxBatchDelayMs = 5)
    {
        _maxBatchSize = maxBatchSize;
        _maxBatchDelayMs = maxBatchDelayMs;
    }

    public void Add(byte[] message, Action<byte[]> sendAction)
    {
        lock (_lock)
        {
            _pendingMessages.Add(message);

            if (_pendingMessages.Count >= _maxBatchSize)
            {
                Flush(sendAction);
            }
            else if (_flushTimer == null)
            {
                _flushTimer = new Timer(_ => Flush(sendAction), null, _maxBatchDelayMs, Timeout.Infinite);
            }
        }
    }

    private void Flush(Action<byte[]> sendAction)
    {
        List<byte[]> toSend;
        lock (_lock)
        {
            toSend = _pendingMessages.ToList();
            _pendingMessages.Clear();
            _flushTimer?.Dispose();
            _flushTimer = null;
        }

        foreach (var msg in toSend)
        {
            sendAction(msg);
        }
    }
}
```

---

## 七、优化优先级

| 优先级 | 优化项 | 预期收益 | 工作量 |
|--------|--------|----------|--------|
| 🔴 高 | ArrayPool 用于热路径 byte[] | GC 减少 50%+ | 中 |
| 🔴 高 | 消除 LINQ 在 RouteMessage | 减少分配 | 低 |
| 🔴 高 | 移除每实例 Timer | 内存/CPU | 中 |
| 🟡 中 | 路由索引优化 | 查找 O(1) | 中 |
| 🟡 中 | 静态缓存字符串/模板 | 减少分配 | 低 |
| 🟡 中 | UI 计数器批量更新 | UI 流畅度 | 中 |
| 🟢 低 | SocketAsyncEventArgs | 减少异步开销 | 高 |
| 🟢 低 | 消息批处理 | 网络效率 | 中 |
| 🟢 低 | 时间戳缓存 | 减少系统调用 | 低 |

---

## 八、性能测试建议

### 1. 基准测试

```csharp
// Test/Benchmarks/MidiRoutingBenchmark.cs
using BenchmarkDotNet.Attributes;

[MemoryDiagnoser]
public class MidiRoutingBenchmark
{
    private MidiRouter _router = null!;
    private MidiDevice _source = null!;
    private MidiDevice _target = null!;
    private byte[] _midiMessage = { 0x90, 60, 100 };

    [GlobalSetup]
    public void Setup()
    {
        // Setup router and devices
    }

    [Benchmark(Baseline = true)]
    public void RouteMessage_Current()
    {
        _router.RouteMessage(_source, _midiMessage);
    }

    [Benchmark]
    public void RouteMessage_Optimized()
    {
        // Optimized version
    }
}
```

### 2. 关键指标

- **GC 暂停时间**: 应 < 1ms (Gen0)
- **消息延迟**: 应 < 1ms (本地路由)
- **吞吐量**: 应 > 1000 msg/sec
- **内存分配**: 应 < 1KB/sec (稳定状态)

---

## 九、实施步骤

### 第一阶段: 内存优化 (高优先级)

1. 引入 `ArrayPool<byte>` 到 `LocalMidiService`
2. 引入 `ArrayPool<byte>` 到 `RtpMidiService`
3. 缓存静态字节数组 (`s_nameBytes` 等)

### 第二阶段: 热路径优化 (高优先级)

1. 重写 `MidiRouter.RouteMessage` 消除 LINQ
2. 添加路由索引 `_routesBySource`
3. 使用 `Span<byte>` 优化时间戳编码

### 第三阶段: UI 优化 (中优先级)

1. 移除 `MidiDevice`/`MidiRoute` 实例 Timer
2. 实现 `TransmitIndicatorManager`
3. 计数器批量更新

### 第四阶段: 网络优化 (低优先级)

1. 评估 `SocketAsyncEventArgs` 收益
2. 消息批处理 (可选)

---

## 十、注意事项

1. **渐进式优化**: 每次修改后进行性能测试
2. **保持兼容**: 不改变外部 API
3. **避免过度优化**: 先测量，再优化
4. **线程安全**: 所有优化需考虑并发场景
5. **WPF 线程**: UI 更新必须在 Dispatcher 线程