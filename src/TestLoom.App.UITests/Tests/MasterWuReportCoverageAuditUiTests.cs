using System.IO.Compression;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Copertura FlaUI della pagina Audit non ancora esercitata (COPERTURA_FLAUI.md, sezione Audit): esportazione della
/// matrice in Excel su un file temporaneo scelto dal dialog di sistema, e filtro per tipo di requisito (Standard, Extra)
/// con azzeramento. Dataset sintetico: un componente associata a due veicoli, un requisito e un Test con una valutazione
/// derivata (origine Diretto), mostrata nella sezione Standard della matrice.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Audit")]
[Trait("Category", "Regression")]
public sealed class MasterWuReportCoverageAuditUiTests : UiTestBase
{
    private const string Componente = "Componente-AUD-QA";
    private const string RequisitoId = "R-AUD-QA";
    private const string VeicoloOrigine = "Veicolo-AUD-ORIG";
    private const string VeicoloDestinazione = "Veicolo-AUD-DEST";

    public MasterWuReportCoverageAuditUiTests() : base(CreaFixture()) { }

    private static TestWorkspace CreaFixture()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(MasterWuReportCoverageAuditUiTests), seedTestFixtures: false);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        state.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = VeicoloOrigine, ModificatoDa = "UITests" },
            new() { Nome = VeicoloDestinazione, ModificatoDa = "UITests" },
        });
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = Componente, VeicoliAssociati = { VeicoloOrigine, VeicoloDestinazione }, ModificatoDa = "UITests" },
        });
        state.Master.Requisiti.Save(new List<Requisito>
        {
            new() { Id = RequisitoId, Testo = "Requisito QA per l'audit", ApplicabileBanco = true, ModificatoDa = "UITests" },
        });
        var tc = new TestCase { Id = "TC-AUD-QA", Componente = Componente, RequisitoId = { RequisitoId }, Veicolo = VeicoloOrigine };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V-AUD-QA",
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = RequisitoId, Valore = PunteggioConformita.Conforme } },
        });
        state.TestCases.Save(tc, true);
        return ws;
    }

    private static byte[] PrimiByte(string percorso, int quanti)
    {
        using var fs = File.OpenRead(percorso);
        var buffer = new byte[quanti];
        var letti = fs.Read(buffer, 0, quanti);
        return buffer.Take(letti).ToArray();
    }

    /// <summary>True quando il file è uno ZIP completo (il .xlsx è scritto per intero: il central directory sta in fondo).</summary>
    private static bool ArchivioZipCompleto(string percorso)
    {
        try
        {
            using var archivio = ZipFile.OpenRead(percorso);
            return archivio.Entries.Count > 0;
        }
        catch (IOException) { return false; }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>Testo di tutte le parti del .xlsx (ZIP): i valori di testo (sharedStrings, fogli) vi compaiono in chiaro.</summary>
    private static string TestoDelloXlsx(string percorso)
    {
        using var archivio = ZipFile.OpenRead(percorso);
        var testo = new StringBuilder();
        foreach (var voce in archivio.Entries)
        {
            using var lettore = new StreamReader(voce.Open());
            testo.Append(lettore.ReadToEnd());
        }
        return testo.ToString();
    }

    private AuditPage ApriAuditSulVeicoloOrigine()
    {
        var window = App.WaitForMainWindow();
        var audit = new MainShellPage(window, App).GoToAudit();
        audit.SelezionaVeicolo(VeicoloOrigine);
        // Il fixture produce una valutazione derivata da un Test (origine Diretto): la matrice la mostra come cella di
        // testo senza AutomationId, perché solo le valutazioni Manuale/Propagata sono celle cliccabili con l'id
        // Audit_ValutazioneSalvata_…. L'ancora stabile dei test di filtro ed export è il titolo "Standard · 1".
        Wait.ForCondition(() => audit.TitoloSezioneStandard == "Standard · 1", "matrice Audit: sezione Standard con il requisito del fixture");
        return audit;
    }

    [Fact]
    public void EsportaMatriceExcel_SalvaUnXlsxTemporaneo()
    {
        try
        {
            var audit = ApriAuditSulVeicoloOrigine();
            var cartella = Path.Combine(Workspace.RootDir, "esporti");
            Directory.CreateDirectory(cartella);
            var percorso = Path.Combine(cartella, "TestLoom_Audit_QA.xlsx");

            var dialogo = audit.EsportaMatriceExcel();
            dialogo.ConfermaPercorso(percorso);

            Wait.ForCondition(() => File.Exists(percorso) && ArchivioZipCompleto(percorso), "file .xlsx della matrice Audit scritto per intero");
            Assert.Equal(new byte[] { 0x50, 0x4B }, PrimiByte(percorso, 2)); // un .xlsx è un archivio ZIP ("PK")
            // Il contenuto esportato è la matrice dei dati seminati: il componente e il requisito del fixture vi compaiono.
            var testo = TestoDelloXlsx(percorso);
            Assert.Contains(Componente, testo);
            Assert.Contains(RequisitoId, testo);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(EsportaMatriceExcel_SalvaUnXlsxTemporaneo), ex);
        }
    }

    [Fact]
    public void FiltroTipoRequisito_Extra_NascondeLaSezioneStandard_EAzzeraLaRipristina()
    {
        try
        {
            var audit = ApriAuditSulVeicoloOrigine();
            Assert.Equal("Tutti", audit.TestoFiltroTipoRequisito);

            var flyout = audit.ApriFiltroTipoRequisito();
            flyout.SelezionaVoce("Extra");
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);

            Wait.ForCondition(() => audit.TestoFiltroTipoRequisito == "Extra", "riepilogo del filtro mostra Extra");
            Wait.ForCondition(() => audit.TitoloSezioneStandard == null,
                "con il filtro Extra la sezione Standard non è più mostrata");

            audit.AzzeraFiltroTipoRequisito();
            Wait.ForCondition(() => audit.TestoFiltroTipoRequisito == "Tutti", "azzeramento: riepilogo torna a Tutti");
            Wait.ForCondition(() => audit.TitoloSezioneStandard == "Standard · 1",
                "dopo l'azzeramento la sezione Standard torna visibile con il requisito");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(FiltroTipoRequisito_Extra_NascondeLaSezioneStandard_EAzzeraLaRipristina), ex);
        }
    }
}
