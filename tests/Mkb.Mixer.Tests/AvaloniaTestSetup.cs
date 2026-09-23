using Avalonia;
using Avalonia.Headless;

namespace Mkb.Mixer.Tests;

/// <summary>
/// Runs UI work on a headless Avalonia session.
/// </summary>
/// <remarks>
/// Avalonia ships an xunit adapter, but only for xunit v2, and this project is on
/// v3. Driving <see cref="HeadlessUnitTestSession"/> directly is a few lines and
/// avoids pinning the whole suite to the older framework.
/// </remarks>
public static class AvaloniaTest
{
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(AvaloniaTest)));

    public static void Run(Action body) => Session.Value.Dispatch(body, default).GetAwaiter().GetResult();

    /// <summary>Skia rather than the stub renderer, so frames can actually be captured.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Mkb.Mixer.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
