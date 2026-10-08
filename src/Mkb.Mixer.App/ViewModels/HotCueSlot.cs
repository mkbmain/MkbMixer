using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mkb.Mixer.App.ViewModels;

/// <summary>One of a deck's numbered hot cue buttons.</summary>
public sealed partial class HotCueSlot(int index) : ObservableObject
{
    public int Index { get; } = index;
    public string Label { get; } = (index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Where the cue is, or null when the slot is empty. 0 is a real cue at the start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSet))]
    private double? _seconds;

    public bool IsSet => Seconds is not null;
}
