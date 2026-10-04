using System;
using Avalonia.Controls;
using Mkb.Mixer.App.ViewModels;

namespace Mkb.Mixer.App.Views;

/// <summary>
/// The whole mixer UI. A desktop <see cref="MainWindow"/> hosts it; on Android it
/// is the activity's content directly, since there are no windows.
/// </summary>
public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        // Styles.axaml keys the tablet layout off this class.
        if (OperatingSystem.IsAndroid())
        {
            Classes.Add("touch");
            Resources["ScrollBarSize"] = 22.0;
            Resources["ScrollBarThickness"] = 22.0;
        }

        Loaded += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.LoadRoots();
            vm.Start();   // the transport clock must be created on the UI thread
        };
    }
}
