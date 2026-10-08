using Avalonia.Media;

namespace Mkb.Mixer.App.Controls;

/// <summary>The hot cue colours, shared by the deck buttons and the waveform markers so they always agree.</summary>
public static class HotCuePalette
{
    // Not gold: the waveform already uses the accent for the played part.
    private static readonly IBrush[] Brushes =
    [
        new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)),
        new SolidColorBrush(Color.FromRgb(0x3C, 0xD0, 0x70)),
        new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xF0)),
        new SolidColorBrush(Color.FromRgb(0xC0, 0x7C, 0xF0)),
    ];

    public static IBrush Brush(int index) => Brushes[(index % Brushes.Length + Brushes.Length) % Brushes.Length];
}
