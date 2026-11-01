using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TestLoom.Core;

namespace TestLoom.Desktop;

public sealed record FirstRunChoice(string Name, string PresetId, string Language, bool IncludeSamples, string Tester);

/// <summary>
/// First-run setup: pick the language and a preset (vocabulary and starting templates), name the workspace and
/// optionally start with example content. Nothing is written until the user presses Create.
/// </summary>
public sealed class FirstRunWindow : Window
{
    private readonly ComboBox _language = new() { ItemsSource = new[] { "English", "Italiano" }, SelectedIndex = 0, MinWidth = 160 };
    private readonly TextBox _name = new();
    private readonly TextBox _tester = new();
    private readonly ListBox _presets = new() { MinHeight = 220 };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _samples = new();
    private readonly Button _create = new() { IsEnabled = false };
    private readonly Button _cancel = new();
    private readonly TextBlock _title = new() { FontSize = 26, FontWeight = FontWeight.Bold };
    private readonly TextBlock _intro = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBlock _languageLabel = new();
    private readonly TextBlock _nameLabel = new();
    private readonly TextBlock _testerLabel = new();
    private readonly TextBlock _presetLabel = new();
    private TestLoomPresetCard[] _cards = Array.Empty<TestLoomPresetCard>();

    public FirstRunWindow(string language = "en")
    {
        Title = "TestLoom";
        Icon = AboutInfo.LoadIcon();
        Width = 640;
        Height = 700;
        MinWidth = 520;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var body = new StackPanel { Spacing = 10, Margin = new Thickness(24) };
        body.Children.Add(_title);
        body.Children.Add(_intro);
        body.Children.Add(_languageLabel);
        body.Children.Add(_language);
        body.Children.Add(_nameLabel);
        body.Children.Add(_name);
        body.Children.Add(_testerLabel);
        body.Children.Add(_tester);
        body.Children.Add(_presetLabel);
        body.Children.Add(_presets);
        body.Children.Add(_detail);
        body.Children.Add(_samples);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        buttons.Children.Add(_create);
        buttons.Children.Add(_cancel);
        body.Children.Add(buttons);
        Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        _language.SelectionChanged += (_, _) => ApplyLanguage();
        _presets.SelectionChanged += (_, _) => ShowSelected();
        _name.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdateCreate(); };
        _create.Click += (_, _) =>
        {
            var card = SelectedCard();
            var name = _name.Text?.Trim();
            if (card is null || string.IsNullOrWhiteSpace(name)) return;
            Close(new FirstRunChoice(name, card.Id, Language, _samples.IsChecked == true, _tester.Text?.Trim() ?? ""));
        };
        _cancel.Click += (_, _) => Close(null);
        _language.SelectedIndex = language == "it" ? 1 : 0;
        ApplyLanguage();
        _presets.SelectedIndex = 0;
    }

    private string Language => _language.SelectedIndex == 1 ? "it" : "en";

    private TestLoomPresetCard? SelectedCard() =>
        _presets.SelectedIndex >= 0 && _presets.SelectedIndex < _cards.Length ? _cards[_presets.SelectedIndex] : null;

    private string L(string english) => WorkspaceLocalization.T(english, Language);

    private void ApplyLanguage()
    {
        var keep = _presets.SelectedIndex;
        _title.Text = L("Welcome to TestLoom");
        _intro.Text = L("Choose a starting point. The preset sets the vocabulary and the first test templates; everything can be changed later.");
        _languageLabel.Text = L("Language");
        _nameLabel.Text = L("Workspace name");
        _testerLabel.Text = L("Your name (recorded on test runs, optional)");
        _presetLabel.Text = L("Starting preset");
        _samples.Content = L("Include example content (a requirement, a test and a plan)");
        _create.Content = L("Create workspace");
        _cancel.Content = L("Decide later");
        _name.Watermark = L("e.g. Release 1.0 verification");
        _cards = TestLoomPresetInfo.Cards(Language).ToArray();
        _presets.ItemsSource = _cards.Select(c => c.DisplayName).ToArray();
        _presets.SelectedIndex = keep < 0 ? 0 : keep;
        ShowSelected();
    }

    private void ShowSelected()
    {
        var card = SelectedCard();
        if (card is null) { _detail.Text = ""; return; }
        _detail.Text = card.Description + Environment.NewLine +
            $"{L("Vocabulary")}: {card.SubjectLabel} · {card.ComponentLabel} · {card.PlanLabel}" +
            (card.TestTemplates.Count == 0 ? "" : Environment.NewLine + $"{L("Templates")}: {string.Join(", ", card.TestTemplates)}");
        UpdateCreate();
    }

    private void UpdateCreate() => _create.IsEnabled = SelectedCard() is not null && !string.IsNullOrWhiteSpace(_name.Text);
}
