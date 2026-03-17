using System.Windows;
using MidiBridge.Services;
using Serilog;

namespace MidiBridge;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        LogService.Initialize();
        Log.Information("=== MidiBridge 启动 ===");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("=== MidiBridge 正在退出 ===");
        LogService.Close();
        base.OnExit(e);
    }
}

