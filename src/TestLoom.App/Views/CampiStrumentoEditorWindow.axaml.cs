using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>Editor a tabella dei campi personalizzati di uno strumento (Addendum §5/§8, feedback QA):
/// condiviso tra Impostazioni → Strumenti e l'editor del test, perché deve essere possibile gestire i campi di
/// uno strumento anche direttamente da dentro un'operazione, non solo da Impostazioni — stessa identica UI in
/// entrambi i posti, non due implementazioni da tenere allineate a mano. Era un Flyout (CampiStrumentoFlyoutHelper);
/// convertito in finestra ridimensionabile come TestTemplateEditorWindow (Addendum: collaudo live 2026-08-17,
/// punto 7 del grill-me — un Flyout stretto (MinWidth 620) non dava abbastanza spazio a 6 colonne + azioni).
///
/// La finestra modifica sempre una working copy profonda. L'oggetto del chiamante viene aggiornato solo quando
/// l'utente preme Salva; Annulla e X scartano quindi davvero le modifiche. Se il callback di commit immediato
/// del Test editor fallisce per conflitto/IO, ripristiniamo l'oggetto originale prima di mostrare l'errore e la
/// working copy resta aperta, così l'utente non perde ciò che stava tentando di salvare.</summary>
public partial class CampiStrumentoEditorWindow : Window
{
    public CampiStrumentoEditorWindow()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this, grande: true);
    }

    /// <param name="vista">Dal Test: i campi per operazione di questo strumento con la loro visibilita' in QUESTO Test (solo scelta di
    /// visibilita', i dati non vengono toccati). Null = finestra aperta da Impostazioni.</param>
    /// <param name="puoModificareCampi">Falso per chi non gestisce gli strumenti: vede solo la scelta di visibilita'.</param>
    private static void ApplicaVista(List<(ViewModels.VoceVistaCampo Voce, CheckBox Check)> scelte)
    {
        foreach (var (voce, check) in scelte)
            if (voce.PuoCambiare && check.IsChecked is bool v && v != voce.Visibile) voce.Visibile = v;
    }

    public static Task Apri(Window owner, StrumentoDiagnostico strumento, Action? alChiudere = null,
        IReadOnlyList<ViewModels.VoceVistaCampo>? vista = null, bool puoModificareCampi = true)
    {
        var loc = LocalizationService.Instance;
        string T(string key) => loc.Traduci(key);

        // Non editare mai direttamente l'istanza posseduta dal ViewModel: nel percorso Test quella stessa
        // istanza vive dentro _tuttiGliStrumenti e viene passata al repository. Se il Save fallisce, lasciarla
        // mutata renderebbe impossibile un vero Annulla e potrebbe sporcare ulteriori operazioni in memoria.
        var working = EntityStateFingerprint.Clone(strumento);

        var w = new CampiStrumentoEditorWindow();
        var titolo = string.Format(T("CampiStrumentoEditorTitoloFmt"), strumento.Nome);
        w.Title = titolo;
        w.TitoloText.Text = titolo;
        w.AnnullaButton.Click += (_, _) => w.Close();
        w.AnteprimaButton.Click += (_, _) => TestFormPreviewWindow.Apri(w, () => new List<PreviewSezione> { TestFormPreviewRenderer.DaStrumento(working) });

        // Scelta dei campi visibili in questo Test: lavora su copie e viene applicata solo con Salva.
        var scelte = new List<(ViewModels.VoceVistaCampo Voce, CheckBox Check)>();
        var haVista = vista is { Count: > 0 };
        if (haVista)
        {
            var sezione = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(0, 0, 0, 12) };
            sezione.Children.Add(new TextBlock { Text = T("VistaCampiTitolo"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
            sezione.Children.Add(new TextBlock { Text = T("VistaCampiHelp"), Classes = { "Muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var wrapVista = new WrapPanel();
            foreach (var voce in vista!)
            {
                var check = new CheckBox
                {
                    Content = voce.Compilato ? $"{voce.Nome} {T("VistaCampiCompilato")}" : voce.Nome,
                    IsChecked = voce.Visibile, IsEnabled = voce.PuoCambiare, Margin = new Avalonia.Thickness(0, 0, 16, 4)
                };
                Avalonia.Automation.AutomationProperties.SetAutomationId(check, "CampiStrumento_Visibile_" + voce.Chiave + "_Check");
                scelte.Add((voce, check));
                wrapVista.Children.Add(check);
            }
            sezione.Children.Add(wrapVista);
            w.RootPanel.Children.Add(sezione);
        }
        if (!puoModificareCampi)
        {
            w.AnteprimaButton.IsVisible = false;
            w.SalvaButton.Click += (_, _) => { ApplicaVista(scelte); w.Close(); };
            return w.ShowDialog(owner);
        }

        w.SalvaButton.Click += (_, _) =>
        {
            ApplicaVista(scelte);
            // Conserviamo solo la parte che questa finestra è autorizzata a mutare. Non rimpiazziamo l'intero
            // StrumentoDiagnostico: Nome/Archiviata/metadati possono essere cambiati contemporaneamente
            // dall'editor master principale e non appartengono a questa finestra.
            var campiOriginali = EntityStateFingerprint.Clone(strumento.Campi);
            try
            {
                strumento.Campi = EntityStateFingerprint.Clone(working.Campi);
                alChiudere?.Invoke();
            }
            catch (Exception ex)
            {
                // Il callback del Test editor salva immediatamente il master. Se fallisce, il repository non ha
                // scritto nulla: ripristinare anche l'istanza in memoria mantiene la stessa atomicità percepita
                // e permette ad Annulla/X di uscire senza trascinarsi dietro modifiche mai committate.
                strumento.Campi = campiOriginali;
                CrashLogService.Registra("Salvataggio campi strumento", ex);
                w.ErroreSalvataggioText.Text = string.Format(
                    T("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
                w.ErroreSalvataggioBorder.IsVisible = true;
                return;
            }

            w.ErroreSalvataggioBorder.IsVisible = false;
            w.ErroreSalvataggioText.Text = "";
            w.Close();
        };

        var tipiLabel = Enum.GetValues(typeof(TipoCampoStrumento)).Cast<TipoCampoStrumento>().Select(t => t.Label()).ToList();
        var ambitiLabel = Enum.GetValues(typeof(TipoAmbitoCampo)).Cast<TipoAmbitoCampo>().Select(a => a.Label()).ToList();

        var root = w.RootPanel;

        // Larghezze allargate (Addendum: collaudo live 2026-08-17, feedback QA su testi tagliati): "Testo
        // libero" in un ComboBox e i placeholder di Gruppo non stavano in 100/110px.
        const string cols = "1.4*,150,150,1.1*,1.1*,1.1*,Auto";
        var intestazione = new Grid { ColumnDefinitions = new ColumnDefinitions(cols) };
        void Intesta(int colonna, string chiave, string? tooltip = null)
        {
            // TextWrapping="Wrap" (Addendum: collaudo live 2026-08-17, feedback QA): senza, "Opzioni (Lista) /
            // Unità (Numero)" sbordava visivamente sulla colonna "Stabilità" successiva.
            var t = new TextBlock { Text = T(chiave), FontSize = 11, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = Avalonia.Media.Brush.Parse("#64748B"), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 4, 0) };
            if (tooltip != null) Avalonia.Controls.ToolTip.SetTip(t, T(tooltip));
            Grid.SetColumn(t, colonna);
            intestazione.Children.Add(t);
        }
        Intesta(0, "NomeCampoColonna"); Intesta(1, "TipoCampoColonna"); Intesta(2, "GruppoCampoColonna", "GruppoCampoTooltip"); Intesta(3, "OpzioniListaColonna"); Intesta(4, "AmbitoCampoColonna"); Intesta(5, "ValoreDefaultCampoColonna", "ValoreDefaultCampoTooltip");
        root.Children.Add(intestazione);

        var tabella = new StackPanel { Spacing = 3 };
        root.Children.Add(tabella);

        void Ridisegna()
        {
            tabella.Children.Clear();
            // Ordine di visualizzazione = Ordine, stesso criterio ora usato anche dal form reale
            // (CampoStrumentoValoreLayout.SpaccaPerGruppo) e dall'anteprima (TestFormPreviewRenderer.DaStrumento):
            // lettura pura, mai una mutazione (revisione F2 .ai/artifacts/review_riordino.json).
            var visuale = CampoOrdinamentoService.OrdinePerVisualizzazione(working.Campi);
            foreach (var campo in visuale)
            {
                var riga = new Grid { ColumnDefinitions = new ColumnDefinitions(cols) };

                var nome = new TextBox { Text = campo.Nome, PlaceholderText = T("NomeCampoPlaceholder"), Margin = new Avalonia.Thickness(0, 0, 4, 0) };
                nome.TextChanged += (_, _) => campo.Nome = nome.Text ?? "";
                Grid.SetColumn(nome, 0);
                riga.Children.Add(nome);

                var opzioni = new Button
                {
                    Classes = { "Secondary" }, Margin = new Avalonia.Thickness(4, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left, IsVisible = campo.Tipo == TipoCampoStrumento.Lista
                };
                void AggiornaTestoOpzioni() => opzioni.Content = campo.OpzioniLista.Count > 0
                    ? $"{T("ModificaListaBtn")} ({campo.OpzioniLista.Count})" : T("ModificaListaBtn");
                AggiornaTestoOpzioni();
                async Task ModificaOpzioni()
                {
                    var risultato = await ListaOpzioniEditorWindow.Apri(w, campo.OpzioniLista);
                    if (risultato == null) return;
                    campo.OpzioniLista = risultato;
                    AggiornaTestoOpzioni();
                }
                // Errore dell'editor opzioni: registrato e segnalato all'utente, mai un Task scartato in silenzio (P4 3).
                Task ModificaOpzioniProtetta() => UiExceptionGuard.Esegui(ModificaOpzioni, "Modifica opzioni campo strumento",
                    mostra: () => ErroreOperazioneDialogo.MostraAsync(w));
                opzioni.Click += async (_, _) => await ModificaOpzioniProtetta();

                var unita = new TextBox
                {
                    Text = campo.UnitaMisura, PlaceholderText = T("UnitaMisuraPlaceholder"),
                    Margin = new Avalonia.Thickness(4, 0, 4, 0), IsVisible = campo.Tipo == TipoCampoStrumento.Numero
                };
                unita.TextChanged += (_, _) => campo.UnitaMisura = string.IsNullOrWhiteSpace(unita.Text) ? null : unita.Text.Trim();

                var tipo = new ComboBox { ItemsSource = tipiLabel, SelectedIndex = (int)campo.Tipo, Margin = new Avalonia.Thickness(0, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
                tipo.SelectionChanged += (_, _) =>
                {
                    var precedente = campo.Tipo;
                    campo.Tipo = (TipoCampoStrumento)Math.Max(0, tipo.SelectedIndex);
                    opzioni.IsVisible = campo.Tipo == TipoCampoStrumento.Lista;
                    unita.IsVisible = campo.Tipo == TipoCampoStrumento.Numero;
                    // Passando a Lista un campo senza opzioni, l'editor si apre subito (come nel catalogo di sistema).
                    if (ListaOpzioniEditorWindow.DeveProporre(precedente, campo.Tipo, campo.OpzioniLista.Count)) _ = ModificaOpzioniProtetta();
                };
                Grid.SetColumn(tipo, 1);
                riga.Children.Add(tipo);

                var gruppo = new TextBox { Text = campo.Gruppo, PlaceholderText = T("GruppoCampoPlaceholder"), Margin = new Avalonia.Thickness(4, 0, 4, 0) };
                gruppo.TextChanged += (_, _) => campo.Gruppo = string.IsNullOrWhiteSpace(gruppo.Text) ? null : gruppo.Text.Trim();
                Grid.SetColumn(gruppo, 2);
                riga.Children.Add(gruppo);

                Grid.SetColumn(opzioni, 3); Grid.SetColumn(unita, 3);
                riga.Children.Add(opzioni); riga.Children.Add(unita);

                var ambito = new ComboBox { ItemsSource = ambitiLabel, SelectedIndex = (int)campo.Ambito, Margin = new Avalonia.Thickness(4, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
                ambito.SelectionChanged += (_, _) => campo.Ambito = (TipoAmbitoCampo)Math.Max(0, ambito.SelectedIndex);
                Grid.SetColumn(ambito, 4);
                riga.Children.Add(ambito);

                // Fallback per chi non ha ancora un proprio valore personale salvato (feedback utente
                // 2026-08-24, punto 3): il default vero e proprio lo salva ogni operatore per sé dal test
                // ("Ricorda come mio default"), qui il Master imposta solo un punto di partenza ragionevole.
                var valoreDefault = new TextBox { Text = campo.ValoreDefault, PlaceholderText = T("ValoreDefaultCampoPlaceholder"), Margin = new Avalonia.Thickness(4, 0, 4, 0) };
                valoreDefault.TextChanged += (_, _) => campo.ValoreDefault = string.IsNullOrWhiteSpace(valoreDefault.Text) ? null : valoreDefault.Text.Trim();
                Grid.SetColumn(valoreDefault, 5);
                riga.Children.Add(valoreDefault);

                // Sposta su/giù (spec riordino campi 2026-09-23): stesso principio di
                // ImpostazioniViewModel.SpostaSuNavPrincipale per i campi custom di uno strumento diagnostico.
                var azioni = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                var indiceVisuale = visuale.IndexOf(campo);
                var su = new Button { Content = "▲", Classes = { "Link" }, IsEnabled = indiceVisuale > 0 };
                su.Click += (_, _) => { if (CampoOrdinamentoService.SpostaSu(working.Campi, campo)) Ridisegna(); };
                var giu = new Button { Content = "▼", Classes = { "Link" }, IsEnabled = indiceVisuale < visuale.Count - 1 };
                giu.Click += (_, _) => { if (CampoOrdinamentoService.SpostaGiu(working.Campi, campo)) Ridisegna(); };
                var rimuovi = new Button { Content = "✕", Classes = { "Link" } };
                rimuovi.Click += (_, _) => { working.Campi.Remove(campo); Ridisegna(); };
                azioni.Children.Add(su); azioni.Children.Add(giu); azioni.Children.Add(rimuovi);
                Grid.SetColumn(azioni, 6);
                riga.Children.Add(azioni);

                // AutomationId per i test FlaUI (copertura: campi strumento): per riga, individuati per indice visuale.
                var prefissoRiga = $"CampiStrumento_Riga_{indiceVisuale}";
                AutomationProperties.SetAutomationId(nome, $"{prefissoRiga}_Nome_TextBox");
                AutomationProperties.SetAutomationId(tipo, $"{prefissoRiga}_Tipo_ComboBox");
                AutomationProperties.SetAutomationId(gruppo, $"{prefissoRiga}_Gruppo_TextBox");
                AutomationProperties.SetAutomationId(opzioni, $"{prefissoRiga}_Opzioni_Button");
                AutomationProperties.SetAutomationId(unita, $"{prefissoRiga}_Unita_TextBox");
                AutomationProperties.SetAutomationId(ambito, $"{prefissoRiga}_Ambito_ComboBox");
                AutomationProperties.SetAutomationId(valoreDefault, $"{prefissoRiga}_ValoreDefault_TextBox");
                AutomationProperties.SetAutomationId(su, $"{prefissoRiga}_Su_Button");
                AutomationProperties.SetAutomationId(giu, $"{prefissoRiga}_Giu_Button");
                AutomationProperties.SetAutomationId(rimuovi, $"{prefissoRiga}_Rimuovi_Button");

                tabella.Children.Add(riga);
            }
            if (working.Campi.Count == 0)
                tabella.Children.Add(new TextBlock { Text = T("NessunCampoDefinito"), Classes = { "Muted" }, Margin = new Avalonia.Thickness(0, 4) });
        }
        Ridisegna();

        root.Children.Add(new Separator());
        var aggiungi = new Button { Content = T("AggiungiCampoBtn"), Classes = { "Secondary" }, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(aggiungi, "CampiStrumento_Aggiungi_Button");
        aggiungi.Click += (_, _) =>
        {
            CampoOrdinamentoService.InserisciInPosizione(working.Campi, new DefinizioneCampoStrumento { Nome = "", Tipo = TipoCampoStrumento.Testo });
            Ridisegna();
        };
        root.Children.Add(aggiungi);

        return w.ShowDialog(owner);
    }
}
