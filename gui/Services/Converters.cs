using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MasterRelayVPN.Services;

public class BoolToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => value is Visibility v && v == Visibility.Visible;
}

public class StringToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}
