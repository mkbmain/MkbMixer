using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Mkb.Mixer.App.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can swap its whole contents under
/// one notification, so a bound grid relayouts once instead of once per row.
/// </summary>
public sealed class ReplaceableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        // Items is the backing list; changing it directly skips the per-item events.
        Items.Clear();
        foreach (T item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
