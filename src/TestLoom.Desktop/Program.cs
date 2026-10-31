using Avalonia;

namespace TestLoom.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestLoomApplication>()
            .UsePlatformDetect()
            .LogToTrace();
}
