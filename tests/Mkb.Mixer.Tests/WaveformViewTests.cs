using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mkb.Mixer.App.Controls;
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class WaveformViewTests
{
    private static readonly Color Played = Color.FromRgb(0xFF, 0x00, 0x00);
    private static readonly Color Unplayed = Color.FromRgb(0x00, 0x00, 0xFF);

    internal static (Window, WaveformView) Build(int width = 400, int height = 100)
    {
        var peaks = new float[2000];
        for (int i = 0; i < peaks.Length; i++) peaks[i] = 0.2f + 0.7f * (float)Math.Abs(Math.Sin(i * 0.05));
        var view = new WaveformView
        {
            Waveform = new Waveform(peaks),
            PlayedBrush = new SolidColorBrush(Played),
            UnplayedBrush = new SolidColorBrush(Unplayed),
        };
        var window = new Window { Content = view, Width = width, Height = height, Background = Brushes.Black };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static uint[] Pixels(Bitmap frame, out int width)
    {
        _rgba = frame.Format == Avalonia.Platform.PixelFormat.Rgba8888;
        width = frame.PixelSize.Width;
        var data = new uint[width * frame.PixelSize.Height];
        GCHandle h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(frame.PixelSize), h.AddrOfPinnedObject(), data.Length * 4, width * 4); }
        finally { h.Free(); }
        return data;
    }

    [ThreadStatic] private static bool _rgba;

    private static Color At(uint[] px, int width, int x, int y)
    {
        uint p = px[y * width + x];   // little-endian: the first byte is the low one
        return _rgba
            ? Color.FromRgb((byte)p, (byte)(p >> 8), (byte)(p >> 16))
            : Color.FromRgb((byte)(p >> 16), (byte)(p >> 8), (byte)p);
    }

    [Fact]
    public void ColoursTheWaveformEitherSideOfThePlayhead() => AvaloniaTest.Run(() =>
    {
        var (window, view) = Build();
        foreach (double progress in new[] { 0.25, 0.7 })
        {
            view.Progress = progress;
            Dispatcher.UIThread.RunJobs();
            using Bitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
            uint[] px = Pixels(frame, out int w);
            int mid = frame.PixelSize.Height / 2;
            int playhead = (int)(w * progress);

            for (int x = 2; x < w - 2; x += 7)
            {
                if (Math.Abs(x - playhead) < 4) continue;
                Assert.Equal(x < playhead ? Played : Unplayed, At(px, w, x, mid));
            }
            // Above the tallest bar stays background.
            Assert.Equal(Colors.Black, At(px, w, 10, 1));
        }
        window.Close();
    });
}
