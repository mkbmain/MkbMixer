using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
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

    /// <summary>Hot cue positions as 0..1 of the track, one per slot, null where a slot is empty.</summary>
    public static readonly StyledProperty<double?[]?> CueFractionsProperty =
        AvaloniaProperty.Register<WaveformView, double?[]?>(nameof(CueFractions));

    public double?[]? CueFractions
    {
        get => GetValue(CueFractionsProperty);
        set => SetValue(CueFractionsProperty, value);
    }

    /// <summary>Raised with a 0..1 fraction when the user clicks or drags on the waveform.</summary>
    public event EventHandler<double>? Seeked;

    static WaveformView()
    {
        AffectsRender<WaveformView>(WaveformProperty, ProgressProperty, PlayedBrushProperty, UnplayedBrushProperty, CueFractionsProperty);
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

    private static readonly IPen Playhead = new ImmutablePen(Brushes.White, 1.5);

    // The bars only change with the track, the size or the colours, but the
    // playhead moves ten times a second. So they are drawn once into a bitmap in
    // each colour, and a frame is just the two halves either side of the playhead,
    // instead of a rectangle per pixel column every time.
    private RenderTargetBitmap? _played, _unplayed;
    private (float[] Peaks, Size Size, double Scale, IBrush Played, IBrush Unplayed) _barsKey;

    private void UpdateBars(float[] peaks, Size size, double scale)
    {
        var key = (peaks, size, scale, PlayedBrush, UnplayedBrush);
        if (_played is not null && key.Equals(_barsKey)) return;

        DisposeBars();
        _played = DrawBars(peaks, size, scale, PlayedBrush);
        _unplayed = DrawBars(peaks, size, scale, UnplayedBrush);
        _barsKey = key;
    }

    private static RenderTargetBitmap DrawBars(float[] peaks, Size size, double scale, IBrush brush)
    {
        var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
                                   Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
        var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        using DrawingContext g = bitmap.CreateDrawingContext();
        double midY = size.Height / 2;

        // One vertical bar per pixel column, taking the loudest peak that falls in it.
        int columns = Math.Max(1, (int)size.Width);
        for (int x = 0; x < columns; x++)
        {
            int from = (int)((long)x * peaks.Length / columns);
            int to = (int)((long)(x + 1) * peaks.Length / columns);
            if (to <= from) to = Math.Min(from + 1, peaks.Length);

            float peak = 0f;
            for (int i = from; i < to; i++)
                if (peaks[i] > peak) peak = peaks[i];

            double half = Math.Max(0.5, peak * midY);
            g.FillRectangle(brush, new Rect(x, midY - half, 1, half * 2));
        }
        return bitmap;
    }

    private void DisposeBars()
    {
        _played?.Dispose();
        _unplayed?.Dispose();
        _played = _unplayed = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DisposeBars();
    }

    private static readonly FormattedText?[] CueLabels = new FormattedText?[8];

    private static FormattedText CueLabel(int i)
    {
        if (i >= CueLabels.Length)
            return new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, Brushes.Black);
        return CueLabels[i] ??= new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, Brushes.Black);
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

        UpdateBars(peaks, bounds.Size, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        if (playedX > 0)
        {
            var left = new Rect(0, 0, playedX, bounds.Height);
            context.DrawImage(_played!, left, left);
        }
        if (playedX < bounds.Width)
        {
            var right = new Rect(playedX, 0, bounds.Width - playedX, bounds.Height);
            context.DrawImage(_unplayed!, right, right);
        }

        context.DrawLine(Playhead, new Point(playedX, 0), new Point(playedX, bounds.Height));

        if (CueFractions is not { } cues) return;
        for (int i = 0; i < cues.Length; i++)
        {
            if (cues[i] is not { } fraction) continue;
            IBrush brush = HotCuePalette.Brush(i);
            double x = Math.Round(bounds.Width * fraction) + 0.5;
            context.DrawLine(new Pen(brush, 2), new Point(x, 0), new Point(x, bounds.Height));
            FormattedText label = CueLabel(i);
            context.FillRectangle(brush, new Rect(x, 0, label.Width + 4, label.Height));
            context.DrawText(label, new Point(x + 2, 0));
        }
    }
}
