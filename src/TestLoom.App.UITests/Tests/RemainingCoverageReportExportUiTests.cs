using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Percorsi di conferma non ancora coperti del Report: esportazione Excel e PDF attraverso il dialog di sistema
/// "Salva con nome" (NativeFileDialogPage, file scelto dal test dentro il workspace temporaneo), annullamento del dialog
/// (nessun file, nessuna voce in cronologia) e annullamento di "Ordina colonne" (l'ordine non cambia e nulla viene scritto).
/// Il dialogo "Ordina righe" e la conferma di "Ordina colonne" sono gia' coperti da MasterGapsCoverageReportUiTests.
/// La cronologia mostra solo il nome del file: il pulsante "Apri" della cronologia avvia il file con la shell, quindi
/// questi test non lo premono mai.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Report")]
public sealed class RemainingCoverageReportExportUiTests : UiTestBase
{
    private const string Componente1 = "Componente-RX1";
    private const string Componente2 = "Componente-RX2";
    private const string Veicolo1 = "Veicolo-RX1";
    private const string Veicolo2 = "Veicolo-RX2";
    private const string Funz1 = "Funzionalita-RX1";
    private const string Funz2 = "Funzionalita-RX2";
    private const string CatBase = "Diag. base RX";
    private const string CatAvanzata = "Diagnostica avanzata RX";
    private const string NomeExcel = "TestLoom_Report_QA_Excel.xlsx";
    private const string NomePdf = "TestLoom_Report_QA_PDF.pdf";
    private const string NomeAnnullato = "TestLoom_Report_QA_Annullato.xlsx";

    public RemainingCoverageReportExportUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(RemainingCoverageReportExportUiTests), seedTestFixtures: false);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);

        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = Componente1, VeicoliAssociati = { Veicolo1 }, ModificatoDa = "UITests" },
            new() { Nome = Componente2, VeicoliAssociati = { Veicolo2 }, ModificatoDa = "UITests" },
        });
        state.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = Veicolo1, ModificatoDa = "UITests" },
            new() { Nome = Veicolo2, ModificatoDa = "UITests" },
        });
        state.Master.Funzionalita.Save(new List<TestLoom.App.Models.Funzionalita>
        {
            new() { Nome = Funz1, Categorie = { CatBase }, ModificatoDa = "UITests" },
            new() { Nome = Funz2, Categorie = { CatAvanzata }, ModificatoDa = "UITests" },
        });
        SalvaTest(state, "TC-RX-0001", Componente1, Veicolo1, Funz1, Esito.Conforme);
        SalvaTest(state, "TC-RX-0002", Componente2, Veicolo2, Funz2, Esito.NonConforme);
        return ws;
    }

    private static void SalvaTest(AppState state, string id, string componente, string veicolo, string funz, Esito esito)
    {
        var tc = new TestCase { Id = id, Componente = componente, Veicolo = veicolo, Funzionalita = { funz } };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = DateTime.Now,
            Condizioni = { new Condizione { Funzionalita = funz, Esito = esito } },
        });
        state.TestCases.Save(tc, true);
    }

    private ReportPage ApriReport(Window window) => new MainShellPage(window, App).GoToReport();

    /// <summary>Cartella di export dentro il workspace temporaneo del test (mai una cartella reale dell'utente).</summary>
    private string PercorsoExport(string nomeFile)
    {
        var cartella = Path.Combine(Workspace.RootDir, "export");
        Directory.CreateDirectory(cartella);
        return Path.Combine(cartella, nomeFile);
    }

    /// <summary>Ordine salvato nel config.json per l'operatore attivo e la dimensione indicata (null se assente).</summary>
    private IReadOnlyList<string>? OrdineSalvato(string dimensione) =>
        OrdineAssiReportService.Leggi(new AppConfigService(Workspace.ConfigPath).Load(), RuoloOperatore.Master, dimensione);

    private static List<string> VociLista(Window dialogo) =>
        ElementFinders.WaitListBoxById(dialogo, "OrdinaValori_Lista").Items.Select(i => i.Text).ToList();

    [Fact]
    public void EsportaExcel_SalvaUnXlsxNelPercorsoScelto_ELoRegistraInCronologia()
    {
        try
        {
            var pagina = ApriReport(App.WaitForMainWindow());
            var percorso = PercorsoExport(NomeExcel);

            pagina.EsportaExcel().ConfermaPercorso(percorso);

            Wait.ForCondition(() => File.Exists(percorso) && new FileInfo(percorso).Length > 0,
                "file Excel del report scritto nel percorso scelto", TimeSpan.FromSeconds(30));
            // Un .xlsx e' un contenitore ZIP: i primi due byte sono "PK".
            var bytes = File.ReadAllBytes(percorso);
            Assert.True(bytes.Length > 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K', "il file esportato e' un contenitore XLSX (ZIP)");
            Wait.ForCondition(() => File.ReadAllText(Workspace.ConfigPath).Contains(NomeExcel, StringComparison.Ordinal),
                "export Excel registrato nella cronologia del config locale");
            Wait.ForCondition(() => pagina.CronologiaMostra(NomeExcel), "la cronologia mostra il file Excel esportato");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(EsportaExcel_SalvaUnXlsxNelPercorsoScelto_ELoRegistraInCronologia), ex); }
    }

    [Fact]
    public void EsportaPdf_SalvaUnPdfNelPercorsoScelto_ELoRegistraInCronologia()
    {
        try
        {
            var pagina = ApriReport(App.WaitForMainWindow());
            var percorso = PercorsoExport(NomePdf);

            pagina.EsportaPdf().ConfermaPercorso(percorso);

            Wait.ForCondition(() => File.Exists(percorso) && new FileInfo(percorso).Length > 0,
                "file PDF del report scritto nel percorso scelto", TimeSpan.FromSeconds(30));
            var bytes = File.ReadAllBytes(percorso);
            Assert.True(bytes.Length > 4 && bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F',
                "il file esportato inizia con la firma %PDF");
            Wait.ForCondition(() => File.ReadAllText(Workspace.ConfigPath).Contains(NomePdf, StringComparison.Ordinal),
                "export PDF registrato nella cronologia del config locale");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(EsportaPdf_SalvaUnPdfNelPercorsoScelto_ELoRegistraInCronologia), ex); }
    }

    [Fact]
    public void EsportaExcel_AnnullaIlDialogoDiSalvataggio_NonCreaFileNeCronologia()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var pagina = ApriReport(window);
            var percorso = PercorsoExport(NomeAnnullato);

            var dialogo = pagina.EsportaExcel();
            dialogo.Annulla();

            Assert.False(dialogo.Aperto, "il dialog di salvataggio e' chiuso dopo Annulla");
            // Nessuna esportazione in corso: il pulsante torna abilitato e nulla viene scritto.
            Wait.ForCondition(() => ElementFinders.TryFindById(window, "Report_EsportaExcel_Button")?.IsEnabled == true,
                "pulsante Esporta Excel di nuovo abilitato dopo l'annullamento");
            Assert.False(File.Exists(percorso), "annullando il dialogo non viene creato alcun file");
            Assert.DoesNotContain(NomeAnnullato, File.ReadAllText(Workspace.ConfigPath), StringComparison.Ordinal);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(EsportaExcel_AnnullaIlDialogoDiSalvataggio_NonCreaFileNeCronologia), ex); }
    }

    [Fact]
    public void OrdinaColonne_Annulla_NonScriveNelConfigENonCambiaLOrdine()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var pagina = ApriReport(window);

            // Asse colonne della matrice = componente: il dialogo sposta componente-2 in testa, ma l'annullamento lo scarta.
            var dialogo = pagina.ApriDialogoOrdine(righe: false);
            Assert.Equal(new[] { Componente1, Componente2 }, VociLista(dialogo));
            pagina.SpostaSuNelDialogo(dialogo, Componente2);
            pagina.AnnullaDialogoOrdine(dialogo);

            Assert.Null(OrdineSalvato(DimensioniMatrice.Componente));
            Assert.Equal(new[] { Componente1, Componente2 }, pagina.OrdineColonne(Componente1, Componente2));
            var riaperto = pagina.ApriDialogoOrdine(righe: false);
            Assert.Equal(new[] { Componente1, Componente2 }, VociLista(riaperto));
            pagina.AnnullaDialogoOrdine(riaperto);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(OrdinaColonne_Annulla_NonScriveNelConfigENonCambiaLOrdine), ex); }
    }
}
