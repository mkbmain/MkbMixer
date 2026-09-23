using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Mkb.Mixer.App.Converters;

/// <summary>Picks one of two brushes from a boolean, used for the LIVE/MUTE indicator.</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public IBrush TrueBrush { get; set; } = Brushes.Red;
    public IBrush FalseBrush { get; set; } = Brushes.Green;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
