using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.App.Controls;

/// <summary>
/// Draws a track's amplitude peaks with a playhead, and reports clicks as a
/// fraction of the track so the deck can seek. The original had no seek control
/// at all, so this is the one genuinely new interaction.
/// </summary>
public sealed class WaveformView : Control
{
    public static readonly StyledProperty<Waveform?> WaveformProperty =
        AvaloniaProperty.Register<WaveformView, Waveform?>(nameof(Waveform));

    /// <summary>Playhead position as 0..1 of the track.</summary>
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(Progress));

    public static readonly StyledProperty<IBrush> PlayedBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush>(
            nameof(PlayedBrush), new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3C)));

    public static readonly StyledProperty<IBrush> UnplayedBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush>(
            nameof(UnplayedBrush), new SolidColorBrush(Color.FromRgb(0x50, 0x55, 0x60)));

    /// <summary>Raised with a 0..1 fraction when the user clicks or drags on the waveform.</summary>
    public event EventHandler<double>? Seeked;

    static WaveformView()
    {
        AffectsRender<WaveformView>(WaveformProperty, ProgressProperty, PlayedBrushProperty, UnplayedBrushProperty);
    }

    public Waveform? Waveform
    {
        get => GetValue(WaveformProperty);
        set => SetValue(WaveformProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public IBrush PlayedBrush
    {
        get => GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public IBrush UnplayedBrush
    {
        get => GetValue(UnplayedBrushProperty);
        set => SetValue(UnplayedBrushProperty, value);
    }

    public WaveformView() => Cursor = new Cursor(StandardCursorType.Hand);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        RaiseSeek(e.GetPosition(this).X);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Equals(e.Pointer.Captured, this))
            RaiseSeek(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    private void RaiseSeek(double x)
    {
        if (Bounds.Width <= 0) return;
        Seeked?.Invoke(this, Math.Clamp(x / Bounds.Width, 0, 1));
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        float[] peaks = Waveform?.Peaks ?? [];
        double midY = bounds.Height / 2;
        double playedX = bounds.Width * Math.Clamp(Progress, 0, 1);

        if (peaks.Length == 0)
        {
            // Nothing analysed yet: a centre line reads better than an empty box.
            context.DrawLine(new Pen(UnplayedBrush, 1), new Point(0, midY), new Point(bounds.Width, midY));
            return;
        }

        // One vertical bar per pixel column, taking the loudest peak that falls in it.
        int columns = Math.Max(1, (int)bounds.Width);
        for (int x = 0; x < columns; x++)
        {
            int from = (int)((long)x * peaks.Length / columns);
            int to = (int)((long)(x + 1) * peaks.Length / columns);
            if (to <= from) to = Math.Min(from + 1, peaks.Length);

            float peak = 0f;
            for (int i = from; i < to; i++)
                if (peaks[i] > peak) peak = peaks[i];

            double half = Math.Max(0.5, peak * midY);
            IBrush brush = x <= playedX ? PlayedBrush : UnplayedBrush;
            context.FillRectangle(brush, new Rect(x, midY - half, 1, half * 2));
        }

        context.DrawLine(
            new Pen(Brushes.White, 1.5),
            new Point(playedX, 0),
            new Point(playedX, bounds.Height));
    }
}
