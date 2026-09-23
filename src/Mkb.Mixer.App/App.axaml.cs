using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.App.Views;

namespace Mkb.Mixer.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
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

        base.OnFrameworkInitializationCompleted();
    }
}
