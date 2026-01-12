using System.Drawing;
using System.Globalization;
using System.Windows.Data;

namespace SolutionDumper.Converters;

public sealed class BoolToGrayBrushConverter : IValueConverter
{
    public Brush NormalBrush { get; set; } = Brushes.Black;
    public Brush DisabledBrush { get; set; } = Brushes.Gray;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return NormalBrush;

        return DisabledBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
