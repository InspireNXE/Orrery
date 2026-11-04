using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Orrery.App.Tests.TestApp))]

namespace Orrery.App.Tests;

/// <summary>A bare app with the same theme as Orrery, so controls get their real templates.</summary>
public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Orrery.App.Input.DialogKeys.Install();
        Orrery.App.Input.RadioKeys.Install();
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
