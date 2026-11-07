using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;
using static TestLoom.App.Tests.LayoutHeadlessMisure;

namespace TestLoom.App.Tests;

/// <summary>Editor Test in headless (Category=LayoutHeadless), dentro una finestra di 1280x672 con la colonna della sidebar
/// (200 DIP in modo compatto), cioe' la pagina che si vede davvero sul portatile. Verifica la barra azioni che non tronca (menu
/// "Vai a" al posto dei link di sezione), il testo "modifiche non salvate" con tooltip, le card su una colonna sotto 1100 DIP,
/// il pulsante "Duplica ultima operazione" e il "Copia dalla precedente" di riga nascosto sulla prima operazione.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class LayoutEditorTestHeadlessTests
{
    [Fact]
    public void Editor1280x672_BarraAzioniNonTroncataEMenuVaiA() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = CreaFixtureEditor();
            var editor = CreaEditor(fixture.State, 1280, 672, out var vm, out var finestra);
            try
            {
                Assert.Equal(ModoLayout.Compatto, LayoutAdattivo.ModoCorrente);
                Assert.True(vm.EditorCompatto);
                Assert.True(vm.MostraMenuVaiA);
                Assert.False(vm.MostraLinkSezioni);
                var vaiA = Trova<Button>(editor, "Editor_VaiA_Button");
                Assert.True(vaiA.IsEffectivelyVisible);
                Assert.Equal(6, ((MenuFlyout)vaiA.Flyout!).Items.Count);

                // Con le modifiche non salvate il testo compare nella barra: non deve sovrapporsi ai pulsanti ne' uscire dalla finestra.
                vm.ModificheNonSalvate = true;
                LayoutHeadlessSessione.Assesta(finestra);
                var testo = editor.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.IsEffectivelyVisible && t.Text == "Modifiche non salvate");
                Assert.Equal(TextTrimming.CharacterEllipsis, testo.TextTrimming);
                Assert.NotNull(ToolTip.GetTip(testo));
                var annulla = Trova<Button>(editor, "Editor_Cancel_Button");
                Assert.True(Rettangolo(testo, finestra).Right <= Rettangolo(annulla, finestra).Left + 0.5,
                    "il testo 'modifiche non salvate' si sovrappone al pulsante Torna alla lista");

                // In modifica compare anche Salva: la barra deve restare dentro la finestra.
                if (vm.PuoModificare) LayoutHeadlessSessione.AttendiComando(vm.IniziaModificaCommand.ExecuteAsync(null), TimeSpan.FromSeconds(30));
                LayoutHeadlessSessione.Assesta(finestra);
                var salva = Trova<Button>(editor, "Editor_Save_Button");
                Assert.True(salva.IsEffectivelyVisible);
                Assert.Same(vm.SalvaSicuroCommand, salva.Command); // la barra usa il commit transazionale
                var salvaInFondo = Trova<Button>(editor, "Editor_Save_Footer_Button");
                Assert.Same(vm.SalvaSicuroCommand, salvaInFondo.Command); // anche il pulsante in fondo al form
                foreach (var controllo in new Control[] { vaiA, annulla, salva, testo })
                    AsserisciDentroFinestra(controllo, finestra, controllo.GetType().Name);
            }
            finally
            {
                finestra.Hide();
            }
        });

    [Fact]
    public void Editor1280x672_DuplicaUltimaOperazioneNellaSezioneECopiaDiRigaNascostaSullaPrima() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = CreaFixtureEditor();
            var editor = CreaEditor(fixture.State, 1280, 672, out _, out var finestra);
            try
            {
                var sezione = Trova<Button>(editor, "Editor_CopiaDallaPrecedente_Button");
                Assert.Equal("Duplica ultima operazione", sezione.Content);
                Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(sezione) as string));

                // La prima operazione non ha una precedente: il pulsante di riga e' nascosto (IsVisible = false), non disabilitato.
                var prima = Trova<Button>(editor, $"Editor_Condizione_{fixture.Prima}_CopiaDallaPrecedente_Button");
                Assert.False(prima.IsVisible);
                var seconda = Trova<Button>(editor, $"Editor_Condizione_{fixture.Seconda}_CopiaDallaPrecedente_Button");
                Assert.True(seconda.IsVisible);
                Assert.True(seconda.IsEnabled);
            }
            finally
            {
                finestra.Hide();
            }
        });

    [Fact]
    public void CampoCheckboxOperazione_ParteSpentoESupportaDefaultPersonale()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = CreaFixtureEditor();
            var strumenti = fixture.State.Master.Strumenti.Load();
            var capturetool = strumenti.SingleOrDefault(s => s.Nome == "CaptureTool");
            if (capturetool == null)
            {
                capturetool = new StrumentoDiagnostico { Nome = "CaptureTool" };
                strumenti.Add(capturetool);
            }
            capturetool.Campi =
            [
                new DefinizioneCampoStrumento
                {
                    Nome = "Enabled",
                    Tipo = TipoCampoStrumento.Checkbox,
                    Ambito = TipoAmbitoCampo.PerOperazione,
                },
            ];
            fixture.State.Master.Strumenti.Save(strumenti);

            var editor = CreaEditor(fixture.State, 1280, 672, out var vm, out var finestra);
            try
            {
                LayoutHeadlessSessione.AttendiComando(vm.IniziaModificaCommand.ExecuteAsync(null), TimeSpan.FromSeconds(30));
                vm.StrumentiOptions.Single(s => s.Valore == "CaptureTool").Selezionato = true;
                LayoutHeadlessSessione.Assesta(finestra);

                var operazione = vm.RevisioneCorrente!.Condizioni.Single(c => c.Uid == fixture.Prima);
                var campo = operazione.CampiVisual!.Single(c => c.Strumento == "CaptureTool" && c.Nome == "Enabled");
                var toggle = editor.GetVisualDescendants().OfType<ToggleSwitch>()
                    .Single(t => ReferenceEquals(t.DataContext, campo));
                Assert.False(toggle.IsChecked);
                Assert.Null(campo.ValoreCheckbox);
                Assert.DoesNotContain("CaptureTool::Enabled", operazione.ValoriStrumenti.Keys);

                // Il valore spento iniziale si può ricordare come default personale senza trasformarlo
                // in un dato della prova: il comando riceve un proxy "false" e il dizionario resta vuoto.
                var ricorda = editor.GetVisualDescendants().OfType<Button>()
                    .Single(b => AutomationProperties.GetAutomationId(b) == campo.AutomationIdRicordaDefault
                        && ReferenceEquals(b.DataContext, campo));
                Assert.True(ricorda.IsVisible);
                Assert.Same(campo.CampoPerRicordareDefault, ricorda.CommandParameter);
                Assert.NotNull(ricorda.Command);
                Assert.True(ricorda.Command!.CanExecute(ricorda.CommandParameter));
                ricorda.Command.Execute(ricorda.CommandParameter);
                Assert.Equal("false", fixture.State.OperatoreCorrente!.ValoriDefaultCampi["CaptureTool::Enabled"]);
                Assert.Null(campo.ValoreCheckbox);
                Assert.DoesNotContain("CaptureTool::Enabled", operazione.ValoriStrumenti.Keys);

                var rimuoviDefaultSpento = editor.GetVisualDescendants().OfType<Button>()
                    .Single(b => AutomationProperties.GetAutomationId(b) == campo.AutomationIdRimuoviDefault
                        && ReferenceEquals(b.DataContext, campo));
                Assert.True(rimuoviDefaultSpento.IsVisible);
                Assert.NotNull(rimuoviDefaultSpento.Command);
                Assert.True(rimuoviDefaultSpento.Command!.CanExecute(rimuoviDefaultSpento.CommandParameter));
                rimuoviDefaultSpento.Command.Execute(rimuoviDefaultSpento.CommandParameter);
                Assert.DoesNotContain("CaptureTool::Enabled", fixture.State.OperatoreCorrente!.ValoriDefaultCampi.Keys);
                Assert.Null(campo.ValoreCheckbox);
                Assert.DoesNotContain("CaptureTool::Enabled", operazione.ValoriStrumenti.Keys);
                campo.ConfermaDefaultSalvato = false;

                toggle.IsChecked = true;
                Assert.True(campo.ValoreCheckbox);
                Assert.Equal("true", operazione.ValoriStrumenti["CaptureTool::Enabled"]);

                ricorda = editor.GetVisualDescendants().OfType<Button>()
                    .Single(b => AutomationProperties.GetAutomationId(b) == campo.AutomationIdRicordaDefault
                        && ReferenceEquals(b.DataContext, campo));
                Assert.True(ricorda.IsVisible);
                Assert.NotNull(ricorda.Command);
                Assert.True(ricorda.Command!.CanExecute(ricorda.CommandParameter));
                ricorda.Command.Execute(ricorda.CommandParameter);
                Assert.True(campo.HaDefaultPersonale);
                Assert.True(campo.EDefaultPersonale);

                var rimuovi = editor.GetVisualDescendants().OfType<Button>()
                    .Single(b => AutomationProperties.GetAutomationId(b) == campo.AutomationIdRimuoviDefault
                        && ReferenceEquals(b.DataContext, campo));
                Assert.True(rimuovi.IsVisible);
                Assert.NotNull(rimuovi.Command);
                Assert.True(rimuovi.Command!.CanExecute(rimuovi.CommandParameter));
                rimuovi.Command.Execute(rimuovi.CommandParameter);
                Assert.False(campo.HaDefaultPersonale);
                Assert.Equal("true", campo.Valore);
            }
            finally
            {
                finestra.Hide();
            }
        });
    }

    [Fact]
    public void Editor1280x672_CardPianificazioneEDettagliSuUnaColonna() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = CreaFixtureEditor();
            var editor = CreaEditor(fixture.State, 1280, 672, out _, out var finestra);
            try
            {
                var pianificazione = editor.FindControl<Border>("Pianificazione")!;
                var griglia = pianificazione.GetVisualDescendants().OfType<Grid>().First(g => g.RowDefinitions.Count == 7);
                var campi = VisibiliDirettiDi(griglia);
                Assert.True(campi.Count >= 3, $"campi visibili della Pianificazione: {campi.Count}");
                AsserisciUnaColonna(griglia, campi, "Pianificazione");

                // Campi classici di Dettagli: griglia a sei righe e tre colonne dentro la card Dettagli (Veicolo, componente, Tipo test, matricola, Baseline).
                var cardDettagli = editor.FindControl<Border>("Dettagli")!;
                var grigliaClassici = cardDettagli.GetVisualDescendants().OfType<Grid>()
                    .First(g => g.RowDefinitions.Count == 6 && g.ColumnDefinitions.Count == 3);
                var classici = VisibiliDirettiDi(grigliaClassici);
                Assert.True(classici.Count >= 2, $"campi classici visibili in Dettagli: {classici.Count}");
                AsserisciUnaColonna(grigliaClassici, classici, "Dettagli (campi classici)");
            }
            finally
            {
                finestra.Hide();
            }
        });

    [Fact]
    public void Editor1920x1080_CardTreColonneELinkSezioniInBarra() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = CreaFixtureEditor();
            var editor = CreaEditor(fixture.State, 1920, 1080, out var vm, out var finestra);
            try
            {
                // Ampio: pagina di circa 1624 DIP, abbastanza per i sei link con i pulsanti della barra (menu "Vai a" sotto 1400).
                Assert.False(vm.EditorCompatto);
                Assert.False(vm.BarraCompatta);
                Assert.True(vm.MostraLinkSezioni);
                Assert.False(vm.MostraMenuVaiA);
                AsserisciDentroFinestra(Trova<Button>(editor, "Editor_Cancel_Button"), finestra, "Torna alla lista");
                var pianificazione = editor.FindControl<Border>("Pianificazione")!;
                var griglia = pianificazione.GetVisualDescendants().OfType<Grid>().First(g => g.RowDefinitions.Count == 7);
                var colonne = ColonneOccupate(griglia, VisibiliDirettiDi(griglia));
                Assert.True(colonne >= 2, $"colonne occupate in larghezza: {colonne}");
            }
            finally
            {
                finestra.Hide();
            }
        });

    /// <summary>Storico revisioni e cronologia modifiche virtualizzati (6.6.1): con 1.500 revisioni passate e 1.500 voci di
    /// cronologia la pagina crea solo le righe visibili, e le due liste contengono tutte le voci (stesso numero, stesso ordine
    /// della sorgente: storico dalla revisione piu' recente, cronologia nell'ordine del Test).</summary>
    [Fact]
    public void Editor_StoricoECronologiaVirtualizzati_CreanoSoloLeRigheVisibili() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = CreaFixtureEditor();
            var vm = new TestCaseEditorViewModel(fixture.State, IdTestLayout, () => { }, _ => { });
            vm.ModoEditorRapido = false; // Legacy virtualization checks the full historical view.
            var inizio = new DateTime(2026, 1, 1, 8, 0, 0);
            for (var i = 1; i <= 1_500; i++)
                vm.Esecuzioni.Add(new Esecuzione { Iterazione = i, Data = inizio.AddMinutes(i), Operatore = "Mario" });
            for (var i = 1; i <= 1_500; i++)
                vm.TestCase.Changelog.Add(new ChangelogEntry
                {
                    Data = inizio.AddMinutes(i), Operatore = "Mario", Campo = "Nota",
                    ValorePrecedente = "prima " + i, ValoreNuovo = "dopo " + i,
                });
            var editor = new TestCaseEditorView { DataContext = vm };
            var finestra = MostraInSidebar(editor, 1280, 672);
            try
            {
                LayoutHeadlessSessione.Assesta(finestra);
                var storico = Trova<ListBox>(editor, "Test_Storico_Revisioni_Lista");
                var cronologia = Trova<ListBox>(editor, "Test_Cronologia_Modifiche_Lista");
                // Tutte le revisioni passate (l'ultima e' nella scheda corrente) e tutte le voci di cronologia del Test.
                Assert.Equal(vm.Esecuzioni.Count - 1, storico.ItemCount);
                Assert.Equal(vm.TestCase.Changelog.Count, cronologia.ItemCount);
                Assert.True(vm.TestCase.Changelog.Count >= 1_500, $"voci di cronologia: {vm.TestCase.Changelog.Count}");
                var creati = storico.GetVisualDescendants().OfType<ListBoxItem>().Count()
                    + cronologia.GetVisualDescendants().OfType<ListBoxItem>().Count();
                Assert.True(creati > 0 && creati < 200, $"righe create per 3.000 voci: {creati}");
            }
            finally
            {
                finestra.Hide();
            }
        });

    [Fact]
    public void EditorQuick_FileDropAndOnDemandSectionsKeepFullEditorAvailable() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = CreaFixtureEditor();
            var vm = new TestCaseEditorViewModel(fixture.State, IdTestLayout, () => { }, _ => { });
            var editor = new TestCaseEditorView { DataContext = vm };
            var finestra = MostraInSidebar(editor, 1280, 672);
            try
            {
                Assert.False(vm.ModoEditorRapido); // complete version is the default
                vm.ModoEditorRapido = true;
                LayoutHeadlessSessione.Assesta(finestra);
                var quick = Trova<Border>(editor, "Editor_Quick_Workspace");
                Assert.True(quick.IsEffectivelyVisible);
                Assert.True(DragDrop.GetAllowDrop(quick));
                Assert.True(Trova<Button>(editor, "Editor_Quick_PasteScreenshot_Button").IsEffectivelyVisible);
                Assert.False(vm.MostraSchedaAllegati);
                Assert.False(vm.MostraCronologiaEditor);
                vm.MostraSezioneRapida("Allegati");
                LayoutHeadlessSessione.Assesta(finestra);
                var allegati = editor.FindControl<Border>("Allegati")!;
                Assert.True(allegati.IsEffectivelyVisible);
                Assert.True(DragDrop.GetAllowDrop(allegati));
                vm.ModoEditorRapido = false;
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.False(quick.IsVisible);
                Assert.True(vm.MostraCronologiaEditor);
                Assert.True(vm.EspandiParametriOperazione);
            }
            finally { finestra.Hide(); }
        });

    /// <summary>Editor Test nella finestra di prova con la colonna della sidebar.</summary>
    private static TestCaseEditorView CreaEditor(AppState state, int larghezza, int altezza, out TestCaseEditorViewModel vm, out Window finestra)
    {
        vm = new TestCaseEditorViewModel(state, IdTestLayout, () => { }, _ => { });
        vm.ModoEditorRapido = false;
        var editor = new TestCaseEditorView { DataContext = vm };
        finestra = MostraInSidebar(editor, larghezza, altezza);
        return editor;
    }
}
