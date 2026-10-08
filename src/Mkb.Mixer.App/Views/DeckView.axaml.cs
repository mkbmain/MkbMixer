using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Mkb.Mixer.App.Controls;
using Mkb.Mixer.App.ViewModels;

namespace Mkb.Mixer.App.Views;

public partial class DeckView : UserControl
{
    private HotCueSlot? _clearedSlot;
    private long _clearedAt;

    private DeckViewModel? Vm => DataContext as DeckViewModel;

    public DeckView()
    {
        InitializeComponent();
        // The waveform reports a 0..1 fraction; the deck turns that into a seek.
        this.FindControl<WaveformView>("Wave")!.Seeked += (_, fraction) =>
            (DataContext as DeckViewModel)?.SeekToFraction(fraction);
        HoldToNudge("NudgeDown", -1);
        HoldToNudge("NudgeUp", +1);
    }

    private void OnTempoDoubleTapped(object? sender, TappedEventArgs e) =>
        (DataContext as DeckViewModel)?.ResetTempoCommand.Execute(null);

    /// <summary>
    /// Nudges for as long as the button is held. A Button marks its own press as
    /// handled, so these listen on the tunnel with handledEventsToo.
    /// </summary>
    private void HoldToNudge(string name, int direction)
    {
        Button button = this.FindControl<Button>(name)!;
        button.AddHandler(PointerPressedEvent, (_, _) => Vm?.BeginNudge(direction),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        button.AddHandler(PointerReleasedEvent, (_, _) => Vm?.EndNudge(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        button.PointerCaptureLost += (_, _) => Vm?.EndNudge();
    }

    private void OnHotCueClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HotCueSlot slot }) return;
        // Lifting the finger after a long-press also clicks. That must not set
        // again the cue the long-press just cleared.
        if (ReferenceEquals(slot, _clearedSlot) && Environment.TickCount64 - _clearedAt < 1500)
        {
            _clearedSlot = null;
            return;
        }
        Vm?.HotCueCommand.Execute(slot);
    }

    /// <summary>Right-click, or a long-press on touch, clears the cue.</summary>
    private void OnHotCueContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button { DataContext: HotCueSlot slot }) return;
        Vm?.ClearHotCueCommand.Execute(slot);
        _clearedSlot = slot;
        _clearedAt = Environment.TickCount64;
        e.Handled = true;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
