using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using MidiBridge.Models;
using Serilog;

namespace MidiBridge.Services;

public class MidiRouter
{
    private readonly MidiDeviceManager _deviceManager;
    private readonly ConfigService _configService;
    private readonly ConcurrentDictionary<string, MidiRoute> _routes = new();
    private readonly ConcurrentDictionary<string, RouteConfig> _savedRoutes = new();

    public event EventHandler<MidiRoute>? RouteAdded;
    public event EventHandler<MidiRoute>? RouteRemoved;
    public event EventHandler<MidiRoute>? RouteUpdated;

    public ObservableCollection<MidiRoute> Routes { get; } = new();

    public MidiRouter(MidiDeviceManager deviceManager, ConfigService configService)
    {
        _deviceManager = deviceManager;
        _configService = configService;
        LoadSavedRoutes();
    }

    public MidiRoute? CreateRoute(MidiDevice source, MidiDevice target, bool skipSave = false, bool isEnabled = true)
    {
        if (!source.IsInput || !target.IsOutput) return null;

        string routeId = $"route-{source.Id}-{target.Id}";

        if (_routes.ContainsKey(routeId)) return null;

        var route = new MidiRoute
        {
            Id = routeId,
            Source = source,
            Target = target,
            IsEnabled = isEnabled
        };

        route.PropertyChanged += Route_PropertyChanged;

        _routes[routeId] = route;

        if (isEnabled)
        {
            source.HasActiveRoute = true;
            target.HasActiveRoute = true;
        }

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            Routes.Add(route);
        });

        RouteAdded?.Invoke(this, route);

        if (!skipSave)
        {
            SaveRouteToConfig(source.Id, target.Id, isEnabled);
        }

        return route;
    }

    public void RemoveRoute(string routeId, bool skipSave = false)
    {
        if (_routes.TryRemove(routeId, out var route))
        {
            route.PropertyChanged -= Route_PropertyChanged;
            
            UpdateDeviceRouteStatus(route.Source);
            UpdateDeviceRouteStatus(route.Target);

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                Routes.Remove(route);
            });

            RouteRemoved?.Invoke(this, route);

            if (!skipSave)
            {
                RemoveRouteFromConfig(route.Source.Id, route.Target.Id);
            }
        }
    }

    private void Route_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MidiRoute.IsEnabled) && sender is MidiRoute route)
        {
            UpdateDeviceRouteStatus(route.Source);
            UpdateDeviceRouteStatus(route.Target);
        }
    }

    private void UpdateDeviceRouteStatus(MidiDevice device)
    {
        bool hasRoute = _routes.Values.Any(r => 
            r.IsEnabled && 
            (r.Source.Id == device.Id || r.Target.Id == device.Id) &&
            IsDeviceOnline(r.Source) && 
            IsDeviceOnline(r.Target));
        device.HasActiveRoute = hasRoute;
    }

    private bool IsDeviceOnline(MidiDevice device)
    {
        return _deviceManager.InputDevices.Any(d => d.Id == device.Id) ||
               _deviceManager.OutputDevices.Any(d => d.Id == device.Id);
    }

    public void OnDeviceDisconnected(MidiDevice device)
    {
        var affectedRoutes = _routes.Values
            .Where(r => r.Source.Id == device.Id || r.Target.Id == device.Id)
            .ToList();

        var devicesToUpdate = new HashSet<string>();
        foreach (var route in affectedRoutes)
        {
            devicesToUpdate.Add(route.Source.Id);
            devicesToUpdate.Add(route.Target.Id);
        }

        foreach (var deviceId in devicesToUpdate)
        {
            var inputDevice = _deviceManager.InputDevices.FirstOrDefault(d => d.Id == deviceId);
            if (inputDevice != null)
            {
                UpdateDeviceRouteStatus(inputDevice);
            }
            var outputDevice = _deviceManager.OutputDevices.FirstOrDefault(d => d.Id == deviceId);
            if (outputDevice != null)
            {
                UpdateDeviceRouteStatus(outputDevice);
            }
        }
    }

    public void RemoveRoutesForDevice(MidiDevice device, bool removeFromConfig = false)
    {
        var routesToRemove = _routes.Values
            .Where(r => r.Source.Id == device.Id || r.Target.Id == device.Id)
            .ToList();

        foreach (var route in routesToRemove)
        {
            RemoveRoute(route.Id, !removeFromConfig);
        }
    }

    public void EnableRoute(string routeId, bool enabled)
    {
        if (_routes.TryGetValue(routeId, out var route))
        {
            route.IsEnabled = enabled;
            
            string key = $"{route.Source.Id}|{route.Target.Id}";
            if (_savedRoutes.TryGetValue(key, out var savedRoute))
            {
                savedRoute.IsEnabled = enabled;
                _configService.SaveRoute(route.Source.Id, route.Target.Id, enabled);
            }
            
            RouteUpdated?.Invoke(this, route);
        }
    }

    public void RouteMessage(MidiDevice source, byte[] data)
    {
        if (data.Length < 1) return;
        if (!source.IsEnabled) return;

        byte status = data[0];
        
        // 判断消息类型
        bool isSysEx = (status == 0xF0);
        bool isSystemMessage = (status >= 0xF0 && status <= 0xF7);
        bool isRealtimeMessage = (status >= 0xF8);
        
        var matchingRoutes = _routes.Values
            .Where(r => r.Source.Id == source.Id && r.IsEffectivelyEnabled)
            .ToList();

        foreach (var route in matchingRoutes)
        {
            // SysEx、系统消息、实时消息直接转发，不做过滤
            if (isSysEx || isSystemMessage || isRealtimeMessage)
            {
                if (data.Length <= 3)
                {
                    _deviceManager.SendMidiMessage(route.Target, data);
                }
                else
                {
                    _deviceManager.SendMidiBuffer(route.Target, data);
                }
            }
            else
            {
                // 通道消息，检查过滤
                int command = status & 0xF0;
                if (!ShouldForward(route, command)) continue;
                
                _deviceManager.SendMidiMessage(route.Target, data);
            }

            route.TransferredMessages++;
            route.PulseTransmit();
            RouteUpdated?.Invoke(this, route);
        }
    }

    private bool ShouldForward(MidiRoute route, int command)
    {
        return command switch
        {
            0x90 => route.FilterNoteOn,
            0x80 => route.FilterNoteOff,
            0xB0 => route.FilterControlChange,
            0xC0 => route.FilterProgramChange,
            0xE0 => route.FilterPitchBend,
            0xA0 or 0xD0 => route.FilterAftertouch,
            _ => true
        };
    }

    public IReadOnlyList<MidiDevice> GetTargetsForSource(MidiDevice source)
    {
        return _routes.Values
            .Where(r => r.Source.Id == source.Id)
            .Select(r => r.Target)
            .ToList();
    }

    public IReadOnlyList<MidiDevice> GetSourcesForTarget(MidiDevice target)
    {
        return _routes.Values
            .Where(r => r.Target.Id == target.Id)
            .Select(r => r.Source)
            .ToList();
    }

    public bool HasRoute(MidiDevice source, MidiDevice target)
    {
        string routeId = $"route-{source.Id}-{target.Id}";
        return _routes.ContainsKey(routeId);
    }

    public void ClearAllRoutes()
    {
        foreach (var routeId in _routes.Keys.ToList())
        {
            RemoveRoute(routeId);
        }
    }

    private void SaveRouteToConfig(string sourceId, string targetId, bool enabled)
    {
        string key = $"{sourceId}|{targetId}";
        _savedRoutes[key] = new RouteConfig
        {
            SourceId = sourceId,
            TargetId = targetId,
            IsEnabled = enabled
        };
        _configService.SaveRoute(sourceId, targetId, enabled);
        Log.Debug("[MidiRouter] 已保存路由: {Source} -> {Target}", sourceId, targetId);
    }

    private void RemoveRouteFromConfig(string sourceId, string targetId)
    {
        string key = $"{sourceId}|{targetId}";
        _savedRoutes.TryRemove(key, out _);
        _configService.RemoveRoute(sourceId, targetId);
    }

    private void LoadSavedRoutes()
    {
        try
        {
            var routesList = _configService.GetRoutes();
            if (routesList == null || routesList.Count == 0) return;

            foreach (var route in routesList)
            {
                string key = $"{route.SourceId}|{route.TargetId}";
                _savedRoutes[key] = route;
            }

            Log.Information("[MidiRouter] 从配置文件加载了 {Count} 条保存的路由", routesList.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MidiRouter] 加载路由配置失败");
        }
    }

    public void TryRestoreRoutesForDevice(MidiDevice device)
    {
        var routesToRestore = _savedRoutes.Values
            .Where(r => r.SourceId == device.Id || r.TargetId == device.Id)
            .ToList();

        foreach (var savedRoute in routesToRestore)
        {
            var source = _deviceManager.InputDevices.FirstOrDefault(d => d.Id == savedRoute.SourceId);
            var target = _deviceManager.OutputDevices.FirstOrDefault(d => d.Id == savedRoute.TargetId);

            if (source != null && target != null)
            {
                if (source.Type == MidiDeviceType.LocalInput && source.Status != MidiDeviceStatus.Connected)
                {
                    _deviceManager.ConnectDevice(source.Id);
                    Log.Information("[MidiRouter] 已自动连接输入设备: {Name}", source.Name);
                }
                if (target.Type == MidiDeviceType.LocalOutput && target.Status != MidiDeviceStatus.Connected)
                {
                    _deviceManager.ConnectDevice(target.Id);
                    Log.Information("[MidiRouter] 已自动连接输出设备: {Name}", target.Name);
                }

                string routeId = $"route-{source.Id}-{target.Id}";
                if (_routes.TryGetValue(routeId, out var existingRoute))
                {
                    existingRoute.Source = source;
                    existingRoute.Target = target;
                    existingRoute.IsEnabled = savedRoute.IsEnabled;
                    if (savedRoute.IsEnabled)
                    {
                        source.HasActiveRoute = true;
                        target.HasActiveRoute = true;
                    }
                    Log.Information("[MidiRouter] 已更新路由引用: {Source} -> {Target} ({Status})", 
                        source.Name, target.Name, savedRoute.IsEnabled ? "启用" : "禁用");
                }
                else
                {
                    CreateRoute(source, target, skipSave: true, isEnabled: savedRoute.IsEnabled);
                    Log.Information("[MidiRouter] 已恢复路由: {Source} -> {Target} ({Status})", 
                        source.Name, target.Name, savedRoute.IsEnabled ? "启用" : "禁用");
                }
            }
        }
    }

    public bool HasSavedRoute(string sourceId, string targetId)
    {
        string key = $"{sourceId}|{targetId}";
        return _savedRoutes.ContainsKey(key);
    }

    public void TryRestoreAllRoutes()
    {
        foreach (var savedRoute in _savedRoutes.Values.ToList())
        {
            var source = _deviceManager.InputDevices.FirstOrDefault(d => d.Id == savedRoute.SourceId);
            var target = _deviceManager.OutputDevices.FirstOrDefault(d => d.Id == savedRoute.TargetId);

            if (source != null && target != null)
            {
                if (source.Type == MidiDeviceType.LocalInput && source.Status != MidiDeviceStatus.Connected)
                {
                    _deviceManager.ConnectDevice(source.Id);
                    Log.Information("[MidiRouter] 已自动连接输入设备: {Name}", source.Name);
                }
                if (target.Type == MidiDeviceType.LocalOutput && target.Status != MidiDeviceStatus.Connected)
                {
                    _deviceManager.ConnectDevice(target.Id);
                    Log.Information("[MidiRouter] 已自动连接输出设备: {Name}", target.Name);
                }

                string routeId = $"route-{source.Id}-{target.Id}";
                if (_routes.TryGetValue(routeId, out var existingRoute))
                {
                    existingRoute.Source = source;
                    existingRoute.Target = target;
                    existingRoute.IsEnabled = savedRoute.IsEnabled;
                    if (savedRoute.IsEnabled)
                    {
                        source.HasActiveRoute = true;
                        target.HasActiveRoute = true;
                    }
                    Log.Information("[MidiRouter] 已更新路由引用: {Source} -> {Target} ({Status})", 
                        source.Name, target.Name, savedRoute.IsEnabled ? "启用" : "禁用");
                }
                else
                {
                    CreateRoute(source, target, skipSave: true, isEnabled: savedRoute.IsEnabled);
                    Log.Information("[MidiRouter] 已恢复路由: {Source} -> {Target} ({Status})", 
                        source.Name, target.Name, savedRoute.IsEnabled ? "启用" : "禁用");
                }
            }
        }
    }
}