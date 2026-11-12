using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinKit.Core;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            int i => i == 0,
            System.Collections.ICollection c => c.Count == 0,
            _ => false
        };
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase)) empty = !empty;
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToTextConverter : IValueConverter
{
    public string TrueText { get; set; } = "开";
    public string FalseText { get; set; } = "关";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? TrueText : FalseText;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s == TrueText;
}
