using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Copertura FlaUI della pagina Report non ancora esercitata (COPERTURA_FLAUI.md, sezione Report): esportazione
/// Excel e PDF su un file temporaneo scelto dal dialog di sistema (con verifica del file scritto e della cronologia),
/// annullamento del dialogo "Ordina colonne" senza effetti su matrice e configurazione, filtro per tipo di test e
/// cambio della dimensione dell'asse X. Il dataset è sintetico: due componente, un veicolo, una categoria e due tipi di test.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Report")]
[Trait("Category", "Regression")]
public sealed class MasterWuReportCoverageReportUiTests : UiTestBase
{
    private const string EcuA = "Componente-ORD-A";
    private const string EcuB = "Componente-ORD-B";
    private const string Veicolo = "Veicolo-ORD";
    private const string Categoria = "Diag. base";
    private const string TipoAlfa = "Tipo Alfa";
    private const string TipoBeta = "Tipo Beta";

    public MasterWuReportCoverageReportUiTests() : base(CreaFixture()) { }

    private static TestWorkspace CreaFixture()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(MasterWuReportCoverageReportUiTests), seedTestFixtures: false);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = EcuA, VeicoliAssociati = { Veicolo }, ModificatoDa = "UITests" },
            new() { Nome = EcuB, VeicoliAssociati = { Veicolo }, ModificatoDa = "UITests" },
        });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = Veicolo, ModificatoDa = "UITests" } });
        state.Master.Funzionalita.Save(new List<TestLoom.App.Models.Funzionalita>
        {
            new() { Nome = "Funz ORD", Categorie = { Categoria }, ModificatoDa = "UITests" },
        });
        var alfa = new TestTemplate { Nome = TipoAlfa, Icona = "🅰", ModificatoDa = "UITests" };
        var beta = new TestTemplate { Nome = TipoBeta, Icona = "🅱", ModificatoDa = "UITests" };
        state.Master.TestTemplate.Save(new List<TestTemplate> { alfa, beta });
        SalvaTest(state, "TC-ORD-A", EcuA, alfa.Uid);
        SalvaTest(state, "TC-ORD-B", EcuB, beta.Uid);
        return ws;
    }

    private static void SalvaTest(AppState state, string id, string componente, Guid templateUid)
    {
        var tc = new TestCase { Id = id, Componente = componente, Veicolo = Veicolo, Funzionalita = { "Funz ORD" }, TestTemplateUid = templateUid, ModificatoDa = "UITests" };
        tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.Now, Condizioni = { new Condizione { Funzionalita = "Funz ORD", Esito = Esito.Conforme } } });
        state.TestCases.Save(tc, true);
    }

    /// <summary>Cartella di destinazione dei file esportati: dentro il workspace del test, cancellata con esso.</summary>
    private string CartellaEsporti()
    {
        var dir = Path.Combine(Workspace.RootDir, "esporti");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] PrimiByte(string percorso, int quanti)
    {
        using var fs = File.OpenRead(percorso);
        var buffer = new byte[quanti];
        var letti = fs.Read(buffer, 0, quanti);
        return buffer.Take(letti).ToArray();
    }

    private ReportPage ApriMatrice()
    {
        var window = App.WaitForMainWindow();
        var report = new MainShellPage(window, App).GoToReport();
        Wait.ForCondition(() => report.CellaVisibile(Categoria, EcuA), "matrice Report popolata");
        return report;
    }

    [Fact]
    public void EsportaExcel_SalvaUnXlsxTemporaneo_ELoMostraInCronologia()
    {
        try
        {
            var report = ApriMatrice();
            var percorso = Path.Combine(CartellaEsporti(), "TestLoom_Report_QA.xlsx");

            var dialogo = report.EsportaExcel();
            dialogo.ConfermaPercorso(percorso);

            Wait.ForCondition(() => File.Exists(percorso) && new FileInfo(percorso).Length > 0, "file .xlsx scritto nella cartella temporanea");
            Assert.Equal(new byte[] { 0x50, 0x4B }, PrimiByte(percorso, 2)); // un .xlsx è un archivio ZIP ("PK")
            Wait.ForCondition(() => report.CronologiaMostra(Path.GetFileName(percorso)), "cronologia del Report mostra il file esportato");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(EsportaExcel_SalvaUnXlsxTemporaneo_ELoMostraInCronologia), ex);
        }
    }

    [Fact]
    public void EsportaPdf_SalvaUnPdfTemporaneo_ELoMostraInCronologia()
    {
        try
        {
            var report = ApriMatrice();
            var percorso = Path.Combine(CartellaEsporti(), "TestLoom_Report_QA.pdf");

            var dialogo = report.EsportaPdf();
            dialogo.ConfermaPercorso(percorso);

            Wait.ForCondition(() => File.Exists(percorso) && new FileInfo(percorso).Length > 0, "file .pdf scritto nella cartella temporanea");
            Assert.Equal("%PDF"u8.ToArray(), PrimiByte(percorso, 4));
            Wait.ForCondition(() => report.CronologiaMostra(Path.GetFileName(percorso)), "cronologia del Report mostra il PDF esportato");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(EsportaPdf_SalvaUnPdfTemporaneo_ELoMostraInCronologia), ex);
        }
    }

    [Fact]
    public void OrdinaColonne_Annulla_NonCambiaLaMatriceNeLaConfigurazione()
    {
        try
        {
            var report = ApriMatrice();
            Assert.Equal(new[] { EcuA, EcuB }, report.OrdineColonne(EcuA, EcuB));
            var configPrima = new AppConfigService(Workspace.ConfigPath).Load();
            var ordinePrimaDelloSpostamento = configPrima.OrdineAssiReport.Count;

            var dialogo = report.ApriDialogoOrdine(righe: false);
            report.SpostaSuNelDialogo(dialogo, EcuB);
            report.AnnullaDialogoOrdine(dialogo);

            Assert.Equal(new[] { EcuA, EcuB }, report.OrdineColonne(EcuA, EcuB));
            Assert.Equal(ordinePrimaDelloSpostamento, new AppConfigService(Workspace.ConfigPath).Load().OrdineAssiReport.Count);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(OrdinaColonne_Annulla_NonCambiaLaMatriceNeLaConfigurazione), ex);
        }
    }

    [Fact]
    public void FiltroTipoDiTest_RestringeLaMatrice_EMostraIlRiepilogoScelto()
    {
        try
        {
            var report = ApriMatrice();
            Assert.True(report.CellaVisibile(Categoria, EcuB), "prima del filtro la colonna componente-ORD-B ha dati");
            Assert.Equal("Tutti", report.TestoFiltroTemplate);

            var flyout = report.ApriFiltroTemplate();
            flyout.SelezionaVoce(TipoAlfa);
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);

            Wait.ForCondition(() => report.TestoFiltroTemplate == TipoAlfa, "riepilogo del filtro mostra il tipo scelto");
            Wait.ForCondition(() => !report.CellaVisibile(Categoria, EcuB), "la colonna componente-ORD-B sparisce con il filtro Tipo Alfa");
            Assert.True(report.CellaVisibile(Categoria, EcuA));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(FiltroTipoDiTest_RestringeLaMatrice_EMostraIlRiepilogoScelto), ex);
        }
    }

    [Fact]
    public void AsseY_CambioInTipoTest_SostituisceLeRigheEPersisteAllaRiapertura()
    {
        try
        {
            var report = ApriMatrice();
            Assert.True(report.CellaVisibile(Categoria, EcuA), "prima del cambio l'asse Y mostra la categoria");

            report.ImpostaAsseY("Tipo di test");

            Wait.ForCondition(() => report.CellaVisibile(TipoAlfa, EcuA), "la riga del primo tipo di test compare sull'asse Y");
            Wait.ForCondition(() => report.CellaVisibile(TipoBeta, EcuB), "la riga del secondo tipo di test compare sull'asse Y");
            Wait.ForCondition(() => !report.CellaVisibile(Categoria, EcuA), "la categoria precedente non compare più come riga");

            var selezioni = new AppConfigService(Workspace.ConfigPath).Load().UltimaSelezionePagine;
            Assert.Equal(DimensioniMatrice.Template, selezioni["Report.AsseY"]);

            App.Dispose();
            using var app2 = TestLoomApplication.Launch(Workspace);
            var reportRiaperto = new MainShellPage(app2.WaitForMainWindow(), app2).GoToReport();
            Wait.ForCondition(() => reportRiaperto.CellaVisibile(TipoAlfa, EcuA), "dopo il riavvio resta selezionato il tipo di test sull'asse Y");
            Wait.ForCondition(() => reportRiaperto.CellaVisibile(TipoBeta, EcuB), "dopo il riavvio restano entrambe le righe del tipo di test");
            Assert.False(reportRiaperto.CellaVisibile(Categoria, EcuA), "dopo il riavvio la categoria non è tornata sull'asse Y");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(AsseY_CambioInTipoTest_SostituisceLeRigheEPersisteAllaRiapertura), ex);
        }
    }
}
