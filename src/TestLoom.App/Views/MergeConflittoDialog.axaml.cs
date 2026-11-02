using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

public partial class MergeConflittoDialog : Window
{
    private readonly Dictionary<string, SceltaMerge> _scelte = new(StringComparer.Ordinal);

    public MergeConflittoDialog()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this, grande: true);
        AnnullaButton.Click += (_, _) => Close(null);
        ConfermaButton.Click += (_, _) => Close((IReadOnlyDictionary<string, SceltaMerge>)_scelte);
    }

    public static MergeConflittoDialog Crea(IReadOnlyList<CampoMergeTestCase> conflitti)
    {
        var dialog = new MergeConflittoDialog();
        var loc = LocalizationService.Instance;
        dialog.Title = loc.Traduci("MergeTitolo");
        dialog.IstruzioniText.Text = loc.Traduci("MergeIstruzioni");
        dialog.AnnullaButton.Content = loc.Traduci("AnnullaSemplice");
        dialog.ConfermaButton.Content = loc.Traduci("MergeApplica");
        foreach (var campo in conflitti)
        {
            dialog._scelte[campo.Nome] = SceltaMerge.Mia;
            var mia = new RadioButton { Content = loc.Traduci("MergeMantieniMia"), GroupName = campo.Nome, IsChecked = true };
            mia.IsCheckedChanged += (_, _) => { if (mia.IsChecked == true) dialog._scelte[campo.Nome] = SceltaMerge.Mia; };
            var loro = new RadioButton { Content = loc.Traduci("MergeMantieniSalvata"), GroupName = campo.Nome };
            loro.IsCheckedChanged += (_, _) => { if (loro.IsChecked == true) dialog._scelte[campo.Nome] = SceltaMerge.Loro; };
            var colonna = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
            colonna.Children.Add(new TextBox { Text = campo.MioTesto, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 });
            var testoLoro = new TextBox { Text = campo.LoroTesto, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
            Grid.SetColumn(testoLoro, 1); colonna.Children.Add(testoLoro);
            var opzioni = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 28 };
            opzioni.Children.Add(mia); opzioni.Children.Add(loro);
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = campo.Nome, FontWeight = FontWeight.SemiBold });
            panel.Children.Add(opzioni); panel.Children.Add(colonna);
            dialog.CampiPanel.Children.Add(new Border { Classes = { "Card" }, Padding = new Avalonia.Thickness(12), Child = panel });
        }
        return dialog;
    }
}
