using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MidiBridge.Models;

namespace MidiBridge.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility.Visible;
    }
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && !b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility.Collapsed;
    }
}

public class BoolToMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b ? new Thickness(8, 0, 20, 0) : new Thickness(20, 0, 8, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is MidiDeviceStatus status)
        {
            return status switch
            {
                MidiDeviceStatus.Connected => Brushes.Orange,
                MidiDeviceStatus.Active => Brushes.LimeGreen,
                MidiDeviceStatus.Error => Brushes.Red,
                _ => Brushes.Gray
            };
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class IsConnectedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is MidiDeviceStatus status)
        {
            return status == MidiDeviceStatus.Connected || status == MidiDeviceStatus.Active;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class StatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is MidiDeviceStatus status)
        {
            return status switch
            {
                MidiDeviceStatus.Connected => "已连接",
                MidiDeviceStatus.Active => "活动中",
                MidiDeviceStatus.Error => "错误",
                _ => "未连接"
            };
        }
        return "未知";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class BoolToWarningTagConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b ? "Warning" : null;
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string s && s == "Warning";
    }
}