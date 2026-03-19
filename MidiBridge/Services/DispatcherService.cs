using System.Windows;
using MidiBridge.Services.Interfaces;

namespace MidiBridge.Services;

/// <summary>
/// 调度器服务实现，用于在 UI 线程上执行操作。
/// </summary>
public class DispatcherService : IDispatcherService
{
    /// <inheritdoc/>
    public bool CheckAccess()
    {
        return Application.Current?.Dispatcher.CheckAccess() ?? true;
    }

    /// <inheritdoc/>
    public void Invoke(Action action)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == true)
        {
            action();
        }
        else
        {
            Application.Current?.Dispatcher.Invoke(action);
        }
    }

    /// <inheritdoc/>
    public T Invoke<T>(Func<T> func)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == true)
        {
            return func();
        }
        
#pragma warning disable CS8603 // 可能发生 null 引用参数。
        return Application.Current!.Dispatcher.Invoke(func);
#pragma warning restore CS8603
    }

    /// <inheritdoc/>
    public void InvokeAsync(Action action)
    {
        Application.Current?.Dispatcher.InvokeAsync(action);
    }

    /// <inheritdoc/>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == true)
        {
            return Task.FromResult(func());
        }
        
#pragma warning disable CS8603 // 可能发生 null 引用参数。
        return Application.Current!.Dispatcher.InvokeAsync(func).Task;
#pragma warning restore CS8603
    }
}