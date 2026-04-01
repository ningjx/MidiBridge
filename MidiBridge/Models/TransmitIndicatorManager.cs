using System.Collections.Concurrent;
using System.Windows;

namespace MidiBridge.Models;

public static class TransmitIndicatorManager
{
    private static readonly ConcurrentDictionary<object, byte> s_activeTransmissions = new();

    public static event EventHandler<TransmitEventArgs>? TransmitPulse;

    public static void Start() { }

    public static void Stop()
    {
        s_activeTransmissions.Clear();
    }

    public static void Pulse(object target)
    {
        if (target == null) return;

        s_activeTransmissions[target] = 1;

        TransmitPulse?.Invoke(null, new TransmitEventArgs(target));
    }

    public static void Remove(object target)
    {
        s_activeTransmissions.TryRemove(target, out _);
    }

    public static bool IsTransmitting(object target)
    {
        return s_activeTransmissions.ContainsKey(target);
    }

    public static void ClearAll()
    {
        s_activeTransmissions.Clear();
    }
}

public class TransmitEventArgs : EventArgs
{
    public object Target { get; }

    public TransmitEventArgs(object target)
    {
        Target = target;
    }
}