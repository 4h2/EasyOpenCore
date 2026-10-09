using System.Globalization;
using System.Windows;
using System.Windows.Data;
using EasyOpenCore.Core.Checks;
using EasyOpenCore.Core.Localization;

namespace EasyOpenCore.App.Converters;

public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current.FindResource(value switch
        {
            Severity.Blocker => "BlockerBrush",
            Severity.Warning => "WarningBrush",
            _ => "InfoBrush",
        });

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SeverityToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Loc.T($"severity.{value}");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
