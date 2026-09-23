using Avalonia.Controls;
using Mkb.Mixer.App.ViewModels;

namespace Mkb.Mixer.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.LoadRoots();
            vm.Start();   // the transport clock must be created on the UI thread
        };
        Closing += (_, _) => (DataContext as MainViewModel)?.SaveState(Width, Height);
    }
}
