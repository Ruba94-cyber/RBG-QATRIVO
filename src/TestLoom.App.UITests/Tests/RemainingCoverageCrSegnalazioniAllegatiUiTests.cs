using System.IO.Compression;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Allegati e pacchetto di Change Request e Segnalazione rete, non ancora coperti. Allegato aggiunto con il dialog di
/// sistema "Apri" (NativeFileDialogPage), allegato rimosso (il file esce dalla cartella dell'elemento e va nel
/// recupero), pacchetto esportato: dialog di selezione sezioni (PolarionSelezioneDialog), poi il selettore di cartella
/// di sistema (NativeFileDialogPage con il percorso della cartella), infine uno ZIP con il PDF dell'elemento.
/// Le CR e le segnalazioni sono salvate prima del test: il caricamento di un allegato richiede un elemento gia' salvato.
/// L'apertura di una CR passa dalla scheda Change Request della pagina Anomalie (stesso percorso di
/// AnomalieCrCoverageChangeRequestUiTests); la segnalazione rete si apre dalla riga NC con "Modifica segnalazione".
/// Il selettore di cartella e' un IFileOpenDialog con FOS_PICKFOLDERS: il campo percorso e il pulsante di conferma sono
/// gli stessi del dialog "Apri" (Id 1148 e 1), ma questo percorso non e' ancora stato osservato dal vivo.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class RemainingCoverageCrSegnalazioniAllegatiUiTests : UiTestBase
{
    private const string TemplateCr = "CR Template AL";
    private const string CrId = "CR-AL-0001";
    private static readonly Guid CrUid = new("5a1c0001-0000-4000-8000-00000000c001");
    private const string SegId = "NC-AL-0001";
    private static readonly Guid SegUid = new("5a1c0002-0000-4000-8000-00000000d001");
    private const string AllegatoSeminato = "allegato_seminato_qa.txt";
    private const string AllegatoNuovo = "allegato_nuovo_qa.txt";
    private const string ContenutoAllegato = "Allegato sintetico QA\n";

    public RemainingCoverageCrSegnalazioniAllegatiUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(RemainingCoverageCrSegnalazioniAllegatiUiTests));
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var paths = new DataPaths(ws.WorkspaceDir);

        var templates = state.Master.TestTemplate.Load();
        var templateCr = new TestTemplate { Nome = TemplateCr, IsChangeRequest = true, ModificatoDa = "UITests" };
        templates.Add(templateCr);
        state.Master.TestTemplate.Save(templates);

        var cr = new ChangeRequest
        {
            Uid = CrUid,
            Id = CrId,
            Titolo = "CR con allegati QA",
            Descrizione = "Descrizione della CR con allegati QA",
            TemplateUid = templateCr.Uid,
            EcuImpattate = new() { TestWorkspace.DefaultEcuName },
            Responsabile = "",
            Stato = StatoChangeRequest.Aperta,
        };
        cr.Allegati.Add(NuovoAllegato());
        state.ChangeRequests.Save(cr);
        var cartellaCr = paths.AllegatiDirChangeRequest(cr);
        Directory.CreateDirectory(cartellaCr);
        File.WriteAllText(Path.Combine(cartellaCr, AllegatoSeminato), ContenutoAllegato);

        var seg = new NonConformita
        {
            Uid = SegUid,
            Id = SegId,
            DaSegnalazioneRete = true,
            TestCaseId = "",
            Descrizione = "Segnalazione rete con allegati QA",
            // Dati minimi che la validazione del Salva richiede (ticket/pratica, matricola, licenza, sintesi, ambito componente).
            DescrizioneBreveSegnalata = "Sintesi segnalazione con allegati QA",
            NumeriPraticaRete = new() { "PRATICA-AL-0001" },
            VinCliente = new() { "ZFA0000000000AL01" },
            Licenze = new() { "LIC-AL-0001" },
            EcuSegnalate = new EcuSelection(new[] { TestWorkspace.DefaultEcuName }),
            Owner = TestWorkspace.DefaultOperatorName,
            StatoWorkflow = StatoAnomalia.InVerifica,
            Severita = SeveritaAnomalia.Medium,
            Priorita = PrioritaOperativa.Normal,
            DataApertura = DateTime.Now,
        };
        seg.Allegati.Add(NuovoAllegato());
        state.NonConformita.Save(seg);
        var cartellaSeg = paths.AllegatiDirNonConformita(seg);
        Directory.CreateDirectory(cartellaSeg);
        File.WriteAllText(Path.Combine(cartellaSeg, AllegatoSeminato), ContenutoAllegato);

        return ws;
    }

    private static Allegato NuovoAllegato() => new()
    {
        NomeFile = AllegatoSeminato,
        CaricatoDa = TestWorkspace.DefaultOperatorName,
        DataCaricamento = DateTime.Now,
        DimensioneByte = ContenutoAllegato.Length,
    };

    private string CartellaAllegatiCr() => new DataPaths(Workspace.WorkspaceDir).AllegatiDirChangeRequest(new ChangeRequest { Uid = CrUid });

    private string CartellaAllegatiSeg() => new DataPaths(Workspace.WorkspaceDir).AllegatiDirNonConformita(new NonConformita { Uid = SegUid });

    private string CartellaPacchetto(string nome)
    {
        var cartella = Path.Combine(Workspace.RootDir, nome);
        Directory.CreateDirectory(cartella);
        return cartella;
    }

    /// <summary>Apre l'editor della CR in modifica: "Apri" nella riga mostra l'editor in sola lettura, poi "Modifica"
    /// (stesso percorso di ChangeRequestEditorFlow.ApriInModifica, usato dalle altre suite CR).</summary>
    private ChangeRequestEditorPage ApriCr() =>
        ChangeRequestEditorFlow.ApriInModifica(App.WaitForMainWindow(), CrId, CrId);

    /// <summary>Riga dell'elenco Anomalie che contiene la segnalazione rete indicata (testo Id della riga NC).</summary>
    private static AutomationElement? RigaSegnalazione(Window window)
    {
        var testo = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).FirstOrDefault(t => t.Name == SegId);
        for (var corrente = testo?.Parent; corrente != null; corrente = corrente.Parent)
        {
            if (corrente.FindFirstDescendant(cf => cf.ByAutomationId("Anomalie_Riga_ModificaRapida_Button")) != null)
                return corrente;
        }
        return null;
    }

    /// <summary>Apre l'editor della segnalazione rete dal flusso di Modifica rapida NC.</summary>
    private SegnalazioneReteEditorPage ApriSegnalazione()
    {
        var window = App.WaitForMainWindow();
        var anomalie = new MainShellPage(window).GoToAnomalie();
        anomalie.Cerca(SegId);
        Wait.ForCondition(() => anomalie.IdRighe().Contains(SegId), $"la segnalazione {SegId} compare nell'elenco Anomalie");
        RigaSegnalazione(window)?.FindFirstDescendant(cf => cf.ByAutomationId("Anomalie_Riga_ModificaRapida_Button"))?.AsButton().SafeInvoke();
        var modificaRapida = App.WaitForWindowByContent("Anomalie_NcModificaRapida_ModificaSegnalazione_Button");
        ElementFinders.WaitButtonById(modificaRapida, "Anomalie_NcModificaRapida_ModificaSegnalazione_Button").SafeInvoke();
        Wait.ForCondition(() => ElementFinders.TryFindById(window, "Segnalazione_Title_Text") != null,
            $"l'editor della segnalazione {SegId} e' aperto");
        return new SegnalazioneReteEditorPage(window);
    }

    /// <summary>Nomi degli allegati visibili nella scheda: un pulsante per riga con il nome del file.</summary>
    private static bool AllegatoVisibile(Window window, string nome, string automationId) =>
        window.FindAllDescendants(cf => cf.ByAutomationId(automationId)).Any(b => b.Name == nome);

    /// <summary>Uscita con modifiche non salvate: se compare la conferma di abbandono ("Procedi senza salvare",
    /// Confirm_Yes_Button) la conferma e vero; altrimenti falso.</summary>
    private bool ConfermaAbbandonoSeCompare()
    {
        if (!Wait.TryForCondition(() => !App.IsWindowClosedByContent("Confirm_Yes_Button"), TimeSpan.FromSeconds(10)))
            return false;
        new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
        Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura della conferma di abbandono");
        return true;
    }

    [Fact]
    public void CrAllegatoAggiunto_DalDialogoDiSistema_CompareNellaSchedaERestaNelWorkspace()
    {
        try
        {
            ApriCr();
            var window = App.WaitForMainWindow();

            ElementFinders.WaitButtonById(window, "Cr_ScegliAllegato_Button").SafeInvoke();
            var sorgente = TestCoverageSeed.CreaFileDiProva(Workspace, AllegatoNuovo, ContenutoAllegato);
            NativeFileDialogPage.Attendi(App).ConfermaPercorso(sorgente);

            Wait.ForCondition(() => AllegatoVisibile(window, AllegatoNuovo, "Cr_Allegato_Nome_Button"),
                "l'allegato scelto compare nella scheda Allegati della CR");
            Wait.ForCondition(() => File.Exists(Path.Combine(CartellaAllegatiCr(), AllegatoNuovo)),
                "l'allegato e' copiato nella cartella degli allegati della CR");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CrAllegatoAggiunto_DalDialogoDiSistema_CompareNellaSchedaERestaNelWorkspace), ex); }
    }

    [Fact]
    public void CrAllegatoRimosso_SpariscDallaSchedaEIlFileEsceDallaCartellaDellaCr()
    {
        try
        {
            var editor = ApriCr();
            var window = App.WaitForMainWindow();
            Wait.ForCondition(() => AllegatoVisibile(window, AllegatoSeminato, "Cr_Allegato_Nome_Button"),
                "l'allegato seminato compare nella scheda Allegati");

            window.FindAllDescendants(cf => cf.ByAutomationId("Cr_Allegato_Rimuovi_Button")).First().AsButton().SafeInvoke();
            Wait.ForCondition(() => !AllegatoVisibile(window, AllegatoSeminato, "Cr_Allegato_Nome_Button"),
                "l'allegato rimosso esce dalla scheda");
            // Rimozione differita (AttachmentRemovalQueue): prima del Salva il file resta nella cartella della CR.
            Assert.True(File.Exists(Path.Combine(CartellaAllegatiCr(), AllegatoSeminato)),
                "prima del Salva il file dell'allegato resta nella cartella della CR (rimozione differita)");

            editor.Salva();
            Wait.ForCondition(() => !File.Exists(Path.Combine(CartellaAllegatiCr(), AllegatoSeminato)),
                "dopo il Salva il file esce dalla cartella della CR", TimeSpan.FromSeconds(30));
            // Il file e' nella sottocartella di recupero dell'allegato (nome legato all'Uid), non piu' al percorso attivo.
            Assert.Single(Directory.GetFiles(CartellaAllegatiCr(), "*.txt", SearchOption.AllDirectories));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CrAllegatoRimosso_SpariscDallaSchedaEIlFileEsceDallaCartellaDellaCr), ex); }
    }

    [Fact]
    public void CrAllegatoRimosso_UscitaSenzaSalvare_IlFileRestaNellaCartellaEIlAllegatoRestaNellaCr()
    {
        try
        {
            ApriCr();
            var window = App.WaitForMainWindow();
            Wait.ForCondition(() => AllegatoVisibile(window, AllegatoSeminato, "Cr_Allegato_Nome_Button"),
                "l'allegato seminato compare nella scheda Allegati");

            window.FindAllDescendants(cf => cf.ByAutomationId("Cr_Allegato_Rimuovi_Button")).First().AsButton().SafeInvoke();
            Wait.ForCondition(() => !AllegatoVisibile(window, AllegatoSeminato, "Cr_Allegato_Nome_Button"),
                "l'allegato rimosso esce dalla scheda");

            ElementFinders.WaitButtonById(window, "Cr_TornaAllaLista_Button").SafeInvoke();
            Assert.True(ConfermaAbbandonoSeCompare(), "con modifiche non salvate l'uscita dalla CR chiede conferma");
            Wait.ForCondition(() => ElementFinders.ExistsById(App.WaitForMainWindow(), "Anomalie_Title_Text"),
                "uscita dalla CR: torna alla pagina Anomalie");

            Assert.True(File.Exists(Path.Combine(CartellaAllegatiCr(), AllegatoSeminato)),
                "dopo l'abbandono il file resta nella cartella della CR");
            // La CR salvata continua a elencare l'allegato: riaprendola compare ancora.
            ApriCr();
            Wait.ForCondition(() => AllegatoVisibile(App.WaitForMainWindow(), AllegatoSeminato, "Cr_Allegato_Nome_Button"),
                "dopo l'abbandono l'allegato e' ancora nella CR salvata");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CrAllegatoRimosso_UscitaSenzaSalvare_IlFileRestaNellaCartellaEIlAllegatoRestaNellaCr), ex); }
    }

    [Fact]
    public void CrEsportaPacchetto_SceltaSezioniECartella_CreaLoZipConIlPdfDellaCr()
    {
        try
        {
            ApriCr();
            var window = App.WaitForMainWindow();

            ElementFinders.WaitButtonById(window, "Cr_EsportaPacchetto_Button").SafeInvoke();
            var dialogoSezioni = App.WaitForWindowByContent("PolarionSelezione_Genera_Button");
            // Sezioni preselezionate: tutte. "Genera" apre il selettore della cartella di destinazione.
            ElementFinders.WaitButtonById(dialogoSezioni, "PolarionSelezione_Genera_Button").SafeInvoke();

            var cartella = CartellaPacchetto("pacchetto-cr");
            NativeFileDialogPage.Attendi(App).ConfermaPercorso(cartella);

            Wait.ForCondition(() => Directory.GetFiles(cartella, "*.zip").Length == 1,
                "pacchetto CR scritto nella cartella scelta", TimeSpan.FromSeconds(30));
            using var zip = ZipFile.OpenRead(Directory.GetFiles(cartella, "*.zip")[0]);
            Assert.Contains(zip.Entries, e => e.FullName == $"TestLoom_{CrId}.pdf");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CrEsportaPacchetto_SceltaSezioniECartella_CreaLoZipConIlPdfDellaCr), ex); }
    }

    [Fact]
    public void SegnalazioneAllegatoAggiunto_DalDialogoDiSistema_CompareNellaSchedaERestaNelWorkspace()
    {
        try
        {
            ApriSegnalazione();
            var window = App.WaitForMainWindow();

            ElementFinders.WaitButtonById(window, "Segnalazione_ScegliAllegato_Button").SafeInvoke();
            var sorgente = TestCoverageSeed.CreaFileDiProva(Workspace, AllegatoNuovo, ContenutoAllegato);
            NativeFileDialogPage.Attendi(App).ConfermaPercorso(sorgente);

            Wait.ForCondition(() => AllegatoVisibile(window, AllegatoNuovo, "Segnalazione_Allegato_Nome_Button"),
                "l'allegato scelto compare nella scheda Allegati della segnalazione");
            Wait.ForCondition(() => File.Exists(Path.Combine(CartellaAllegatiSeg(), AllegatoNuovo)),
                "l'allegato e' copiato nella cartella degli allegati della segnalazione");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(SegnalazioneAllegatoAggiunto_DalDialogoDiSistema_CompareNellaSchedaERestaNelWorkspace), ex); }
    }

    [Fact]
    public void SegnalazioneAllegatoRimosso_SpariscDallaSchedaEIlFileEsceDallaCartella()
    {
        try
        {
            var editor = ApriSegnalazione();
            var window = App.WaitForMainWindow();
            Wait.ForCondition(() => AllegatoVisibile(window, AllegatoSeminato, "Segnalazione_Allegato_Nome_Button"),
                "l'allegato seminato compare nella scheda Allegati della segnalazione");

            window.FindAllDescendants(cf => cf.ByAutomationId("Segnalazione_Allegato_Rimuovi_Button")).First().AsButton().SafeInvoke();
            Wait.ForCondition(() => !AllegatoVisibile(window, AllegatoSeminato, "Segnalazione_Allegato_Nome_Button"),
                "l'allegato rimosso esce dalla scheda della segnalazione");
            // Rimozione differita (AttachmentRemovalQueue): prima del Salva il file resta nella cartella della segnalazione.
            Assert.True(File.Exists(Path.Combine(CartellaAllegatiSeg(), AllegatoSeminato)),
                "prima del Salva il file dell'allegato resta nella cartella della segnalazione (rimozione differita)");

            editor.Salva();
            Wait.ForCondition(() => !File.Exists(Path.Combine(CartellaAllegatiSeg(), AllegatoSeminato)),
                "dopo il Salva il file esce dalla cartella della segnalazione", TimeSpan.FromSeconds(30));
            Assert.Single(Directory.GetFiles(CartellaAllegatiSeg(), "*.txt", SearchOption.AllDirectories));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(SegnalazioneAllegatoRimosso_SpariscDallaSchedaEIlFileEsceDallaCartella), ex); }
    }

    [Fact]
    public void SegnalazioneAllegatoRimosso_UscitaSenzaSalvare_IlFileRestaNellaCartella()
    {
        try
        {
            ApriSegnalazione();
            var window = App.WaitForMainWindow();
            Wait.ForCondition(() => AllegatoVisibile(window, AllegatoSeminato, "Segnalazione_Allegato_Nome_Button"),
                "l'allegato seminato compare nella scheda Allegati della segnalazione");

            window.FindAllDescendants(cf => cf.ByAutomationId("Segnalazione_Allegato_Rimuovi_Button")).First().AsButton().SafeInvoke();
            Wait.ForCondition(() => !AllegatoVisibile(window, AllegatoSeminato, "Segnalazione_Allegato_Nome_Button"),
                "l'allegato rimosso esce dalla scheda della segnalazione");

            // Annulla con modifiche non salvate chiede conferma ("Procedi senza salvare"): la rimozione non e' persistita,
            // quindi dopo l'abbandono il file non viene toccato.
            ElementFinders.WaitButtonById(window, "Segnalazione_Annulla_Button").SafeInvoke();
            Assert.True(ConfermaAbbandonoSeCompare(), "con modifiche non salvate Annulla chiede conferma");
            Wait.ForCondition(() => ElementFinders.ExistsById(App.WaitForMainWindow(), "Anomalie_Title_Text"),
                "Annulla torna alla lista delle anomalie");
            Assert.True(File.Exists(Path.Combine(CartellaAllegatiSeg(), AllegatoSeminato)),
                "dopo l'abbandono il file resta nella cartella della segnalazione");

            // La segnalazione salvata continua a elencare l'allegato: riaprendola compare ancora.
            ApriSegnalazione();
            Wait.ForCondition(() => AllegatoVisibile(App.WaitForMainWindow(), AllegatoSeminato, "Segnalazione_Allegato_Nome_Button"),
                "dopo l'abbandono l'allegato e' ancora nella segnalazione salvata");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(SegnalazioneAllegatoRimosso_UscitaSenzaSalvare_IlFileRestaNellaCartella), ex); }
    }
}
