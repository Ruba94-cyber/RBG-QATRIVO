using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace TestLoom.Desktop;

/// <summary>Window icon and the "About" window: version, origin, license and credits (the same text as README and NOTICE).</summary>
internal static class AboutInfo
{
    public const string Copyright = "Copyright (c) 2026 Paolo Rubagotti";
    public const string RequiredNotice = "Required Notice: " + Copyright;
    public const string Origin =
        "TestLoom is a generalized adaptation of a more specific private application developed and used by its author.";
    public const string License =
        "Source-available under the PolyForm Shield License 1.0.0 (see the LICENSE file or https://polyformproject.org/licenses/shield/1.0.0).";
    public const string Credits = "If you use or redistribute TestLoom, keep the LICENSE and NOTICE files and credit the author: \"Based on TestLoom by Paolo Rubagotti\".";
    public const string Contact = "Contact: paolo.ruba.1913@gmail.com";
    public const string NameAndIcon = "The name TestLoom and its icon are not licensed for use as the name or branding of another product.";

    /// <summary>Never throws: a missing resource must not prevent the application from starting.</summary>
    public static WindowIcon? LoadIcon()
    {
        try
        {
            return new WindowIcon(AssetLoader.Open(new Uri("avares://TestLoom/Assets/testloom-256.png")));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Window Create()
    {
        var version = typeof(AboutInfo).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        TextBlock Line(string text, double size = 13, FontWeight weight = FontWeight.Normal) =>
            new() { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(20) };
        panel.Children.Add(Line("TestLoom", 22, FontWeight.SemiBold));
        panel.Children.Add(Line("Version " + version + " - early technical preview"));
        panel.Children.Add(Line(Origin));
        panel.Children.Add(Line(License));
        panel.Children.Add(Line(RequiredNotice, 13, FontWeight.SemiBold));
        panel.Children.Add(Line(Credits));
        panel.Children.Add(Line(NameAndIcon));
        panel.Children.Add(Line(Contact));
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(close);
        var window = new Window
        {
            Title = "About TestLoom",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = LoadIcon(),
            Content = panel,
        };
        close.Click += (_, _) => window.Close();
        return window;
    }
}
