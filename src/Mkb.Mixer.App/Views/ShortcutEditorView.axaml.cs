using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Mkb.Mixer.App.Views;

/// <summary>
/// The shortcuts overlay. Its data context is the editor model; closing belongs to the
/// main model (it saves), so the host hands that command in.
/// </summary>
public partial class ShortcutEditorView : UserControl
{
    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<ShortcutEditorView, ICommand?>(nameof(CloseCommand));

    public ShortcutEditorView() => InitializeComponent();

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }
}
