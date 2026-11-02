using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>NonConformitaExportService (feedback utente 2026-09-17: "devo poter esportare le non conformità
/// con tutti i loro dati in excel") — un foglio con una colonna per ciascun campo proprio di NonConformita,
/// comprese le informazioni esclusive delle segnalazioni di rete.</summary>
public class NonConformitaExportServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public NonConformitaExportServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoomNcExportTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static NonConformita CreaNcCompleta(string id) => new()
    {
        Id = id,
        TestCaseId = "",
        EsecuzioneIterazione = 3,
        DataApertura = new DateTime(2026, 1, 10),
        DataChiusura = new DateTime(2026, 2, 1),
        Owner = "Mario",
        StatoWorkflow = StatoAnomalia.Conclusa,
        Severita = SeveritaAnomalia.High,
        Priorita = PrioritaOperativa.Major,
        Destinatario = DestinatarioSegnalazione.TD,
        MotivoChiusura = "Risolto in produzione",
        Descrizione = "Descrizione completa del problema",
        Azione = "Azione correttiva applicata",
        DaSegnalazioneRete = true,
        EcuSegnalate = new EcuSelection(new[] { "BCM" }),
        VeicoliSegnalati = new List<string> { "VeicoloX" },
        FunzionalitaSegnalate = new List<string> { "FunzA", "FunzB" },
        StrumentoSegnalato = "Asist",
        DescrizioneBreveSegnalata = "Sintesi breve",
        NumeriPraticaRete = new List<string> { "ASIST-111", "ASIST-222" },
        VinCliente = new List<string> { "VF1AAAAA000000001" },
        Licenze = new List<string> { "LIC-1", "LIC-2" },
        ConcessionariaOfficina = "Officina Rossi",
        CollegamentiPolarion = new List<CollegamentoPolarion> { new()
        {
            Codice = "TICKET-99999", Url = "https://ticketing.example/TICKET-99999",
            CollegatoIl = new DateTime(2026, 1, 11, 10, 30, 0), CollegatoDa = "Paolo", Note = "ticket aperto"
        } },
        OperazioniSegnalate = new List<OperazioneSegnalazione> { new()
        {
            Componente = "BCM", Funzionalita = "FunzA",
            ValoriStrumenti = new Dictionary<string, string> { ["SW version"] = "4.2", ["HW version"] = "B" }
        } },
        ProveCollegate = new List<RiferimentoProva> { new()
        {
            TestId = "TC-PROVA-1", EsecuzioneIterazione = 7, CondizioneUid = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            EvidenzaComparativa = true
        } },
        Allegati = new List<Allegato> { new()
        {
            NomeFile = "log.txt", Descrizione = "Log diagnostico", DataCaricamento = new DateTime(2026, 1, 12, 9, 15, 0),
            CaricatoDa = "Paolo", DimensioneByte = 1234, Sha256 = "abc123", Archiviata = false
        } },
        Changelog = new List<ChangelogEntry> { new()
        {
            Data = new DateTime(2026, 1, 13, 8, 0, 0), Operatore = "Paolo", Campo = "StatoWorkflow",
            ValorePrecedente = "In verifica", ValoreNuovo = "Conclusa"
        } },
        ChangeRequestIds = new List<string> { "CR-0001" },
    };

    [Fact]
    public void GeneraExcel_SuElencoVuoto_NonLanciaEProduceUnFileNonVuoto()
    {
        var bytes = NonConformitaExportService.GeneraExcel(Array.Empty<NonConformita>(), _state);
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void GeneraExcel_HaLIntestazioneDiTutteLeColonnePrincipali()
    {
        var bytes = NonConformitaExportService.GeneraExcel(Array.Empty<NonConformita>(), _state);
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet("Non Conformità");
        var intestazioni = ws.Row(1).CellsUsed().Select(c => c.GetString()).ToList();

        foreach (var atteso in new[]
        {
            "ID", "Stato", "Stato workflow", "Severità", "Priorità", "Owner", "Destinatario",
            "Componente segnalate", "Veicoli segnalati", "Funzionalità segnalate", "Strumento segnalato",
            "Numeri pratica rete", "Matricola cliente", "Licenze", "Concessionaria/Officina",
        })
            Assert.Contains(atteso, intestazioni);
    }

    [Fact]
    public void GeneraExcel_ConNcDiSegnalazioneRete_RiportaTuttiICampiPropriDellaSegnalazione()
    {
        var nc = CreaNcCompleta("NC-EXPORT-1");
        var bytes = NonConformitaExportService.GeneraExcel(new[] { nc }, _state);
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet("Non Conformità");
        var intestazioni = ws.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        var riga = ws.Row(2);

        string Cella(string intestazione) => riga.Cell(intestazioni.IndexOf(intestazione) + 1).GetString();

        Assert.Equal("NC-EXPORT-1", Cella("ID"));
        Assert.Equal("Sì", Cella("Da segnalazione rete"));
        Assert.Equal("BCM", Cella("Componente segnalate"));
        Assert.Equal("VeicoloX", Cella("Veicoli segnalati"));
        Assert.Equal("FunzA, FunzB", Cella("Funzionalità segnalate"));
        Assert.Equal("Asist", Cella("Strumento segnalato"));
        Assert.Equal("Sintesi breve", Cella("Descrizione breve segnalata"));
        Assert.Equal("ASIST-111, ASIST-222", Cella("Numeri pratica rete"));
        Assert.Equal("VF1AAAAA000000001", Cella("Matricola cliente"));
        Assert.Equal("LIC-1, LIC-2", Cella("Licenze"));
        Assert.Equal("Officina Rossi", Cella("Concessionaria/Officina"));
        Assert.Equal("TICKET-99999", Cella("Ticket TICKET/Ticketing"));
        Assert.Equal("CR-0001", Cella("Change Request collegate"));
        Assert.Equal("Mario", Cella("Owner"));
        Assert.Equal("Risolto in produzione", Cella("Motivo chiusura"));
        Assert.Equal("Descrizione completa del problema", Cella("Descrizione"));
    }

    [Fact]
    public void GeneraExcel_ConTestCollegato_RisolveIlNomeDelTestDalArchivio()
    {
        var tc = new TestCase { Id = "TC-EXPORT-1" };
        _state.TestCases.Save(tc, isNew: true);
        var nc = CreaNcCompleta("NC-EXPORT-2");
        nc.TestCaseId = "TC-EXPORT-1";

        var bytes = NonConformitaExportService.GeneraExcel(new[] { nc }, _state);
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet("Non Conformità");
        var intestazioni = ws.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        var colonnaTest = intestazioni.IndexOf("Test collegato") + 1;

        Assert.Equal("TC-EXPORT-1", ws.Row(2).Cell(colonnaTest).GetString());
    }

    [Fact]
    public void GeneraExcel_SelezioneNc_EsportaDueFogliConColonneVeicoloGammaEDettagliCompleti()
    {
        var selezionata = CreaNcCompleta("NC-SELEZIONATA");
        var esclusa = CreaNcCompleta("NC-ESCLUSA");
        _state.Master.Veicoli.Save([new Veicolo { Nome = "VeicoloX", GammaNome = "GammaX" }]);
        var bytes = NonConformitaExportService.GeneraExcel(new[] { selezionata }, _state);
        using var wb = new XLWorkbook(new MemoryStream(bytes));

        Assert.Single(wb.Worksheets);

        var principale = wb.Worksheet("Non Conformità");
        var headers = principale.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        string Cella(string nome) => principale.Row(2).Cell(headers.IndexOf(nome) + 1).GetString();
        Assert.Equal(3, principale.Row(2).Cell(headers.IndexOf("Iterazione test") + 1).GetValue<int>());
        Assert.DoesNotContain(esclusa.Id, principale.CellsUsed().Select(c => c.GetString()));
        Assert.Equal("VeicoloX", Cella("Veicolo"));
        Assert.Equal("GammaX", Cella("Gamma"));
        Assert.Contains("FunzA", Cella("Funzionalità"));
        Assert.Contains("SW version", Cella("Operazioni segnalate (dettagli)"));
        Assert.Contains("TC-PROVA-1", Cella("Prove collegate (dettagli)"));
        Assert.Contains("log.txt", Cella("Allegati (dettagli)"));
        Assert.Contains("StatoWorkflow", Cella("Cronologia (dettagli)"));
        Assert.Contains("https://ticketing.example/TICKET-99999", Cella("Collegamenti Ticketing (dettagli)"));
    }

    [Fact]
    public void GeneraExcel_DettaglioOltreIlLimiteCella_RestaCompletoNelFoglioEsteso()
    {
        var nc = CreaNcCompleta("NC-LUNGA");
        nc.Descrizione = new string('x', 32_768);
        var bytes = NonConformitaExportService.GeneraExcel([nc], _state);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var principale = workbook.Worksheet("Non Conformità");
        var headers = principale.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        var marker = principale.Cell(2, headers.IndexOf("Descrizione") + 1).GetString();
        Assert.Contains("Testo esteso", marker);
        var dettaglio = workbook.Worksheets.Single(w => w.Name.StartsWith("Non Conformità dettagli estesi", StringComparison.Ordinal));
        var testoCompleto = string.Concat(dettaglio.RowsUsed().Skip(1)
            .Where(r => r.Cell(1).GetString() == "NC-LUNGA" && r.Cell(2).GetString() == "Descrizione")
            .OrderBy(r => int.Parse(r.Cell(3).GetString().Split('/')[0], System.Globalization.CultureInfo.InvariantCulture))
            .Select(r => r.Cell(4).GetString()));
        Assert.Equal(nc.Descrizione, testoCompleto);
    }
}
