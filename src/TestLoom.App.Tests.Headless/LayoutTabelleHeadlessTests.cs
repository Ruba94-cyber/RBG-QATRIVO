using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;
using Avalonia.Interactivity;

namespace TestLoom.App.Tests;

/// <summary>Pagine con tabella in headless (Category=LayoutHeadless): la pagina e' montata nella shell reale alla dimensione
/// data, con dati demo sintetici. Misura le righe intere visibili della tabella, la posizione della paginazione e le colonne a
/// bassa priorita'. I valori minimi sono quelli del piano UI (1280x672 caso peggiore).</summary>
internal static class TabellaHeadless
{
    /// <summary>Righe sintetiche minime: ID, descrizione ed componente bastano a riempire la tabella.</summary>
    internal static List<TestCase> RigheSintetiche(int quante) =>
        Enumerable.Range(1, quante).Select(i => new TestCase
        {
            Id = $"ST-{i:000}",
            DescrizioneBreve = $"Test sintetico numero {i}",
            Componente = "Componente-DEMO",
        }).ToList();

    /// <summary>Righe della griglia che stanno per intero nell'area visibile (sotto l'intestazione, sopra la barra di scorrimento
    /// orizzontale se presente). Conta solo le righe materializzate: la griglia virtualizza le altre.</summary>
    internal static int RigheIntereVisibili(DataGrid griglia)
    {
        var fineIntestazione = griglia.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Select(h => h.TranslatePoint(new Point(0, h.Bounds.Height), griglia)?.Y ?? 0)
            .DefaultIfEmpty(0).Max();
        var barreOrizzontali = griglia.GetVisualDescendants().OfType<ScrollBar>()
            .Where(s => s.Orientation == Orientation.Horizontal && s.IsEffectivelyVisible)
            .Sum(s => s.Bounds.Height);
        var fondo = griglia.Bounds.Height - 2 - barreOrizzontali;
        return griglia.GetVisualDescendants().OfType<DataGridRow>()
            .Count(r => r.IsEffectivelyVisible && r.TranslatePoint(new Point(0, 0), griglia) is { } p
                        && p.Y >= fineIntestazione - 0.5 && p.Y + r.Bounds.Height <= fondo + 0.5);
    }

    /// <summary>Intestazioni di colonna il cui testo non sta per intero nell'area assegnata (testo troncato).</summary>
    internal static List<string> IntestazioniTroncate(DataGrid griglia)
    {
        var troncate = new List<string>();
        foreach (var intestazione in griglia.GetVisualDescendants().OfType<DataGridColumnHeader>().Where(h => h.IsEffectivelyVisible && h.Bounds.Width > 0))
            foreach (var testo in intestazione.GetVisualDescendants().OfType<TextBlock>().Where(t => !string.IsNullOrEmpty(t.Text)))
            {
                testo.Measure(Size.Infinity);
                if (testo.Bounds.Width + 1 < testo.DesiredSize.Width) troncate.Add($"'{testo.Text}' {testo.Bounds.Width:0} px su {testo.DesiredSize.Width:0} px (colonna {griglia.Columns.FirstOrDefault(c => c.Header?.ToString() == testo.Text)?.ActualWidth:0})");
            }
        return troncate;
    }

    /// <summary>Vero se il pulsante e' sotto il bordo inferiore della tabella (paginazione sotto, non sopra).</summary>
    internal static bool SottoLaTabella(Control elemento, DataGrid griglia, Window finestra)
    {
        var inizio = elemento.TranslatePoint(new Point(0, 0), finestra);
        var fineTabella = griglia.TranslatePoint(new Point(0, griglia.Bounds.Height), finestra);
        return inizio is { } a && fineTabella is { } b && a.Y >= b.Y - 0.5;
    }
}

/// <summary>Lista Test (6.6.1): pannello filtri comprimibile, chip a pannello chiuso, colonne a bassa priorita' in Compatto,
/// paginazione sotto la tabella. Stessi AutomationId della pagina precedente.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class LayoutListaTestHeadlessTests
{
    [Theory]
    [InlineData(1280, 672, false, 12)]  // caso peggiore, filtri chiusi: almeno 12 righe intere
    [InlineData(1280, 672, true, 5)]    // caso peggiore, filtri aperti: almeno 5 righe intere
    [InlineData(1920, 1080, true, 14)]  // Ampio: la tabella usa lo spazio residuo
    public void ListaTest_RigheVisibiliEPaginazioneSottoLaTabella(int larghezza, int altezza, bool filtriAperti, int minimo) =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var stato = TestStateFactory.Create();
            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, larghezza, altezza);
            var lista = new TestCaseListViewModel(stato, _ => { });
            lista.Righe.ReplaceWith(TabellaHeadless.RigheSintetiche(60));
            lista.FiltriVisibili = filtriAperti;
            var vista = new TestCaseListView { DataContext = lista };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
                LayoutHeadlessSessione.Assesta(finestra);

                var griglia = vista.FindControl<DataGrid>("TabellaTest")!;
                var righe = TabellaHeadless.RigheIntereVisibili(griglia);
                Assert.True(righe >= minimo,
                    $"Lista Test {larghezza}x{altezza}, filtri {(filtriAperti ? "aperti" : "chiusi")}: {righe} righe intere, attese almeno {minimo}");
                Assert.True(griglia.Bounds.Height >= LayoutAdattivo.Token(LayoutAdattivo.ModoCorrente).AltezzaMinimaTabella - 0.5,
                    $"tabella alta {griglia.Bounds.Height} px, sotto l'altezza minima del token");

                var paginazione = vista.GetVisualDescendants().OfType<Button>()
                    .Single(b => AutomationProperties.GetAutomationId(b) == "TestList_PaginaSuccessiva_Button");
                Assert.True(TabellaHeadless.SottoLaTabella(paginazione, griglia, finestra), "paginazione sopra la tabella");
            }
            finally
            {
                finestra.Hide();
            }
        });

    [Fact]
    public void ListaTest_InCompattoLeColonneABassaPrioritaPartonoNascosteSenzaPreferenza() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var stato = TestStateFactory.Create();
            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 672);
            var lista = new TestCaseListViewModel(stato, _ => { });
            lista.Righe.ReplaceWith(TabellaHeadless.RigheSintetiche(5));
            var vista = new TestCaseListView { DataContext = lista };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
                LayoutHeadlessSessione.Assesta(finestra);
                var griglia = vista.FindControl<DataGrid>("TabellaTest")!;
                var bassaPriorita = griglia.Columns.Where(TableLayoutPreferences.GetBassaPriorita).ToList();
                Assert.Equal(4, bassaPriorita.Count);
                Assert.All(bassaPriorita, c => Assert.False(c.IsVisible));
                Assert.True(griglia.Columns.Where(c => !TableLayoutPreferences.GetBassaPriorita(c)).All(c => c.IsVisible),
                    "una colonna di priorita' normale e' nascosta");
            }
            finally
            {
                finestra.Hide();
            }
        });

    [Fact]
    public void ListaTest_ChipRimuoveSoloIlSuoFiltro() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var lista = new TestCaseListViewModel(TestStateFactory.Create(), _ => { });
            lista.FiltriVisibili = false;
            lista.FiltroSoloSenzaEcu = true;
            lista.FiltroSoloImportatoUdt = true;
            Assert.True(lista.MostraChipFiltri);
            Assert.Equal(2, lista.ChipFiltriAttivi.Count);

            lista.ChipFiltriAttivi[0].RimuoviCommand.Execute(null);

            Assert.False(lista.FiltroSoloSenzaEcu);
            Assert.True(lista.FiltroSoloImportatoUdt);
            Assert.Single(lista.ChipFiltriAttivi);
            lista.FiltriVisibili = true;
            Assert.False(lista.MostraChipFiltri);   // a pannello aperto i chip non compaiono
        });

    [Fact]
    public void ListaTest_StatoPannelloFiltriRicordatoPerOperatore() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var stato = TestStateFactory.Create();
            stato.Config.OperatoreAttivo = "Operatore A";
            new TestCaseListViewModel(stato, _ => { }).FiltriVisibili = false;

            Assert.False(new TestCaseListViewModel(stato, _ => { }).FiltriVisibili);      // stesso operatore: ricordato
            stato.Config.OperatoreAttivo = "Operatore B";
            Assert.True(new TestCaseListViewModel(stato, _ => { }).FiltriVisibili);       // altro operatore: predefinito (aperto)
        });
}

/// <summary>Anomalie/CR (6.6.1) in headless: tabella in riga * con altezza minima, colonna azioni dentro la griglia, intestazioni
/// non troncate, chip che annullano un solo filtro. Dati sintetici: righe NC con solo ID e descrizione.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class LayoutAnomalieHeadlessTests
{
    private static List<NcRiga> RigheNcSintetiche(int quante) =>
        Enumerable.Range(1, quante).Select(i => new NcRiga
        {
            Nc = new NonConformita { Id = $"NC-{i:0000}", Descrizione = $"Anomalia sintetica numero {i}" },
            TestCaseIdVisualizzato = $"ST-{i:000}",
        }).ToList();

    /// <summary>Colonna Ticket (6.6.1 corretta): apertura diretta del ticket dalla tabella, quindi visibile per default anche in Compatto
    /// e per intero dentro la griglia (senza scorrimento orizzontale), con il link del ticket attivo sulla prima riga.</summary>
    [Theory]
    [InlineData(1280, 672)]   // caso peggiore (FHD al 150% meno barra)
    [InlineData(1280, 800)]   // risoluzione logica della macchina di test FlaUI
    public void Anomalie_ColonnaTicketVisibilePerDefaultInCompatto(int larghezza, int altezza) =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var stato = TestStateFactory.Create();
            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, larghezza, altezza);
            var nc = new NonConformitaViewModel(stato, _ => { });
            var righe = RigheNcSintetiche(12);
            righe[0].Nc.CollegamentiPolarion.Add(new CollegamentoPolarion { Codice = "TICKET-12345", Url = "https://ticketing.example.test/wi/12345" });
            nc.Righe.ReplaceWith(righe);
            var vista = new NonConformitaView();
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                vista.DataContext = nc;   // dopo la finestra: la pagina legge il modo di layout reale alla creazione
                finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
                LayoutHeadlessSessione.Assesta(finestra);

                Assert.Equal(ModoLayout.Compatto, LayoutAdattivo.ModoCorrente);
                var griglia = vista.FindControl<DataGrid>("AnomalieGrid")!;
                var colonna = griglia.Columns.Single(c => c.Header?.ToString() == "Ticket");
                Assert.False(TableLayoutPreferences.GetBassaPriorita(colonna));
                Assert.True(colonna.IsVisible, $"{larghezza}x{altezza}: la colonna Ticket e' nascosta in Compatto");

                var barraVerticale = griglia.GetVisualDescendants().OfType<ScrollBar>()
                    .Where(s => s.Orientation == Orientation.Vertical && s.IsEffectivelyVisible).Sum(s => s.Bounds.Width);
                var bordoDestro = griglia.Bounds.Width - barraVerticale - 1;

                // Intestazione per intero dentro la griglia: nessuno scorrimento orizzontale per vedere la colonna.
                var testoIntestazione = griglia.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Ticket" && t.GetVisualAncestors().OfType<DataGridColumnHeader>().Any());
                var intestazione = testoIntestazione.GetVisualAncestors().OfType<DataGridColumnHeader>().First();
                var sinistraIntestazione = intestazione.TranslatePoint(new Point(0, 0), griglia)!.Value.X;
                Assert.True(sinistraIntestazione >= -0.5 && sinistraIntestazione + intestazione.Bounds.Width <= bordoDestro + 0.5,
                    $"{larghezza}x{altezza}: intestazione Ticket fuori dalla griglia (da {sinistraIntestazione:0} a {sinistraIntestazione + intestazione.Bounds.Width:0}, bordo {bordoDestro:0})");

                // Link del ticket: presente, attivo (URL web valida) e dentro la griglia.
                var link = griglia.GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == "TICKET-12345");
                Assert.True(link.IsEffectivelyVisible && link.IsEnabled, $"{larghezza}x{altezza}: il link TICKET-12345 non e' attivo nella tabella");
                var bordoLink = link.TranslatePoint(new Point(link.Bounds.Width, 0), griglia)!.Value.X;
                Assert.True(bordoLink <= bordoDestro + 0.5, $"{larghezza}x{altezza}: il link TICKET-12345 esce dalla griglia a destra");
            }
            finally
            {
                finestra.Hide();
            }
        });

    [Theory]
    [InlineData(true, 3, FiltroTipoAnomaliaOCr.Entrambi)]    // pannello aperto, CR chiusa: almeno 3 righe intere
    [InlineData(false, 5, FiltroTipoAnomaliaOCr.Entrambi)]   // pannello chiuso, CR chiusa: la tabella prende quasi tutta l'area
    [InlineData(false, 9, FiltroTipoAnomaliaOCr.Anomalie)]   // solo anomalie: la tabella prende tutta l'area (cromo fisso della pagina ~190 px)
    public void Anomalie_TabellaInRigaAzioniDentroLaGrigliaEIntestazioniIntere(bool pannelloAperto, int minimoRighe, FiltroTipoAnomaliaOCr tipo) =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var stato = TestStateFactory.Create();
            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 672);
            var nc = new NonConformitaViewModel(stato, _ => { });
            ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", "0");
            nc.FiltroTipo = tipo;
            var righeSintetiche = RigheNcSintetiche(40);
            righeSintetiche[0].Nc.DaSegnalazioneRete = true; // rende visibile anche l'azione Modifica
            nc.Righe.ReplaceWith(righeSintetiche);
            var vista = new NonConformitaView();
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                vista.DataContext = nc;   // dopo la finestra: la pagina legge il modo di layout reale alla creazione
                finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
                LayoutHeadlessSessione.Assesta(finestra);
                var pannello = vista.FindControl<StackPanel>("PannelloFiltriNc")!;
                if (pannello.IsVisible != pannelloAperto)
                {
                    vista.FindControl<Button>("BottoneFiltriNc")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    LayoutHeadlessSessione.Assesta(finestra);
                }

                var griglia = vista.FindControl<DataGrid>("AnomalieGrid")!;
                var colonnaId = griglia.Columns.Single(c => c.SortMemberPath == "Id");
                var colonnaAzioni = griglia.Columns.Single(c => c.Header?.ToString() == LocalizationService.Instance.Traduci("ColonnaAzioni"));
                var colonnaTicket = griglia.Columns.Single(c => c.Header?.ToString() == LocalizationService.Instance.Traduci("ColTicketNc"));
                Assert.Equal(0, griglia.FrozenColumnCount);
                Assert.Equal(0, colonnaId.DisplayIndex);
                Assert.Equal(1, colonnaAzioni.DisplayIndex);
                Assert.Equal(2, colonnaTicket.DisplayIndex);
                Assert.True(colonnaId.IsVisible && colonnaAzioni.IsVisible, "ID e azioni devono restare visibili");
                var righe = TabellaHeadless.RigheIntereVisibili(griglia);
                Assert.True(righe >= minimoRighe,
                    $"Anomalie 1280x672, pannello {(pannelloAperto ? "aperto" : "chiuso")}: {righe} righe intere, attese almeno {minimoRighe}");
                // La griglia Anomalie ha volutamente MinHeight 160 (NonConformitaView.axaml: 200 coprirebbe la paginazione con la CR aperta a 1280x672).
                Assert.True(griglia.MinHeight <= 160 && griglia.Bounds.Height >= griglia.MinHeight - 0.5,
                    $"tabella alta {griglia.Bounds.Height} px, sotto il MinHeight dichiarato ({griglia.MinHeight})");

                // Flag/info non possono essere riconoscibili solo al passaggio del mouse: le due azioni
                // anomalia devono mostrare etichette localizzate e conservare gli AutomationId esistenti.
                var modificaSegnalazione = griglia.GetVisualDescendants().OfType<Button>()
                    .First(b => AutomationProperties.GetAutomationId(b) == "Anomalie_Riga_ModificaRapida_Button");
                var dettaglioAnomalia = griglia.GetVisualDescendants().OfType<Button>()
                    .First(b => AutomationProperties.GetAutomationId(b) == "Anomalie_Riga_Dettaglio_Button");
                Assert.Equal(LocalizationService.Instance.Traduci("ModificaRapidaAzione"), modificaSegnalazione.Content?.ToString());
                Assert.Equal(LocalizationService.Instance.Traduci("ApriChangeRequestBtn"), dettaglioAnomalia.Content?.ToString());
                Assert.NotEqual("⚑", modificaSegnalazione.Content?.ToString());
                Assert.NotEqual("ⓘ", dettaglioAnomalia.Content?.ToString());
                Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(modificaSegnalazione) as string));
                Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(dettaglioAnomalia) as string));
                Assert.True(modificaSegnalazione.Bounds.Width >= 68 - 0.5,
                    $"pulsante Modifica largo {modificaSegnalazione.Bounds.Width:0.0}px, minimo 68px");
                Assert.True(dettaglioAnomalia.Bounds.Width >= 60 - 0.5,
                    $"pulsante Dettagli largo {dettaglioAnomalia.Bounds.Width:0.0}px, minimo 60px");

                // Pulsanti di riga tutti dentro la griglia (anche in orizzontale: la colonna azioni non esce a destra).
                var barraVerticale = griglia.GetVisualDescendants().OfType<ScrollBar>()
                    .Where(s => s.Orientation == Orientation.Vertical && s.IsEffectivelyVisible).Sum(s => s.Bounds.Width);
                var bordoDestro = griglia.Bounds.Width - barraVerticale - 1;
                foreach (var pulsante in griglia.GetVisualDescendants().OfType<Button>().Where(b =>
                             b.IsEffectivelyVisible && (AutomationProperties.GetAutomationId(b) ?? "").StartsWith("Anomalie_Riga_", StringComparison.Ordinal)))
                {
                    var destra = pulsante.TranslatePoint(new Point(pulsante.Bounds.Width, 0), griglia);
                    Assert.True(destra is { } d && d.X <= bordoDestro + 0.5,
                        $"pulsante di riga '{AutomationProperties.GetAutomationId(pulsante)}' esce dalla griglia: destra={destra?.X:0.0}, " +
                        $"bottone={pulsante.Bounds.Width:0.0}, griglia={griglia.Bounds.Width:0.0}, bordo={bordoDestro:0.0}, scrollbar={barraVerticale:0.0}, " +
                        $"colonne={string.Join(", ", griglia.Columns.Select(c => $"{c.Header}:{c.ActualWidth:0}"))}");
                }

                // Intestazioni: il testo sta per intero nella colonna (niente "Priorit" troncato). Tutte le intestazioni
                // troncate vengono riportate insieme, con la larghezza della colonna e il minimo impostato.
                var troncate = new List<string>();
                foreach (var intestazione in griglia.GetVisualDescendants().OfType<DataGridColumnHeader>().Where(h => h.IsEffectivelyVisible && h.Bounds.Width > 0))
                    foreach (var testo in intestazione.GetVisualDescendants().OfType<TextBlock>().Where(t => !string.IsNullOrEmpty(t.Text)))
                    {
                        testo.Measure(Size.Infinity);
                        if (testo.Bounds.Width + 1 >= testo.DesiredSize.Width) continue;
                        var colonna = griglia.Columns.FirstOrDefault(c => c.Header?.ToString() == testo.Text);
                        troncate.Add($"'{testo.Text}' {testo.Bounds.Width:0} px su {testo.DesiredSize.Width:0} px (colonna {colonna?.ActualWidth:0}, minimo {colonna?.MinWidth})");
                    }
                Assert.True(troncate.Count == 0, "intestazioni troncate: " + string.Join("; ", troncate));

                // La scheda anomalie non invade quella CR: la sua paginazione sta sopra l'inizio della scheda CR (quando e' visibile).
                var cr = vista.FindControl<Grid>("AreaSchede")!.Children.OfType<Border>().Last();
                if (cr.IsEffectivelyVisible)
                {
                    var paginaAnomalie = vista.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Anomalie_PaginaSuccessiva_Button");
                    var fondoPaginazione = paginaAnomalie.TranslatePoint(new Point(0, paginaAnomalie.Bounds.Height), finestra)!.Value.Y;
                    var inizioCr = cr.TranslatePoint(new Point(0, 0), finestra)!.Value.Y;
                    Assert.True(fondoPaginazione <= inizioCr + 0.5, $"la scheda anomalie invade la CR: paginazione fino a {fondoPaginazione:0}, CR da {inizioCr:0}");
                }

                // Nessuno scroll orizzontale della pagina: la vista sta nella larghezza della finestra.
                Assert.True(vista.Bounds.Width <= finestra.ClientSize.Width + 0.5, "la pagina Anomalie esce in orizzontale");
            }
            finally
            {
                finestra.Hide();
            }
        });

    [Fact]
    public void Anomalie_ColonneIdAzioniETicketSeguonoOrdinePersonaleSalvato()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var stato = TestStateFactory.Create();
            var vista = new NonConformitaView();
            var grigliaIniziale = vista.FindControl<DataGrid>("AnomalieGrid")!;
            var indiceId = grigliaIniziale.Columns.IndexOf(grigliaIniziale.Columns.Single(c => c.SortMemberPath == "Id"));
            var indiceAzioni = grigliaIniziale.Columns.IndexOf(grigliaIniziale.Columns.Single(c => c.Header?.ToString() == LocalizationService.Instance.Traduci("ColonnaAzioni")));
            var indiceTicket = grigliaIniziale.Columns.IndexOf(grigliaIniziale.Columns.Single(c => c.Header?.ToString() == LocalizationService.Instance.Traduci("ColTicketNc")));
            var ordineSalvato = Enumerable.Range(0, grigliaIniziale.Columns.Count)
                .Reverse()
                .ToArray();
            var indiceNascosto = ordineSalvato.First(i => i != indiceId && i != indiceAzioni && i != indiceTicket);
            var indiciNascosti = new HashSet<int> { indiceNascosto };
            var preferenza = new TableLayoutPreference
            {
                Versione = 2,
                Colonne = ordineSalvato.Select((indice, ordine) => new TableColumnPreference
                {
                    Indice = indice,
                    Ordine = ordine,
                    Visibile = !indiciNascosti.Contains(indice)
                }).ToList()
            };
            var chiave = TableLayoutPreferences.Chiave(stato.Config.OperatoreAttivo, stato.RuoloAttivo, "anomalie");
            stato.Config.LayoutTabelle[chiave] = preferenza;

            ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", "0");
            var nc = new NonConformitaViewModel(stato, _ => { });
            nc.Righe.ReplaceWith(RigheNcSintetiche(1));
            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 672);
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                vista.DataContext = nc;
                finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
                LayoutHeadlessSessione.Assesta(finestra);

                var griglia = vista.FindControl<DataGrid>("AnomalieGrid")!;
                var colonnaId = griglia.Columns[indiceId];
                var colonnaAzioni = griglia.Columns[indiceAzioni];
                var colonnaTicket = griglia.Columns[indiceTicket];
                Assert.Equal(Array.IndexOf(ordineSalvato, indiceId), colonnaId.DisplayIndex);
                Assert.Equal(Array.IndexOf(ordineSalvato, indiceAzioni), colonnaAzioni.DisplayIndex);
                Assert.Equal(Array.IndexOf(ordineSalvato, indiceTicket), colonnaTicket.DisplayIndex);
                Assert.True(colonnaId.IsVisible && colonnaAzioni.IsVisible, "ID e azioni devono seguire la preferenza di visibilita' salvata");
                Assert.True(colonnaTicket.IsVisible, "la visibilita' salvata di Ticket deve restare visibile");
                Assert.False(griglia.Columns[indiceNascosto].IsVisible, "la visibilita' salvata delle altre colonne va mantenuta");
                Assert.Equal(ordineSalvato, griglia.Columns.OrderBy(c => c.DisplayIndex).Select(c => griglia.Columns.IndexOf(c)));

                var preferenzaPersistita = stato.Config.LayoutTabelle[chiave].Colonne;
                Assert.Equal(ordineSalvato, preferenzaPersistita.OrderBy(c => c.Ordine).Select(c => c.Indice));
                Assert.Equal(preferenza.Colonne.Select(c => (c.Indice, c.Ordine, c.Visibile)),
                    preferenzaPersistita.Select(c => (c.Indice, c.Ordine, c.Visibile)));
            }
            finally
            {
                finestra.Hide();
            }
        });
    }

    [Fact]
    public void Anomalie_ChipRimuovonoSoloIlLoroFiltro() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var stato = TestStateFactory.Create();
            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 672);
            var nc = new NonConformitaViewModel(stato, _ => { });
            var vista = new NonConformitaView();
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                vista.DataContext = nc;   // dopo la finestra: la pagina legge il modo di layout reale alla creazione
                finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
                LayoutHeadlessSessione.Assesta(finestra);
                nc.FiltroTesto = "sintetica";
                nc.FiltroTicket = "TICKET-1";
                LayoutHeadlessSessione.Assesta(finestra);

                var chip = Assert.IsType<List<ChipFiltroLista>>(vista.FindControl<ItemsControl>("ChipFiltriNc")!.ItemsSource);
                Assert.Equal(3, chip.Count);   // testo, stato (Aperta di default) ed TICKET
                chip[0].RimuoviCommand.Execute(null);
                LayoutHeadlessSessione.Assesta(finestra);

                Assert.Equal("", nc.FiltroTesto);
                Assert.Equal("TICKET-1", nc.FiltroTicket);
                Assert.Equal(2, Assert.IsType<List<ChipFiltroLista>>(vista.FindControl<ItemsControl>("ChipFiltriNc")!.ItemsSource).Count);
            }
            finally
            {
                finestra.Hide();
            }
        });
}

/// <summary>Archivio (6.6.1) in headless: tabella in riga * con altezza minima, azioni dentro la griglia, intestazioni intere.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class LayoutArchivioHeadlessTests
{
    [Fact]
    public void Archivio_RigheVisibiliAzioniDentroLaGrigliaEIntestazioniIntere() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var dir = Path.Combine(Path.GetTempPath(), "TestLoom_layout_archivio_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var stato = TestStateFactory.Create();
            stato.InizializzaCartella(dir);
            var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
            stato.Master.Operatori.Save(new List<Operatore> { master });
            stato.ImpostaOperatore(master.Nome, "1234");
            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 672);
            var archivio = new ArchivioViewModel(stato);
            foreach (var i in Enumerable.Range(1, 40))
                archivio.Voci.Add(new VoceArchivio
                {
                    IdTecnico = $"id-{i}",
                    TipoEntita = "Test",
                    Etichetta = $"T-OPA-{i:0000}",
                    UltimaModifica = new DateTime(2026, 10, 1).AddHours(i),
                    ModificatoDa = "Operatore Prova A",
                    Ripristina = () => Task.FromResult<string?>(null),
                    EliminaDefinitivamente = () => Task.FromResult<string?>(null),
                });
            var vista = new ArchivioView { DataContext = archivio };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
                LayoutHeadlessSessione.Assesta(finestra);
                LayoutHeadlessSessione.Assesta(finestra);   // secondo passaggio: la barra verticale compare dopo la prima misura delle righe

                var griglia = vista.FindControl<DataGrid>("Archivio_Grid")!;
                var righe = TabellaHeadless.RigheIntereVisibili(griglia);
                Assert.True(righe >= 10, $"Archivio 1280x672: {righe} righe intere, attese almeno 10");
                Assert.True(griglia.Bounds.Height >= LayoutAdattivo.Token(LayoutAdattivo.ModoCorrente).AltezzaMinimaTabella - 0.5);

                var barraVerticale = griglia.GetVisualDescendants().OfType<ScrollBar>()
                    .Where(s => s.Orientation == Orientation.Vertical && s.IsEffectivelyVisible).Sum(s => s.Bounds.Width);
                foreach (var pulsante in griglia.GetVisualDescendants().OfType<Button>().Where(b =>
                             b.IsEffectivelyVisible && (AutomationProperties.GetAutomationId(b) ?? "").StartsWith("Archivio_", StringComparison.Ordinal)
                             && (AutomationProperties.GetAutomationId(b) ?? "").Contains("_Button") && b.GetVisualAncestors().OfType<DataGridRow>().Any()))
                {
                    var destra = pulsante.TranslatePoint(new Point(pulsante.Bounds.Width, 0), griglia);
                    Assert.True(destra is { } d && d.X <= griglia.Bounds.Width - barraVerticale - 1 + 0.5,
                        $"pulsante '{AutomationProperties.GetAutomationId(pulsante)}' a {destra?.X:0} px su griglia {griglia.Bounds.Width:0} px; colonne {string.Join(", ", griglia.Columns.Select(c => c.ActualWidth.ToString("0")))}");
                }

                var troncate = TabellaHeadless.IntestazioniTroncate(griglia);
                Assert.True(troncate.Count == 0, "intestazioni troncate: " + string.Join("; ", troncate));
                Assert.True(vista.Bounds.Width <= finestra.ClientSize.Width + 0.5, "la pagina Archivio esce in orizzontale");
            }
            finally
            {
                finestra.Hide();
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        });
}

/// <summary>Impostazioni e viste master (6.6.1) in headless: navigazione delle sotto-sezioni comprimibile in Compatto e colonna ID
/// dei Requisiti sempre leggibile (mai "...").</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class LayoutImpostazioniHeadlessTests
{
    [Theory]
    [InlineData(1280, 672, 64, true)]    // Compatto: solo icone con tooltip, la navigazione occupa 60 px
    [InlineData(1366, 768, 222, false)]  // Standard: 220 px con i nomi delle sotto-sezioni
    public void Impostazioni_NavigazioneCompattaInCompattoEConNomiInStandard(int larghezza, int altezza, double massimoLarghezza, bool soloIcone) =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var stato = TestStateFactory.Create();
            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, larghezza, altezza);
            var vista = new ImpostazioniView { DataContext = new ImpostazioniViewModel(stato, () => { }) };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
                LayoutHeadlessSessione.Assesta(finestra);

                var navigazione = vista.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("SubNavImpostazioni"));
                Assert.True(navigazione.Bounds.Width <= massimoLarghezza + 0.5, $"navigazione larga {navigazione.Bounds.Width} px");
                var testi = navigazione.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("NavTesto")).ToList();
                Assert.NotEmpty(testi);
                Assert.All(testi, t => Assert.Equal(!soloIcone, t.IsEffectivelyVisible));
            }
            finally
            {
                finestra.Hide();
            }
        });

    [Fact]
    public void Requisiti_ColonnaIdLargaQuantoIlTestoStCento() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var dir = Path.Combine(Path.GetTempPath(), "TestLoom_layout_requisiti_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var stato = TestStateFactory.Create();
            stato.InizializzaCartella(dir);
            var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
            stato.Master.Operatori.Save(new List<Operatore> { master });
            stato.ImpostaOperatore(master.Nome, "1234");
            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 672);
            var vista = new RequisitiView { DataContext = new RequisitiViewModel(stato) };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
                LayoutHeadlessSessione.Assesta(finestra);

                var griglia = vista.FindControl<DataGrid>("GridPrincipale")!;
                var colonnaId = griglia.Columns.OfType<DataGridTextColumn>().Single(c => c.Header?.ToString() == "ID");
                var testo = new TextBlock { Text = "ST-100", FontWeight = Avalonia.Media.FontWeight.SemiBold };
                testo.Measure(Size.Infinity);
                Assert.True(colonnaId.ActualWidth >= 110 - 0.5, $"colonna ID di {colonnaId.ActualWidth} px");
                Assert.True(colonnaId.ActualWidth - 16 >= testo.DesiredSize.Width,
                    $"colonna ID di {colonnaId.ActualWidth} px troppo stretta per 'ST-100' ({testo.DesiredSize.Width:0} px)");
            }
            finally
            {
                finestra.Hide();
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        });
}

/// <summary>Geometria di Impostazioni in headless: la vista master sta nell'area a destra della navigazione compatta (60 px),
/// con il padding di pagina del token. Dati demo sintetici; verifica colonna identificativa, intestazioni e scroll di pagina.</summary>
internal static class MasterHeadless
{
    internal static AppState StatoConCartella(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "TestLoom_layout_master_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var stato = TestStateFactory.Create();
        stato.InizializzaCartella(dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        stato.Master.Operatori.Save(new List<Operatore> { master });
        stato.ImpostaOperatore(master.Nome, "1234");
        return stato;
    }

    internal static void Pulisci(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    /// <summary>Monta la vista nella finestra a 1280x672 e verifica: il testo piu' lungo della colonna identificativa sta per intero
    /// e la colonna e' piu' larga di lui; nessuna intestazione troncata; nessuno scroll orizzontale della pagina.</summary>
    internal static void VerificaVista(AppState stato, UserControl vista, string nomeGriglia, string chiaveIntestazione, IReadOnlyList<string> testi)
    {
        LayoutAdattivo.ReimpostaStato();
        var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 672);
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            var pagina = new ScrollViewer { Padding = new Thickness(LayoutAdattivo.Token(LayoutAdattivo.ModoCorrente).PagePadding), Content = vista };
            var contenitore = new Grid { ColumnDefinitions = new ColumnDefinitions("60,*") };
            contenitore.Children.Add(new Border { Width = 60 });
            Grid.SetColumn(pagina, 1);
            contenitore.Children.Add(pagina);
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = contenitore;
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);

            var griglia = vista.FindControl<DataGrid>(nomeGriglia)!;
            Assert.True(TabellaHeadless.RigheIntereVisibili(griglia) >= 1, "nessuna riga visibile");

            var intestazione = LocalizationService.Instance.Traduci(chiaveIntestazione);
            var colonna = griglia.Columns.Single(c => c.Header?.ToString() == intestazione);
            var celle = griglia.GetVisualDescendants().OfType<TextBlock>().Where(t => testi.Contains(t.Text)).ToList();
            Assert.Equal(testi.Count, celle.Count);
            // Il TextBlock ha margini (6 o 12 px per lato): DesiredSize li include, Bounds no. Il testo e' cio' che non deve troncarsi.
            var piuLargoTesto = 0.0;
            var piuLargoConMargini = 0.0;
            foreach (var cella in celle)
            {
                cella.Measure(Size.Infinity);
                var testo = cella.DesiredSize.Width - cella.Margin.Left - cella.Margin.Right;
                piuLargoTesto = Math.Max(piuLargoTesto, testo);
                piuLargoConMargini = Math.Max(piuLargoConMargini, cella.DesiredSize.Width);
                Assert.True(cella.Bounds.Width + 0.5 >= testo,
                    $"'{cella.Text}' troncato nella colonna '{intestazione}': {cella.Bounds.Width:0} px su {testo:0} px di testo");
            }
            // Cella compatta: 8 px di padding per lato. La colonna contiene il testo piu' lungo con i suoi margini.
            Assert.True(colonna.ActualWidth >= piuLargoConMargini + 16 - 0.5,
                $"colonna '{intestazione}' di {colonna.ActualWidth:0} px, il testo piu' lungo richiede {piuLargoConMargini + 16:0} px");

            var troncate = TabellaHeadless.IntestazioniTroncate(griglia);
            Assert.True(troncate.Count == 0, "intestazioni troncate: " + string.Join("; ", troncate));

            // Tabella senza compressione: la somma delle colonne visibili sta nella griglia. Se non ci sta, Avalonia riduce
            // le colonne a larghezza fissa al loro minimo (una colonna "Da" e' finita a 20 px).
            var barraVerticale = griglia.GetVisualDescendants().OfType<ScrollBar>()
                .Where(s => s.Orientation == Orientation.Vertical && s.IsEffectivelyVisible).Sum(s => s.Bounds.Width);
            var spazio = griglia.Bounds.Width - barraVerticale - 2;
            var sommaColonne = griglia.Columns.Where(c => c.IsVisible).Sum(c => c.ActualWidth);
            Assert.True(sommaColonne <= spazio + 0.5,
                $"colonne larghe {sommaColonne:0} px su {spazio:0} px: la tabella scorre in orizzontale");

            Assert.True(pagina.Extent.Width <= pagina.Viewport.Width + 0.5,
                $"scroll orizzontale di pagina: contenuto {pagina.Extent.Width:0} px su {pagina.Viewport.Width:0} px");
            Assert.True(vista.Bounds.Width <= pagina.Viewport.Width + 0.5, "la vista master esce dalla pagina in orizzontale");
        }
        finally
        {
            finestra.Hide();
        }
    }
}

/// <summary>Viste master (6.6.1) a 1280x672, una per vista. Dati demo sintetici con il testo piu' lungo previsto.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class LayoutMasterHeadlessTests
{
    [Fact]
    public void MasterFunzionalita_NomeLeggibileEPaginaSenzaScroll() => LayoutHeadlessSessione.Esegui(() =>
    {
        var stato = MasterHeadless.StatoConCartella(out var dir);
        try
        {
            var vm = new FunzionalitaViewModel(stato);
            var nomi = new[] { "Monitoraggio pressione pneumatici", "Diagnosi rete CAN bus", "Test isolamento alta tensione" };
            foreach (var n in nomi) vm.ItemsAttivi.Add(new Funzionalita { Nome = n, Categorie = new List<string> { "Diagnosi" } });
            MasterHeadless.VerificaVista(stato, new FunzionalitaView { DataContext = vm }, "GridPrincipale", "ColFunzionalita", nomi);
        }
        finally { MasterHeadless.Pulisci(dir); }
    });

    [Fact]
    public void MasterGamma_NomeLeggibileEPaginaSenzaScroll() => LayoutHeadlessSessione.Esegui(() =>
    {
        var stato = MasterHeadless.StatoConCartella(out var dir);
        try
        {
            var vm = new GammaViewModel(stato);
            var nomi = new[] { "Gamma veicoli leggeri", "Gamma pesante 26 t", "Gamma prova per centralina" };
            foreach (var n in nomi) vm.ItemsAttivi.Add(new Gamma { Nome = n });
            MasterHeadless.VerificaVista(stato, new GammaView { DataContext = vm }, "GridPrincipale", "GammaColNome", nomi);
        }
        finally { MasterHeadless.Pulisci(dir); }
    });

    [Fact]
    public void MasterEcu_NomeLeggibileEPaginaSenzaScroll() => LayoutHeadlessSessione.Esegui(() =>
    {
        var stato = MasterHeadless.StatoConCartella(out var dir);
        try
        {
            var vm = new EcuViewModel(stato);
            var nomi = new[] { "Componente motore EDC17", "Centralina carrozzeria BCM", "Modulo controllo freni ESP" };
            foreach (var n in nomi) vm.ItemsAttivi.Add(new Componente { Nome = n, VeicoliAssociati = new List<string> { "Furgone 3,5 t" } });
            MasterHeadless.VerificaVista(stato, new EcuView { DataContext = vm }, "GridPrincipale", "EcuColNome", nomi);
        }
        finally { MasterHeadless.Pulisci(dir); }
    });

    [Fact]
    public void MasterOperatori_NomeLeggibileEPaginaSenzaScroll() => LayoutHeadlessSessione.Esegui(() =>
    {
        var stato = MasterHeadless.StatoConCartella(out var dir);
        try
        {
            var vm = new OperatoriViewModel(stato);
            var nomi = new[] { "Laura Bianchi Verdi", "Mario Rossi", "Operatore Prova A" };
            var iniziali = new[] { "LBV", "MR", "OPA" };
            for (var i = 0; i < nomi.Length; i++)
                vm.ItemsAttivi.Add(new Operatore { Nome = nomi[i], Iniziali = iniziali[i], Ruolo = RuoloOperatore.Standard });
            MasterHeadless.VerificaVista(stato, new OperatoriView { DataContext = vm }, "GridPrincipale", "ColNome", nomi);
        }
        finally { MasterHeadless.Pulisci(dir); }
    });

    [Fact]
    public void MasterStrumenti_NomeLeggibileEPaginaSenzaScroll() => LayoutHeadlessSessione.Esegui(() =>
    {
        var stato = MasterHeadless.StatoConCartella(out var dir);
        try
        {
            var vm = new StrumentiViewModel(stato);
            var nomi = new[] { "Multimetro digitale 1000 V", "Oscilloscopio portatile 4 canali", "Analizzatore di rete CAN FD" };
            foreach (var n in nomi) vm.ItemsAttivi.Add(new StrumentoDiagnostico { Nome = n, Tipo = TipoStrumento.Supporto });
            MasterHeadless.VerificaVista(stato, new StrumentiView { DataContext = vm }, "GridPrincipale", "StrumentiColNome", nomi);
        }
        finally { MasterHeadless.Pulisci(dir); }
    });

    [Fact]
    public void MasterTestTemplate_NomeLeggibileEPaginaSenzaScroll() => LayoutHeadlessSessione.Esegui(() =>
    {
        var stato = MasterHeadless.StatoConCartella(out var dir);
        try
        {
            var vm = new TestTemplateViewModel(stato);
            var nomi = new[] { "Test funzionale centralina motore", "Verifica isolamento batteria", "Collaudo centralina porta" };
            foreach (var n in nomi) vm.ItemsAttivi.Add(new TestTemplate { Uid = Guid.NewGuid(), Nome = n, Icona = "T" });
            MasterHeadless.VerificaVista(stato, new TestTemplateView { DataContext = vm }, "GridPrincipale", "TplColNome", nomi);
        }
        finally { MasterHeadless.Pulisci(dir); }
    });

    [Fact]
    public void MasterVeicoli_NomeLeggibileEPaginaSenzaScroll() => LayoutHeadlessSessione.Esegui(() =>
    {
        var stato = MasterHeadless.StatoConCartella(out var dir);
        try
        {
            var vm = new VeicoliViewModel(stato);
            var nomi = new[] { "Furgone 3,5 t allestito", "Autocarro 26 t trattore", "Veicolo prova versione lunga" };
            foreach (var n in nomi) vm.ItemsAttivi.Add(new Veicolo { Nome = n, GammaNome = "Gamma 1" });
            MasterHeadless.VerificaVista(stato, new VeicoliView { DataContext = vm }, "GridPrincipale", "VeicoliColNome", nomi);
        }
        finally { MasterHeadless.Pulisci(dir); }
    });

    [Fact]
    public void MasterCampoSistema_ChiaveLeggibileEPaginaSenzaScroll() => LayoutHeadlessSessione.Esegui(() =>
    {
        var stato = MasterHeadless.StatoConCartella(out var dir);
        try
        {
            var vm = new CampoSistemaDefinizioneViewModel(stato, GruppoCampoSistema.Pianificazione);
            var chiavi = new[] { "DATA_COLLAUDO", "NUMERO_SERIE_BANCO", "TEMPERATURA_AMBIENTE" };
            var etichette = new[] { "Data del collaudo", "Numero di serie del banco", "Temperatura ambiente di prova" };
            for (var i = 0; i < chiavi.Length; i++)
                vm.ItemsAttivi.Add(new CampoSistemaDefinizione { Uid = Guid.NewGuid(), Chiave = chiavi[i], Etichetta = etichette[i], Gruppo = GruppoCampoSistema.Pianificazione, Tipo = TipoCampoStrumento.Testo });
            MasterHeadless.VerificaVista(stato, new CampoSistemaDefinizioneView { DataContext = vm }, "GridPrincipale", "CampoSistemaColChiave", chiavi);
        }
        finally { MasterHeadless.Pulisci(dir); }
    });
}

/// <summary>Anomalie/CR (6.6.1) a 1280x672 con pannello filtri aperto: la tabella non scende nella scheda CR, sia con la CR
/// aperta (caso peggiore dello spazio) sia chiusa (predefinito in Compatto), e con la CR chiusa rispetta la sua altezza minima.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class LayoutAnomalieCrHeadlessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Anomalie_TabellaNonScendeNellaCrConPannelloAperto(bool crAperta) => LayoutHeadlessSessione.Esegui(() =>
    {
        LayoutAdattivo.ReimpostaStato();
        var stato = TestStateFactory.Create();
        var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 672);
        ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", crAperta ? "1" : "0");
        ViewStateService.ScriviPerOperatore("Anomalie.PannelloFiltri", "1");
        var nc = new NonConformitaViewModel(stato, _ => { });
        nc.FiltroTipo = FiltroTipoAnomaliaOCr.Entrambi;
        nc.Righe.ReplaceWith(Enumerable.Range(1, 40).Select(i => new NcRiga
        {
            Nc = new NonConformita { Id = $"NC-{i:0000}", Descrizione = $"Anomalia sintetica {i}" },
            TestCaseIdVisualizzato = $"ST-{i:000}",
        }).ToList());
        var vista = new NonConformitaView();
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            vista.DataContext = nc;
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);

            var griglia = vista.FindControl<DataGrid>("AnomalieGrid")!;
            var cr = vista.FindControl<Grid>("AreaSchede")!.Children.OfType<Border>().Last();
            var inizioCr = cr.TranslatePoint(new Point(0, 0), finestra)!.Value.Y;
            var fondoGriglia = griglia.TranslatePoint(new Point(0, griglia.Bounds.Height), finestra)!.Value.Y;
            Assert.True(fondoGriglia <= inizioCr + 0.5,
                $"la tabella anomalie scende nella CR (CR {(crAperta ? "aperta" : "chiusa")}): fondo {fondoGriglia:0}, CR da {inizioCr:0}");
            if (!crAperta)
                Assert.True(griglia.Bounds.Height >= 160 - 0.5, $"tabella alta {griglia.Bounds.Height:0} px con la CR chiusa: sotto 160");
        }
        finally
        {
            finestra.Hide();
        }
    });

    /// <summary>Bug 6.7.2: con anomalie e CR visibili e la CR espansa, a 1280x776 la lista CR deve mostrare almeno 3 righe
    /// intere e non deve coprire la paginazione. Prima la scheda CR prendeva un quarto dell'area e la sua intestazione,
    /// i filtri e la paginazione occupavano quasi tutto lo spazio: restava visibile una riga sola. Ora la CR prende il resto
    /// dell'area dopo il minimo della tabella anomalie (160 px): a 1280x776 restano poche righe, 5 servono da 1920x1080 in su.</summary>
    [Fact]
    public void Cr_ConAnomalieVisibili_ListaCrMostraAlmenoTreRigheA1280x776() => LayoutHeadlessSessione.Esegui(() =>
    {
        LayoutAdattivo.ReimpostaStato();
        var stato = TestStateFactory.Create();
        var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 776);
        ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", "1");
        ViewStateService.ScriviPerOperatore("Anomalie.PannelloFiltri", "1");
        ViewStateService.ScriviPerOperatore("Anomalie.RapportoSchede", "0.45");
        var nc = new NonConformitaViewModel(stato, _ => { });
        nc.FiltroTipo = FiltroTipoAnomaliaOCr.Entrambi;
        nc.Righe.ReplaceWith(Enumerable.Range(1, 40).Select(i => new NcRiga
        {
            Nc = new NonConformita { Id = $"NC-{i:0000}", Descrizione = $"Anomalia sintetica {i}" },
            TestCaseIdVisualizzato = $"ST-{i:000}",
        }).ToList());
        nc.ChangeRequests.ReplaceWith(Enumerable.Range(1, 40).Select(i => new ChangeRequest
        {
            Id = $"CR-2026-{i:0000}",
            Titolo = $"Change request sintetica {i}",
        }).ToList());
        var vista = new NonConformitaView();
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            vista.DataContext = nc;
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);

            var lista = vista.FindControl<DataGrid>("ListaCr")!;
            var paginazione = vista.FindControl<Grid>("PaginazioneCr")!;
            var area = vista.FindControl<Grid>("AreaSchede")!;
            var righe = TabellaHeadless.RigheIntereVisibili(lista);
            var passo = lista.GetVisualDescendants().OfType<DataGridRow>().Select(i => i.Bounds.Height).DefaultIfEmpty(0).Max();
            var fondoLista = lista.TranslatePoint(new Point(0, lista.Bounds.Height), finestra)!.Value.Y;
            var inizioPaginazione = paginazione.TranslatePoint(new Point(0, 0), finestra)!.Value.Y;
            var misure = $"lista alta {lista.Bounds.Height:0} px, riga {passo:0} px, area schede {area.Bounds.Height:0} px, " +
                         $"righe area {string.Join(" / ", area.RowDefinitions.Select(r => $"{r.ActualHeight:0}"))} px";
            Assert.True(righe >= 3, $"lista CR a 1280x776: {righe} righe intere, attese almeno 3 ({misure})");
            Assert.True(fondoLista <= inizioPaginazione + 0.5,
                $"la lista CR copre la paginazione: lista fino a {fondoLista:0}, paginazione da {inizioPaginazione:0} ({misure})");

            var primaRiga = lista.GetVisualDescendants().OfType<DataGridRow>()
                .First(r => ReferenceEquals(r.DataContext, nc.ChangeRequests[0]));
            var apri = primaRiga.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetAutomationId(b) == "Anomalie_Cr_Apri_Button");
            var modifica = primaRiga.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetAutomationId(b) == "Anomalie_Cr_ModificaRapida_Button");
            var cellaAzioni = apri.GetVisualAncestors().OfType<DataGridCell>().First();
            var cella = cellaAzioni.TranslatePoint(new Point(0, 0), lista)!.Value;
            var apriPos = apri.TranslatePoint(new Point(0, 0), lista)!.Value;
            var modificaPos = modifica.TranslatePoint(new Point(0, 0), lista)!.Value;
            Assert.True(apri.IsEffectivelyVisible && modifica.IsEffectivelyVisible,
                "entrambe le azioni CR devono essere visibili nella riga");
            Assert.True(apri.Bounds.Width > 0 && apri.Bounds.Height > 0 && modifica.Bounds.Width > 0 && modifica.Bounds.Height > 0,
                "le azioni CR non devono essere compresse fino a sparire");
            Assert.True(apriPos.X + apri.Bounds.Width <= modificaPos.X + 0.5,
                $"i pulsanti CR si sovrappongono: apri {apriPos.X:0}+{apri.Bounds.Width:0}, modifica {modificaPos.X:0}");
            Assert.True(apriPos.X >= cella.X - 0.5 && modificaPos.X + modifica.Bounds.Width <= cella.X + cellaAzioni.Bounds.Width + 0.5,
                "i pulsanti CR devono rimanere dentro la colonna Azioni");
            var centroGruppo = (apriPos.X + modificaPos.X + modifica.Bounds.Width) / 2;
            var centroCella = cella.X + cellaAzioni.Bounds.Width / 2;
            Assert.InRange(Math.Abs(centroGruppo - centroCella), 0, 2);
        }
        finally
        {
            finestra.Hide();
        }
    });

    /// <summary>Il rapporto salvato per l'operatore si applica come stelle di riga: a 1920x1080 (minimi non attivi) 0,6 vuol dire il 60%
    /// dello spazio alle anomalie in una finestra abbastanza alta da non attivare i minimi adattivi.</summary>
    [Fact]
    public void Splitter_RapportoSalvatoSiApplicaComeStelleDiRiga() => LayoutHeadlessSessione.Esegui(() =>
    {
        LayoutAdattivo.ReimpostaStato();
        var stato = TestStateFactory.Create();
        var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1920, 1440);
        ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", "1");
        ViewStateService.ScriviPerOperatore("Anomalie.PannelloFiltri", "1");
        ViewStateService.ScriviPerOperatore("Anomalie.RapportoSchede", "0.6");
        var nc = new NonConformitaViewModel(stato, _ => { });
        nc.FiltroTipo = FiltroTipoAnomaliaOCr.Entrambi;
        nc.Righe.ReplaceWith(Enumerable.Range(1, 40).Select(i => new NcRiga
        {
            Nc = new NonConformita { Id = $"NC-{i:0000}", Descrizione = $"Anomalia sintetica {i}" },
            TestCaseIdVisualizzato = $"ST-{i:000}",
        }).ToList());
        nc.ChangeRequests.ReplaceWith(Enumerable.Range(1, 40).Select(i => new ChangeRequest
        {
            Id = $"CR-2026-{i:0000}",
            Titolo = $"Change request sintetica {i}",
        }).ToList());
        var vista = new NonConformitaView();
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            vista.DataContext = nc;
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);

            var area = vista.FindControl<Grid>("AreaSchede")!;
            var schede = area.Children.OfType<Border>().ToList();
            var anomalie = schede.First().Bounds.Height;
            var cr = schede.Last().Bounds.Height;
            var quota = anomalie / (anomalie + cr);
            Assert.True(Math.Abs(quota - 0.6) < 0.02, $"quota anomalie {quota:0.000} (anomalie {anomalie:0} px, CR {cr:0} px), attesa 0,600");
            Assert.True(area.RowDefinitions[0].Height.IsStar && area.RowDefinitions[2].Height.IsStar, "le due schede devono essere righe a stelle");
        }
        finally
        {
            finestra.Hide();
        }
    });

    /// <summary>Il rapporto non scende sotto le altezze minime: con una quota troppo bassa per le anomalie la scheda anomalie resta
    /// al suo minimo responsivo; con una quota troppo alta resta al minimo la scheda CR.</summary>
    [Theory]
    [InlineData("0.05", true)]
    [InlineData("0.97", false)]
    public void Splitter_MinimiRispettatiQualunqueSiaIlRapporto(string rapporto, bool anomalieAlMinimo) => LayoutHeadlessSessione.Esegui(() =>
    {
        LayoutAdattivo.ReimpostaStato();
        var stato = TestStateFactory.Create();
        var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 776);
        ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", "1");
        ViewStateService.ScriviPerOperatore("Anomalie.PannelloFiltri", "1");
        ViewStateService.ScriviPerOperatore("Anomalie.RapportoSchede", rapporto);
        var nc = new NonConformitaViewModel(stato, _ => { });
        nc.FiltroTipo = FiltroTipoAnomaliaOCr.Entrambi;
        nc.Righe.ReplaceWith(Enumerable.Range(1, 40).Select(i => new NcRiga
        {
            Nc = new NonConformita { Id = $"NC-{i:0000}", Descrizione = $"Anomalia sintetica {i}" },
            TestCaseIdVisualizzato = $"ST-{i:000}",
        }).ToList());
        nc.ChangeRequests.ReplaceWith(Enumerable.Range(1, 40).Select(i => new ChangeRequest
        {
            Id = $"CR-2026-{i:0000}",
            Titolo = $"Change request sintetica {i}",
        }).ToList());
        var vista = new NonConformitaView();
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            vista.DataContext = nc;
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);

            var area = vista.FindControl<Grid>("AreaSchede")!;
            var schede = area.Children.OfType<Border>().ToList();
            var anomalie = schede.First().Bounds.Height;
            var cr = schede.Last().Bounds.Height;
            var minAnomalie = area.RowDefinitions[0].MinHeight;
            var minCr = area.RowDefinitions[2].MinHeight;
            var griglia = vista.FindControl<DataGrid>("AnomalieGrid")!;
            Assert.True(minAnomalie > 0 && minCr > 0, $"minimi non misurati (anomalie {minAnomalie:0}, CR {minCr:0})");
            Assert.True(anomalie >= minAnomalie - 0.5, $"anomalie {anomalie:0} px sotto il minimo {minAnomalie:0}");
            Assert.True(cr >= minCr - 0.5, $"CR {cr:0} px sotto il minimo {minCr:0}");
            Assert.True(griglia.Bounds.Height >= griglia.MinHeight - 0.5, $"tabella anomalie {griglia.Bounds.Height:0} px sotto il minimo {griglia.MinHeight:0}");
            if (anomalieAlMinimo)
                Assert.True(anomalie <= minAnomalie + 1.5, $"rapporto {rapporto}: le anomalie devono stare al minimo {minAnomalie:0} (sono {anomalie:0})");
            else
                Assert.True(cr <= minCr + 1.5, $"rapporto {rapporto}: la CR deve stare al minimo {minCr:0} (e' {cr:0})");
        }
        finally
        {
            finestra.Hide();
        }
    });

    /// <summary>A 1366x800 (modo Standard, filtri visibili, CR aperta di default) la CR mostra almeno una riga con la ripartizione
    /// predefinita: le anomalie restano alla loro altezza minima.</summary>
    [Fact]
    public void Cr_Standard1366x800_ConRapportoPredefinito_MostraAlmenoUnaRiga() => LayoutHeadlessSessione.Esegui(() =>
    {
        LayoutAdattivo.ReimpostaStato();
        var stato = TestStateFactory.Create();
        var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1366, 800);
        ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", "1");
        ViewStateService.ScriviPerOperatore("Anomalie.PannelloFiltri", "1");
        ViewStateService.ScriviPerOperatore("Anomalie.RapportoSchede", "0.45");
        var nc = new NonConformitaViewModel(stato, _ => { });
        nc.FiltroTipo = FiltroTipoAnomaliaOCr.Entrambi;
        nc.Righe.ReplaceWith(Enumerable.Range(1, 40).Select(i => new NcRiga
        {
            Nc = new NonConformita { Id = $"NC-{i:0000}", Descrizione = $"Anomalia sintetica {i}" },
            TestCaseIdVisualizzato = $"ST-{i:000}",
        }).ToList());
        nc.ChangeRequests.ReplaceWith(Enumerable.Range(1, 40).Select(i => new ChangeRequest
        {
            Id = $"CR-2026-{i:0000}",
            Titolo = $"Change request sintetica {i}",
        }).ToList());
        var vista = new NonConformitaView();
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            vista.DataContext = nc;
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);

            var lista = vista.FindControl<DataGrid>("ListaCr")!;
            var righe = TabellaHeadless.RigheIntereVisibili(lista);
            var areaMisurata = vista.FindControl<Grid>("AreaSchede")!;
            var misure = $"lista {lista.Bounds.Height:0}px, minima {lista.MinHeight:0}px, righe schede {string.Join("/", areaMisurata.RowDefinitions.Select(r => $"{r.ActualHeight:0}"))}, " +
                         $"finestra {finestra.ClientSize.Width:0}x{finestra.ClientSize.Height:0}px, CR {vista.FindControl<Border>("CardChangeRequest")!.Bounds.Height:0}px";
            Assert.True(righe >= 1, $"lista CR a 1366x800 (Standard): {righe} righe intere, attese almeno 1 ({misure})");
            // Con poco spazio la CR scende alle sue parti fisse ma non sborda dall'area schede.
            var area = vista.FindControl<Grid>("AreaSchede")!;
            var cr = area.Children.OfType<Border>().Last();
            var fondoCr = cr.TranslatePoint(new Point(0, cr.Bounds.Height), finestra)!.Value.Y;
            var fondoArea = area.TranslatePoint(new Point(0, area.Bounds.Height), finestra)!.Value.Y;
            Assert.True(fondoCr <= fondoArea + 0.5,
                $"la scheda CR sborda l'area schede: fondo {fondoCr:0}, area fino a {fondoArea:0} " +
                $"(area {area.Bounds.Height:0}, righe {string.Join("/", area.RowDefinitions.Select(r => $"{r.ActualHeight:0}"))}, " +
                $"minimi {string.Join("/", area.RowDefinitions.Select(r => $"{r.MinHeight:0}"))}, lista {lista.Bounds.Height:0})");

            // I filtri vengono solo compressi a viewport basso; tornando a un'area alta la preferenza si riapplica.
            Assert.False(vista.FindControl<ScrollViewer>("ScorrimentoFiltriNc")!.IsVisible,
                "i filtri NC si comprimono quando la finestra non lascia spazio a entrambe le schede");
            finestra.Height = 900;
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.True(vista.FindControl<ScrollViewer>("ScorrimentoFiltriNc")!.IsVisible,
                "i filtri NC tornano visibili quando la finestra cresce");
        }
        finally
        {
            finestra.Hide();
        }
    });

    /// <summary>Alla dimensione minima consentita della finestra principale, entrambe le schede restano entro l'area disponibile e la maniglia non viene espulsa dal layout.</summary>
    [Fact]
    public void AnomalieCr_FinestraMinima1000x560_NonSbordaEDEntrambeRestanoVisibili() => LayoutHeadlessSessione.Esegui(() =>
    {
        LayoutAdattivo.ReimpostaStato();
        var stato = TestStateFactory.Create();
        var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1000, 560);
        ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", "1");
        ViewStateService.ScriviPerOperatore("Anomalie.PannelloFiltri", "1");
        var nc = new NonConformitaViewModel(stato, _ => { });
        nc.FiltroTipo = FiltroTipoAnomaliaOCr.Entrambi;
        nc.Righe.ReplaceWith(Enumerable.Range(1, 40).Select(i => new NcRiga
        {
            Nc = new NonConformita { Id = $"NC-{i:0000}", Descrizione = $"Anomalia sintetica {i}" },
            TestCaseIdVisualizzato = $"ST-{i:000}",
        }).ToList());
        nc.ChangeRequests.ReplaceWith(Enumerable.Range(1, 40).Select(i => new ChangeRequest
        {
            Id = $"CR-2026-{i:0000}",
            Titolo = $"Change request sintetica {i}",
        }).ToList());
        var vista = new NonConformitaView();
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            vista.DataContext = nc;
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);

            var area = vista.FindControl<Grid>("AreaSchede")!;
            var schede = area.Children.OfType<Border>().ToList();
            var anomalie = schede.First();
            var cr = schede.Last();
            var splitter = vista.FindControl<GridSplitter>("SplitterSchede")!;
            double Fondo(Control c) => c.TranslatePoint(new Point(0, c.Bounds.Height), finestra)!.Value.Y;
            var fondoArea = Fondo(area);
            var cardCr = vista.FindControl<Border>("CardChangeRequest")!;
            var layoutCr = (Grid)cardCr.Child!;
            var filtriCr = vista.FindControl<Grid>("FiltriCr")!;
            var layoutNc = (Grid)vista.FindControl<Border>("CardAnomalie")!.Child!;
            var misure = $"area {area.Bounds.Height:0}px (da {area.TranslatePoint(new Point(0, 0), finestra)!.Value.Y:0} a {fondoArea:0}), " +
                         $"cards {string.Join("/", schede.Select(c => $"{c.Bounds.Height:0}"))}, righe {string.Join("/", area.RowDefinitions.Select(r => $"{r.ActualHeight:0} (min {r.MinHeight:0})"))}, " +
                         $"min filters {vista.FindControl<ScrollViewer>("ScorrimentoFiltriNc")!.IsVisible}, " +
                         $"rows CR {string.Join("/", layoutCr.RowDefinitions.Select(r => $"{r.ActualHeight:0}"))} filters {filtriCr.Bounds.Height:0}px/{filtriCr.IsVisible}, " +
                         $"rows NC {string.Join("/", layoutNc.RowDefinitions.Select(r => $"{r.ActualHeight:0}"))}, splitter {splitter.Bounds.Height:0}px";
            Assert.True(anomalie.IsVisible && cr.IsVisible && splitter.IsVisible, $"NC, CR e splitter devono restare visibili a 1000x560 ({misure})");
            Assert.True(Fondo(anomalie) <= fondoArea + 0.5, $"scheda NC oltre AreaSchede ({Fondo(anomalie):0} > {fondoArea:0}; {misure})");
            Assert.True(Fondo(cr) <= fondoArea + 0.5, $"scheda CR oltre AreaSchede ({Fondo(cr):0} > {fondoArea:0}; {misure})");
            Assert.True(splitter.Bounds.Height > 0, "splitter deve mantenere un hit target visibile");
            Assert.False(vista.FindControl<Grid>("FiltriCr")!.IsVisible, "i filtri CR si comprimono temporaneamente nel viewport minimo");
            nc.FiltroStatoCr = Enum.GetValues<StatoChangeRequest>().First();
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.False(vista.FindControl<Grid>("FiltriCr")!.IsVisible,
                "un aggiornamento dei filtri non deve riaprire i filtri CR finché la finestra resta sotto la soglia");
            nc.FiltroStatoCr = null;
            finestra.Height = 900;
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.True(vista.FindControl<Grid>("FiltriCr")!.IsVisible, "i filtri CR tornano visibili quando la finestra cresce");
            Assert.Equal(112, vista.FindControl<DataGrid>("AnomalieGrid")!.MinHeight);
        }
        finally
        {
            finestra.Hide();
        }
    });

    /// <summary>"Espandi" nell'intestazione della CR riduce le anomalie alla sola intestazione e lascia alla CR tutta l'area; un
    /// secondo clic ripristina le anomalie e la ripartizione.</summary>
    [Fact]
    public void Espandi_RiduceLeAnomalieAllIntestazioneEDaAllaCrTuttaLArea() => LayoutHeadlessSessione.Esegui(() =>
    {
        LayoutAdattivo.ReimpostaStato();
        var stato = TestStateFactory.Create();
        var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 776);
        ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", "1");
        ViewStateService.ScriviPerOperatore("Anomalie.PannelloFiltri", "1");
        var nc = new NonConformitaViewModel(stato, _ => { });
        nc.FiltroTipo = FiltroTipoAnomaliaOCr.Entrambi;
        nc.Righe.ReplaceWith(Enumerable.Range(1, 40).Select(i => new NcRiga
        {
            Nc = new NonConformita { Id = $"NC-{i:0000}", Descrizione = $"Anomalia sintetica {i}" },
            TestCaseIdVisualizzato = $"ST-{i:000}",
        }).ToList());
        nc.ChangeRequests.ReplaceWith(Enumerable.Range(1, 40).Select(i => new ChangeRequest
        {
            Id = $"CR-2026-{i:0000}",
            Titolo = $"Change request sintetica {i}",
        }).ToList());
        var vista = new NonConformitaView();
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            vista.DataContext = nc;
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);

            var area = vista.FindControl<Grid>("AreaSchede")!;
            var anomalie = area.Children.OfType<Border>().First();
            var lista = vista.FindControl<DataGrid>("ListaCr")!;
            var griglia = vista.FindControl<DataGrid>("AnomalieGrid")!;
            var espandi = vista.FindControl<Button>("BottoneEspandiCr")!;
            var righeAprima = TabellaHeadless.RigheIntereVisibili(lista);
            Assert.True(espandi.IsVisible, "il pulsante Espandi deve essere visibile con anomalie e CR aperte");
            Assert.Equal(LocalizationService.Instance.Traduci("CrEspandiBtn"), espandi.Content as string);

            espandi.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.False(griglia.IsEffectivelyVisible, "con Espandi la tabella anomalie non si vede");
            Assert.True(anomalie.Bounds.Height <= 120, $"anomalie ridotte alte {anomalie.Bounds.Height:0} px, attese la sola intestazione");
            var righeDopo = TabellaHeadless.RigheIntereVisibili(lista);
            Assert.True(righeDopo >= 6, $"con Espandi la CR mostra {righeDopo} righe intere, attese almeno 6 (prima: {righeAprima})");
            Assert.Equal(LocalizationService.Instance.Traduci("CrRipristinaBtn"), espandi.Content as string);

            espandi.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.True(griglia.IsEffectivelyVisible, "ripristinate le anomalie, la tabella torna visibile");
            Assert.Equal(righeAprima, TabellaHeadless.RigheIntereVisibili(lista));
        }
        finally
        {
            finestra.Hide();
        }
    });

    /// <summary>Lo spostamento della maniglia salva il rapporto per l'operatore; un aggiornamento successivo del riepilogo lo riapplica
    /// invece di tornare al 50/50; il ripristino riporta al 50/50.</summary>
    [Fact]
    public void Splitter_TrascinamentoSalvaIlRapportoEIlRiepilogoNonLoSovrascrive() => LayoutHeadlessSessione.Esegui(() =>
    {
        LayoutAdattivo.ReimpostaStato();
        var stato = TestStateFactory.Create();
        var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1920, 1440);
        ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", "1");
        ViewStateService.ScriviPerOperatore("Anomalie.PannelloFiltri", "1");
        var nc = new NonConformitaViewModel(stato, _ => { });
        nc.FiltroTipo = FiltroTipoAnomaliaOCr.Entrambi;
        nc.Righe.ReplaceWith(Enumerable.Range(1, 40).Select(i => new NcRiga
        {
            Nc = new NonConformita { Id = $"NC-{i:0000}", Descrizione = $"Anomalia sintetica {i}" },
            TestCaseIdVisualizzato = $"ST-{i:000}",
        }).ToList());
        nc.ChangeRequests.ReplaceWith(Enumerable.Range(1, 40).Select(i => new ChangeRequest
        {
            Id = $"CR-2026-{i:0000}",
            Titolo = $"Change request sintetica {i}",
        }).ToList());
        var vista = new NonConformitaView();
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            vista.DataContext = nc;
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);

            var area = vista.FindControl<Grid>("AreaSchede")!;
            // Simula lo spostamento della maniglia: 65% alle anomalie, 35% alla CR.
            area.RowDefinitions[0].Height = new GridLength(65, GridUnitType.Star);
            area.RowDefinitions[2].Height = new GridLength(35, GridUnitType.Star);
            LayoutHeadlessSessione.Assesta(finestra);
            vista.RegistraRapportoTrascinato();
            Assert.True(double.TryParse(ViewStateService.LeggiPerOperatore("Anomalie.RapportoSchede"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var salvato), "rapporto non salvato");
            Assert.True(Math.Abs(salvato - 0.65) < 0.01, $"rapporto salvato {salvato:0.000}, atteso 0,650");

            // Un aggiornamento del riepilogo (un filtro di testo) non deve riportare al rapporto predefinito.
            nc.FiltroTesto = "x";
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);
            var quota = area.RowDefinitions[0].Height.Value / (area.RowDefinitions[0].Height.Value + area.RowDefinitions[2].Height.Value);
            Assert.True(Math.Abs(quota - 0.65) < 0.01, $"dopo un aggiornamento la quota delle anomalie e' {quota:0.000}, attesa 0,650");

            vista.RipristinaRapportoPredefinito();
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.Equal("0.45", ViewStateService.LeggiPerOperatore("Anomalie.RapportoSchede"));
        }
        finally
        {
            finestra.Hide();
        }
    });

    /// <summary>In Compatto, con anomalie e CR aperte, il pannello filtri si nasconde (la sua scelta resta salvata) e il
    /// pulsante Filtri lo riapre chiudendo la CR: le due schede non stanno aperte insieme.</summary>
    [Fact]
    public void Cr_InCompatto_ConCrAperta_IlPannelloFiltriSiNascondeEIlPulsanteLoRiapreChiudendoLaCr() => LayoutHeadlessSessione.Esegui(() =>
    {
        LayoutAdattivo.ReimpostaStato();
        var stato = TestStateFactory.Create();
        var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 776);
        ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", "1");
        ViewStateService.ScriviPerOperatore("Anomalie.PannelloFiltri", "1");
        var nc = new NonConformitaViewModel(stato, _ => { });
        nc.FiltroTipo = FiltroTipoAnomaliaOCr.Entrambi;
        nc.Righe.ReplaceWith(Enumerable.Range(1, 40).Select(i => new NcRiga
        {
            Nc = new NonConformita { Id = $"NC-{i:0000}", Descrizione = $"Anomalia sintetica {i}" },
            TestCaseIdVisualizzato = $"ST-{i:000}",
        }).ToList());
        nc.ChangeRequests.ReplaceWith(Enumerable.Range(1, 40).Select(i => new ChangeRequest
        {
            Id = $"CR-2026-{i:0000}",
            Titolo = $"Change request sintetica {i}",
        }).ToList());
        var vista = new NonConformitaView();
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            vista.DataContext = nc;
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);

            var pannello = vista.FindControl<ScrollViewer>("ScorrimentoFiltriNc")!;
            var lista = vista.FindControl<DataGrid>("ListaCr")!;
            Assert.False(pannello.IsEffectivelyVisible, "con la CR aperta in Compatto il pannello filtri deve nascondersi");
            Assert.True(lista.IsEffectivelyVisible, "la CR aperta deve mostrare la sua lista");

            vista.FindControl<Button>("BottoneFiltriNc")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.True(pannello.IsEffectivelyVisible, "il pulsante Filtri deve riaprire il pannello");
            Assert.False(lista.IsEffectivelyVisible, "aprire i filtri in Compatto deve chiudere la CR");
            Assert.Equal("0", ViewStateService.LeggiPerOperatore("Anomalie.CrEspansa"));
        }
        finally
        {
            finestra.Hide();
        }
    });

    /// <summary>Bug 6.7.2 a 1920x1080 (modo Ampio): con anomalie e CR aperte la lista CR mostra almeno 5 righe intere, perche'
    /// la tabella anomalie prende solo il suo minimo e la CR il resto dell'area.</summary>
    [Fact]
    public void Cr_ConAnomalieVisibili_ListaCrMostraAlmenoCinqueRigheA1920x1080() => LayoutHeadlessSessione.Esegui(() =>
    {
        LayoutAdattivo.ReimpostaStato();
        var stato = TestStateFactory.Create();
        var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1920, 1080);
        ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", "1");
        ViewStateService.ScriviPerOperatore("Anomalie.PannelloFiltri", "1");
        var nc = new NonConformitaViewModel(stato, _ => { });
        nc.FiltroTipo = FiltroTipoAnomaliaOCr.Entrambi;
        nc.Righe.ReplaceWith(Enumerable.Range(1, 40).Select(i => new NcRiga
        {
            Nc = new NonConformita { Id = $"NC-{i:0000}", Descrizione = $"Anomalia sintetica {i}" },
            TestCaseIdVisualizzato = $"ST-{i:000}",
        }).ToList());
        nc.ChangeRequests.ReplaceWith(Enumerable.Range(1, 40).Select(i => new ChangeRequest
        {
            Id = $"CR-2026-{i:0000}",
            Titolo = $"Change request sintetica {i}",
        }).ToList());
        var vista = new NonConformitaView();
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            vista.DataContext = nc;
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);

            var lista = vista.FindControl<DataGrid>("ListaCr")!;
            var righe = TabellaHeadless.RigheIntereVisibili(lista);
            Assert.True(righe >= 5, $"lista CR a 1920x1080: {righe} righe intere, attese almeno 5 (lista alta {lista.Bounds.Height:0} px)");
        }
        finally
        {
            finestra.Hide();
        }
    });

    /// <summary>Con anomalie e CR visibili e la CR espansa, a 1280x776 la tabella anomalie resta visibile anche nel minimo condiviso;
    /// lo splitter permette di allargarla oltre tale quota quando occorre consultare più righe.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cr_ConAnomalieVisibili_TabellaAnomalieRestaUsabileA1280x776(bool pannelloAperto) => LayoutHeadlessSessione.Esegui(() =>
    {
        LayoutAdattivo.ReimpostaStato();
        var stato = TestStateFactory.Create();
        var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 776);
        ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", "1");
        ViewStateService.ScriviPerOperatore("Anomalie.PannelloFiltri", pannelloAperto ? "1" : "0");
        ViewStateService.ScriviPerOperatore("Anomalie.RapportoSchede", "0.45");
        var nc = new NonConformitaViewModel(stato, _ => { });
        nc.FiltroTipo = FiltroTipoAnomaliaOCr.Entrambi;
        nc.Righe.ReplaceWith(Enumerable.Range(1, 40).Select(i => new NcRiga
        {
            Nc = new NonConformita { Id = $"NC-{i:0000}", Descrizione = $"Anomalia sintetica {i}" },
            TestCaseIdVisualizzato = $"ST-{i:000}",
        }).ToList());
        nc.ChangeRequests.ReplaceWith(Enumerable.Range(1, 40).Select(i => new ChangeRequest
        {
            Id = $"CR-2026-{i:0000}",
            Titolo = $"Change request sintetica {i}",
        }).ToList());
        var vista = new NonConformitaView();
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            vista.DataContext = nc;
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);

            var griglia = vista.FindControl<DataGrid>("AnomalieGrid")!;
            var righe = TabellaHeadless.RigheIntereVisibili(griglia);
            var intestazione = griglia.GetVisualDescendants().OfType<DataGridColumnHeader>().Select(r => r.Bounds.Height).DefaultIfEmpty(0).Max();
            var altezzeRiga = string.Join("/", griglia.GetVisualDescendants().OfType<DataGridRow>().Select(r => $"{r.Bounds.Height:0}"));
            Assert.True(righe >= 1,
                $"anomalie a 1280x776 (pannello {(pannelloAperto ? "aperto" : "chiuso")}): {righe} righe intere, attese almeno 1 " +
                $"(tabella alta {griglia.Bounds.Height:0} px, intestazione {intestazione:0}, righe {altezzeRiga})");
        }
        finally
        {
            finestra.Hide();
        }
    });
}

/// <summary>Anomalie/CR (6.6.1) nel caso peggiore dello spazio a 1280x672: CR aperta, pannello filtri aperto e "Altri filtri"
/// espanso. La tabella (con la sua altezza minima) e la paginazione non scendono nella scheda CR.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class LayoutAnomalieCasoPeggioreHeadlessTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void Anomalie_CrAperta_PannelloAperto_NonSovrapponeLaTabellaNellaCr(bool crAperta, bool altriFiltriAperti) => LayoutHeadlessSessione.Esegui(() =>
    {
        LayoutAdattivo.ReimpostaStato();
        var stato = TestStateFactory.Create();
        var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 672);
        ViewStateService.ScriviPerOperatore("Anomalie.CrEspansa", crAperta ? "1" : "0");
        ViewStateService.ScriviPerOperatore("Anomalie.PannelloFiltri", "1");
        var nc = new NonConformitaViewModel(stato, _ => { });
        nc.FiltroTipo = FiltroTipoAnomaliaOCr.Entrambi;
        nc.FiltriEspansi = altriFiltriAperti;
        nc.Righe.ReplaceWith(Enumerable.Range(1, 40).Select(i => new NcRiga
        {
            Nc = new NonConformita { Id = $"NC-{i:0000}", Descrizione = $"Anomalia sintetica {i}" },
            TestCaseIdVisualizzato = $"ST-{i:000}",
        }).ToList());
        var vista = new NonConformitaView();
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            vista.DataContext = nc;
            LayoutHeadlessSessione.Assesta(finestra);
            LayoutHeadlessSessione.Assesta(finestra);

            var cr = vista.FindControl<Grid>("AreaSchede")!.Children.OfType<Border>().Last();
            var inizioCr = cr.TranslatePoint(new Point(0, 0), finestra)!.Value.Y;
            var griglia = vista.FindControl<DataGrid>("AnomalieGrid")!;
            var fondoGriglia = griglia.TranslatePoint(new Point(0, griglia.Bounds.Height), finestra)!.Value.Y;
            Assert.True(fondoGriglia <= inizioCr + 0.5,
                $"la tabella scende nella CR (altri filtri {(altriFiltriAperti ? "aperti" : "chiusi")}): fondo {fondoGriglia:0}, CR da {inizioCr:0}");
            var paginazione = vista.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetAutomationId(b) == "Anomalie_PaginaSuccessiva_Button");
            var fondoPaginazione = paginazione.TranslatePoint(new Point(0, paginazione.Bounds.Height), finestra)!.Value.Y;
            Assert.True(fondoPaginazione <= inizioCr + 0.5,
                $"la paginazione scende nella CR (altri filtri {(altriFiltriAperti ? "aperti" : "chiusi")}): fondo {fondoPaginazione:0}, CR da {inizioCr:0}");
            var inizioPaginazione = paginazione.TranslatePoint(new Point(0, 0), finestra)!.Value.Y;
            Assert.True(fondoGriglia <= inizioPaginazione + 0.5,
                $"la tabella copre la paginazione (CR {(crAperta ? "aperta" : "chiusa")}, altri filtri {(altriFiltriAperti ? "aperti" : "chiusi")}): griglia fino a {fondoGriglia:0}, paginazione da {inizioPaginazione:0}");
            Assert.True(griglia.MinHeight >= 112 - 0.5,
                $"minimo adattivo tabella anomalie {griglia.MinHeight:0} px sotto il limite responsivo di 112");
            Assert.True(griglia.Bounds.Height >= griglia.MinHeight - 0.5,
                $"tabella alta {griglia.Bounds.Height:0} px, sotto il minimo adattivo di {griglia.MinHeight:0}");
        }
        finally
        {
            finestra.Hide();
        }
    });
}
