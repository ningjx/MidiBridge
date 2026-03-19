using System.Collections.ObjectModel;
using MidiBridge.Models;

namespace MidiBridge.Services.Interfaces;

/// <summary>
/// MIDI 路由器接口，负责消息路由和路由管理。
/// </summary>
public interface IMidiRouter
{
    /// <summary>
    /// 获取路由集合。
    /// </summary>
    ObservableCollection<MidiRoute> Routes { get; }

    /// <summary>
    /// 创建路由。
    /// </summary>
    /// <param name="source">源设备。</param>
    /// <param name="target">目标设备。</param>
    /// <param name="skipSave">是否跳过保存。</param>
    /// <param name="isEnabled">是否启用。</param>
    /// <returns>创建的路由，如果已存在则返回 null。</returns>
    MidiRoute? CreateRoute(MidiDevice source, MidiDevice target, bool skipSave = false, bool isEnabled = true);

    /// <summary>
    /// 移除路由。
    /// </summary>
    /// <param name="routeId">路由ID。</param>
    /// <param name="skipSave">是否跳过保存。</param>
    void RemoveRoute(string routeId, bool skipSave = false);

    /// <summary>
    /// 路由 MIDI 消息。
    /// </summary>
    /// <param name="source">源设备。</param>
    /// <param name="data">MIDI 数据。</param>
    void RouteMessage(MidiDevice source, byte[] data);

    /// <summary>
    /// 启用或禁用路由。
    /// </summary>
    /// <param name="routeId">路由ID。</param>
    /// <param name="enabled">是否启用。</param>
    void EnableRoute(string routeId, bool enabled);

    /// <summary>
    /// 检查路由是否存在。
    /// </summary>
    /// <param name="source">源设备。</param>
    /// <param name="target">目标设备。</param>
    /// <returns>如果路由存在返回 true。</returns>
    bool HasRoute(MidiDevice source, MidiDevice target);

    /// <summary>
    /// 清除所有路由。
    /// </summary>
    void ClearAllRoutes();

    /// <summary>
    /// 移除与设备相关的所有路由。
    /// </summary>
    /// <param name="device">设备。</param>
    /// <param name="removeFromConfig">是否从配置中移除。</param>
    void RemoveRoutesForDevice(MidiDevice device, bool removeFromConfig = false);

    /// <summary>
    /// 获取指定源设备的所有目标设备。
    /// </summary>
    /// <param name="source">源设备。</param>
    /// <returns>目标设备列表。</returns>
    IReadOnlyList<MidiDevice> GetTargetsForSource(MidiDevice source);

    /// <summary>
    /// 获取指定目标设备的所有源设备。
    /// </summary>
    /// <param name="target">目标设备。</param>
    /// <returns>源设备列表。</returns>
    IReadOnlyList<MidiDevice> GetSourcesForTarget(MidiDevice target);

    /// <summary>
    /// 处理设备断开连接事件。
    /// </summary>
    /// <param name="device">断开连接的设备。</param>
    void OnDeviceDisconnected(MidiDevice device);

    /// <summary>
    /// 尝试恢复设备的路由。
    /// </summary>
    /// <param name="device">设备。</param>
    void TryRestoreRoutesForDevice(MidiDevice device);

    /// <summary>
    /// 尝试恢复所有路由。
    /// </summary>
    void TryRestoreAllRoutes();

    /// <summary>
    /// 检查路由是否已保存。
    /// </summary>
    /// <param name="sourceId">源设备ID。</param>
    /// <param name="targetId">目标设备ID。</param>
    /// <returns>如果路由已保存返回 true。</returns>
    bool HasSavedRoute(string sourceId, string targetId);

    /// <summary>
    /// 路由添加事件。
    /// </summary>
    event EventHandler<MidiRoute>? RouteAdded;

    /// <summary>
    /// 路由移除事件。
    /// </summary>
    event EventHandler<MidiRoute>? RouteRemoved;

    /// <summary>
    /// 路由更新事件。
    /// </summary>
    event EventHandler<MidiRoute>? RouteUpdated;
}