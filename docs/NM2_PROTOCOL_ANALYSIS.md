# Network MIDI 2.0 协议支持分析报告

> 基于项目代码与 NM2 规范文档对比分析

---

## 一、已实现功能 ✅

| 功能 | 状态 | 说明 |
|------|------|------|
| **UDP 包结构** | ✅ | Signature (0x4D494449) + 多命令打包 |
| **Invitation 流程** | ✅ | Invitation → InvitationReplyAccepted |
| **认证流程** | ✅ | SharedSecret / UserAuth (SHA-256) |
| **Ping/Pong** | ✅ | 心跳检测，RTT 统计 |
| **UMP Data 传输** | ✅ | 序列号管理，发送/接收 |
| **Bye 流程** | ✅ | Bye + ByeReply |
| **Session Reset** | ✅ | SessionReset + SessionResetReply |
| **NAK 响应** | ✅ | 错误通知 |
| **重传机制** | ✅ | RetransmitRequest + RetransmitError |
| **FEC 前向纠错** | ✅ | 冗余发送，自动恢复 |
| **丢包检测** | ✅ | 序列号检查，统计 |
| **会话超时** | ✅ | Ping 超时 + 活动超时 |
| **mDNS 发现** | ✅ | `_midi2._udp.local` 服务发布 |

---

## 二、未实现/不完整功能 ❌

### 1. Invitation Reply 拒绝类型

**规范要求**: 应支持多种拒绝原因

```
InvitationReplyRejected (0x14) - 拒绝邀请
InvitationReplyTooManySessions (0x15) - 会话数过多
```

**当前实现**: 只实现了 Accepted/Pending/AuthRequired/UserAuthRequired

### 2. MIDI 2.0 完整 UMP 支持

**规范要求**: 支持所有 UMP Message Type (0x0-0xF)

**当前实现**: 只支持 Message Type 0x2 (MIDI 1.0) 和部分 0x4 (Note On/Off 64-bit)

```csharp
// NetworkMidi2Service.cs:1401-1424
private byte[] ConvertMidi1ToUMP(byte[] midiData)
{
    if (status >= 0xF0) return Array.Empty<byte>();  // ❌ 系统消息不支持
    int messageType = 0x2;  // ❌ 只用 MIDI 1.0 格式
    ...
}
```

**缺失的 UMP 类型**:
- 0x0: Misc (Jitter Reduction Clock, Delta Time)
- 0x1: System Common/DIY
- 0x3: System Exclusive (7-bit/8-bit)
- 0x5: Note On/Off (128-bit, with attribute)
- 0x6: Per-Note Controller
- 0x7: Registered/Assignable Per-Note Controller
- 0x8-0xF: Flex Data, Stream, etc.

### 3. MIDI-CI 协议

**规范要求**: 支持 MIDI Capability Inquiry (M2-101)

**当前实现**: 未实现任何 MIDI-CI 功能
- Profile Configuration
- Property Exchange
- Process Inquiry

### 4. Jitter Reduction

**规范要求**: 支持 JR Clock 同步 (M2-104)

**当前实现**: 未实现

```csharp
// 缺失: UMP Message Type 0x0
// JR Clock (0x00), JR Timestamp (0x10)
```

### 5. SysEx 支持

**规范要求**: 支持 System Exclusive (UMP Type 0x3)

**当前实现**:

```csharp
// NetworkMidi2Service.cs:1408
if (status >= 0xF0) return Array.Empty<byte>();  // ❌ 直接丢弃
```

### 6. 功能块 (Function Block) 配置

**规范要求**: 支持 Function Block Profile (M2-118)

**当前实现**: 未实现

### 7. 流控制

**规范要求**: 应支持更精细的流量控制

**当前实现**: 有 Idle Period 机制，但缺少：
- 接收端缓冲区状态反馈
- 动态速率调整

---

## 三、建议优先级

| 优先级 | 功能 | 工作量 | 影响 |
|--------|------|--------|------|
| 🔴 高 | SysEx 支持 | 中 | 很多设备需要 |
| 🔴 高 | 完整 UMP Type 支持 | 高 | MIDI 2.0 核心功能 |
| 🟡 中 | Invitation Rejection | 低 | 协议完整性 |
| 🟡 中 | MIDI-CI | 高 | 设备互操作性 |
| 🟢 低 | Jitter Reduction | 中 | 低延迟场景 |
| 🟢 低 | Function Block | 中 | 复杂设备配置 |

---

## 四、功能说明

### MIDI-CI (Capability Inquiry)

**用途**: 设备之间互相"询问"对方能做什么

```
设备A: "你好，我能发送 Note On/Off、Control Change"
设备B: "我能接收这些，还支持 MPE（多维复音表情）"
设备A: "好的，那我开启 MPE 模式"
```

**实际场景**:
- 自动配置最佳工作模式
- 发现设备支持的功能（Profile）
- 交换设备属性（音色名称、参数范围）

**对 MidiBridge 是否必要**: ❌ 不必要。路由器只转发数据，不配置设备。

### Jitter Reduction (抖动减少)

**用途**: 精确时间戳同步，减少网络延迟抖动

```
没有 JR:
  MIDI消息 → 网络延迟波动 → 时间不均匀 → 节奏不稳

有 JR:
  发送方带精确时间戳 → 接收方按时间戳播放 → 节奏稳定
```

**实际场景**:
- 专业现场演出
- 多设备同步录音

**对 MidiBridge 是否必要**: ⚠️ 可选。对基础路由器非必需，但对低延迟场景有帮助。

### SysEx (System Exclusive)

**用途**: 传输厂商自定义数据（音色、固件、设置）

```
标准 MIDI: Note On/Off、CC 等（固定格式）
SysEx: F0 厂商ID 数据... F7（任意长度，任意内容）
```

**实际场景**:
- 传输音色文件
- 同步设备设置
- 固件升级
- 与特定设备深度交互

**对 MidiBridge 是否必要**: ⚠️ 建议支持。很多专业设备需要 SysEx 通信。

### Function Block (功能块)

**用途**: 描述设备的输入/输出端口结构

```
设备: "我有 2 个输入块、1 个输出块"
块1: "键盘输入，支持 Note/CC/Aftertouch"
块2: "踏板输入，只支持 CC"
输出块: "合成器输出"
```

**实际场景**:
- 复杂设备有多个端口（如主键盘 + 踏板 + 推子）
- 让宿主软件知道每个端口的功能

**对 MidiBridge 是否必要**: ❌ 不必要。路由器只看设备级别，不管理设备内部结构。

---

## 五、总结

**当前项目 NM2 实现成熟度**: 约 **70%**

**主要优点**:
- 核心会话管理完善
- 认证机制完整
- 重传和 FEC 工作正常
- 统计功能齐全

**主要缺陷**:
- UMP 支持不完整（只支持 MIDI 1.0 兼容模式）
- SysEx 不支持
- MIDI-CI 未实现

**结论**: 对于基本 MIDI 路由功能，当前实现已足够。但若需支持完整 MIDI 2.0 设备（如高级控制器、合成器），需要补充 UMP 和 SysEx 支持。

---

## 六、参考文档

| 文档 | 说明 |
|------|------|
| M2-124-UM | Network MIDI 2.0 UDP 协议规范 |
| M2-104-UM | UMP and MIDI 2.0 Protocol Specification |
| M2-101-UM | MIDI-CI Specification |
| M2-118-UM | Function Block Profile |