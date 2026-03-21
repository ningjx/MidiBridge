using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Threading;

namespace MidiBridge.Models;

public static class TransmitIndicatorManager
{
    private static readonly ConcurrentDictionary<object, byte> s_activeIndicators = new();
    private static readonly System.Timers.Timer s_timer;
    private static readonly Dispatcher s_dispatcher;
    private static bool s_isRunning;

    static TransmitIndicatorManager()
    {
        s_dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        s_timer = new System.Timers.Timer(100);
        s_timer.AutoReset = true;
        s_timer.Elapsed += OnTimerElapsed;
    }

    public static void Start()
    {
        if (s_isRunning) return;
        s_isRunning = true;
        s_timer.Start();
    }

    public static void Stop()
    {
        s_isRunning = false;
        s_timer.Stop();
        ClearAll();
    }

    public static void Pulse(object target)
    {
        if (target == null) return;
        
        s_activeIndicators.TryAdd(target, 1);
        
        SetTransmitting(target, true);
    }

    public static void Remove(object target)
    {
        s_activeIndicators.TryRemove(target, out _);
    }

    private static void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (s_activeIndicators.IsEmpty) return;

        var keys = s_activeIndicators.Keys.ToArray();
        s_activeIndicators.Clear();

        s_dispatcher.BeginInvoke(() =>
        {
            foreach (var key in keys)
            {
                SetTransmitting(key, false);
            }
        }, DispatcherPriority.Background);
    }

    private static void ClearAll()
    {
        var keys = s_activeIndicators.Keys.ToArray();
        s_activeIndicators.Clear();

        s_dispatcher.BeginInvoke(() =>
        {
            foreach (var key in keys)
            {
                SetTransmitting(key, false);
            }
        }, DispatcherPriority.Background);
    }

    private static void SetTransmitting(object target, bool value)
    {
        if (target is MidiDevice device)
        {
            device.SetTransmittingInternal(value);
        }
        else if (target is MidiRoute route)
        {
            route.SetTransmittingInternal(value);
        }
    }
}