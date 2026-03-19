using System.Diagnostics;
using System.IO;
using Serilog;

namespace MidiBridge.Services;

public static class LogService
{
    private static readonly object _lock = new();
    private static bool _initialized;

    public static void Initialize()
    {
        lock (_lock)
        {
            if (_initialized) return;

            try
            {
                var appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MidiBridge");
                var logDir = Path.Combine(appDataDir, "logs");
                
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                var logPath = Path.Combine(logDir, "log-.log");

                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.File(
                        logPath,
                        rollingInterval: RollingInterval.Day,
                        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                        fileSizeLimitBytes: 2 * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: 5,
                        retainedFileTimeLimit: TimeSpan.FromDays(30)
                    )
                    .CreateLogger();

                _initialized = true;

                Log.Information("=== MidiBridge 启动 ===");
                Log.Information("日志目录: {LogDir}", logDir);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"日志服务初始化失败: {ex}");
            }
        }
    }

    public static void Close()
    {
        try
        {
            Log.Information("=== MidiBridge 停止 ===");
            Log.CloseAndFlush();
        }
        catch (Exception)
        {
            // 日志系统关闭时忽略错误
        }
    }
}