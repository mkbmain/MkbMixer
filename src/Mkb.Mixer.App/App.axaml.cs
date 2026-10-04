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
            activity.MainViewFactory = () => new MainView { DataContext = ViewModel ??= new MainViewModel() };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView { DataContext = ViewModel = new MainViewModel() };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
