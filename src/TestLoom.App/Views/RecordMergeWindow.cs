using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>
/// Confronto grafico di due versioni dello STESSO record: a sinistra i soli campi diversi, uno per riga, con la versione
/// "mia" e quella "altra" affiancate e la scelta (tieni la mia / tieni l'altra / scrivi a mano); a destra l'anteprima del
/// record risultante, con TUTTI i campi nell'ordine del record e quelli modificati evidenziati. Nessun JSON e' mai mostrato.
/// "Applica" si abilita solo quando ogni campo diverso ha una scelta valida: nessuna scelta implicita.
/// </summary>
public sealed class RecordMergeWindow : Window
{
    private readonly RecordMergePlan _plan;
    private readonly RecordMergeChoices _scelte = new();
    private readonly StackPanel _anteprima = new() { Spacing = 6 };
    private readonly Button _applica = new();
    private readonly TextBlock _stato = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, TextBox> _risultati = new(StringComparer.Ordinal);
    private bool _aggiornamento;

    public MergeUiResult? Esito { get; private set; }

    private static string T(string it, string en, string fr) => LocalizationService.Instance.Scegli(it, en, fr);

    public RecordMergeWindow(MergeUiRequest richiesta)
    {
        _plan = richiesta.Plan;
        Title = T("Risolvi conflitto", "Resolve conflict", "Résoudre le conflit") + " · " + richiesta.Titolo;
        Width = 1180; Height = 760; MinWidth = 760; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "RecordMerge_Window");
        AdattamentoDialogo.Registra(this, grande: true);

        var intestazione = new StackPanel { Spacing = 4 };
        intestazione.Children.Add(new TextBlock { Text = richiesta.Titolo, FontWeight = FontWeight.Bold, FontSize = 18 });
        if (!string.IsNullOrWhiteSpace(richiesta.Sottotitolo))
            intestazione.Children.Add(new TextBlock { Text = richiesta.Sottotitolo, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        intestazione.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = T("Per ogni campo diverso scegli quale versione tenere, oppure scrivi tu il valore. A destra vedi come diventerà il record. Nulla viene salvato finché non premi \"Applica e salva\"; le versioni originali finiscono nel cestino di recupero.",
                     "For each differing field choose which version to keep, or type the value yourself. On the right you see what the record will look like. Nothing is saved until you press \"Apply and save\"; the original versions go to the recovery bin.",
                     "Pour chaque champ différent, choisissez la version à conserver ou saisissez la valeur. À droite, vous voyez le résultat. Rien n'est enregistré avant « Appliquer et enregistrer » ; les versions d'origine vont dans la corbeille de récupération.")
        });

        if (!string.IsNullOrWhiteSpace(_plan.Nota))
            intestazione.Children.Add(new TextBlock { Text = _plan.Nota, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SteelBlue, FontWeight = FontWeight.SemiBold });
        var barra = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        barra.Children.Add(BottoneTutti(T("Tutti: tieni i miei", "All: keep mine", "Tout : garder les miens"), RecordFieldChoice.Mia, "RecordMerge_TuttiMia_Button"));
        barra.Children.Add(BottoneTutti(T("Tutti: tieni gli altri", "All: keep theirs", "Tout : garder les autres"), RecordFieldChoice.Loro, "RecordMerge_TuttiLoro_Button"));

        var colonne = new StackPanel { Spacing = 10 };
        colonne.Children.Add(IntestazioneColonne(richiesta.EtichettaMia, richiesta.EtichettaAltra));
        foreach (var campo in _plan.Differences) colonne.Children.Add(RigaCampo(campo));
        var uguali = _plan.Fields.Count - _plan.Differences.Count;
        if (uguali > 0)
            colonne.Children.Add(new TextBlock
            {
                Opacity = 0.7,
                Text = string.Format(T("{0} campi sono uguali nelle due versioni e non richiedono scelte.",
                    "{0} fields are the same in both versions and need no choice.", "{0} champs identiques dans les deux versions, aucun choix requis."), uguali)
            });

        var sinistra = new DockPanel();
        DockPanel.SetDock(barra, Dock.Top);
        barra.Margin = new Thickness(0, 0, 0, 8);
        sinistra.Children.Add(barra);
        sinistra.Children.Add(new ScrollViewer { Content = colonne, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });

        var destra = new DockPanel();
        var titoloAnteprima = new TextBlock
        {
            Text = T("Anteprima del record risultante", "Preview of the resulting record", "Aperçu de l'enregistrement résultant"),
            FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6)
        };
        DockPanel.SetDock(titoloAnteprima, Dock.Top);
        destra.Children.Add(titoloAnteprima);
        destra.Children.Add(new ScrollViewer { Content = _anteprima, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        AutomationProperties.SetAutomationId(_anteprima, "RecordMerge_Anteprima");

        var corpo = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,12,*") };
        corpo.Children.Add(sinistra);
        var separatore = new Border { Width = 1, Background = UiTema.Separatore, Opacity = 0.35, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumn(separatore, 1); corpo.Children.Add(separatore);
        var destraBordo = new Border { Child = destra, Padding = new Thickness(4) };
        Grid.SetColumn(destraBordo, 2);
        corpo.Children.Add(destraBordo);

        var intera = new Button { Content = T("Scegli la versione intera…", "Choose a whole version…", "Choisir une version entière…"), Classes = { "Secondary" } };
        AutomationProperties.SetAutomationId(intera, "RecordMerge_VersioneIntera_Button");
        intera.Click += (_, _) => { Esito = new MergeUiResult(MergeUiEsito.VersioneIntera, null); Close(Esito); };
        var annulla = new Button { Content = T("Annulla", "Cancel", "Annuler"), Classes = { "Secondary" } };
        AutomationProperties.SetAutomationId(annulla, "RecordMerge_Annulla_Button");
        annulla.Click += (_, _) => { Esito = new MergeUiResult(MergeUiEsito.Annulla, null); Close(Esito); };
        _applica.Content = T("Applica e salva", "Apply and save", "Appliquer et enregistrer");
        _applica.Classes.Add("Primary");
        AutomationProperties.SetAutomationId(_applica, "RecordMerge_Applica_Button");
        _applica.Click += (_, _) =>
        {
            Esito = new MergeUiResult(MergeUiEsito.Unisci, _scelte);
            Close(Esito);
        };
        var piede = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, 10, 0, 0) };
        piede.Children.Add(intera);
        Grid.SetColumn(_stato, 1); _stato.VerticalAlignment = VerticalAlignment.Center; piede.Children.Add(_stato);
        Grid.SetColumn(annulla, 2); piede.Children.Add(annulla);
        Grid.SetColumn(_applica, 3); piede.Children.Add(_applica);

        var radice = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(intestazione, Dock.Top); intestazione.Margin = new Thickness(0, 0, 0, 12);
        DockPanel.SetDock(piede, Dock.Bottom);
        radice.Children.Add(intestazione);
        radice.Children.Add(piede);
        radice.Children.Add(corpo);
        Content = radice;

        Closing += (_, _) => Esito ??= new MergeUiResult(MergeUiEsito.Annulla, null);
        Aggiorna();
    }

    private Button BottoneTutti(string testo, RecordFieldChoice scelta, string id)
    {
        var b = new Button { Content = testo, Classes = { "Secondary" } };
        AutomationProperties.SetAutomationId(b, id);
        b.Click += (_, _) =>
        {
            foreach (var f in _plan.Differences) _scelte.Scelte[f.Name] = scelta;
            RicostruisciRighe();
        };
        return b;
    }

    // Le righe si ricostruiscono solo per i pulsanti "tutti": piu' semplice e a prova di stato incoerente.
    private void RicostruisciRighe()
    {
        _aggiornamento = true;
        foreach (var (nome, box) in _risultati)
        {
            var campo = _plan.Fields.First(f => f.Name == nome);
            if (campo.Differs && _scelte.Scelte.TryGetValue(nome, out var sc)) ImpostaRadio(nome, sc);
            box.Text = TestoRisultato(campo);
        }
        _aggiornamento = false;
        Aggiorna();
    }

    private readonly Dictionary<string, Dictionary<RecordFieldChoice, RadioButton>> _radio = new(StringComparer.Ordinal);

    private void ImpostaRadio(string nome, RecordFieldChoice scelta)
    {
        if (_radio.TryGetValue(nome, out var gruppo) && gruppo.TryGetValue(scelta, out var rb)) rb.IsChecked = true;
    }

    private static Control IntestazioneColonne(string mia, string altra)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 10 };
        g.Children.Add(new TextBlock { Text = mia, FontWeight = FontWeight.SemiBold });
        var b = new TextBlock { Text = altra, FontWeight = FontWeight.SemiBold }; Grid.SetColumn(b, 1); g.Children.Add(b);
        var c = new TextBlock { Text = T("Risultato", "Result", "Résultat"), FontWeight = FontWeight.SemiBold }; Grid.SetColumn(c, 2); g.Children.Add(c);
        return g;
    }

    private string TestoRisultato(RecordMergeField campo)
    {
        if (!_scelte.Scelte.TryGetValue(campo.Name, out var s)) return T("(da scegliere)", "(to be chosen)", "(à choisir)");
        return s switch
        {
            RecordFieldChoice.Mia => campo.MiaTesto,
            RecordFieldChoice.Loro => campo.LoroTesto,
            _ => _scelte.ValoriManuali.TryGetValue(campo.Name, out var m) ? m : campo.MiaModifica
        };
    }

    private Control RigaCampo(RecordMergeField campo)
    {
        var id = "RecordMerge_Campo_" + campo.Name;
        var mia = LatoSolaLettura(campo.MiaTesto, id + "_Mia");
        var loro = LatoSolaLettura(campo.LoroTesto, id + "_Altra");
        var risultato = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, IsReadOnly = true,
            Text = TestoRisultato(campo)
        };
        AutomationProperties.SetAutomationId(risultato, id + "_Risultato");
        _risultati[campo.Name] = risultato;
        risultato.TextChanged += (_, _) =>
        {
            if (_aggiornamento || risultato.IsReadOnly) return;
            _scelte.ValoriManuali[campo.Name] = risultato.Text ?? "";
            Aggiorna();
        };

        var gruppo = new Dictionary<RecordFieldChoice, RadioButton>();
        _radio[campo.Name] = gruppo;
        var opzioni = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 22 };
        RadioButton Opzione(string testo, RecordFieldChoice scelta, string suffisso)
        {
            var rb = new RadioButton { Content = testo, GroupName = campo.Name };
            AutomationProperties.SetAutomationId(rb, id + suffisso);
            rb.IsCheckedChanged += (_, _) =>
            {
                if (_aggiornamento || rb.IsChecked != true) return;
                _scelte.Scelte[campo.Name] = scelta;
                risultato.IsReadOnly = scelta != RecordFieldChoice.Manuale;
                _aggiornamento = true;
                if (scelta == RecordFieldChoice.Manuale)
                {
                    if (!_scelte.ValoriManuali.ContainsKey(campo.Name))
                        _scelte.ValoriManuali[campo.Name] = string.IsNullOrEmpty(campo.MiaModifica) ? campo.LoroModifica : campo.MiaModifica;
                    risultato.Text = _scelte.ValoriManuali[campo.Name];
                }
                else risultato.Text = TestoRisultato(campo);
                _aggiornamento = false;
                Aggiorna();
            };
            gruppo[scelta] = rb;
            return rb;
        }
        opzioni.Children.Add(Opzione(T("Tieni la mia", "Keep mine", "Garder la mienne"), RecordFieldChoice.Mia, "_TieniMia"));
        opzioni.Children.Add(Opzione(T("Tieni l'altra", "Keep theirs", "Garder l'autre"), RecordFieldChoice.Loro, "_TieniAltra"));
        if (campo.EditKind != RecordEditKind.Nessuno)
            opzioni.Children.Add(Opzione(T("Scrivi a mano", "Type manually", "Saisir à la main"), RecordFieldChoice.Manuale, "_Manuale"));

        var griglia = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 10 };
        griglia.Children.Add(mia);
        Grid.SetColumn(loro, 1); griglia.Children.Add(loro);
        Grid.SetColumn(risultato, 2); griglia.Children.Add(risultato);

        var pannello = new StackPanel { Spacing = 6 };
        pannello.Children.Add(new TextBlock { Text = campo.Label, FontWeight = FontWeight.SemiBold });
        pannello.Children.Add(opzioni);
        pannello.Children.Add(griglia);
        if (campo.EditKind == RecordEditKind.ElencoTesto)
            pannello.Children.Add(new TextBlock { Text = T("Un elemento per riga.", "One item per line.", "Un élément par ligne."), Opacity = 0.6, FontSize = 11 });
        var scheda = new Border { Child = pannello, Padding = new Thickness(12) };
        scheda.Classes.Add("Card");
        return scheda;
    }

    private static TextBox LatoSolaLettura(string testo, string id)
    {
        var t = new TextBox { Text = testo, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
        AutomationProperties.SetAutomationId(t, id);
        return t;
    }

    private void Aggiorna()
    {
        var righe = _plan.Preview(_scelte);
        _anteprima.Children.Clear();
        foreach (var r in righe)
        {
            var blocco = new StackPanel { Spacing = 1 };
            var etichetta = r.Cambiata ? $"{r.Label}  ·  {r.Origine}" : r.Label;
            blocco.Children.Add(new TextBlock { Text = etichetta, FontSize = 11, Opacity = r.Cambiata ? 1 : 0.6, FontWeight = r.Cambiata ? FontWeight.SemiBold : FontWeight.Normal });
            // Nessun Foreground locale per i valori validi: un valore null impostato esplicitamente annullerebbe l'ereditarieta'
            // del colore del tema e il testo risulterebbe invisibile (bug del tema scuro). Il colore arriva dal tema della finestra.
            var valore = new TextBlock { Text = r.Valida ? r.Testo : (r.Errore ?? ""), TextWrapping = TextWrapping.Wrap };
            if (!r.Valida) valore.Foreground = Brushes.IndianRed;
            AutomationProperties.SetAutomationId(valore, "RecordMerge_Anteprima_Valore_" + r.Name);
            blocco.Children.Add(valore);
            var riquadro = new Border
            {
                Child = blocco, Padding = new Thickness(8, 4),
                BorderThickness = new Thickness(r.Cambiata ? 3 : 0, 0, 0, 0),
                BorderBrush = r.Valida ? Brushes.SteelBlue : Brushes.IndianRed
            };
            _anteprima.Children.Add(riquadro);
        }
        var incompleti = righe.Count(r => !r.Valida);
        _applica.IsEnabled = incompleti == 0;
        _stato.Text = incompleti == 0
            ? T("Tutti i campi sono stati decisi.", "All fields have been decided.", "Tous les champs ont été décidés.")
            : string.Format(T("Campi ancora da decidere o non validi: {0}.", "Fields still to decide or invalid: {0}.", "Champs à décider ou invalides : {0}."), incompleti);
    }

    /// <summary>Mostra la finestra come dialogo e restituisce l'esito (annulla se chiusa con la X).</summary>
    public static async Task<MergeUiResult> MostraAsync(Window owner, MergeUiRequest richiesta)
    {
        // Conflitti fra copie di un Test: finestra semplificata (riepilogo oppure solo le decisioni rimaste).
        if (richiesta.Plan.Kind == RecordMergeKind.TestCase) return await TestMergeDecisionsWindow.MostraAsync(owner, richiesta);
        var w = new RecordMergeWindow(richiesta);
        var esito = await w.ShowDialog<MergeUiResult?>(owner);
        return esito ?? w.Esito ?? new MergeUiResult(MergeUiEsito.Annulla, null);
    }
}
