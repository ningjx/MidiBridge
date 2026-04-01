using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MidiBridge.Models;

public class MidiRoute : INotifyPropertyChanged
{
    private bool _isTransmitting;
    private bool _isEnabled = true;
    private long _transferredMessages;

    public string Id { get; init; } = Guid.NewGuid().ToString();

    private MidiDevice _source = null!;
    public MidiDevice Source
    {
        get => _source;
        set
        {
            if (_source != null)
            {
                _source.PropertyChanged -= OnDevicePropertyChanged;
            }
            _source = value;
            if (_source != null)
            {
                _source.PropertyChanged += OnDevicePropertyChanged;
            }
        }
    }

    private MidiDevice _target = null!;
    public MidiDevice Target
    {
        get => _target;
        set
        {
            if (_target != null)
            {
                _target.PropertyChanged -= OnDevicePropertyChanged;
            }
            _target = value;
            if (_target != null)
            {
                _target.PropertyChanged += OnDevicePropertyChanged;
            }
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEffectivelyEnabled));
        }
    }

    public bool IsEffectivelyEnabled => IsEnabled && (Source?.IsEnabled ?? true) && (Target?.IsEnabled ?? true);

    public DateTime CreatedTime { get; init; } = DateTime.Now;

    public long TransferredMessages => _transferredMessages;

    public bool FilterNoteOn { get; set; } = true;
    public bool FilterNoteOff { get; set; } = true;
    public bool FilterControlChange { get; set; } = true;
    public bool FilterProgramChange { get; set; } = true;
    public bool FilterPitchBend { get; set; } = true;
    public bool FilterAftertouch { get; set; } = true;

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

    public void PulseTransmit()
    {
        Serilog.Log.Debug("[MidiRoute] PulseTransmit: {RouteId}, HashCode={HashCode}", Id, GetHashCode());
        TransmitIndicatorManager.Pulse(this);
    }

    internal void SetTransmittingInternal(bool value)
    {
        if (_isTransmitting == value) return;
        _isTransmitting = value;
        Serilog.Log.Debug("[MidiRoute] SetTransmittingInternal: {RouteId}, Value={Value}", Id, value);
        OnPropertyChanged(nameof(IsTransmitting));
    }

    internal void AddTransferredMessages(long count)
    {
        if (count <= 0) return;
        _transferredMessages += count;
        OnPropertyChanged(nameof(TransferredMessages));
    }

    public void IncrementTransferred()
    {
        TransmitIndicatorManager.IncrementTransferred(this);
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MidiDevice.IsEnabled))
        {
            OnPropertyChanged(nameof(IsEffectivelyEnabled));
        }
    }

    public string DisplayName => $"{Source?.Name ?? "Unknown"} -> {Target?.Name ?? "Unknown"}";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}