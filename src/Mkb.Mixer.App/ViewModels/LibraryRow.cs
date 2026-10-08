using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.App.ViewModels;

/// <summary>
/// One track in the library list, with the parts that change while it is on
/// screen: its BPM, which background analysis fills in, and whether it has been
/// played this session.
/// </summary>
public sealed partial class LibraryRow(Track track) : ObservableObject
{
    public Track Track { get; } = track;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BpmText), nameof(BpmSortKey))]
    private double? _bpm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayedMark), nameof(RowOpacity))]
    private bool _isPlayed;

    public string BpmText => Bpm is { } b ? b.ToString("0.0", CultureInfo.InvariantCulture) : "—";

    /// <summary>Unknown BPMs sort after every known one in ascending order.</summary>
    public double BpmSortKey => Bpm ?? double.MaxValue;

    public string PlayedMark => IsPlayed ? "✓" : "";

    /// <summary>Played rows are dimmed so the next pick stands out.</summary>
    public double RowOpacity => IsPlayed ? 0.5 : 1.0;
}
