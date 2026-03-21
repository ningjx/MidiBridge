using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using MidiBridge.Models;
using MidiBridge.Services.Interfaces;
using Serilog;

namespace MidiBridge.Services;

/// <summary>
/// MIDI 路由器实现，负责消息路由和路由管理。
/// </summary>
public class MidiRouter : IMidiRouter
{
    private readonly MidiDeviceManager _deviceManager;
    private readonly IConfigService _configService;
    private readonly ConcurrentDictionary<string, MidiRoute> _routes = new();
    private readonly ConcurrentDictionary<string, RouteConfig> _savedRoutes = new();
    private readonly ConcurrentDictionary<string, List<MidiRoute>> _routesBySource = new();
    private readonly ConcurrentDictionary<string, List<MidiRoute>> _routesByTarget = new();

    public event EventHandler<MidiRoute>? RouteAdded;
    public event EventHandler<MidiRoute>? RouteRemoved;
    public event EventHandler<MidiRoute>? RouteUpdated;

    public SafeObservableCollection<MidiRoute> Routes { get; } = new();

    ObservableCollection<MidiRoute> IMidiRouter.Routes => Routes;

    public MidiRouter(MidiDeviceManager deviceManager, IConfigService configService)
    {
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
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

        var sourceRoutes = _routesBySource.GetOrAdd(source.Id, _ => new List<MidiRoute>());
        lock (sourceRoutes)
        {
            sourceRoutes.Add(route);
        }

        var targetRoutes = _routesByTarget.GetOrAdd(target.Id, _ => new List<MidiRoute>());
        lock (targetRoutes)
        {
            targetRoutes.Add(route);
        }

        if (isEnabled)
        {
            source.HasActiveRoute = true;
            target.HasActiveRoute = true;
        }

        Routes.Add(route);

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

            if (_routesBySource.TryGetValue(route.Source.Id, out var sourceRoutes))
            {
                lock (sourceRoutes)
                {
                    sourceRoutes.Remove(route);
                }
            }

            if (_routesByTarget.TryGetValue(route.Target.Id, out var targetRoutes))
            {
                lock (targetRoutes)
                {
                    targetRoutes.Remove(route);
                }
            }

            UpdateDeviceRouteStatus(route.Source);
            UpdateDeviceRouteStatus(route.Target);

            Routes.Remove(route);

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
        bool hasRoute = false;

        if (_routesBySource.TryGetValue(device.Id, out var sourceRoutes))
        {
            lock (sourceRoutes)
            {
                foreach (var r in sourceRoutes)
                {
                    if (r.IsEnabled && IsDeviceOnline(r.Source) && IsDeviceOnline(r.Target))
                    {
                        hasRoute = true;
                        break;
                    }
                }
            }
        }

        if (!hasRoute && _routesByTarget.TryGetValue(device.Id, out var targetRoutes))
        {
            lock (targetRoutes)
            {
                foreach (var r in targetRoutes)
                {
                    if (r.IsEnabled && IsDeviceOnline(r.Source) && IsDeviceOnline(r.Target))
                    {
                        hasRoute = true;
                        break;
                    }
                }
            }
        }

        device.HasActiveRoute = hasRoute;
    }

    private bool IsDeviceOnline(MidiDevice device)
    {
        foreach (var d in _deviceManager.InputDevices)
        {
            if (d.Id == device.Id) return true;
        }
        foreach (var d in _deviceManager.OutputDevices)
        {
            if (d.Id == device.Id) return true;
        }
        return false;
    }

    public void OnDeviceDisconnected(MidiDevice device)
    {
        var devicesToUpdate = new HashSet<string>();
        devicesToUpdate.Add(device.Id);

        if (_routesBySource.TryGetValue(device.Id, out var sourceRoutes))
        {
            lock (sourceRoutes)
            {
                foreach (var route in sourceRoutes)
                {
                    devicesToUpdate.Add(route.Target.Id);
                }
            }
        }

        if (_routesByTarget.TryGetValue(device.Id, out var targetRoutes))
        {
            lock (targetRoutes)
            {
                foreach (var route in targetRoutes)
                {
                    devicesToUpdate.Add(route.Source.Id);
                }
            }
        }

        foreach (var deviceId in devicesToUpdate)
        {
            foreach (var d in _deviceManager.InputDevices)
            {
                if (d.Id == deviceId)
                {
                    UpdateDeviceRouteStatus(d);
                    break;
                }
            }
            foreach (var d in _deviceManager.OutputDevices)
            {
                if (d.Id == deviceId)
                {
                    UpdateDeviceRouteStatus(d);
                    break;
                }
            }
        }
    }

    public void RemoveRoutesForDevice(MidiDevice device, bool removeFromConfig = false)
    {
        var routeIds = new List<string>();

        if (_routesBySource.TryGetValue(device.Id, out var sourceRoutes))
        {
            lock (sourceRoutes)
            {
                foreach (var route in sourceRoutes)
                {
                    routeIds.Add(route.Id);
                }
            }
        }

        if (_routesByTarget.TryGetValue(device.Id, out var targetRoutes))
        {
            lock (targetRoutes)
            {
                foreach (var route in targetRoutes)
                {
                    routeIds.Add(route.Id);
                }
            }
        }

        foreach (var routeId in routeIds)
        {
            RemoveRoute(routeId, !removeFromConfig);
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
        if (!_routesBySource.TryGetValue(source.Id, out var routes) || routes.Count == 0) return;

        byte status = data[0];
        bool isSysEx = status == 0xF0;
        bool isSystemMessage = status >= 0xF0 && status <= 0xF7;
        bool isRealtimeMessage = status >= 0xF8;
        int command = status & 0xF0;

        MidiRoute[] routesCopy;
        lock (routes)
        {
            routesCopy = routes.ToArray();
        }

        foreach (var route in routesCopy)
        {
            if (!route.IsEffectivelyEnabled) continue;

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
        var result = new List<MidiDevice>();
        if (_routesBySource.TryGetValue(source.Id, out var routes))
        {
            lock (routes)
            {
                foreach (var route in routes)
                {
                    result.Add(route.Target);
                }
            }
        }
        return result;
    }

    public IReadOnlyList<MidiDevice> GetSourcesForTarget(MidiDevice target)
    {
        var result = new List<MidiDevice>();
        if (_routesByTarget.TryGetValue(target.Id, out var routes))
        {
            lock (routes)
            {
                foreach (var route in routes)
                {
                    result.Add(route.Source);
                }
            }
        }
        return result;
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