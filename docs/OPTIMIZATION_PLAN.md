# MidiBridge 项目优化方案

> 基于 .NET 最佳实践审视，针对架构清晰度、代码可读性、可维护性和健壮性的完整优化方案。

---

## 一、架构层面优化

### 1. 引入依赖注入 (DI)

**问题**: 当前服务直接在构造函数中实例化，难以测试和扩展。

**建议**: 使用 `Microsoft.Extensions.DependencyInjection`

```csharp
// Services/ServiceCollectionExtensions.cs
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMidiBridgeServices(this IServiceCollection services)
    {
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<IMidiDeviceManager, MidiDeviceManager>();
        services.AddSingleton<IMidiRouter, MidiRouter>();
        services.AddSingleton<INetworkMidi2Service, NetworkMidi2Service>();
        services.AddSingleton<IMdnsDiscoveryService, MdnsDiscoveryService>();
        services.AddSingleton<MainViewModel>();
        
        return services;
    }
}
```

### 2. 定义服务接口

**问题**: 服务类没有接口，违反依赖倒置原则。

**建议**:

```csharp
// Services/Interfaces/IConfigService.cs
public interface IConfigService
{
    AppConfig Config { get; }
    AppConfig Load();
    void Save();
    void SaveRoute(string sourceId, string targetId, bool enabled);
    void RemoveRoute(string sourceId, string targetId);
    bool IsDeviceEnabled(string deviceId);
    void SetDeviceEnabled(string deviceId, bool enabled);
}

// Services/Interfaces/IMidiDeviceManager.cs
public interface IMidiDeviceManager : IDisposable
{
    ObservableCollection<MidiDevice> InputDevices { get; }
    ObservableCollection<MidiDevice> OutputDevices { get; }
    bool IsRunning { get; }
    
    void ScanLocalDevices();
    bool Start(int rtpPort, int nm2Port);
    void Stop();
    bool ConnectDevice(string deviceId);
    void DisconnectDevice(string deviceId);
    void SendMidiMessage(MidiDevice target, byte[] data);
    
    event EventHandler<MidiDevice>? DeviceAdded;
    event EventHandler<MidiDevice>? DeviceRemoved;
    event EventHandler<string>? StatusChanged;
}

// Services/Interfaces/IMidiRouter.cs
public interface IMidiRouter
{
    ObservableCollection<MidiRoute> Routes { get; }
    MidiRoute? CreateRoute(MidiDevice source, MidiDevice target, bool skipSave = false, bool isEnabled = true);
    void RemoveRoute(string routeId, bool skipSave = false);
    void RouteMessage(MidiDevice source, byte[] data);
    bool HasRoute(MidiDevice source, MidiDevice target);
}
```

### 3. 拆分 MidiDeviceManager

**问题**: MidiDeviceManager 有 879 行，承担了太多职责（本地设备管理、RTP-MIDI、Network MIDI 2.0、mDNS发现）。

**建议**: 拆分为多个专注的服务

```
Services/
├── Interfaces/
│   ├── IConfigService.cs
│   ├── IMidiDeviceManager.cs
│   ├── IMidiRouter.cs
│   ├── ILocalMidiService.cs
│   ├── IRtpMidiService.cs
│   ├── INetworkMidi2Service.cs
│   └── IMdnsDiscoveryService.cs
├── LocalMidiService.cs          # 本地 MIDI 设备管理
├── RtpMidiService.cs            # RTP-MIDI 协议处理
├── NetworkMidi2/
│   ├── NetworkMidi2Service.cs   # Network MIDI 2.0
│   ├── NetworkMidi2Protocol.cs
│   └── MdnsDiscoveryService.cs
├── MidiDeviceManager.cs         # 协调器（简化版）
├── MidiRouter.cs
├── ConfigService.cs
└── PortChecker.cs
```

---

## 二、代码质量优化

### 1. 添加 XML 文档注释

**问题**: 公共 API 缺乏文档。

**建议**:

```csharp
/// <summary>
/// 管理 MIDI 设备的生命周期，包括本地设备、RTP-MIDI 和 Network MIDI 2.0 设备。
/// </summary>
public interface IMidiDeviceManager : IDisposable
{
    /// <summary>
    /// 获取输入设备集合。
    /// </summary>
    ObservableCollection<MidiDevice> InputDevices { get; }
    
    /// <summary>
    /// 启动网络 MIDI 服务。
    /// </summary>
    /// <param name="rtpPort">RTP-MIDI 控制端口。</param>
    /// <param name="nm2Port">Network MIDI 2.0 端口。</param>
    /// <returns>如果启动成功返回 true，否则返回 false。</returns>
    bool Start(int rtpPort, int nm2Port);
}
```

### 2. 消除 Models 中的 UI 依赖

**问题**: `MidiDevice` 和 `MidiRoute` 直接调用 `Application.Current.Dispatcher`，违反了 MVVM 分离原则。

**建议**: 引入 `IDispatcherService`

```csharp
// Services/Interfaces/IDispatcherService.cs
public interface IDispatcherService
{
    void Invoke(Action action);
    T Invoke<T>(Func<T> func);
}

// Services/DispatcherService.cs
public class DispatcherService : IDispatcherService
{
    public void Invoke(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            action();
        else
            System.Windows.Application.Current?.Dispatcher.Invoke(action);
    }
    
    public T Invoke<T>(Func<T> func)
    {
        return System.Windows.Application.Current?.Dispatcher.Invoke(func) ?? default!;
    }
}
```

### 3. 消除空 catch 块

**问题**: 多处使用 `catch { }` 吞掉异常。

**建议**:

```csharp
// 不好的做法
catch { }

// 好的做法
catch (Exception ex)
{
    Log.Warning(ex, "[NM2] Dispatcher 调用失败，可能应用程序正在关闭");
}
```

### 4. 使用常量替代魔法数字/字符串

**问题**: 硬编码的端口号、命令字符串等。

**建议**:

```csharp
// Constants/MidiConstants.cs
public static class MidiConstants
{
    public static class Ports
    {
        public const int DefaultRtpControlPort = 5004;
        public const int DefaultNm2Port = 5506;
    }
    
    public static class Commands
    {
        public const string Invitation = "IN";
        public const string Accept = "OK";
        public const string Bye = "BY";
        public const string Clock = "CK";
    }
    
    public static class MessageTypes
    {
        public const int NoteOn = 0x90;
        public const int NoteOff = 0x80;
        public const int ControlChange = 0xB0;
        public const int ProgramChange = 0xC0;
        public const int PitchBend = 0xE0;
    }
}
```

### 5. 添加配置验证

**问题**: AppConfig 缺乏验证。

**建议**:

```csharp
// Models/AppConfig.cs
using System.ComponentModel.DataAnnotations;

public class NetworkConfig
{
    private int _rtpPort = 5004;
    private int _nm2Port = 5506;
    
    [Range(1024, 65535, ErrorMessage = "端口必须在 1024-65535 范围内")]
    public int RtpPort 
    { 
        get => _rtpPort;
        set => _rtpPort = value;
    }
    
    [Range(1024, 65535, ErrorMessage = "端口必须在 1024-65535 范围内")]
    public int NM2Port 
    { 
        get => _nm2Port;
        set => _nm2Port = value;
    }
    
    public bool AutoStart { get; set; }
}
```

---

## 三、健壮性优化

### 1. 统一异常处理

**建议**: 添加全局异常处理器

```csharp
// App.xaml.cs
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        DispatcherUnhandledException += (s, args) =>
        {
            Log.Error(args.Exception, "[App] 未处理的 UI 异常");
            args.Handled = true;
        };
        
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            Log.Error(args.ExceptionObject as Exception, "[App] 未处理的域异常");
        };
        
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            Log.Error(args.Exception, "[App] 未观察的任务异常");
            args.SetObserved();
        };
    }
}
```

### 2. 资源管理改进

**问题**: 部分资源未正确释放。

**建议**: 使用标准的 Dispose 模式

```csharp
public class MidiDeviceManager : IDisposable
{
    private bool _disposed;
    
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        
        if (disposing)
        {
            Stop();
            
            foreach (var midiIn in _localInputs.Values)
                midiIn.Dispose();
            foreach (var midiOut in _localOutputs.Values)
                midiOut.Dispose();
                
            _localInputs.Clear();
            _localOutputs.Clear();
            _cts?.Dispose();
        }
        
        _disposed = true;
    }
}
```

### 3. 添加 CancellationToken 支持

**问题**: 异步操作缺乏取消支持。

**建议**:

```csharp
public async Task<bool> InviteDeviceAsync(string host, int port, string? name = null, CancellationToken cancellationToken = default)
{
    // 使用 cancellationToken 替代硬编码的延迟
    await Task.Delay(NetworkMidi2Protocol.INVITATION_RETRY_INTERVAL_MS, cancellationToken);
}
```

### 4. 并发与线程安全

**问题**: `ObservableCollection` 在多线程环境下不安全，`Dispatcher.Invoke` 存在死锁风险。

**建议**:

```csharp
// 使用 BindingOperations.EnableCollectionSynchronization 实现线程安全
public class MidiDeviceManager : IDisposable
{
    private readonly object _devicesLock = new();
    
    public MidiDeviceManager()
    {
        InputDevices = new ObservableCollection<MidiDevice>();
        OutputDevices = new ObservableCollection<MidiDevice>();
        
        // 启用跨线程同步
        BindingOperations.EnableCollectionSynchronization(InputDevices, _devicesLock);
        BindingOperations.EnableCollectionSynchronization(OutputDevices, _devicesLock);
    }
}

// 使用 Dispatcher.InvokeAsync 替代 Invoke 避免死锁
public void AddDevice(MidiDevice device)
{
    Application.Current?.Dispatcher.InvokeAsync(() =>
    {
        lock (_devicesLock)
        {
            if (!_devices.ContainsKey(device.Id))
            {
                _devices[device.Id] = device;
                if (device.IsInput)
                    InputDevices.Add(device);
                else
                    OutputDevices.Add(device);
            }
        }
    });
}
```

### 5. 网络健壮性

**问题**: 网络连接缺乏重连、心跳和超时机制。

**建议**:

```csharp
// Services/Network/ReconnectionPolicy.cs
public class ReconnectionPolicy
{
    public int MaxRetries { get; set; } = 5;
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);
    public double BackoffMultiplier { get; set; } = 2.0;
    
    public TimeSpan GetDelay(int retryCount)
    {
        var delay = InitialDelay * Math.Pow(BackoffMultiplier, retryCount);
        return delay > MaxDelay ? MaxDelay : delay;
    }
}

// 心跳检测
public class HeartbeatService
{
    private readonly Timer _heartbeatTimer;
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);
    private DateTime _lastResponse;
    
    public event EventHandler<TimeSpan>? ConnectionLost;
    
    public void Start()
    {
        _lastResponse = DateTime.UtcNow;
        _heartbeatTimer = new Timer(_heartbeatInterval.TotalMilliseconds);
        _heartbeatTimer.Elapsed += SendHeartbeat;
        _heartbeatTimer.Start();
    }
    
    private void SendHeartbeat(object? sender, ElapsedEventArgs e)
    {
        if (DateTime.UtcNow - _lastResponse > _timeout)
        {
            ConnectionLost?.Invoke(this, DateTime.UtcNow - _lastResponse);
        }
        // 发送心跳包...
    }
}
```

---

## 四、性能优化

### 1. 对象池减少 GC 压力

**问题**: MIDI 消息处理频繁分配 `byte[]`，造成 GC 压力。

**建议**:

```csharp
// Utils/ByteArrayPool.cs
public class ByteArrayPool
{
    private readonly ConcurrentBag<byte[]> _pool = new();
    private readonly int _bufferSize;
    
    public ByteArrayPool(int bufferSize = 1024)
    {
        _bufferSize = bufferSize;
    }
    
    public byte[] Rent()
    {
        return _pool.TryTake(out var buffer) ? buffer : new byte[_bufferSize];
    }
    
    public void Return(byte[] buffer)
    {
        if (buffer.Length == _bufferSize)
            _pool.Add(buffer);
    }
}

// 使用 ArrayPool<T> (System.Buffers)
public void ProcessMidiData(byte[] data)
{
    var buffer = ArrayPool<byte>.Shared.Rent(data.Length);
    try
    {
        Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
        // 处理数据...
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
```

### 2. 高频事件节流

**问题**: MIDI 消息频率高，UI 更新过于频繁。

**建议**:

```csharp
// Utils/ThrottleDispatcher.cs
public class ThrottleDispatcher
{
    private readonly TimeSpan _interval;
    private DateTime _lastUpdate;
    private readonly object _lock = new();
    
    public ThrottleDispatcher(TimeSpan interval)
    {
        _interval = interval;
    }
    
    public void Throttle(Action action)
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _lastUpdate >= _interval)
            {
                _lastUpdate = DateTime.UtcNow;
                action();
            }
        }
    }
}

// 在 ViewModel 中使用
private readonly ThrottleDispatcher _statusThrottler = new(TimeSpan.FromMilliseconds(100));

public void OnMidiMessageReceived(byte[] data)
{
    _statusThrottler.Throttle(() =>
    {
        MessageCount++;
        OnPropertyChanged(nameof(MessageCount));
    });
}
```

### 3. 避免热路径分配

**问题**: 字符串拼接、LINQ 在高频调用中产生大量临时对象。

**建议**:

```csharp
// 使用 StringBuilder 或 string.Create
public static string FormatMidiMessage(byte status, byte data1, byte data2)
{
    return string.Create(11, (status, data1, data2), (span, state) =>
    {
        span[0] = '[';
        state.status.TryFormat(span.Slice(1), out _, "X2");
        span[3] = ' ';
        state.data1.TryFormat(span.Slice(4), out _, "X2");
        span[6] = ' ';
        state.data2.TryFormat(span.Slice(7), out _, "X2");
        span[9] = ']';
    });
}

// 使用 ValueTask 避免异步方法同步完成时的分配
public ValueTask<bool> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
{
    if (_socket.Connected)
    {
        _socket.Send(data.Span);
        return new ValueTask<bool>(true);
    }
    return new ValueTask<bool>(ConnectAndSendAsync(data, ct));
}
```

---

## 五、可观测性

### 1. 添加 Metrics 指标

**建议**:

```csharp
// Services/Metrics/MidiMetrics.cs
public class MidiMetrics
{
    private long _messagesReceived;
    private long _messagesSent;
    private long _bytesTransferred;
    private long _errors;
    
    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);
    public long MessagesSent => Interlocked.Read(ref _messagesSent);
    public long BytesTransferred => Interlocked.Read(ref _bytesTransferred);
    public long Errors => Interlocked.Read(ref _errors);
    
    public void RecordReceived(int bytes) 
    {
        Interlocked.Increment(ref _messagesReceived);
        Interlocked.Add(ref _bytesTransferred, bytes);
    }
    
    public void RecordSent(int bytes)
    {
        Interlocked.Increment(ref _messagesSent);
        Interlocked.Add(ref _bytesTransferred, bytes);
    }
    
    public void RecordError() => Interlocked.Increment(ref _errors);
    
    public MetricsSnapshot GetSnapshot()
    {
        return new MetricsSnapshot
        {
            MessagesReceived = MessagesReceived,
            MessagesSent = MessagesSent,
            BytesTransferred = BytesTransferred,
            Errors = Errors,
            Timestamp = DateTime.UtcNow
        };
    }
}

public record MetricsSnapshot
{
    public long MessagesReceived { get; init; }
    public long MessagesSent { get; init; }
    public long BytesTransferred { get; init; }
    public long Errors { get; init; }
    public DateTime Timestamp { get; init; }
}
```

### 2. 健康检查

**建议**:

```csharp
// Services/HealthCheck.cs
public class HealthCheck
{
    private readonly MidiDeviceManager _deviceManager;
    
    public HealthCheck(MidiDeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
    }
    
    public HealthStatus Check()
    {
        var issues = new List<string>();
        
        if (!_deviceManager.IsRunning)
            issues.Add("Network service not running");
            
        if (_deviceManager.InputDevices.Count == 0)
            issues.Add("No input devices available");
            
        return new HealthStatus
        {
            IsHealthy = issues.Count == 0,
            Issues = issues,
            InputDeviceCount = _deviceManager.InputDevices.Count,
            OutputDeviceCount = _deviceManager.OutputDevices.Count,
            ActiveRouteCount = _deviceManager.Router.Routes.Count(r => r.IsEnabled)
        };
    }
}

public record HealthStatus
{
    public bool IsHealthy { get; init; }
    public List<string> Issues { get; init; } = new();
    public int InputDeviceCount { get; init; }
    public int OutputDeviceCount { get; init; }
    public int ActiveRouteCount { get; init; }
}
```

---

## 六、测试支持

### 1. 项目结构

```
MidiBridge/
├── MidiBridge/                  # 主项目
├── MidiBridge.Tests/            # 单元测试项目
│   ├── Services/
│   │   ├── MidiRouterTests.cs
│   │   ├── ConfigServiceTests.cs
│   │   └── NetworkMidi2ProtocolTests.cs
│   └── ViewModels/
│       └── MainViewModelTests.cs
└── MidiBridge.Tests.Integration/ # 集成测试
```

### 2. 示例测试

```csharp
// MidiBridge.Tests/Services/MidiRouterTests.cs
[TestClass]
public class MidiRouterTests
{
    private Mock<IMidiDeviceManager> _mockDeviceManager = null!;
    private Mock<IConfigService> _mockConfigService = null!;
    private MidiRouter _router = null!;
    
    [TestInitialize]
    public void Setup()
    {
        _mockDeviceManager = new Mock<IMidiDeviceManager>();
        _mockConfigService = new Mock<IConfigService>();
        _router = new MidiRouter(_mockDeviceManager.Object, _mockConfigService.Object);
    }
    
    [TestMethod]
    public void CreateRoute_ValidDevices_ReturnsRoute()
    {
        // Arrange
        var source = new MidiDevice { Id = "in-1", Type = MidiDeviceType.LocalInput };
        var target = new MidiDevice { Id = "out-1", Type = MidiDeviceType.LocalOutput };
        
        // Act
        var route = _router.CreateRoute(source, target);
        
        // Assert
        Assert.IsNotNull(route);
        Assert.AreEqual(source, route.Source);
        Assert.AreEqual(target, route.Target);
    }
}
```

---

## 七、重构优先级

| 优先级 | 任务 | 工作量 | 影响 |
|--------|------|--------|------|
| 🔴 高 | 定义服务接口 | 中 | 可测试性、可维护性 |
| 🔴 高 | 引入依赖注入 | 中 | 架构清晰度 |
| 🔴 高 | 拆分 MidiDeviceManager | 高 | 单一职责原则 |
| 🔴 高 | 并发与线程安全 | 中 | 稳定性、避免死锁 |
| 🟡 中 | 网络健壮性（重连、心跳） | 中 | 网络稳定性 |
| 🟡 中 | 添加 XML 文档 | 低 | 可读性 |
| 🟡 中 | 消除空 catch 块 | 低 | 调试能力 |
| 🟡 中 | 添加配置验证 | 低 | 健壮性 |
| 🟡 中 | 性能优化（对象池、节流） | 中 | 实时性能 |
| 🟢 低 | 添加单元测试 | 高 | 质量保证 |
| 🟢 低 | 消除 Models 中的 UI 依赖 | 中 | MVVM 纯度 |
| 🟢 低 | 可观测性（Metrics、健康检查） | 中 | 运维能力 |

---

## 八、重构后的目录结构

```
MidiBridge/
├── MidiBridge/
│   ├── App.xaml.cs
│   ├── MainWindow.xaml(.cs)
│   ├── AssemblyInfo.cs
│   ├── Constants/
│   │   └── MidiConstants.cs
│   ├── Models/
│   │   ├── MidiDevice.cs
│   │   ├── MidiRoute.cs
│   │   └── AppConfig.cs
│   ├── ViewModels/
│   │   ├── MainViewModel.cs
│   │   ├── ViewModelBase.cs
│   │   └── RelayCommand.cs
│   ├── Services/
│   │   ├── Interfaces/
│   │   │   ├── IConfigService.cs
│   │   │   ├── IMidiDeviceManager.cs
│   │   │   ├── IMidiRouter.cs
│   │   │   ├── ILocalMidiService.cs
│   │   │   ├── IRtpMidiService.cs
│   │   │   ├── INetworkMidi2Service.cs
│   │   │   ├── IMdnsDiscoveryService.cs
│   │   │   └── IDispatcherService.cs
│   │   ├── ConfigService.cs
│   │   ├── DispatcherService.cs
│   │   ├── LocalMidiService.cs
│   │   ├── RtpMidiService.cs
│   │   ├── MidiDeviceManager.cs
│   │   ├── MidiRouter.cs
│   │   ├── PortChecker.cs
│   │   ├── ServiceCollectionExtensions.cs
│   │   ├── Metrics/
│   │   │   ├── MidiMetrics.cs
│   │   │   └── HealthCheck.cs
│   │   └── NetworkMidi2/
│   │       ├── NetworkMidi2Service.cs
│   │       ├── NetworkMidi2Protocol.cs
│   │       └── MdnsDiscoveryService.cs
│   ├── Utils/
│   │   ├── ByteArrayPool.cs
│   │   └── ThrottleDispatcher.cs
│   ├── Converters/
│   └── Controls/
├── MidiBridge.Tests/
│   ├── Services/
│   └── ViewModels/
└── Test/                         # 现有的集成测试工具
```

---

## 九、实施步骤

### 第一阶段：接口定义与依赖注入（高优先级）

1. 创建 `Services/Interfaces/` 目录
2. 定义所有服务接口
3. 创建 `ServiceCollectionExtensions.cs`
4. 修改 `App.xaml.cs` 使用 DI 容器
5. 更新 `MainViewModel` 使用构造函数注入

### 第二阶段：服务拆分（高优先级）

1. 创建 `LocalMidiService.cs` - 提取本地 MIDI 设备管理逻辑
2. 创建 `RtpMidiService.cs` - 提取 RTP-MIDI 协议处理逻辑
3. 简化 `MidiDeviceManager.cs` 为协调器

### 第三阶段：并发安全与网络健壮性（高优先级）

1. 启用 `BindingOperations.EnableCollectionSynchronization`
2. 替换 `Dispatcher.Invoke` 为 `InvokeAsync`
3. 实现断线重连机制
4. 添加心跳检测

### 第四阶段：代码质量改进（中优先级）

1. 添加 XML 文档注释
2. 创建 `MidiConstants.cs`
3. 消除空 catch 块
4. 添加配置验证

### 第五阶段：性能优化（中优先级）

1. 引入 `ArrayPool<byte>` 减少内存分配
2. 实现高频事件节流
3. 优化热路径代码

### 第六阶段：测试与可观测性（低优先级）

1. 创建单元测试项目
2. 添加全局异常处理
3. 改进资源管理
4. 添加 Metrics 指标
5. 实现健康检查

---

## 十、注意事项

1. **渐进式重构**: 每次只改一个模块，确保编译通过后再继续
2. **保留现有功能**: 重构过程中不改变外部行为
3. **版本控制**: 每完成一个阶段提交一次
4. **测试验证**: 使用 Test 项目验证重构后的功能正确性
5. **性能基准**: 重构前后对比 MIDI 消息处理延迟和吞吐量
6. **线程安全验证**: 使用并发测试验证多线程场景下的稳定性
7. **网络测试**: 模拟网络断开、重连场景验证健壮性