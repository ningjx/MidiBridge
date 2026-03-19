using System.Windows;
using System.Windows.Threading;
using MidiBridge.Services.Interfaces;

namespace MidiBridge.Services;

/// <summary>
/// 调度器服务实现，用于在 UI 线程上执行操作。
/// </summary>
public class DispatcherService : IDispatcherService
{
    private readonly Dispatcher? _dispatcher;

    public DispatcherService()
    {
        _dispatcher = Application.Current?.Dispatcher;
    }

    /// <inheritdoc/>
    public bool CheckAccess()
    {
        return _dispatcher?.CheckAccess() ?? true;
    }

    /// <inheritdoc/>
    public void Invoke(Action action)
    {
        if (_dispatcher == null)
        {
            action();
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.Invoke(action);
        }
    }

    /// <inheritdoc/>
    public T Invoke<T>(Func<T> func)
    {
        if (_dispatcher == null)
        {
            return func();
        }

        if (_dispatcher.CheckAccess())
        {
            return func();
        }

#pragma warning disable CS8603 // 可能发生 null 引用参数
        return _dispatcher.Invoke(func);
#pragma warning restore CS8603
    }

    /// <inheritdoc/>
    public void InvokeAsync(Action action)
    {
        if (_dispatcher == null)
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action);
    }

    /// <inheritdoc/>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        if (_dispatcher == null)
        {
            return Task.FromResult(func());
        }

        if (_dispatcher.CheckAccess())
        {
            return Task.FromResult(func());
        }

#pragma warning disable CS8603 // 可能发生 null 引用参数
        return _dispatcher.InvokeAsync(func).Task;
#pragma warning restore CS8603
    }

    /// <summary>
    /// 全局调度器实例，供静态方法使用。
    /// </summary>
    public static DispatcherService? Instance { get; private set; }

    /// <summary>
    /// 初始化全局调度器实例。
    /// </summary>
    public static void Initialize()
    {
        Instance = new DispatcherService();
    }

    /// <summary>
    /// 在 UI 线程上异步执行操作（静态方法）。
    /// </summary>
    public static void RunOnUIThread(Action action)
    {
        Instance?.InvokeAsync(action);
    }
}