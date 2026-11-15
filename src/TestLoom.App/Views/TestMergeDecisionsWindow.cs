using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>
/// Risoluzione semplificata di un conflitto fra due copie di un Test. Se l'unione delle REV e' automatica non chiede nulla:
/// mostra una frase di riepilogo e una conferma. Se restano decisioni (campi di testata diversi, REV non ordinabili) mostra SOLO
/// quelle, una riga per campo con i due valori affiancati, autore e data, scelta con un clic e preselezione del valore piu'
/// recente. "Conferma" si abilita solo a scelte complete. Il valore non scelto va comunque nel recovery-bin (fatto dal servizio
/// di risoluzione) ed e' elencato nella cronologia del Test.
/// </summary>
public sealed class TestMergeDecisionsWindow : Window
{
    private readonly RecordMergePlan _plan;
    private readonly RecordMergeChoices _scelte = new();
    private readonly Button _conferma = new();
    private readonly TextBlock _stato = new() { TextWrapping = TextWrapping.Wrap };

    public MergeUiResult? Esito { get; private set; }

    private static string T(string it, string en, string fr) => LocalizationService.Instance.Scegli(it, en, fr);

    public TestMergeDecisionsWindow(MergeUiRequest richiesta)
    {
        _plan = richiesta.Plan;
        Title = T("Conflitto fra copie del Test", "Test copies conflict", "Conflit entre copies du Test") + " · " + richiesta.Titolo;
        Width = 900; Height = 600; MinWidth = 640; MinHeight = 420; MaxWidth = 1024; MaxHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "TestMerge_Window");
        AdattamentoDialogo.Registra(this);

        var decisioni = _plan.DecisioniManuali;
        var corpo = new StackPanel { Spacing = 10 };
        corpo.Children.Add(new TextBlock { Text = richiesta.Titolo, FontWeight = FontWeight.Bold, FontSize = 18 });

        var riepilogo = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15 };
        AutomationProperties.SetAutomationId(riepilogo, "TestMerge_Riepilogo");
        if (_plan.RevisioniUnite is { } unite)
            riepilogo.Text = string.Format(T("Unite {0} REV da 2 copie, nessun dato scartato.",
                "{0} REV merged from 2 copies, no data discarded.", "{0} REV fusionnées à partir de 2 copies, aucune donnée écartée."), unite.Revisioni.Count);
        else if (decisioni.Count == 0)
            riepilogo.Text = T("Le due copie non hanno differenze da decidere.", "The two copies have nothing to decide.", "Les deux copies n'ont rien à décider.");
        if (!string.IsNullOrEmpty(riepilogo.Text)) corpo.Children.Add(riepilogo);

        if (decisioni.Count > 0)
        {
            corpo.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold,
                Text = decisioni.Count == 1
                    ? T("Resta 1 decisione: scegli quale valore tenere.", "1 decision left: choose which value to keep.", "1 décision restante : choisissez la valeur à conserver.")
                    : string.Format(T("Restano {0} decisioni: per ciascuna scegli quale valore tenere.", "{0} decisions left: for each one choose which value to keep.",
                        "{0} décisions restantes : pour chacune, choisissez la valeur à conserver."), decisioni.Count)
            });
            if (_plan.RevisioniUnite == null && !string.IsNullOrWhiteSpace(_plan.Nota))
                corpo.Children.Add(new TextBlock { Text = _plan.Nota, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            foreach (var d in decisioni) corpo.Children.Add(RigaDecisione(d));
        }
        corpo.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12,
            Text = T("Nulla viene cancellato: le versioni originali e i valori non scelti restano nel cestino di recupero e nella cronologia del Test.",
                "Nothing is deleted: the original versions and the values not chosen stay in the recovery bin and in the Test history.",
                "Rien n'est supprimé : les versions d'origine et les valeurs non retenues restent dans la corbeille de récupération et l'historique du Test.")
        });

        var intera = new Button { Content = T("Scegli la versione intera…", "Choose a whole version…", "Choisir une version entière…"), Classes = { "Secondary" } };
        AutomationProperties.SetAutomationId(intera, "TestMerge_VersioneIntera_Button");
        intera.Click += (_, _) => { Esito = new MergeUiResult(MergeUiEsito.VersioneIntera, null); Close(Esito); };
        var annulla = new Button { Content = T("Annulla", "Cancel", "Annuler"), Classes = { "Secondary" } };
        AutomationProperties.SetAutomationId(annulla, "TestMerge_Annulla_Button");
        annulla.Click += (_, _) => { Esito = new MergeUiResult(MergeUiEsito.Annulla, null); Close(Esito); };
        _conferma.Content = T("Conferma e salva", "Confirm and save", "Confirmer et enregistrer");
        _conferma.Classes.Add("Primary");
        AutomationProperties.SetAutomationId(_conferma, "TestMerge_Conferma_Button");
        _conferma.Click += (_, _) => { Esito = new MergeUiResult(MergeUiEsito.Unisci, _scelte); Close(Esito); };

        var piede = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, 10, 0, 0) };
        piede.Children.Add(intera);
        Grid.SetColumn(_stato, 1); _stato.VerticalAlignment = VerticalAlignment.Center; piede.Children.Add(_stato);
        Grid.SetColumn(annulla, 2); piede.Children.Add(annulla);
        Grid.SetColumn(_conferma, 3); piede.Children.Add(_conferma);
        AutomationProperties.SetAutomationId(_stato, "TestMerge_Stato");

        var radice = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(piede, Dock.Bottom);
        radice.Children.Add(piede);
        radice.Children.Add(new ScrollViewer { Content = corpo, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = radice;

        Closing += (_, _) => Esito ??= new MergeUiResult(MergeUiEsito.Annulla, null);
        Aggiorna();
    }

    private Control RigaDecisione(DecisioneManuale d)
    {
        var id = "TestMerge_Decisione_" + d.Campo;
        var gruppo = "dec_" + d.Campo;
        RadioButton Lato(string titolo, string valore, string autore, DateTime? data, RecordFieldChoice scelta, string suffisso)
        {
            var pannello = new StackPanel { Spacing = 2 };
            pannello.Children.Add(new TextBlock { Text = titolo, FontWeight = FontWeight.SemiBold });
            pannello.Children.Add(new ScrollViewer
            {
                MaxHeight = 140, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = new TextBlock { Text = string.IsNullOrWhiteSpace(valore) ? T("(vuoto)", "(empty)", "(vide)") : valore, TextWrapping = TextWrapping.Wrap }
            });
            var chi = string.IsNullOrWhiteSpace(autore) ? T("autore non registrato", "author not recorded", "auteur non enregistré") : autore;
            pannello.Children.Add(new TextBlock
            {
                Opacity = 0.7, FontSize = 12,
                Text = chi + (data.HasValue ? " · " + data.Value.ToString("dd/MM/yyyy HH:mm") : "")
            });
            var rb = new RadioButton { Content = pannello, GroupName = gruppo, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(rb, id + suffisso);
            rb.IsCheckedChanged += (_, _) =>
            {
                if (rb.IsChecked != true) return;
                _scelte.Scelte[d.Campo] = scelta;
                Aggiorna();
            };
            return rb;
        }
        var mia = Lato(T("Questa copia", "This copy", "Cette copie"), d.ValoreCopiaMia, d.AutoreCopiaMia, d.DataCopiaMia, RecordFieldChoice.Mia, "_Mia");
        var altra = Lato(T("Altra copia", "Other copy", "Autre copie"), d.ValoreAltraCopia, d.AutoreAltraCopia, d.DataAltraCopia, RecordFieldChoice.Loro, "_Altra");

        var griglia = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        griglia.Children.Add(mia);
        Grid.SetColumn(altra, 1); griglia.Children.Add(altra);
        var pannelloRiga = new StackPanel { Spacing = 6 };
        pannelloRiga.Children.Add(new TextBlock { Text = d.Etichetta, FontWeight = FontWeight.SemiBold });
        pannelloRiga.Children.Add(griglia);
        var scheda = new Border { Child = pannelloRiga, Padding = new Thickness(12) };
        scheda.Classes.Add("Card");

        // Preselezione: il valore piu' recente (se le date sono note e diverse); altrimenti la scelta resta esplicita.
        if (d.DataCopiaMia.HasValue && d.DataAltraCopia.HasValue && d.DataCopiaMia != d.DataAltraCopia)
            (d.DataAltraCopia > d.DataCopiaMia ? altra : mia).IsChecked = true;
        else if (d.DataCopiaMia.HasValue != d.DataAltraCopia.HasValue)
            (d.DataAltraCopia.HasValue ? altra : mia).IsChecked = true;
        return scheda;
    }

    private void Aggiorna()
    {
        var mancanti = _plan.DecisioniManuali.Count(d => !_scelte.Scelte.ContainsKey(d.Campo));
        _conferma.IsEnabled = mancanti == 0;
        _stato.Text = mancanti == 0
            ? T("Tutto deciso.", "All decided.", "Tout est décidé.")
            : string.Format(T("Decisioni mancanti: {0}.", "Decisions missing: {0}.", "Décisions manquantes : {0}."), mancanti);
    }

    public static async Task<MergeUiResult> MostraAsync(Window owner, MergeUiRequest richiesta)
    {
        var w = new TestMergeDecisionsWindow(richiesta);
        var esito = await w.ShowDialog<MergeUiResult?>(owner);
        return esito ?? w.Esito ?? new MergeUiResult(MergeUiEsito.Annulla, null);
    }
}
