using System.IO;
using System.Text.Json;
using MidiBridge.Models;
using MidiBridge.Services.Interfaces;
using Serilog;

namespace MidiBridge.Services;

/// <summary>
/// 配置服务实现，负责应用程序配置的加载、保存和管理。
/// </summary>
public class ConfigService : IConfigService
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MidiBridge");
    
    private static readonly string ConfigPath = Path.Combine(AppDataDir, "config.json");

    private AppConfig _config = new();
    public AppConfig Config => _config;

    public AppConfig Load()
    {
        try
        {
            if (!Directory.Exists(AppDataDir))
            {
                Directory.CreateDirectory(AppDataDir);
            }
            
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                _config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                _config.Routes ??= new List<RouteConfig>();
                Log.Information("[Config] 已加载: RTP={RtpPort}, NM2={NM2Port}, 自动启动={AutoStart}, 路由={RouteCount}",
                    _config.Network.RtpPort, _config.Network.NM2Port, _config.Network.AutoStart, _config.Routes.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Config] 加载失败，使用默认配置");
            _config = new AppConfig();
        }

        return _config;
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(ConfigPath, json);
            Log.Debug("[Config] 已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Config] 保存失败");
        }
    }

    public void UpdateWindowPosition(double left, double top, double width, double height, bool isMaximized)
    {
        _config.Window.Left = left;
        _config.Window.Top = top;
        _config.Window.Width = width;
        _config.Window.Height = height;
        _config.Window.IsMaximized = isMaximized;
    }

    public void UpdateNetworkSettings(int rtpPort, int nm2Port, bool autoStart)
    {
        _config.Network.RtpPort = rtpPort;
        _config.Network.NM2Port = nm2Port;
        _config.Network.AutoStart = autoStart;
    }

    public List<RouteConfig> GetRoutes()
    {
        return _config.Routes;
    }

    public void SaveRoute(string sourceId, string targetId, bool enabled)
    {
        var existing = _config.Routes.FirstOrDefault(r => r.SourceId == sourceId && r.TargetId == targetId);
        if (existing != null)
        {
            existing.IsEnabled = enabled;
        }
        else
        {
            _config.Routes.Add(new RouteConfig
            {
                SourceId = sourceId,
                TargetId = targetId,
                IsEnabled = enabled
            });
        }
        Save();
    }

    public void RemoveRoute(string sourceId, string targetId)
    {
        var route = _config.Routes.FirstOrDefault(r => r.SourceId == sourceId && r.TargetId == targetId);
        if (route != null)
        {
            _config.Routes.Remove(route);
            Save();
        }
    }

    public List<string> GetInputDeviceOrder() => _config.InputDeviceOrder ?? new List<string>();
    public List<string> GetOutputDeviceOrder() => _config.OutputDeviceOrder ?? new List<string>();

    public void SaveDeviceOrder(List<string> inputOrder, List<string> outputOrder)
    {
        _config.InputDeviceOrder = inputOrder;
        _config.OutputDeviceOrder = outputOrder;
        Save();
    }

    public List<string> GetDisabledDevices() => _config.DisabledDevices ?? new List<string>();

    public void SetDeviceEnabled(string deviceId, bool enabled)
    {
        _config.DisabledDevices ??= new List<string>();
        
        if (!enabled && !_config.DisabledDevices.Contains(deviceId))
        {
            _config.DisabledDevices.Add(deviceId);
        }
        else if (enabled && _config.DisabledDevices.Contains(deviceId))
        {
            _config.DisabledDevices.Remove(deviceId);
        }
        Save();
    }

    public bool IsDeviceEnabled(string deviceId)
    {
        return !(_config.DisabledDevices?.Contains(deviceId) ?? false);
    }
}