# MidiBridge

一个将 **MIDI 2.0 Network** 和 **RTP-MIDI** 协议的网络 MIDI 设备桥接到本地 MIDI 端口的 Windows 桌面应用程序。

## 功能特性

- **RTP-MIDI 协议支持** - 兼容 Apple Network MIDI 标准
- **Network MIDI 2.0 支持** - 实现最新的 MIDI 2.0 网络协议
- **本地 MIDI 桥接** - 网络设备与本地物理/虚拟 MIDI 端口双向通信
- **自动设备发现** - 通过 mDNS/Bonjour 自动发现网络 MIDI 设备
- **可视化路由管理** - 直观的拖拽式 MIDI 路由配置
- **消息过滤** - 支持 MIDI 消息类型过滤(暂不支持)
- **配置持久化** - 自动保存路由配置和设备状态

## 系统要求

- Windows 10/11
- .NET 8.0 Runtime

## 架构概览

```
┌─────────────────────────────────────────────────────────────────────┐
│                        MidiBridge Application                       │
├─────────────────────────────────────────────────────────────────────┤
│  UI Layer           │  ViewModel Layer    │  Models Layer           │
│  (MainWindow.xaml)   │  (MainViewModel)    │  (MidiDevice, Route)   │
├─────────────────────────────────────────────────────────────────────┤
│                         Services Layer                              │
├──────────────┬──────────────┬──────────────┬────────────────────────┤
│ MidiDevice   │ MidiRouter   │ ConfigService│ LogService             │
│ Manager      │              │              │ (Serilog)              │
├──────────────┴──────────────┴──────────────┴────────────────────────┤
│  RTP-MIDI Service  │  Network MIDI 2.0  │  mDNS Discovery           │
│  (UDP 5004/5005)   │  (UDP 5506)         │  (224.0.0.251:5353)      │
└─────────────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────┴───────────────────────────────────────┐
│  Local MIDI (NAudio)  │  RTP-MIDI Devices  │  Network MIDI 2.0      │
│  Physical/Virtual     │  Apple Network     │  UMP Protocol          │
└─────────────────────────────────────────────────────────────────────┘
```

## 核心组件

### 协议实现

| 协议 | 端口 | 说明 |
|------|------|------|
| RTP-MIDI | UDP 5004 (控制) / 5005 (数据) | Apple Network MIDI 标准 |
| Network MIDI 2.0 | UDP 5506 | MIDI 2.0 网络协议，支持 UMP |
| mDNS Discovery | UDP 5353 | 自动发现 `_midi2._udp` 服务 |

### MIDI 消息路由流程

```
[源设备]                    [路由决策]                    [目标设备]
   │                            │                            │
   ▼                            ▼                            ▼
本地输入 ─────┐                          ┌─────► 本地输出
(RTP设备) ───┼──► MidiRouter ───────────┼─────► (RTP设备)
(网络MIDI2) ─┘   1. 检查路由是否存在      └─────► (网络MIDI2)
                  2. 检查路由是否启用
                  3. 应用消息过滤器
                  4. 分发到所有匹配目标
```

### UMP (Universal MIDI Packet) 支持

| 类型 | 大小 | 说明 |
|------|------|------|
| 0x2 | 4 字节 | MIDI 1.0 Channel Voice (32-bit) |
| 0x4 | 8 字节 | MIDI 2.0 Channel Voice (64-bit) |

项目实现了完整的 UMP 与 MIDI 1.0 消息格式转换。

## 项目结构

```
MidiBridge/
├── Models/
│   ├── MidiDevice.cs       # MIDI 设备模型
│   ├── MidiRoute.cs        # 路由配置模型
│   └── AppConfig.cs        # 应用配置模型
├── ViewModels/
│   ├── MainViewModel.cs    # 主视图模型
│   ├── ViewModelBase.cs    # MVVM 基类
│   └── RelayCommand.cs     # 命令实现
├── Services/
│   ├── MidiDeviceManager.cs    # 设备管理核心
│   ├── MidiRouter.cs           # 路由引擎
│   ├── ConfigService.cs        # 配置持久化
│   ├── LogService.cs           # 日志服务
│   ├── PortChecker.cs          # 端口检测
│   └── NetworkMidi2/
│       ├── NetworkMidi2Service.cs    # MIDI 2.0 网络服务
│       ├── NetworkMidi2Protocol.cs   # 协议定义
│       └── MdnsDiscoveryService.cs    # mDNS 发现服务
├── Converters/              # UI 值转换器
├── Controls/                # 自定义控件
└── MainWindow.xaml          # 主界面
```

## 配置

配置文件位置: `%LocalAppData%\MidiBridge\config.json`

```json
{
  "Window": {
    "Left": 100,
    "Top": 100,
    "Width": 900,
    "Height": 600,
    "IsMaximized": false
  },
  "Network": {
    "RtpPort": 5004,
    "NM2Port": 5506,
    "AutoStart": false
  },
  "Routes": [
    {
      "SourceId": "local-in-0",
      "TargetId": "nm2-device@192.168.1.5",
      "IsEnabled": true
    }
  ],
  "InputDeviceOrder": ["local-in-0"],
  "OutputDeviceOrder": ["local-out-0"],
  "DisabledDevices": []
}
```

## 日志

日志文件位置: `%LocalAppData%\MidiBridge\logs\`

使用 Serilog 进行结构化日志记录，支持滚动日志文件。

## 依赖项

| 包 | 版本 | 用途 |
|---|------|------|
| NAudio | 2.3.0 | 本地 MIDI 设备访问 |
| Serilog.Sinks.File | 7.0.0 | 文件日志记录 |

## 构建与运行

### 开发环境

```bash
# 克隆仓库
git clone https://github.com/your-repo/MidiBridge.git
cd MidiBridge

# 构建项目
dotnet build

# 运行项目
dotnet run --project MidiBridge
```

### 发布版本

```bash
dotnet publish MidiBridge/MidiBridge.csproj -c Release -r win-x64 --self-contained
```

发布产物位于 `MidiBridge/bin/Release/net8.0-windows/win-x64/publish/`

## 测试项目

`Test` 目录包含完整的测试工具：

- **本地 MIDI 设备测试** - 列出和监控本地 MIDI 设备
- **RTP-MIDI 服务器模拟** - 模拟 RTP-MIDI 服务器
- **RTP-MIDI 钢琴** - 键盘转 MIDI 通过 RTP-MIDI 发送
- **Network MIDI 2.0 客户端** - 完整会话测试
- **端口占用测试** - 验证端口冲突检测
- **多设备发现测试** - 创建多个可发现的 NM2 设备

## 协议细节

### RTP-MIDI 会话流程

```
远程设备                    MidiBridge
   │                          │
   │──── IN 包 ──────────────►│  (连接请求)
   │◄─── OK 包 ────────────── │  (接受)
   │                          │
   │──── RTP MIDI 数据 ─────► │  (MIDI 消息)
   │◄─── CK 响应 ──────────── │  (同步响应)
   │                          │
   │──── BY 包 ─────────────► │  (断开)
```

### Network MIDI 2.0 会话命令

| 命令 | 代码 | 说明 |
|------|------|------|
| Invitation | 0x01 | 会话建立 |
| EndSession | 0x02 | 会话终止 |
| Ping | 0x03 | 心跳保活 |
| UMPData | 0x10 | UMP 数据传输 |

## 许可证

[MIT License](LICENSE.txt)