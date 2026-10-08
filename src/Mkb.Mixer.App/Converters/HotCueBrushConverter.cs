using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Mkb.Mixer.App.Controls;

namespace Mkb.Mixer.App.Converters;

/// <summary>A hot cue slot's index to its colour.</summary>
public sealed class HotCueBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index ? HotCuePalette.Brush(index) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
