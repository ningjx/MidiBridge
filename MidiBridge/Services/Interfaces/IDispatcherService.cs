namespace MidiBridge.Services.Interfaces;

/// <summary>
/// 调度器服务接口，用于在 UI 线程上执行操作。
/// </summary>
public interface IDispatcherService
{
    /// <summary>
    /// 检查当前是否在 UI 线程。
    /// </summary>
    bool CheckAccess();

    /// <summary>
    /// 在 UI 线程上同步执行操作。
    /// </summary>
    /// <param name="action">要执行的操作。</param>
    void Invoke(Action action);

    /// <summary>
    /// 在 UI 线程上同步执行操作并返回结果。
    /// </summary>
    /// <typeparam name="T">返回类型。</typeparam>
    /// <param name="func">要执行的函数。</param>
    /// <returns>函数返回值。</returns>
    T Invoke<T>(Func<T> func);

    /// <summary>
    /// 在 UI 线程上异步执行操作。
    /// </summary>
    /// <param name="action">要执行的操作。</param>
    void InvokeAsync(Action action);

    /// <summary>
    /// 在 UI 线程上异步执行操作并返回结果。
    /// </summary>
    /// <typeparam name="T">返回类型。</typeparam>
    /// <param name="func">要执行的函数。</param>
    /// <returns>包含返回值的任务。</returns>
    Task<T> InvokeAsync<T>(Func<T> func);
}