using System.Globalization;
using System.Windows;
using System.Windows.Data;
using EngineeringWorkspace.Application.Common;

namespace EngineeringWorkspace.Desktop.Converters;

/// <summary>Maps a check state to a glyph; colors come from the CheckState styles in the views.</summary>
public sealed class CheckStateToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        CheckState.Passed => "✔",
        CheckState.Failed => "✖",
        CheckState.Warning => "⚠",
        CheckState.NotApplicable => "—",
        _ => "○"
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the value is a non-empty string.</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    /// <summary>Visible when the text is not empty; ConverterParameter "invert" reverses it.</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is string text && !string.IsNullOrWhiteSpace(text)) ^ (parameter as string == "invert") ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Formats a UTC timestamp in local time, or a dash.</summary>
public sealed class UtcToLocalTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset timestamp => timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", culture),
        _ => "—"
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Negates a boolean (two-way), for "No" options bound to a "Yes" property.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Collapsed when the value is true; visible otherwise.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the value is a positive count.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    /// <summary>Visible when the count is above zero; ConverterParameter "invert" reverses it.</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is int count && count > 0) ^ (parameter as string == "invert") ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Converts a ScrollViewer's viewport width into the width of its page, so pages fit the window and only scroll
/// horizontally when the window is narrower than the page's minimum width.
/// </summary>
public sealed class ViewportWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double width && width > 0 ? width : double.NaN;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
