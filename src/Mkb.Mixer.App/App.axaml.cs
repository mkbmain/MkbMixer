using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.App.Views;

namespace Mkb.Mixer.App;

public partial class App : Application
{
    /// <summary>
    /// The one view model for the app's lifetime. Android can rebuild the view when
    /// the activity is recreated, but the decks and audio engine must survive that.
    /// </summary>
    public MainViewModel? ViewModel { get; private set; }

    /// <summary>
    /// Set by the Android activity before its view is created, when the screen is
    /// too small for the two-deck tablet layout.
    /// </summary>
    public static bool UsePhoneLayout { get; set; }

    /// <summary>
    /// Set by the Android activity to list its mounted storage volumes, which the
    /// shared code cannot discover by itself.
    /// </summary>
    public static Func<IEnumerable<StorageRoot>>? PlatformRoots { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = ViewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
                Width = viewModel.SavedWidth,
                Height = viewModel.SavedHeight
            };

            // The original called Process.Kill() on close to shut the COM players
            // down. Disposing the engine is enough, and it releases the device cleanly.
            desktop.Exit += (_, _) => viewModel.Dispose();
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            // Android: the factory runs each time the activity needs a view.
            activity.MainViewFactory = () =>
            {
                var viewModel = ViewModel ??= new MainViewModel { PlatformRoots = PlatformRoots };
                return UsePhoneLayout
                    ? new PhoneView { DataContext = viewModel }
                    : new MainView { DataContext = viewModel };
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView { DataContext = ViewModel = new MainViewModel() };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
