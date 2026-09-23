using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Mkb.Mixer.App.Controls;
using Mkb.Mixer.App.ViewModels;

namespace Mkb.Mixer.App.Views;

public partial class DeckView : UserControl
{
    public DeckView()
    {
        InitializeComponent();
        // The waveform reports a 0..1 fraction; the deck turns that into a seek.
        this.FindControl<WaveformView>("Wave")!.Seeked += (_, fraction) =>
            (DataContext as DeckViewModel)?.SeekToFraction(fraction);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
