using MidiBridge.Models;

namespace MidiBridge.Services.Interfaces;

/// <summary>
/// 配置服务接口，负责应用程序配置的加载、保存和管理。
/// </summary>
public interface IConfigService
{
    /// <summary>
    /// 获取当前应用程序配置。
    /// </summary>
    AppConfig Config { get; }

    /// <summary>
    /// 从文件加载配置。
    /// </summary>
    /// <returns>加载的配置对象。</returns>
    AppConfig Load();

    /// <summary>
    /// 保存配置到文件。
    /// </summary>
    void Save();

    /// <summary>
    /// 保存路由配置。
    /// </summary>
    /// <param name="sourceId">源设备ID。</param>
    /// <param name="targetId">目标设备ID。</param>
    /// <param name="enabled">是否启用。</param>
    void SaveRoute(string sourceId, string targetId, bool enabled);

    /// <summary>
    /// 移除路由配置。
    /// </summary>
    /// <param name="sourceId">源设备ID。</param>
    /// <param name="targetId">目标设备ID。</param>
    void RemoveRoute(string sourceId, string targetId);

    /// <summary>
    /// 获取所有路由配置。
    /// </summary>
    /// <returns>路由配置列表。</returns>
    List<RouteConfig> GetRoutes();

    /// <summary>
    /// 检查设备是否启用。
    /// </summary>
    /// <param name="deviceId">设备ID。</param>
    /// <returns>如果设备启用返回 true。</returns>
    bool IsDeviceEnabled(string deviceId);

    /// <summary>
    /// 设置设备启用状态。
    /// </summary>
    /// <param name="deviceId">设备ID。</param>
    /// <param name="enabled">是否启用。</param>
    void SetDeviceEnabled(string deviceId, bool enabled);

    /// <summary>
    /// 获取输入设备排序顺序。
    /// </summary>
    /// <returns>设备ID列表。</returns>
    List<string> GetInputDeviceOrder();

    /// <summary>
    /// 获取输出设备排序顺序。
    /// </summary>
    /// <returns>设备ID列表。</returns>
    List<string> GetOutputDeviceOrder();

    /// <summary>
    /// 保存设备排序顺序。
    /// </summary>
    /// <param name="inputOrder">输入设备顺序。</param>
    /// <param name="outputOrder">输出设备顺序。</param>
    void SaveDeviceOrder(List<string> inputOrder, List<string> outputOrder);

    /// <summary>
    /// 更新窗口位置信息。
    /// </summary>
    void UpdateWindowPosition(double left, double top, double width, double height, bool isMaximized);

    /// <summary>
    /// 更新网络设置。
    /// </summary>
    void UpdateNetworkSettings(int rtpPort, int nm2Port, bool autoStart);
}