using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Threading;

namespace MidiBridge.Models;

public static class TransmitIndicatorManager
{
    private const int FLASH_INTERVAL_MS = 50;
    private const int STATS_UPDATE_INTERVAL_MS = 100;

    private static readonly ConcurrentDictionary<object, byte> s_activeIndicators = new();
    private static readonly ConcurrentDictionary<MidiDevice, long> s_pendingReceived = new();
    private static readonly ConcurrentDictionary<MidiDevice, long> s_pendingSent = new();
    private static readonly ConcurrentDictionary<MidiRoute, long> s_pendingTransferred = new();

    private static readonly System.Timers.Timer s_flashTimer;
    private static readonly System.Timers.Timer s_statsTimer;
    private static readonly Dispatcher s_dispatcher;
    private static bool s_isRunning;

    static TransmitIndicatorManager()
    {
        s_dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        s_flashTimer = new System.Timers.Timer(FLASH_INTERVAL_MS);
        s_flashTimer.AutoReset = true;
        s_flashTimer.Elapsed += OnFlashTimerElapsed;

        s_statsTimer = new System.Timers.Timer(STATS_UPDATE_INTERVAL_MS);
        s_statsTimer.AutoReset = true;
        s_statsTimer.Elapsed += OnStatsTimerElapsed;
    }

    public static void Start()
    {
        if (s_isRunning) return;
        s_isRunning = true;
        s_flashTimer.Start();
        s_statsTimer.Start();
    }

    public static void Stop()
    {
        s_isRunning = false;
        s_flashTimer.Stop();
        s_statsTimer.Stop();
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

        if (target is MidiDevice device)
        {
            s_pendingReceived.TryRemove(device, out _);
            s_pendingSent.TryRemove(device, out _);
        }
        else if (target is MidiRoute route)
        {
            s_pendingTransferred.TryRemove(route, out _);
        }
    }

    public static void IncrementReceived(MidiDevice device)
    {
        s_pendingReceived.AddOrUpdate(device, 1, (_, count) => count + 1);
    }

    public static void IncrementSent(MidiDevice device)
    {
        s_pendingSent.AddOrUpdate(device, 1, (_, count) => count + 1);
    }

    public static void IncrementTransferred(MidiRoute route)
    {
        s_pendingTransferred.AddOrUpdate(route, 1, (_, count) => count + 1);
    }

    private static void OnFlashTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
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

    private static void OnStatsTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (s_pendingReceived.IsEmpty && s_pendingSent.IsEmpty && s_pendingTransferred.IsEmpty) return;

        s_dispatcher.BeginInvoke(() =>
        {
            FlushStats();
        }, DispatcherPriority.Background);
    }

    private static void FlushStats()
    {
        foreach (var kvp in s_pendingReceived)
        {
            if (kvp.Value > 0)
            {
                kvp.Key.AddReceivedMessages(kvp.Value);
            }
        }
        s_pendingReceived.Clear();

        foreach (var kvp in s_pendingSent)
        {
            if (kvp.Value > 0)
            {
                kvp.Key.AddSentMessages(kvp.Value);
            }
        }
        s_pendingSent.Clear();

        foreach (var kvp in s_pendingTransferred)
        {
            if (kvp.Value > 0)
            {
                kvp.Key.AddTransferredMessages(kvp.Value);
            }
        }
        s_pendingTransferred.Clear();
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
            FlushStats();
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