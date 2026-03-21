using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using MidiBridge.Models;
using MidiBridge.Services;
using MidiBridge.Services.Interfaces;
using MidiBridge.ViewModels;
using Serilog;

namespace MidiBridge;

public partial class App : Application
{
    /// <summary>
    /// 获取服务提供程序。
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// 获取主视图模型。
    /// </summary>
    public static MainViewModel MainViewModel => Services.GetRequiredService<MainViewModel>();

    protected override void OnStartup(StartupEventArgs e)
    {
        LogService.Initialize();
        Log.Information("=== MidiBridge 启动 ===");

        DispatcherService.Initialize();
        TransmitIndicatorManager.Start();

        var services = new ServiceCollection();
        services.AddMidiBridgeServices();
        Services = services.BuildServiceProvider();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("=== MidiBridge 正在退出 ===");

        TransmitIndicatorManager.Stop();

        var deviceManager = Services.GetService<IMidiDeviceManager>();
        deviceManager?.Dispose();

        LogService.Close();
        base.OnExit(e);
    }
}