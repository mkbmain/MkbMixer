using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Mkb.Mixer.App.Views;

/// <summary>
/// One deck's playlist and its reorder/load/remove buttons. Part of the deck on
/// desktop and tablet; a tab of its own on a phone.
/// </summary>
public partial class PlaylistView : UserControl
{
    public PlaylistView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
