using Microsoft.Extensions.DependencyInjection;
using MidiBridge.Services.Interfaces;
using MidiBridge.Services.NetworkMidi2;
using MidiBridge.ViewModels;

namespace MidiBridge.Services;

/// <summary>
/// 服务集合扩展方法，用于注册所有服务。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 MidiBridge 所有的服务。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>服务集合。</returns>
    public static IServiceCollection AddMidiBridgeServices(this IServiceCollection services)
    {
        // 基础服务（单例）
        services.AddSingleton<IDispatcherService, DispatcherService>();
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<IPortChecker, PortChecker>();

        // MIDI 子系统服务
        services.AddSingleton<ILocalMidiService, LocalMidiService>();
        services.AddSingleton<IRtpMidiService, RtpMidiService>();
        services.AddSingleton<INetworkMidi2Service, NetworkMidi2Service>();
        services.AddSingleton<IMdnsDiscoveryService, MdnsDiscoveryService>();

        // MIDI 协调器
        services.AddSingleton<IMidiDeviceManager, MidiDeviceManager>();

        // ViewModel
        services.AddSingleton<MainViewModel>();

        return services;
    }
}