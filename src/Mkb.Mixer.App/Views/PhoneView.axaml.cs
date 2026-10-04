using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Mkb.Mixer.App.ViewModels;

namespace Mkb.Mixer.App.Views;

/// <summary>
/// The mixer UI for a phone held upright: the same view model as
/// <see cref="MainView"/>, split across tabs because a phone is too narrow for
/// two decks side by side.
/// </summary>
public partial class PhoneView : UserControl
{
    public PhoneView()
    {
        InitializeComponent();

        // Fluent's thin, fading scroll bars leave nothing to drag with a finger.
        Resources["ScrollBarSize"] = 22.0;
        Resources["ScrollBarThickness"] = 22.0;

        Loaded += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.LoadRoots();
            vm.Start();   // the transport clock must be created on the UI thread
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
