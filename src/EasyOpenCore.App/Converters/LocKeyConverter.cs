using System.Globalization;
using System.Windows;
using System.Windows.Data;
using EasyOpenCore.Core.Compatibility;
using EasyOpenCore.Core.Localization;

namespace EasyOpenCore.App.Converters;

/// <summary>Translates an id using the ConverterParameter as key prefix (e.g. "category.").</summary>
public sealed class LocKeyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var id = value?.ToString();
        return string.IsNullOrEmpty(id) ? "" : Loc.T($"{parameter}{id}");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current.FindResource(value switch
        {
            SupportLevel.Supported => "SupportedBrush",
            SupportLevel.Patched => "PatchedBrush",
            SupportLevel.Limited => "WarningBrush",
            SupportLevel.Unknown => "UnknownBrush",
            _ => "BlockerBrush",
        });

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class LevelToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        SupportLevel.Supported => "", // check
        SupportLevel.Patched => "",   // wrench
        SupportLevel.Limited => "",   // warning
        SupportLevel.Unknown => "",   // question
        _ => "",                      // cancel
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
