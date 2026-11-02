using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

/// <summary>Editor del contenuto di un TestTemplate (sezioni/campi del riquadro Dettagli, strumenti
/// preselezionati, azioni collegate, vincolo di collegamento): finestra dedicata, costruita
/// interamente in code-behind come CampiStrumentoEditorWindow, perché la struttura è troppo annidata/dinamica
/// per un DataTemplate statico. Opera direttamente sull'oggetto TestTemplate già presente in
/// TestTemplateViewModel.Items — nessuno snapshot separato, le modifiche sono già "nel modello" quando la
/// finestra si chiude; il salvataggio su disco resta un passo esplicito nella tabella principale (Addendum
/// redesign 2026-08, punto 2 del secondo grill-me).</summary>
public partial class TestTemplateEditorWindow : Window
{
    public TestTemplateEditorWindow()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this, grande: true);
    }

    public static Task Apri(Window owner, AppState state, TestTemplate template, IReadOnlyList<TestTemplate> tuttiITemplate, IReadOnlyList<StrumentoDiagnostico> strumentiDisponibili) =>
        Crea(state, template, tuttiITemplate, strumentiDisponibili).ShowDialog(owner);

    internal static TestTemplateEditorWindow Crea(AppState state, TestTemplate template,
        IReadOnlyList<TestTemplate> tuttiITemplate, IReadOnlyList<StrumentoDiagnostico> strumentiDisponibili)
    {
        var loc = LocalizationService.Instance;
        string T(string key) => loc.Traduci(key);

        var w = new TestTemplateEditorWindow();
        var titolo = string.Format(T("TplEditorTitoloFmt"), template.Nome);
        w.Title = titolo;
        w.TitoloText.Text = titolo;
        w.ChiudiButton.Click += (_, _) => w.Close();
        w.AnteprimaButton.Click += (_, _) => TestFormPreviewWindow.Apri(w, () => TestFormPreviewRenderer.DaTemplate(template));

        var alCambioCampi = new List<Action>();

        var root = w.RootPanel;

        // Change Request (feedback utente 2026-08-21, punto 11): un template così marcato riusa lo stesso
        // motore (sezioni/campi liberi, ticket collegato) ma le sue istanze non compaiono nella lista/wizard
        // Test — vivono nella pagina Anomalie, filtrabile per tipo.
        var crCheck = new CheckBox { Content = T("TplChangeRequestCheck"), IsChecked = template.IsChangeRequest };
        crCheck.IsCheckedChanged += (_, _) =>
        {
            template.IsChangeRequest = crCheck.IsChecked == true;
            if (template.IsChangeRequest)
            {
                template.StrumentiPreselezionati.Clear();
                template.VincoloCollegamento = null;
                foreach (var campo in template.CampiPianificazione.Concat(template.Sezioni.SelectMany(s => s.Campi)).Concat(template.CampiRiferimenti))
                    campo.CondizioneEsito.Clear();
            }
        };
        root.Children.Add(crCheck);


        BuildPianificazione(root, w, state, template, T, alCambioCampi);
        BuildSezioni(root, w, state, template, T, alCambioCampi);
        BuildRiferimenti(root, w, template, T, alCambioCampi);
        BuildStrumentiPreselezionati(root, template, strumentiDisponibili.Select(s => s.Nome).ToList(), T);
        BuildCampiOperazioneNascosti(root, template, strumentiDisponibili, T);
        BuildVincoloCollegamento(root, template, tuttiITemplate, T);
        BuildSezioniPolarion(root, template, T);

        return w;
    }

    /// <summary>Etichette del catalogo campi di sistema per chiave: le chiavi storiche restano
    /// CampiSistema.LabelDefault (fallback), i campi realmente nuovi (Ruolo=Nessuno) mostrano l'Etichetta
    /// scelta dal Master (Addendum: grill-me 2026-08-18, follow-up).</summary>
    private static Func<string, string> EtichettaCampoSistema(IReadOnlyList<CampoSistemaDefinizione> catalogoLibero)
    {
        var perChiave = catalogoLibero.ToDictionary(c => c.Chiave, c => c.Etichetta);
        return chiave => perChiave.GetValueOrDefault(chiave) is { Length: > 0 } e ? e : CampiSistema.LabelDefault(chiave);
    }


    /// <summary>Bottone "Gestisci campi (N)" che apre CampiTemplateEditorWindow, stesso schema del bottone
    /// analogo di Strumenti (Addendum: collaudo live 2026-08-17, punto aggiunto al grill-me — prima i campi di
    /// Pianificazione/Dettagli erano una tabella incollata dentro questa stessa finestra già affollata di
    /// sezioni; ora, come per gli Strumenti, ogni elenco di campi ha una finestra dedicata riutilizzabile,
    /// aperta su richiesta invece di essere sempre visibile).</summary>
    private static Button BuildGestisciCampiBtn(Window owner, AppState? state, GruppoCampoSistema? gruppo, Func<string> titoloFinestra, List<CampoTemplate> campi, IReadOnlyList<string> chiaviFisse, Func<string, string> T, List<Action> alCambioCampi, bool isChangeRequest, TestTemplate? templateProprietario = null)
    {
        var link = new Button { Classes = { "Link" }, HorizontalAlignment = HorizontalAlignment.Left };
        // AutomationId per i test FlaUI (spec riordino campi 2026-09-23 §2, seguito coordinamento 2026-09-24):
        // univoco per Pianificazione/Riferimenti (un solo bottone), ripetuto fra le sezioni di Dettagli (una
        // per sezione) — sufficiente per i test odierni, che si aprono solo sul gruppo Pianificazione.
        Avalonia.Automation.AutomationProperties.SetAutomationId(link, $"TplGestisciCampi_{gruppo?.ToString() ?? "Riferimenti"}");
        void AggiornaTesto() => link.Content = string.Format(T("GestisciCampiFmt"), campi.Count);
        AggiornaTesto();
        link.Click += async (_, _) =>
        {
            // titoloFinestra() valutato solo qui, non catturato prima: sezione.Nome può essere stato
            // rinominato dal campo sopra senza che questo bottone venga ricostruito da RidisegnaSezioni.
            await UiExceptionGuard.Esegui(async () =>
            {
                await CampiTemplateEditorWindow.Apri(owner, state, gruppo, titoloFinestra(), campi, chiaviFisse, T, isChangeRequest, templateProprietario);
                AggiornaTesto();
                foreach (var aggiorna in alCambioCampi) aggiorna();
            }, "Apertura campi template");
        };
        return link;
    }

    private static void BuildPianificazione(StackPanel root, Window owner, AppState state, TestTemplate template, Func<string, string> T, List<Action> alCambioCampi)
    {
        root.Children.Add(new TextBlock { Text = T("TplPianificazioneTitolo"), Classes = { "SectionTitle" } });
        root.Children.Add(new TextBlock { Text = T("TplPianificazioneSottotitolo"), Classes = { "Muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });

        var inner = new StackPanel { Spacing = 4 };
        var card = new Border { Classes = { "Card" }, Padding = new Avalonia.Thickness(14), Child = inner };
        root.Children.Add(card);
        inner.Children.Add(BuildGestisciCampiBtn(owner, state, GruppoCampoSistema.Pianificazione, () => T("TplPianificazioneBreve"), template.CampiPianificazione, CampiSistema.ChiaviPianificazione, T, alCambioCampi, template.IsChangeRequest));
    }

    private static void BuildRiferimenti(StackPanel root, Window owner, TestTemplate template, Func<string, string> T, List<Action> alCambioCampi)
    {
        root.Children.Add(new TextBlock { Text = T("TplRiferimentiTitolo"), Classes = { "SectionTitle" } });
        root.Children.Add(new TextBlock { Text = T("TplRiferimentiSottotitolo"), Classes = { "Muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });

        var inner = new StackPanel { Spacing = 4 };
        var card = new Border { Classes = { "Card" }, Padding = new Avalonia.Thickness(14), Child = inner };
        root.Children.Add(card);
        inner.Children.Add(BuildGestisciCampiBtn(owner, null, null, () => T("TplRiferimentiBreve"), template.CampiRiferimenti, CampiSistema.ChiaviRiferimenti.ToList(), T, alCambioCampi, template.IsChangeRequest));
    }

    private static void BuildSezioni(StackPanel root, Window owner, AppState state, TestTemplate template, Func<string, string> T, List<Action> alCambioCampi)
    {
        root.Children.Add(new TextBlock { Text = T("TplSezioniTitolo"), Classes = { "SectionTitle" } });

        var sezioniPanel = new StackPanel { Spacing = 12 };
        root.Children.Add(sezioniPanel);

        void RidisegnaSezioni()
        {
            sezioniPanel.Children.Clear();
            // Ordine di visualizzazione = Ordine (revisione F2 .ai/artifacts/review_riordino.json): una
            // lettura pura, mai una mutazione — nessuna normalizzazione all'apertura, così la sola apertura
            // dell'editor non genera uno stato "modificato" su template storici (vedi HaModifiche in
            // MasterDataEditorViewModelBase, che confronta col JSON snapshot preso a inizio sessione).
            var visuale = CampoOrdinamentoService.OrdinePerVisualizzazione(template.Sezioni);
            foreach (var sezione in visuale)
            {
                var inner = new StackPanel { Spacing = 8 };
                var card = new Border { Classes = { "Card" }, Padding = new Avalonia.Thickness(14), Child = inner };

                var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var nomeSezione = new TextBox { Text = sezione.Nome, PlaceholderText = T("TplNomeSezionePlaceholder"), FontWeight = Avalonia.Media.FontWeight.SemiBold };
                nomeSezione.TextChanged += (_, _) => sezione.Nome = nomeSezione.Text ?? "";
                Grid.SetColumn(nomeSezione, 0);
                // Sposta su/giù (spec riordino campi 2026-09-23): stesso principio di
                // ImpostazioniViewModel.SpostaSuNavPrincipale per l'elenco Sezioni del template.
                var azioniSezione = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 2 };
                var suSezione = new Button { Content = "▲", Classes = { "Link" }, IsEnabled = visuale.IndexOf(sezione) > 0 };
                suSezione.Click += (_, _) => { if (CampoOrdinamentoService.SpostaSu(template.Sezioni, sezione)) RidisegnaSezioni(); };
                var giuSezione = new Button { Content = "▼", Classes = { "Link" }, IsEnabled = visuale.IndexOf(sezione) < visuale.Count - 1 };
                giuSezione.Click += (_, _) => { if (CampoOrdinamentoService.SpostaGiu(template.Sezioni, sezione)) RidisegnaSezioni(); };
                var rimuoviSezione = new Button { Content = "✕ " + T("TplRimuoviSezione"), Classes = { "Link" } };
                rimuoviSezione.Click += (_, _) => { template.Sezioni.Remove(sezione); RidisegnaSezioni(); };
                azioniSezione.Children.Add(suSezione); azioniSezione.Children.Add(giuSezione); azioniSezione.Children.Add(rimuoviSezione);
                Grid.SetColumn(azioniSezione, 1);
                header.Children.Add(nomeSezione); header.Children.Add(azioniSezione);
                inner.Children.Add(header);

                inner.Children.Add(BuildGestisciCampiBtn(owner, state, GruppoCampoSistema.Dettagli, () => sezione.Nome, sezione.Campi, CampiSistema.Chiavi, T, alCambioCampi, template.IsChangeRequest, template));

                sezioniPanel.Children.Add(card);
            }
            if (template.Sezioni.Count == 0)
                sezioniPanel.Children.Add(new TextBlock { Text = T("TplNessunaSezione"), Classes = { "Muted" } });
        }
        RidisegnaSezioni();

        var aggiungiSezione = new Button { Content = T("TplAggiungiSezione"), Classes = { "Secondary" }, HorizontalAlignment = HorizontalAlignment.Left };
        aggiungiSezione.Click += (_, _) =>
        {
            CampoOrdinamentoService.InserisciInPosizione(template.Sezioni, new SezioneTemplate { Nome = T("TplNuovaSezioneDefault") });
            RidisegnaSezioni();
        };
        root.Children.Add(aggiungiSezione);
    }

    /// <summary>Tabella dei campi condivisa tra Pianificazione (piatta) e ogni sezione di Dettagli: stessa UI,
    /// solo l'elenco di chiavi CampoSistema offerte cambia (Addendum: collaudo live 2026-08-17 — prima
    /// duplicata dentro BuildSezioni, estratta qui anche per aggiungere Unità di misura e Condizione esito
    /// senza raddoppiare la logica).</summary>
    /// <summary>Interno anziché privato: riusato anche da CampiTemplateEditorWindow, che ora ospita questa
    /// stessa tabella in una finestra dedicata invece che incollata dentro questa finestra (Addendum: collaudo
    /// live 2026-08-17, punto aggiunto al grill-me). state/gruppo sono null per Riferimenti (chiavi fisse,
    /// nessun catalogo dietro): solo quando presenti offriamo "Gestisci catalogo campi..." e "+ Crea nuovo
    /// campo..." (Addendum: grill-me 2026-08-18, follow-up — "sposta tutto dentro Tipi di test").</summary>
    /// <summary>Chiavi di sistema per cui ▲/▼ sono davvero un'affordance vera (spec riordino campi 2026-09-23
    /// §2): il modulo Test reale (TestCaseEditorView) legge Ordine per questi campi, a differenza degli altri
    /// campi classici (Funzionalità/Strumenti usati/Descrizioni in Dettagli, tutti quelli di Riferimenti) che
    /// restano nella posizione fissa preesistente — vedi CampiClassiciOrdineService, "Limiti noti" in
    /// RELEASE_NOTES_6.3.1.md.</summary>
    private static readonly HashSet<string> ChiaviClassicheRiordinabiliNelForm = new()
    {
        CampiSistema.Veicolo, CampiSistema.Componente, CampiSistema.Matricola, CampiSistema.BaselineSw, CampiSistema.BaselineHw,
        CampiSistema.StatoLavoro, CampiSistema.Scadenza, CampiSistema.Assegnatario, CampiSistema.AssegnatoDa,
        // Seguito (coordinamento 2026-09-24): riordinabili come BLOCCHI interi, non celle di una griglia — vedi
        // TestCaseEditorViewModel.RigheBlocchiDettagli/RigheBlocchiRiferimenti.
        CampiSistema.Funzionalita, CampiSistema.DescrizioneBreve, CampiSistema.Descrizione,
        CampiSistema.RequisitiCollegati, CampiSistema.TestCorrelati,
        // Generalizzazione (review indipendente PASS, coordinamento 2026-09-24): Strumenti usati e Ticket
        // collegati erano card fisiche separate non adiacenti — spostate dentro Dettagli/Riferimenti come
        // righe riservate proprio per renderle riordinabili (vedi TestCaseEditorView.axaml). Descrizione
        // problema resta invece bloccata: vedi ChiaviClassicheNonRiordinabiliConMotivo, motivo diverso e più
        // specifico del semplice "card separata".
        CampiSistema.StrumentiUsati, CampiSistema.TicketCollegati,
    };

    /// <summary>Campi classici per cui ▲/▼ restano disabilitati con un motivo SPECIFICO (diverso dal
    /// generico "posizione fissa nel form"). Solo Descrizione problema resta qui: non basta la card separata
    /// (già risolta per Strumenti usati/Ticket collegati spostandole dentro Dettagli/Riferimenti) — qui c'è
    /// un motivo esplicito preesistente nel codice (TestCaseEditorView.axaml, commento all'altezza del Border
    /// di Descrizione problema): "spostata qui a sé stante, subito sotto le Operazioni (feedback utente
    /// 2026-08-21, punto 6) — prima appariva dentro Dettagli, cambiando il flusso della pagina ogni volta che
    /// compariva". Riportarla dentro Dettagli reintrodurrebbe esattamente quel bug di UX già corretto (la sua
    /// visibilità dipende dall'Esito della revisione — un salto di layout imprevedibile ogni volta che appare/
    /// scompare mentre l'operatore lavora) — vedi RELEASE_NOTES_6.3.1.md, "Limiti noti".</summary>
    private static readonly Dictionary<string, string> ChiaviClassicheNonRiordinabiliConMotivo = new()
    {
        [CampiSistema.DescrizioneProblema] = "TplPosizioneFissaCardSeparataTooltip",
    };

    internal static void BuildCampiPanel(Panel host, Window owner, AppState? state, GruppoCampoSistema? gruppo, List<CampoTemplate> campi, IReadOnlyList<string> chiaviFisse, Func<string, string> T, bool isChangeRequest = false, TestTemplate? templateProprietario = null)
    {
        const string sentinellaNuovoCampo = "__nuovo_campo__";
        var chiaviConsentite = new List<string>();
        var etichetteConsentite = new List<string>();
        var offreCreazione = state != null && gruppo != null && state.EMaster;

        void RicaricaCatalogo()
        {
            var liberi = state != null && gruppo != null ? CampoSistemaCatalogoService.CampiLiberi(state, gruppo.Value) : new List<CampoSistemaDefinizione>();
            var etichettaDi = EtichettaCampoSistema(liberi);
            chiaviConsentite = chiaviFisse.Concat(liberi.Select(c => c.Chiave)).ToList();
            etichetteConsentite = chiaviConsentite.Select(etichettaDi).ToList();
            if (offreCreazione)
            {
                chiaviConsentite.Add(sentinellaNuovoCampo);
                etichetteConsentite.Add("+ " + T("TplCreaNuovoCampo"));
            }
        }
        RicaricaCatalogo();

        Button? gestisciCatalogo = null;
        if (state != null && gruppo != null)
        {
            gestisciCatalogo = new Button { Classes = { "Secondary" }, Content = "🗂 " + T("TplGestisciCatalogoCampi"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Avalonia.Thickness(0, 0, 0, 8) };
            Avalonia.Automation.AutomationProperties.SetAutomationId(gestisciCatalogo, "TplGestisciCatalogo_Button");
            host.Children.Add(gestisciCatalogo);
        }

        var tipiCampoLabels = new[] { T("TplCampoSistema"), T("TplCampoLibero") };
        var tipiLiberoLabels = Enum.GetValues(typeof(TipoCampoStrumento)).Cast<TipoCampoStrumento>().Select(t => t.Label()).ToList();
        var tuttiGliEsiti = Enum.GetValues(typeof(Esito)).Cast<Esito>().ToList();
        var tuttiGliStati = Enum.GetValues(typeof(StatoLavoroTest)).Cast<StatoLavoroTest>().ToList();
        var tuttiGliStatiCr = Enum.GetValues(typeof(StatoChangeRequest)).Cast<StatoChangeRequest>().ToList();

        // Larghezze riviste (Addendum: collaudo live 2026-08-17, feedback QA su testi tagliati): Condizione e
        // Obbligatorio mostrano ora riepiloghi testuali variabili ("Solo se esito: X, Y", "Obbligatorio se
        // stato: Z"), non più una singola checkbox — le larghezze originali (140/80) erano dimensionate per
        // la vecchia UI più semplice e tagliavano quel testo.
        const string cols = "120,1.4*,90,0.9*,190,190,Auto";
        var intestazione = new Grid { ColumnDefinitions = new ColumnDefinitions(cols) };
        void Intesta(int c, string testo)
        {
            // TextWrapping="Wrap" (Addendum: collaudo live 2026-08-17, feedback QA): senza, un'intestazione
            // lunga come "Opzioni (Lista) / Unità (Numero)" sborda visivamente nella colonna successiva
            // invece di andare a capo, facendo sembrare due intestazioni incollate insieme.
            var t = new TextBlock { Text = testo, FontSize = 11, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = Avalonia.Media.Brush.Parse("#64748B"), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 4, 0) };
            Grid.SetColumn(t, c);
            intestazione.Children.Add(t);
        }
        Intesta(0, T("TplTipoCampo")); Intesta(1, T("TplContenutoCampo")); Intesta(2, T("TipoCampoColonna")); Intesta(3, T("OpzioniListaColonna")); Intesta(4, T("TplCondizioneColonna")); Intesta(5, T("TplObbligatorio"));
        host.Children.Add(intestazione);

        var campiPanel = new StackPanel { Spacing = 4 };
        host.Children.Add(campiPanel);

        // Scelta della posizione all'inserimento di un nuovo campo (spec riordino campi 2026-09-23,
        // "inserisci dopo.../in cima/in fondo"): ricostruita ad ogni ridisegno perché elenca i campi
        // correnti come possibili ancore "Dopo: <nome>".
        var posizioneCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160, Margin = new Avalonia.Thickness(0, 4, 8, 0) };
        // Elenco nell'ordine di visualizzazione corrente (Ordine), ricostruito ad ogni AggiornaPosizioni:
        // usato per tradurre l'indice scelto nel combo nell'elemento "dopo" reale in aggiungiCampo.Click.
        var posizioneVisuale = new List<CampoTemplate>();
        void AggiornaPosizioni()
        {
            var indicePrecedente = posizioneCombo.SelectedIndex;
            posizioneVisuale = CampoOrdinamentoService.OrdinePerVisualizzazione(campi);
            var opzioni = new List<string> { T("TplPosizioneFondo"), T("TplPosizioneCima") };
            opzioni.AddRange(posizioneVisuale.Select(c => string.Format(T("TplPosizioneDopoFmt"), string.IsNullOrWhiteSpace(c.Nome) ? "—" : c.Nome)));
            posizioneCombo.ItemsSource = opzioni;
            posizioneCombo.SelectedIndex = indicePrecedente >= 0 && indicePrecedente < opzioni.Count ? indicePrecedente : 0;
        }

        void RidisegnaCampi()
        {
            campiPanel.Children.Clear();
            // Ordine di visualizzazione = Ordine (revisione F2): lettura pura, mai una mutazione — nessuna
            // normalizzazione alla sola apertura dell'editor (vedi CampoOrdinamentoService, nota di classe).
            var visuale = CampoOrdinamentoService.OrdinePerVisualizzazione(campi);
            foreach (var campo in visuale)
            {
                // Campo di sistema "storico" (revisione F1 .ai/artifacts/review_riordino.json): fino alla 6.3.1
                // il form di compilazione reale del Test (TestCaseEditorView) mostrava TUTTI questi campi in
                // una posizione fissa indipendente da Ordine. Dalla 6.3.1 (spec riordino campi 2026-09-23 §2)
                // il form legge davvero Ordine per il sottoinsieme in ChiaviClassicheRiordinabiliNelForm
                // (Veicolo/Componente/Matricola/BaselineSw/BaselineHw di Dettagli, Stato lavoro/Scadenza/Assegnatario/
                // Assegnato da di Pianificazione): solo per QUESTI riordinare qui è un'affordance vera, quindi
                // ▲/▼ sono ora abilitati. Gli altri campi classici (Funzionalità/Strumenti usati/Descrizioni in
                // Dettagli, tutti quelli di Riferimenti) restano nella posizione fissa preesistente — non
                // affrontati in questa modifica (vedi CampiClassiciOrdineService) — quindi restano disabilitati
                // per loro (i campi liberi/nuovi restano invece riordinabili come prima: per quelli il form
                // reale segue davvero Ordine, vedi TestCaseEditorViewModel.AggiornaCampiCustomTemplate).
                var chiave = campo.CampoSistemaChiave ?? "";
                var eClassico = campo.Tipo == TipoCampoTemplate.CampoSistema && chiaviFisse.Contains(chiave)
                    && !ChiaviClassicheRiordinabiliNelForm.Contains(chiave);
                var riga = new Grid { ColumnDefinitions = new ColumnDefinitions(cols) };

                var tipoCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = tipiCampoLabels, SelectedIndex = (int)campo.Tipo, Margin = new Avalonia.Thickness(0, 0, 4, 0) };
                Grid.SetColumn(tipoCombo, 0);

                var sistemaCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = etichetteConsentite, Margin = new Avalonia.Thickness(0, 0, 4, 0), SelectedIndex = Math.Max(0, chiaviConsentite.IndexOf(campo.CampoSistemaChiave ?? "")) };
                var nomeLiberoBox = new TextBox { Text = campo.Nome, PlaceholderText = T("NomeCampoPlaceholder"), Margin = new Avalonia.Thickness(0, 0, 4, 0) };
                Grid.SetColumn(sistemaCombo, 1); Grid.SetColumn(nomeLiberoBox, 1);

                var tipoLiberoCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = tipiLiberoLabels, SelectedIndex = (int)campo.TipoLibero, Margin = new Avalonia.Thickness(4, 0, 4, 0) };
                Grid.SetColumn(tipoLiberoCombo, 2);

                var opzioniBox = new Button { Classes = { "Secondary" }, Margin = new Avalonia.Thickness(4, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                void AggiornaTestoOpzioni() => opzioniBox.Content = campo.OpzioniListaLibero.Count > 0
                    ? $"{T("ModificaListaBtn")} ({campo.OpzioniListaLibero.Count})" : T("ModificaListaBtn");
                AggiornaTestoOpzioni();
                async Task ModificaOpzioni()
                {
                    var risultato = await ListaOpzioniEditorWindow.Apri(owner, campo.OpzioniListaLibero);
                    if (risultato == null) return;
                    campo.OpzioniListaLibero = risultato;
                    AggiornaTestoOpzioni();
                }
                // Errore dell'editor opzioni: registrato e segnalato all'utente, mai un Task scartato in silenzio (P4 3).
                Task ModificaOpzioniProtetta() => UiExceptionGuard.Esegui(ModificaOpzioni, "Modifica opzioni campo template",
                    mostra: () => ErroreOperazioneDialogo.MostraAsync(owner));
                opzioniBox.Click += async (_, _) => await ModificaOpzioniProtetta();
                var unitaBox = new TextBox { Text = campo.UnitaMisuraLibero, PlaceholderText = T("UnitaMisuraPlaceholder"), Margin = new Avalonia.Thickness(4, 0, 4, 0) };
                Grid.SetColumn(opzioniBox, 3); Grid.SetColumn(unitaBox, 3);

                // Content è un TextBlock che va a capo, non una stringa nuda (Addendum: collaudo live
                // 2026-08-17, feedback QA): con più esiti/stati selezionati il riepilogo può superare la
                // larghezza della colonna anche allargata; il tooltip mostra comunque il testo completo.
                var condizioneBtn = new Button { Classes = { "Link" }, Margin = new Avalonia.Thickness(4, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Left };
                var condizioneTesto = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                condizioneBtn.Content = condizioneTesto;
                void AggiornaTestoCondizione()
                {
                    var testo = isChangeRequest
                        ? campo.StatiCrVisibile.Count == 0 ? "Visibile in tutti gli stati CR" : "Visibile se CR: " + string.Join(", ", campo.StatiCrVisibile)
                        : campo.CondizioneEsito.Count == 0 ? T("TplCondizioneSempreBtn") : string.Format(T("TplCondizioneNEsitiFmt"), string.Join(", ", campo.CondizioneEsito.Select(e => e.Label())));
                    condizioneTesto.Text = testo;
                    Avalonia.Controls.ToolTip.SetTip(condizioneBtn, testo);
                }
                AggiornaTestoCondizione();
                condizioneBtn.Click += (_, _) =>
                {
                    if (isChangeRequest) ApriStatiCrFlyout(condizioneBtn, campo.StatiCrVisibile, tuttiGliStatiCr, LocalizationService.Instance.Traduci("TplStatiCrVisibileTitolo"), AggiornaTestoCondizione, T);
                    else ApriCondizioneEsitoFlyout(condizioneBtn, campo, tuttiGliEsiti, AggiornaTestoCondizione, T);
                };
                Grid.SetColumn(condizioneBtn, 4);

                void AggiornaVisibilita()
                {
                    var eSistema = campo.Tipo == TipoCampoTemplate.CampoSistema;
                    sistemaCombo.IsVisible = eSistema;
                    nomeLiberoBox.IsVisible = !eSistema;
                    tipoLiberoCombo.IsVisible = !eSistema;
                    opzioniBox.IsVisible = !eSistema && campo.TipoLibero == TipoCampoStrumento.Lista;
                    unitaBox.IsVisible = !eSistema && campo.TipoLibero == TipoCampoStrumento.Numero;
                }
                AggiornaVisibilita();

                tipoCombo.SelectionChanged += (_, _) =>
                {
                    campo.Tipo = (TipoCampoTemplate)Math.Max(0, tipoCombo.SelectedIndex);
                    if (campo.Tipo == TipoCampoTemplate.CampoSistema && string.IsNullOrEmpty(campo.CampoSistemaChiave))
                        campo.CampoSistemaChiave = chiaviConsentite.FirstOrDefault();
                    AggiornaVisibilita();
                };
                sistemaCombo.SelectionChanged += async (_, _) =>
                {
                    var idx = Math.Max(0, sistemaCombo.SelectedIndex);
                    if (idx >= chiaviConsentite.Count) return;
                    if (offreCreazione && chiaviConsentite[idx] == sentinellaNuovoCampo)
                    {
                        await UiExceptionGuard.Esegui(async () =>
                        {
                            var creato = await ApriCreaNuovoCampoFlyout(sistemaCombo, state!, gruppo!.Value, T);
                            RicaricaCatalogo();
                            if (creato != null) { campo.CampoSistemaChiave = creato.Chiave; campo.Nome = creato.Etichetta; }
                            RidisegnaCampi();
                        }, "Creazione rapida campo di sistema");
                        return;
                    }
                    campo.CampoSistemaChiave = chiaviConsentite[idx]; campo.Nome = etichetteConsentite[idx];
                };
                nomeLiberoBox.TextChanged += (_, _) => campo.Nome = nomeLiberoBox.Text ?? "";
                tipoLiberoCombo.SelectionChanged += (_, _) =>
                {
                    var precedente = campo.TipoLibero;
                    campo.TipoLibero = (TipoCampoStrumento)Math.Max(0, tipoLiberoCombo.SelectedIndex);
                    AggiornaVisibilita();
                    // Passando a Lista un campo libero senza opzioni, l'editor si apre subito (come nel catalogo di sistema).
                    if (ListaOpzioniEditorWindow.DeveProporre(precedente, campo.TipoLibero, campo.OpzioniListaLibero.Count)) _ = ModificaOpzioniProtetta();
                };
                unitaBox.TextChanged += (_, _) => campo.UnitaMisuraLibero = string.IsNullOrWhiteSpace(unitaBox.Text) ? null : unitaBox.Text.Trim();

                riga.Children.Add(tipoCombo); riga.Children.Add(sistemaCombo); riga.Children.Add(nomeLiberoBox); riga.Children.Add(tipoLiberoCombo);
                riga.Children.Add(opzioniBox); riga.Children.Add(unitaBox); riga.Children.Add(condizioneBtn);

                var obbligatorioBtn = new Button { Classes = { "Link" }, Margin = new Avalonia.Thickness(4, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Left };
                var obbligatorioTesto = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                obbligatorioBtn.Content = obbligatorioTesto;
                void AggiornaTestoObbligatorio()
                {
                    var testo = isChangeRequest
                        ? campo.StatiCrObbligatorio.Count == 0 ? (campo.Obbligatorio ? LocalizationService.Instance.Traduci("TplObbligatorioTuttiStatiCrTxt") : LocalizationService.Instance.Traduci("TplNonObbligatorioTxt")) : LocalizationService.Instance.Traduci("TplObbligatorioSeCrPrefisso") + string.Join(", ", campo.StatiCrObbligatorio)
                        : !campo.Obbligatorio
                        ? T("TplObbligatorioNoBtn")
                        : campo.StatiObbligatorio.Count == 0
                            ? T("TplObbligatorioSiBtn")
                            : string.Format(T("TplObbligatorioSeStatoFmt"), string.Join(", ", campo.StatiObbligatorio.Select(s => s.Label())));
                    obbligatorioTesto.Text = testo;
                    Avalonia.Controls.ToolTip.SetTip(obbligatorioBtn, testo);
                }
                AggiornaTestoObbligatorio();
                obbligatorioBtn.Click += (_, _) =>
                {
                    if (isChangeRequest) ApriObbligatorioCrFlyout(obbligatorioBtn, campo, tuttiGliStatiCr, AggiornaTestoObbligatorio, T);
                    else ApriObbligatorioFlyout(obbligatorioBtn, campo, tuttiGliStati, AggiornaTestoObbligatorio, T);
                };
                Grid.SetColumn(obbligatorioBtn, 5);
                riga.Children.Add(obbligatorioBtn);

                // Sposta su/giù (spec riordino campi 2026-09-23): stesso principio di
                // ImpostazioniViewModel.SpostaSuNavPrincipale, adattato a questa List<CampoTemplate>
                // ridisegnata a mano invece che a un'ObservableCollection legata direttamente al XAML.
                var azioni = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 2 };
                var indiceVisuale = visuale.IndexOf(campo);
                // Correzione di compatibilità Veicolo/Componente (CampiClassiciOrdineService.CorreggiCompatibilitaVeicoloEcu):
                // solo per il gruppo Dettagli (templateProprietario valorizzato solo lì), una volta sola prima
                // del primissimo riordino esplicito di uno di questi 5 campi — vedi commento sulla classe del
                // servizio per il perché non basta leggere Ordine da solo sui template esistenti.
                void PrimaDiRiordinare()
                {
                    if (templateProprietario != null && ChiaviClassicheRiordinabiliNelForm.Contains(chiave))
                        CampiClassiciOrdineService.CorreggiCompatibilitaVeicoloEcu(templateProprietario);
                }
                var suCampo = new Button { Content = "▲", Classes = { "Link" }, IsEnabled = !eClassico && indiceVisuale > 0 };
                suCampo.Click += (_, _) => { PrimaDiRiordinare(); if (CampoOrdinamentoService.SpostaSu(campi, campo)) RidisegnaCampi(); };
                var giuCampo = new Button { Content = "▼", Classes = { "Link" }, IsEnabled = !eClassico && indiceVisuale < visuale.Count - 1 };
                giuCampo.Click += (_, _) => { PrimaDiRiordinare(); if (CampoOrdinamentoService.SpostaGiu(campi, campo)) RidisegnaCampi(); };
                // AutomationId per i test FlaUI (spec riordino campi 2026-09-23 §2, seguito coordinamento
                // 2026-09-24): solo per i campi di sistema, dove la chiave è stabile riga per riga; un campo
                // libero non ha una chiave stabile per un Id univoco riusabile fra ricostruzioni del pannello,
                // quindi resta senza (nessun test UI ne ha bisogno).
                if (!string.IsNullOrEmpty(chiave))
                {
                    Avalonia.Automation.AutomationProperties.SetAutomationId(suCampo, $"TplCampoSu_{chiave}");
                    Avalonia.Automation.AutomationProperties.SetAutomationId(giuCampo, $"TplCampoGiu_{chiave}");
                }
                if (eClassico)
                {
                    // Motivo specifico (card fisica separata) quando disponibile, altrimenti il generico
                    // "posizione fissa nel form" — mai un ▲/▼ disabilitato senza spiegazione in UI.
                    var motivo = ChiaviClassicheNonRiordinabiliConMotivo.TryGetValue(chiave, out var chiaveMotivo)
                        ? T(chiaveMotivo)
                        : T("TplPosizioneFissaNelFormTooltip");
                    Avalonia.Controls.ToolTip.SetTip(suCampo, motivo);
                    Avalonia.Controls.ToolTip.SetTip(giuCampo, motivo);
                }
                var rimuoviCampo = new Button { Content = "✕", Classes = { "Link" } };
                rimuoviCampo.Click += (_, _) => { campi.Remove(campo); RidisegnaCampi(); };
                azioni.Children.Add(suCampo); azioni.Children.Add(giuCampo); azioni.Children.Add(rimuoviCampo);
                Grid.SetColumn(azioni, 6);
                riga.Children.Add(azioni);

                // AutomationId per i test FlaUI (copertura: campi template e campi di sistema), per riga e per indice
                // visuale. Su/Giu dei campi di sistema con chiave mantengono l'Id stabile per chiave assegnato sopra.
                var prefissoCampo = $"TplCampo_Riga_{indiceVisuale}";
                Avalonia.Automation.AutomationProperties.SetAutomationId(tipoCombo, $"{prefissoCampo}_Tipo_ComboBox");
                Avalonia.Automation.AutomationProperties.SetAutomationId(sistemaCombo, $"{prefissoCampo}_Sistema_ComboBox");
                Avalonia.Automation.AutomationProperties.SetAutomationId(nomeLiberoBox, $"{prefissoCampo}_NomeLibero_TextBox");
                Avalonia.Automation.AutomationProperties.SetAutomationId(tipoLiberoCombo, $"{prefissoCampo}_TipoLibero_ComboBox");
                Avalonia.Automation.AutomationProperties.SetAutomationId(opzioniBox, $"{prefissoCampo}_Opzioni_Button");
                Avalonia.Automation.AutomationProperties.SetAutomationId(unitaBox, $"{prefissoCampo}_Unita_TextBox");
                Avalonia.Automation.AutomationProperties.SetAutomationId(condizioneBtn, $"{prefissoCampo}_Condizione_Button");
                Avalonia.Automation.AutomationProperties.SetAutomationId(obbligatorioBtn, $"{prefissoCampo}_Obbligatorio_Button");
                if (string.IsNullOrEmpty(chiave))
                {
                    Avalonia.Automation.AutomationProperties.SetAutomationId(suCampo, $"{prefissoCampo}_Su_Button");
                    Avalonia.Automation.AutomationProperties.SetAutomationId(giuCampo, $"{prefissoCampo}_Giu_Button");
                }
                Avalonia.Automation.AutomationProperties.SetAutomationId(rimuoviCampo, $"{prefissoCampo}_Rimuovi_Button");

                campiPanel.Children.Add(riga);
            }
            if (campi.Count == 0)
                campiPanel.Children.Add(new TextBlock { Text = T("NessunCampoDefinito"), Classes = { "Muted" }, Margin = new Avalonia.Thickness(0, 4) });
            AggiornaPosizioni();
        }
        RidisegnaCampi();

        if (gestisciCatalogo != null)
        {
            gestisciCatalogo.Click += async (_, _) =>
            {
                await UiExceptionGuard.Esegui(async () =>
                {
                    await CampoSistemaCatalogoWindow.Apri(owner, state!, gruppo!.Value);
                    RicaricaCatalogo();
                    RidisegnaCampi();
                }, "Apertura catalogo campi di sistema");
            };
        }

        var aggiungiCampo = new Button { Content = T("TplAggiungiCampo"), Classes = { "Secondary" }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        Avalonia.Automation.AutomationProperties.SetAutomationId(aggiungiCampo, "TplCampo_Aggiungi_Button");
        aggiungiCampo.Click += (_, _) =>
        {
            var nuovo = new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = chiaviConsentite.FirstOrDefault(), Nome = etichetteConsentite.FirstOrDefault() ?? "" };
            var scelta = posizioneCombo.SelectedIndex; // 0=fondo, 1=cima, >=2 => dopo posizioneVisuale[scelta-2]
            if (scelta <= 0) CampoOrdinamentoService.InserisciInPosizione(campi, nuovo);
            else if (scelta == 1) CampoOrdinamentoService.InserisciInPosizione(campi, nuovo, inizio: true);
            else CampoOrdinamentoService.InserisciInPosizione(campi, nuovo, dopo: posizioneVisuale[Math.Min(scelta - 2, posizioneVisuale.Count - 1)]);
            RidisegnaCampi();
        };
        var aggiungiRiga = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 0 };
        aggiungiRiga.Children.Add(posizioneCombo);
        aggiungiRiga.Children.Add(aggiungiCampo);
        host.Children.Add(aggiungiRiga);
    }

    /// <summary>Creazione rapida di un campo del catalogo condiviso senza uscire dall'editor di Tipi di test
    /// (Addendum: grill-me 2026-08-18, follow-up — "+ Crea nuovo campo..."): guida
    /// CampoSistemaDefinizioneViewModel attraverso lo stesso percorso Modifica→Nuovo→Salva usato dal dialogo
    /// completo, quindi con lock file e changelog regolari, invece di scrivere il catalogo a mano bypassandoli.
    /// Restituisce null se annullato o se il salvataggio fallisce (lock altrui, errore disco).</summary>
    private static Task<CampoSistemaDefinizione?> ApriCreaNuovoCampoFlyout(Control anchor, AppState state, GruppoCampoSistema gruppo, Func<string, string> T)
    {
        var tcs = new TaskCompletionSource<CampoSistemaDefinizione?>();
        var panel = new StackPanel { Spacing = 8, MinWidth = 240 };
        panel.Children.Add(new TextBlock { Text = T("TplCreaNuovoCampoTitolo"), FontSize = 12, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap });

        var etichettaBox = new TextBox { PlaceholderText = T("NomeCampoPlaceholder") };
        panel.Children.Add(etichettaBox);

        var tipoLabels = Enum.GetValues(typeof(TipoCampoStrumento)).Cast<TipoCampoStrumento>().Select(t => t.Label()).ToList();
        var tipoCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = tipoLabels, SelectedIndex = 0 };
        panel.Children.Add(tipoCombo);

        // Stessa spunta della colonna Filtro dell'editor del catalogo: default off, il campo resta utilizzabile come filtro solo se spuntata.
        var usabileFiltroBox = new CheckBox { Content = T("TplCreaNuovoCampoUsabileFiltro"), IsChecked = false };
        panel.Children.Add(usabileFiltroBox);

        var erroreText = new TextBlock { Foreground = Avalonia.Media.Brush.Parse("#DC2626"), FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(erroreText);

        var bottoni = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var annulla = new Button { Content = T("Annulla"), Classes = { "Secondary" } };
        Avalonia.Automation.AutomationProperties.SetAutomationId(annulla, "TplCreaNuovoCampo_Annulla_Button");
        var crea = new Button { Content = T("TplCreaNuovoCampoBtn"), Classes = { "Primary" } };
        bottoni.Children.Add(annulla); bottoni.Children.Add(crea);
        panel.Children.Add(bottoni);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        var completato = false;
        var operazioneInCorso = false;
        flyout.Closing += (_, e) =>
        {
            // Closing is cancelable for every Flyout dismissal path (outside click, Escape, or Hide).
            // Only the successful completion below marks the close as intentional while the save is active.
            if (operazioneInCorso && !completato) e.Cancel = true;
        };
        flyout.Closed += (_, _) =>
        {
            if (!completato && !operazioneInCorso) tcs.TrySetResult(null);
        };

        annulla.Click += (_, _) =>
        {
            if (operazioneInCorso) return;
            completato = true;
            flyout.Hide();
            tcs.TrySetResult(null);
        };
        crea.Click += async (_, _) =>
        {
            if (operazioneInCorso || !crea.IsEnabled) return;
            var etichetta = (etichettaBox.Text ?? "").Trim();
            if (etichetta.Length == 0)
            {
                erroreText.Text = T("TplNomeCampoObbligatorio");
                erroreText.IsVisible = true;
                return;
            }

            operazioneInCorso = true;
            annulla.IsEnabled = false;
            crea.IsEnabled = false;
            try
            {
                var vm = new CampoSistemaDefinizioneViewModel(state, gruppo);
                var tipo = (TipoCampoStrumento)Math.Max(0, tipoCombo.SelectedIndex);
                var (nuovaVoce, errore) = await vm.CreaNuovoRapidoAsync(etichetta, tipo, usabileFiltroBox.IsChecked == true);
                if (nuovaVoce == null)
                {
                    erroreText.Text = errore ?? T("TplCreaNuovoCampoErroreGenerico");
                    erroreText.IsVisible = true;
                    return;
                }

                completato = true;
                flyout.Hide();
                tcs.TrySetResult(nuovaVoce);
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Creazione rapida campo di sistema", ex);
                erroreText.Text = T("TplCreaNuovoCampoErroreGenerico");
                erroreText.IsVisible = true;
            }
            finally
            {
                operazioneInCorso = false;
                crea.IsEnabled = true;
                annulla.IsEnabled = !completato;
            }
        };

        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
        return tcs.Task;
    }

    private static void ApriCondizioneEsitoFlyout(Button button, CampoTemplate campo, List<Esito> tuttiGliEsiti, Action aggiornaTesto, Func<string, string> T)
    {
        var panel = new StackPanel { Spacing = 6, MinWidth = 220 };
        panel.Children.Add(new TextBlock { Text = T("TplCondizioneTitolo"), FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
        foreach (var esito in tuttiGliEsiti)
        {
            var check = new CheckBox { Content = esito.Label(), IsChecked = campo.CondizioneEsito.Contains(esito) };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) { if (!campo.CondizioneEsito.Contains(esito)) campo.CondizioneEsito.Add(esito); }
                else campo.CondizioneEsito.Remove(esito);
                aggiornaTesto();
            };
            panel.Children.Add(check);
        }
        var chiudi = new Button { Content = T("TplCondizioneChiudiBtn"), Classes = { "Secondary" }, Margin = new Avalonia.Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        chiudi.Click += (_, _) => flyout.Hide();
        panel.Children.Add(chiudi);
        FlyoutBase.SetAttachedFlyout(button, flyout);
        flyout.ShowAt(button);
    }

    private static void ApriStatiCrFlyout(Button button, List<StatoChangeRequest> selezionati, List<StatoChangeRequest> tutti, string titolo, Action aggiornaTesto, Func<string, string> T)
    {
        var panel = new StackPanel { Spacing = 6, MinWidth = 260 };
        panel.Children.Add(new TextBlock { Text = titolo, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        foreach (var stato in tutti)
        {
            var check = new CheckBox { Content = stato == StatoChangeRequest.DaValidare ? "Da validare" : stato.ToString(), IsChecked = selezionati.Contains(stato) };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) { if (!selezionati.Contains(stato)) selezionati.Add(stato); }
                else selezionati.Remove(stato);
                aggiornaTesto();
            };
            panel.Children.Add(check);
        }
        var chiudi = new Button { Content = T("TplCondizioneChiudiBtn"), Classes = { "Secondary" }, HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        chiudi.Click += (_, _) => flyout.Hide();
        panel.Children.Add(chiudi);
        FlyoutBase.SetAttachedFlyout(button, flyout);
        flyout.ShowAt(button);
    }

    private static void ApriObbligatorioCrFlyout(Button button, CampoTemplate campo, List<StatoChangeRequest> tutti, Action aggiornaTesto, Func<string, string> T)
    {
        var panel = new StackPanel { Spacing = 6, MinWidth = 260 };
        var sempre = new CheckBox { Content = LocalizationService.Instance.Traduci("TplObbligatorioTuttiStatiCrTxt"), IsChecked = campo.Obbligatorio };
        panel.Children.Add(sempre);
        var stati = new StackPanel { Spacing = 4, IsVisible = !campo.Obbligatorio };
        panel.Children.Add(stati);
        stati.Children.Add(new TextBlock { Text = LocalizationService.Instance.Traduci("TplOppureSelezionaStatiCrTxt"), FontSize = 12 });
        foreach (var stato in tutti)
        {
            var check = new CheckBox { Content = stato == StatoChangeRequest.DaValidare ? "Da validare" : stato.ToString(), IsChecked = campo.StatiCrObbligatorio.Contains(stato) };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) { if (!campo.StatiCrObbligatorio.Contains(stato)) campo.StatiCrObbligatorio.Add(stato); }
                else campo.StatiCrObbligatorio.Remove(stato);
                aggiornaTesto();
            };
            stati.Children.Add(check);
        }
        sempre.IsCheckedChanged += (_, _) =>
        {
            campo.Obbligatorio = sempre.IsChecked == true;
            if (campo.Obbligatorio) campo.StatiCrObbligatorio.Clear();
            stati.IsVisible = !campo.Obbligatorio;
            aggiornaTesto();
        };
        var chiudi = new Button { Content = T("TplCondizioneChiudiBtn"), Classes = { "Secondary" }, HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        chiudi.Click += (_, _) => flyout.Hide();
        panel.Children.Add(chiudi);
        FlyoutBase.SetAttachedFlyout(button, flyout);
        flyout.ShowAt(button);
    }

    /// <summary>Obbligatorio e Condizione esito sono due assi indipendenti che si combinano, non alternative
    /// (Addendum: collaudo live 2026-08-17, feedback QA — "non devi perdere la condizione dovuta anche al
    /// risultato, devono rimanere due condizioni, sia stato lavoro che risultato"): un campo può essere
    /// obbligatorio solo in certi Stati lavoro E visibile solo per certi Esiti allo stesso tempo — entrambi i
    /// filtri vengono applicati insieme in TestCaseEditorViewModel.ValidaCampiObbligatoriDaTemplate.</summary>
    private static void ApriObbligatorioFlyout(Button button, CampoTemplate campo, List<StatoLavoroTest> tuttiGliStati, Action aggiornaTesto, Func<string, string> T)
    {
        var panel = new StackPanel { Spacing = 6, MinWidth = 220 };
        var obbligatorioCheck = new CheckBox { Content = T("TplObbligatorio"), IsChecked = campo.Obbligatorio };
        panel.Children.Add(obbligatorioCheck);

        var statiPanel = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(0, 6, 0, 0), IsVisible = campo.Obbligatorio };
        panel.Children.Add(statiPanel);
        statiPanel.Children.Add(new TextBlock { Text = T("TplObbligatorioStatoTitolo"), FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
        foreach (var stato in tuttiGliStati)
        {
            var check = new CheckBox { Content = stato.Label(), IsChecked = campo.StatiObbligatorio.Contains(stato) };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) { if (!campo.StatiObbligatorio.Contains(stato)) campo.StatiObbligatorio.Add(stato); }
                else campo.StatiObbligatorio.Remove(stato);
                aggiornaTesto();
            };
            statiPanel.Children.Add(check);
        }

        obbligatorioCheck.IsCheckedChanged += (_, _) =>
        {
            campo.Obbligatorio = obbligatorioCheck.IsChecked == true;
            statiPanel.IsVisible = campo.Obbligatorio;
            aggiornaTesto();
        };

        var chiudi = new Button { Content = T("TplCondizioneChiudiBtn"), Classes = { "Secondary" }, Margin = new Avalonia.Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        chiudi.Click += (_, _) => flyout.Hide();
        panel.Children.Add(chiudi);
        FlyoutBase.SetAttachedFlyout(button, flyout);
        flyout.ShowAt(button);
    }

    /// <summary>Default per tipo di test dei campi strumento per operazione da nascondere: solo visibilita', ogni Test puo' cambiarla.</summary>
    private static void BuildCampiOperazioneNascosti(StackPanel root, TestTemplate template, IReadOnlyList<StrumentoDiagnostico> strumenti, Func<string, string> T)
    {
        var conCampi = strumenti.Where(s => s.Campi.Any(c => c.Ambito == TipoAmbitoCampo.PerOperazione)).ToList();
        if (conCampi.Count == 0 || template.IsChangeRequest) return;
        root.Children.Add(new TextBlock { Text = T("TplCampiNascostiTitolo"), Classes = { "SectionTitle" } });
        root.Children.Add(new TextBlock { Text = T("TplCampiNascostiHelp"), Classes = { "Muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        foreach (var strumento in conCampi)
        {
            var wrap = new WrapPanel();
            foreach (var campo in strumento.Campi.Where(c => c.Ambito == TipoAmbitoCampo.PerOperazione).OrderBy(c => c.Ordine))
            {
                var chiave = strumento.Nome + "::" + campo.Nome;
                var check = new CheckBox
                {
                    Content = campo.Nome,
                    IsChecked = template.CampiOperazioneNascosti.Contains(chiave, StringComparer.OrdinalIgnoreCase),
                    Margin = new Avalonia.Thickness(0, 0, 16, 6)
                };
                Avalonia.Automation.AutomationProperties.SetAutomationId(check, "Template_CampoNascosto_" + chiave + "_Check");
                check.IsCheckedChanged += (_, _) =>
                {
                    template.CampiOperazioneNascosti.RemoveAll(k => string.Equals(k, chiave, StringComparison.OrdinalIgnoreCase));
                    if (check.IsChecked == true) template.CampiOperazioneNascosti.Add(chiave);
                };
                wrap.Children.Add(check);
            }
            root.Children.Add(new Expander { Header = strumento.Nome, Content = wrap });
        }
    }

    private static void BuildStrumentiPreselezionati(StackPanel root, TestTemplate template, IReadOnlyList<string> strumenti, Func<string, string> T)
    {
        root.Children.Add(new TextBlock { Text = T("TplStrumentiTitolo"), Classes = { "SectionTitle" } });
        var wrap = new WrapPanel();
        var checkBoxes = new Dictionary<string, CheckBox>();
        foreach (var nome in strumenti)
        {
            var check = new CheckBox { Content = nome, IsChecked = template.StrumentiPreselezionati.Contains(nome), Margin = new Avalonia.Thickness(0, 0, 16, 6) };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) { if (!template.StrumentiPreselezionati.Contains(nome)) template.StrumentiPreselezionati.Add(nome); }
                else template.StrumentiPreselezionati.Remove(nome);
            };
            checkBoxes[nome] = check;
            wrap.Children.Add(check);
        }
        if (strumenti.Count == 0) wrap.Children.Add(new TextBlock { Text = T("TplNessunoStrumentoDisponibile"), Classes = { "Muted" } });
        // Ricerca solo oltre soglia (feedback utente 2026-08-21), stessa soglia di ListaOltreSogliaConverter.
        if (strumenti.Count > 10)
        {
            var ricerca = new TextBox { PlaceholderText = LocalizationService.Instance.Traduci("CercaPlaceholder"), Margin = new Avalonia.Thickness(0, 0, 0, 6), MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
            ricerca.TextChanged += (_, _) =>
            {
                var q = ricerca.Text ?? "";
                foreach (var (nome, check) in checkBoxes)
                    check.IsVisible = string.IsNullOrWhiteSpace(q) || nome.Contains(q, StringComparison.OrdinalIgnoreCase);
            };
            root.Children.Add(ricerca);
        }
        root.Children.Add(wrap);
    }

    /// <summary>Quali sezioni del pacchetto Ticketing proporre nel dialogo di esportazione per i test di
    /// questo template: nessuna spuntata = tutte disponibili, come prima di questa configurazione
    /// (TestCaseEditorViewModel.SezioniPolarionDisponibili), così i template esistenti non cambiano
    /// comportamento finché un Master non restringe esplicitamente l'elenco (feedback utente 2026-08-18,
    /// punto 3).</summary>
    private static void BuildSezioniPolarion(StackPanel root, TestTemplate template, Func<string, string> T)
    {
        root.Children.Add(new TextBlock { Text = T("TplSezioniPolarionTitolo"), Classes = { "SectionTitle" } });
        root.Children.Add(new TextBlock { Text = T("TplSezioniPolarionSottotitolo"), Classes = { "Muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var wrap = new WrapPanel();
        foreach (var chiave in PolarionSezioni.Tutte)
        {
            var check = new CheckBox { Content = T("PolarionSezione" + chiave), IsChecked = template.SezioniPolarionDisponibili.Contains(chiave), Margin = new Avalonia.Thickness(0, 0, 16, 6) };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) { if (!template.SezioniPolarionDisponibili.Contains(chiave)) template.SezioniPolarionDisponibili.Add(chiave); }
                else template.SezioniPolarionDisponibili.Remove(chiave);
            };
            wrap.Children.Add(check);
        }
        root.Children.Add(wrap);
    }

    // BuildAzioniCollegate rimosso (feedback utente 2026-08-20, punto 17): la configurazione statica per
    // template delle AzioneCollegataTemplate è superata dal menu interattivo runtime CreaTestCollegatoWindow,
    // che chiede ogni volta quale/i tipo/i di test creare e quali campi copiare, senza limitarsi a destinazioni
    // preconfigurate dal Master.

    private static void BuildVincoloCollegamento(StackPanel root, TestTemplate template, IReadOnlyList<TestTemplate> tutti, Func<string, string> T)
    {
        root.Children.Add(new TextBlock { Text = T("TplVincoloTitolo"), Classes = { "SectionTitle" } });

        var ammessi = tutti.Where(t => t.Uid != template.Uid && !t.Archiviata).ToList();

        var attivo = new CheckBox { Content = T("TplVincoloAttivo"), IsChecked = template.VincoloCollegamento != null };
        root.Children.Add(attivo);

        var dettaglioPanel = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(0, 6, 0, 0), IsVisible = template.VincoloCollegamento != null };
        root.Children.Add(dettaglioPanel);

        var etichetta = new TextBox { Text = template.VincoloCollegamento?.Etichetta ?? T("TplVincoloEtichettaDefault"), PlaceholderText = T("TplEtichettaVincoloPlaceholder") };
        var obbligatorio = new CheckBox { Content = T("TplVincoloObbligatorio"), IsChecked = template.VincoloCollegamento?.Obbligatorio ?? false };
        dettaglioPanel.Children.Add(etichetta);
        dettaglioPanel.Children.Add(obbligatorio);
        dettaglioPanel.Children.Add(new TextBlock { Text = T("TplVincoloTemplateAmmessiLabel"), FontSize = 11, Foreground = Avalonia.Media.Brush.Parse("#64748B") });

        var ammessiWrap = new WrapPanel();

        // Ricerca solo oltre soglia (feedback utente 2026-08-21), stessa soglia di ListaOltreSogliaConverter.
        // Filtra su ammessiWrap.Children al momento della digitazione, non su uno snapshot: RidisegnaAmmessi
        // ricrea le checkbox ogni volta che il vincolo viene (ri)attivato.
        if (ammessi.Count > 10)
        {
            var ricerca = new TextBox { PlaceholderText = LocalizationService.Instance.Traduci("CercaPlaceholder"), Margin = new Avalonia.Thickness(0, 4, 0, 4), MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
            ricerca.TextChanged += (_, _) =>
            {
                var q = ricerca.Text ?? "";
                foreach (var check in ammessiWrap.Children.OfType<CheckBox>())
                    check.IsVisible = string.IsNullOrWhiteSpace(q) || (check.Content?.ToString() ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
            };
            dettaglioPanel.Children.Add(ricerca);
        }

        dettaglioPanel.Children.Add(ammessiWrap);

        void AssicuraVincolo() => template.VincoloCollegamento ??= new VincoloCollegamentoTemplate { Etichetta = etichetta.Text ?? "", Obbligatorio = obbligatorio.IsChecked == true };

        // Ridisegnato ogni volta che il vincolo viene (ri)attivato, non solo alla prima apertura: disattivando
        // "attivo" il modello torna a null (nessun vincolo), quindi le checkbox devono ripartire tutte scariche
        // invece di restare visivamente spuntate da prima — altrimenti la UI mostrerebbe una selezione che il
        // modello sottostante non ha più (bug trovato in revisione: senza questo, riattivare il vincolo dopo
        // averlo disattivato lasciava le checkbox "spuntate a vista" ma TemplateAmmessiUid vuoto finché l'utente
        // non ritoccava manualmente ciascuna voce).
        void RidisegnaAmmessi()
        {
            ammessiWrap.Children.Clear();
            foreach (var t in ammessi)
            {
                var check = new CheckBox { Content = t.Nome, IsChecked = template.VincoloCollegamento?.TemplateAmmessiUid.Contains(t.Uid) ?? false, Margin = new Avalonia.Thickness(0, 0, 12, 4) };
                check.IsCheckedChanged += (_, _) =>
                {
                    AssicuraVincolo();
                    if (check.IsChecked == true) { if (!template.VincoloCollegamento!.TemplateAmmessiUid.Contains(t.Uid)) template.VincoloCollegamento.TemplateAmmessiUid.Add(t.Uid); }
                    else template.VincoloCollegamento!.TemplateAmmessiUid.Remove(t.Uid);
                };
                ammessiWrap.Children.Add(check);
            }
            if (ammessi.Count == 0) ammessiWrap.Children.Add(new TextBlock { Text = T("TplNessunAltroTemplate"), Classes = { "Muted" } });
        }
        RidisegnaAmmessi();

        etichetta.TextChanged += (_, _) => { if (template.VincoloCollegamento != null) template.VincoloCollegamento.Etichetta = etichetta.Text ?? ""; };
        obbligatorio.IsCheckedChanged += (_, _) => { if (template.VincoloCollegamento != null) template.VincoloCollegamento.Obbligatorio = obbligatorio.IsChecked == true; };

        attivo.IsCheckedChanged += (_, _) =>
        {
            if (attivo.IsChecked == true) { AssicuraVincolo(); RidisegnaAmmessi(); dettaglioPanel.IsVisible = true; }
            else { template.VincoloCollegamento = null; dettaglioPanel.IsVisible = false; }
        };
    }

}
