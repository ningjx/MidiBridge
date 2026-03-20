using System.ComponentModel;
using System.Runtime.CompilerServices;
using Timer = System.Timers.Timer;

namespace MidiBridge.Models;

public enum MidiDeviceType
{
    LocalInput,
    LocalOutput,
    RtpMidi,
    NetworkMidi2
}

public enum MidiDeviceStatus
{
    Disconnected,
    Connecting,
    Connected,
    Active,
    Error
}

public class MidiDevice : INotifyPropertyChanged, IDisposable
{
    private MidiDeviceStatus _status = MidiDeviceStatus.Disconnected;
    private bool _isSelected;
    private bool _hasActiveRoute;
    private bool _isTransmitting;
    private bool _isEnabled = true;
    private long _receivedMessages;
    private long _sentMessages;
    private readonly Timer _transmitTimer;

    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Unknown";
    public MidiDeviceType Type { get; init; }
    public string Protocol { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public int ControlPort { get; set; }
    public int? LocalDeviceId { get; set; }
    public DateTime? ConnectedTime { get; set; }
    public DateTime LastActivity { get; set; } = DateTime.Now;
    public string? ErrorMessage { get; set; }

    public MidiDevice()
    {
        _transmitTimer = new Timer(100);
        _transmitTimer.Elapsed += (s, e) =>
        {
            SetTransmitting(false);
            _transmitTimer.Stop();
        };
    }

    public MidiDeviceStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public bool HasActiveRoute
    {
        get => _hasActiveRoute;
        set { _hasActiveRoute = value; OnPropertyChanged(); }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EnabledText));
            }
        }
    }

    public string EnabledText => IsEnabled ? "禁用" : "启用";

    public void PulseTransmit()
    {
        SetTransmitting(true);
        _transmitTimer.Stop();
        _transmitTimer.Start();
    }

    private void SetTransmitting(bool value)
    {
        if (_isTransmitting == value) return;
        _isTransmitting = value;
        OnPropertyChanged(nameof(IsTransmitting));
    }

    public bool IsTransmitting
    {
        get => _isTransmitting;
        set
        {
            if (_isTransmitting == value) return;
            _isTransmitting = value;
            OnPropertyChanged();
        }
    }

    public long ReceivedMessages
    {
        get => _receivedMessages;
        set { _receivedMessages = value; OnPropertyChanged(); }
    }

    public long SentMessages
    {
        get => _sentMessages;
        set { _sentMessages = value; OnPropertyChanged(); }
    }

    public bool IsInput => Type == MidiDeviceType.LocalInput || Type == MidiDeviceType.RtpMidi || Type == MidiDeviceType.NetworkMidi2;
    public bool IsOutput => Type == MidiDeviceType.LocalOutput;
    public bool IsNetwork => Type == MidiDeviceType.RtpMidi || Type == MidiDeviceType.NetworkMidi2;

    public string DisplayName => Type == MidiDeviceType.RtpMidi && ControlPort > 0
        ? $"{Name} ({Host}:{ControlPort}-{Port})"
        : IsNetwork
            ? $"{Name} ({Host}:{Port})"
            : Name;

    public string StatusText => Status switch
    {
        MidiDeviceStatus.Connected => "已连接",
        MidiDeviceStatus.Active => "活动中",
        MidiDeviceStatus.Error => $"错误: {ErrorMessage}",
        _ => "未连接"
    };

    public string TypeText => Type switch
    {
        MidiDeviceType.LocalInput => "本地输入",
        MidiDeviceType.LocalOutput => "本地输出",
        MidiDeviceType.RtpMidi => "RTP-MIDI",
        MidiDeviceType.NetworkMidi2 => "Network MIDI 2.0",
        _ => "未知"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        _transmitTimer?.Stop();
        _transmitTimer?.Dispose();
        Status = MidiDeviceStatus.Disconnected;
    }
}