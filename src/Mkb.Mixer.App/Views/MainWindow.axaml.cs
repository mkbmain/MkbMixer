using Avalonia.Controls;
using Mkb.Mixer.App.ViewModels;

namespace Mkb.Mixer.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => (DataContext as MainViewModel)?.LoadRoots();
        Closing += (_, _) => (DataContext as MainViewModel)?.SaveState(Width, Height);
    }
}
