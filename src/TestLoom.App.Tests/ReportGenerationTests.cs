using TestLoom.App.Models;
using TestLoom.App.Services;
using ClosedXML.Excel;

namespace TestLoom.App.Tests;

/// <summary>ReportService/PdfReportService non avevano nessuna copertura di test nonostante generino Excel/PDF
/// da dati con molti campi opzionali/nullable (baseline, campi specifici per tipo test, escalation, ecc.):
/// il rischio concreto è un'eccezione non gestita durante l'export su dati reali con qualche campo mancante,
/// non sui dati "puliti" che si scriverebbero a mano in un test. Il dataset di semina (SeedDemoDataService)
/// include già apposta casi limite (test esplorativo senza classificazione, componente archiviata, escalation
/// banco->veicolo, UnitLogger collegato, file corrotto) — riusarlo qui esercita l'export sugli stessi casi limite
/// invece di un dataset sintetico troppo pulito per essere rappresentativo.</summary>
[Trait("Category", "Lento")]
public class ReportGenerationTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public ReportGenerationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoomReportTests_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(_dir);
        _state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        _state.InizializzaCartella(_dir); // non tocca la config reale della macchina: solo lettura+repo locali
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void LegacyExportPreservesPreviouslyConfiguredTemplateSheets()
    {
        var templates = _state.Master.TestTemplate.Load();
        var template = templates.First(t => t.FogliReport.Count > 0);
        var sheet = template.FogliReport[0];
        sheet.NomeFoglio = "Synthetic custom sheet";
        sheet.Colonne[0].Intestazione = "Synthetic custom column";
        _state.Master.TestTemplate.Save(templates);

        var saved = _state.Master.TestTemplate.Load().Single(t => t.Uid == template.Uid);
        Assert.Contains(saved.FogliReport, f => f.NomeFoglio == "Synthetic custom sheet" &&
            f.Colonne.Any(c => c.Intestazione == "Synthetic custom column"));
        using var workbook = new XLWorkbook(new MemoryStream(
            ReportService.GeneraExcel(new ReportFilter(), _state, "Synthetic operator")));
        var exported = workbook.Worksheet("Synthetic custom sheet");
        Assert.Contains(exported.Row(3).CellsUsed(), c => c.GetString() == "Synthetic custom column");
    }

    [Fact]
    public void GeneraExcel_SulDatasetCompleto_NonLanciaEProduceUnFileNonVuoto()
    {
        var bytes = ReportService.GeneraExcel(new ReportFilter(), _state, "Operatore Prova A");
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void GeneraExcelVista_EsportaSoloMatriceGraficiOrigineENcCollegate()
    {
        var bytes = ReportService.GeneraExcelVista(new ReportFilter(), _state, "Operatore Prova A");
        using var workbook = new XLWorkbook(new MemoryStream(bytes));

        // Il dettaglio NC rimane nel foglio principale, per mantenere il report semplice da consultare.
        Assert.Contains(workbook.Worksheets, w => w.Name == "Grafici");
        Assert.Contains(workbook.Worksheets, w => w.Name == "Matrice configurabile");
        Assert.Contains(workbook.Worksheets, w => w.Name == "NC matrice");
        Assert.DoesNotContain(workbook.Worksheets, w => w.Name == "NC operazioni");
        Assert.DoesNotContain(workbook.Worksheets, w => w.Name == "NC prove");
        Assert.DoesNotContain(workbook.Worksheets, w => w.Name == "Anomalie legacy");
        Assert.DoesNotContain(workbook.Worksheets, w => w.Name == "Audit Conformità");
    }

    [Fact]
    public void GeneraExcelVista_FiltroDimensioneMatrice_RifletteSuOrigineEGrafici()
    {
        var tutti = _state.TestCases.GetAll().Where(t => !t.Eliminato).ToList();
        var componente = ReportMatriceService.ValoriDisponibili(DimensioniMatrice.Componente, tutti,
            _state.Master.Veicoli.Load(), _state.Master.Funzionalita.Load(), _state.Master.TestTemplate.Load()).First();
        var config = new ConfigurazioneMatriceReport();
        config.Filtri[DimensioniMatrice.Componente] = [componente];

        using var workbook = new XLWorkbook(new MemoryStream(
            ReportService.GeneraExcelVista(new ReportFilter(), _state, "Operatore Prova A", config)));
        var origine = workbook.Worksheet(LocalizationService.Instance.Traduci("OrigineFoglioOrigine"));
        var colEcu = origine.Row(5).CellsUsed().Single(c => c.GetString() == LocalizationService.Instance.Traduci("OrigineColEcu")).Address.ColumnNumber;
        Assert.All(origine.Column(colEcu).CellsUsed().Where(c => c.Address.RowNumber > 5), c => Assert.Equal(componente, c.GetString()));
        Assert.Contains(componente, workbook.Worksheet("Grafici").Cell(2, 1).GetString());
    }

    [Fact]
    public void GeneraExcel_TestVeicolo_RiportaICampiDellaBreakoutBox()
    {
        var bytes = ReportService.GeneraExcel(new ReportFilter(), _state, "Operatore Prova A");
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var foglio = workbook.Worksheet("Test a Veicolo");

        // Colonne configurabili dal template (Addendum redesign 2026-08): si cerca l'intestazione invece di
        // assumere un indice fisso, coerente col fatto che un Master può riordinarle/rinominarle.
        var azioneCol = foglio.Row(3).CellsUsed().First(c => c.GetString() == "Azione da fare a veicolo").Address.ColumnNumber;
        var pinCol = foglio.Row(3).CellsUsed().First(c => c.GetString() == "Pin").Address.ColumnNumber;

        Assert.Contains(foglio.Column(azioneCol).CellsUsed().Skip(1), c => c.GetString().Contains("Azione fittizia su veicolo"));
        Assert.Contains(foglio.Column(pinCol).CellsUsed().Skip(1), c => c.GetString().StartsWith("PIN "));
    }

    [Fact]
    public void GeneraPdf_SulDatasetCompleto_NonLanciaEProduceUnFileNonVuoto()
    {
        var bytes = PdfReportService.GeneraPdf(new ReportFilter(), _state, "Operatore Prova A");
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void GeneraPdf_NcConDescrizioneLungaProduceTutteLePagine()
    {
        var tc = new TestLoom.App.Models.TestCase
        {
            Id = "T-PDF-LONG", Uid = Guid.NewGuid(), Componente = new TestLoom.App.Models.EcuSelection(["BCM"]), Veicoli = ["Daily A"],
            Funzionalita = ["Lettura guasto"], Origine = TestLoom.App.Models.OrigineTest.TestLoom
        };
        tc.Esecuzioni.Add(new TestLoom.App.Models.Esecuzione
        {
            Data = new DateTime(2026, 1, 1),
            Condizioni = [new TestLoom.App.Models.Condizione
            {
                Funzionalita = "Lettura guasto", Componente = new TestLoom.App.Models.EcuSelection(["BCM"]), Esito = TestLoom.App.Models.Esito.NonConforme
            }]
        });
        _state.TestCases.Save(tc, isNew: true);
        _state.NonConformita.Save(new TestLoom.App.Models.NonConformita
        {
            Id = "NC-PDF-LONG", TestCaseId = tc.Id, TestUid = tc.Uid, DataApertura = new DateTime(2026, 1, 2),
            Descrizione = string.Concat(Enumerable.Repeat("Dettaglio verificabile della non conformità. ", 18_000))
        });

        var bytes = PdfReportService.GeneraPdf(new ReportFilter(), _state, "Operatore Prova A");
        var pdf = System.Text.Encoding.ASCII.GetString(bytes);
        var pagine = System.Text.RegularExpressions.Regex.Matches(pdf, @"/Type\s*/Page\b").Count;

        Assert.True(bytes.Length > 0);
        Assert.True(pagine > 1, $"Il PDF con descrizione estesa dovrebbe essere multipagina, pagine trovate: {pagine}.");
    }

    [Fact]
    public void GeneraExcel_ConFiltroCheNonCorrispondeANulla_NonLanciaEProduceComunqueUnFile()
    {
        var filtro = new ReportFilter { Componente = { "Componente-CHE-NON-ESISTE-DAVVERO" } };
        var bytes = ReportService.GeneraExcel(filtro, _state, "Operatore Prova A");
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void GeneraPdf_ConFiltroCheNonCorrispondeANulla_NonLanciaEProduceComunqueUnFile()
    {
        var filtro = new ReportFilter { Componente = { "Componente-CHE-NON-ESISTE-DAVVERO" } };
        var bytes = PdfReportService.GeneraPdf(filtro, _state, "Operatore Prova A");
        Assert.True(bytes.Length > 0);
    }

    [Theory]
    [InlineData("Validazione Banco")]
    [InlineData("Validazione Veicolo")]
    [InlineData("UnitLogger")]
    [InlineData("UnitLogger Performance")]
    public void GeneraExcel_FiltratoPerOgniTemplate_NonLancia(string nomeTemplate)
    {
        var template = _state.Master.TestTemplate.Load().Single(t => t.Nome == nomeTemplate);
        var filtro = new ReportFilter { TemplateUid = { template.Uid } };
        var bytes = ReportService.GeneraExcel(filtro, _state, "Operatore Prova A");
        Assert.True(bytes.Length > 0);
    }

    /// <summary>Marcatura manuale N.A. per cella della matrice (decisione utente piano 6.4.0, punto 4): la
    /// cella marcata deve comparire come "N.A." nel foglio Excel, con la nota dell'utente come commento.</summary>
    [Fact]
    public void GeneraExcel_MatriceConCellaMarcataNA_MostraNAConNotaComeCommento()
    {
        var configurazione = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.CategoriaFunzionalita };
        var risultatoBase = ReportMatriceService.Genera(_state.TestCases.GetAll(), configurazione,
            _state.Master.Veicoli.Load(), _state.Master.Funzionalita.Load(), _state.Master.TestTemplate.Load());
        var primaCellaPopolata = risultatoBase.Righe.SelectMany(r => r.Celle.Select(c => (Riga: r.Riga, c.Colonna))).First(x => !string.IsNullOrEmpty(x.Riga) && !string.IsNullOrEmpty(x.Colonna));

        MatriceNonApplicabileService.Imposta(_state.Config, configurazione.AsseX, configurazione.AsseY,
            primaCellaPopolata.Colonna, primaCellaPopolata.Riga, "Non applicabile a questa configurazione");

        var bytes = ReportService.GeneraExcel(new ReportFilter(), _state, "Operatore Prova A", configurazione);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var foglio = workbook.Worksheet("Matrice configurabile");

        var cellaNA = foglio.CellsUsed().Single(c => c.GetString() == "N.A.");
        Assert.True(cellaNA.HasComment);
        Assert.Contains("Non applicabile a questa configurazione", cellaNA.GetComment().Text);
    }

    /// <summary>Coerenza Excel/PDF richiesta in review: una cella marcata N.A. non deve far lanciare la
    /// generazione del PDF (la stessa combinazione di assi/marcatura usata sopra per l'Excel). Il progetto di
    /// test non ha una libreria di estrazione testo da PDF (nessuna dipendenza aggiunta apposta per questo
    /// punto): la copertura sul CONTENUTO della cella N.A. resta sul livello che già lo garantisce in modo
    /// indipendente dal formato di output — MatriceNonApplicabileServiceTests, che verifica
    /// NonApplicabile/NotaNonApplicabile sullo stesso RisultatoMatriceReport che sia ReportService sia
    /// PdfReportService leggono per scrivere la cella (vedi Testo/NonApplicabile in CellaMatriceReport, usati
    /// identicamente da entrambi gli export).</summary>
    [Fact]
    public void GeneraPdf_MatriceConCellaMarcataNA_NonLanciaEProduceUnFileNonVuoto()
    {
        var configurazione = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.CategoriaFunzionalita };
        var risultatoBase = ReportMatriceService.Genera(_state.TestCases.GetAll(), configurazione,
            _state.Master.Veicoli.Load(), _state.Master.Funzionalita.Load(), _state.Master.TestTemplate.Load());
        var primaCellaPopolata = risultatoBase.Righe.SelectMany(r => r.Celle.Select(c => (Riga: r.Riga, c.Colonna))).First(x => !string.IsNullOrEmpty(x.Riga) && !string.IsNullOrEmpty(x.Colonna));

        MatriceNonApplicabileService.Imposta(_state.Config, configurazione.AsseX, configurazione.AsseY,
            primaCellaPopolata.Colonna, primaCellaPopolata.Riga, "Non applicabile a questa configurazione");

        var bytes = PdfReportService.GeneraPdf(new ReportFilter(), _state, "Operatore Prova A", configurazione);
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void GeneraExcel_ConIntervalloDateCheEscludeTutto_NonLancia()
    {
        var filtro = new ReportFilter { DataDa = DateTime.Now.AddYears(10) };
        var bytes = ReportService.GeneraExcel(filtro, _state, "Operatore Prova A");
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void GeneraPdf_IncludeLaMatriceConfigurata()
    {
        var config = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Sottoveicolo,
            AsseY = DimensioniMatrice.Componente
        };

        var bytes = PdfReportService.GeneraPdf(new ReportFilter(), _state, "Operatore Prova A", config);

        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void GeneraExcel_IncludeLaMatriceConfigurata()
    {
        var config = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Sottoveicolo,
            AsseY = DimensioniMatrice.Componente
        };
        var bytes = ReportService.GeneraExcel(new ReportFilter(), _state, "Operatore Prova A", config);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));

        var foglio = workbook.Worksheet("Matrice configurabile");
        Assert.Contains("sottoveicolo", foglio.Cell(2, 1).GetString());
        Assert.Contains("componente", foglio.Cell(2, 1).GetString());
    }

    [Fact]
    public void GeneraExcel_ContieneFoglioAuditConformitaConPunteggioPerRequisito()
    {
        var requisito = new Requisito { Id = "R-AUDIT-1", Testo = "Requisito di audit di prova" };
        _state.Master.Requisiti.Save(_state.Master.Requisiti.Load().Append(requisito).ToList());

        var tc = new TestCase { Id = "TC-AUDIT-1", Componente = "ECM", RequisitoId = new List<string> { requisito.Id } };
        var esecuzione = new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V" };
        PunteggioConformitaService.SincronizzaPunteggi(esecuzione, tc.RequisitoId);
        tc.Esecuzioni.Add(esecuzione);
        _state.TestCases.Save(tc, true);

        var bytes = ReportService.GeneraExcel(new ReportFilter(), _state, "Operatore Prova A");
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var foglio = workbook.Worksheet("Audit Conformità");

        // RowsUsed() salta le righe vuote: restano titolo (riga 1) e intestazioni (riga 3) prima dei dati.
        var righe = foglio.RowsUsed().Skip(2);
        Assert.Contains(righe, r => r.Cell(1).GetString() == requisito.Id
            && r.Cell(3).GetString() == PunteggioConformita.Conforme.Label()
            && r.Cell(4).GetString().Contains(tc.Id));
    }

    [Fact]
    public void Salva_ScriveSuDiscoENonSovrascriveMai()
    {
        var bytes = ReportService.GeneraExcel(new ReportFilter(), _state, "Operatore Prova A");
        var primo = ReportService.Salva(_state.Paths, "TestLoom_Report", "xlsx", bytes);
        var secondo = ReportService.Salva(_state.Paths, "TestLoom_Report", "xlsx", bytes);

        Assert.NotEqual(primo, secondo);
        Assert.True(File.Exists(primo));
        Assert.True(File.Exists(secondo));
    }
}
