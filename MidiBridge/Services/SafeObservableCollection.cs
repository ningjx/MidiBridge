using System.Collections.ObjectModel;
using System.Windows.Data;
using System.Windows.Threading;

namespace MidiBridge.Services;

/// <summary>
/// 线程安全的 ObservableCollection，支持跨线程访问。
/// </summary>
/// <typeparam name="T">元素类型。</typeparam>
public class SafeObservableCollection<T> : ObservableCollection<T>
{
    private readonly object _lock = new();

    public SafeObservableCollection()
    {
        EnableSynchronization();
    }

    public SafeObservableCollection(IEnumerable<T> collection) : base(collection)
    {
        EnableSynchronization();
    }

    private void EnableSynchronization()
    {
        BindingOperations.EnableCollectionSynchronization(this, _lock);
    }

    /// <summary>
    /// 在锁内执行操作。
    /// </summary>
    public void DoWithLock(Action action)
    {
        lock (_lock)
        {
            action();
        }
    }

    /// <summary>
    /// 线程安全的添加元素。
    /// </summary>
    public void AddSafe(T item)
    {
        lock (_lock)
        {
            Add(item);
        }
    }

    /// <summary>
    /// 线程安全的移除元素。
    /// </summary>
    public bool RemoveSafe(T item)
    {
        lock (_lock)
        {
            return Remove(item);
        }
    }

    /// <summary>
    /// 线程安全的清空集合。
    /// </summary>
    public void ClearSafe()
    {
        lock (_lock)
        {
            Clear();
        }
    }

    /// <summary>
    /// 线程安全的插入元素。
    /// </summary>
    public void InsertSafe(int index, T item)
    {
        lock (_lock)
        {
            Insert(index, item);
        }
    }

    /// <summary>
    /// 线程安全的移动元素。
    /// </summary>
    public void MoveSafe(int oldIndex, int newIndex)
    {
        lock (_lock)
        {
            Move(oldIndex, newIndex);
        }
    }
}