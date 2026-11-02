using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>TIME (6.6.1): parita' dell'export con la vista. L'Excel del Report riporta nella matrice lo stesso testo della cella
/// (minuti · noti/eleggibili) e lo stesso totale generale della modalita' Tempo totale selezionata, con la base scelta.</summary>
[Collection("Localization")]
public sealed class TempoExportParitaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_TempoExport_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Giorno = new(2026, 4, 1);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void ExcelReport_TempoTotale_RiportaTestoECompletezzaDellaVista()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily A", GammaNome = "LCV" } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["Daily A"] } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Lettura guasto", Categorie = ["Diagnostica"] } });
        var tc = new TestCase { Id = "T-XLS", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"], DataCreazione = Giorno, UltimaModifica = Giorno, Assegnatario = "Op" };
        tc.Esecuzioni.Add(Revisione(Giorno, 20));
        tc.Esecuzioni.Add(Revisione(Giorno.AddDays(1), 10));
        state.TestCases.Save(tc, isNew: true);

        var configurazione = new ConfigurazioneMatriceReport
        {
            CampiX = [DimensioniMatrice.Componente],
            CampiY = [DimensioniMatrice.Funzionalita],
            ModalitaCella = ModalitaCellaMatrice.TempoTotale,
            BaseTempo = BaseTempoMatrice.TutteLeEsecuzioni,
        };
        var vista = ReportMatriceService.Genera(state.TestCases.GetAll(), configurazione, state.Master.Veicoli.Load(),
            state.Master.Funzionalita.Load(), state.Master.TestTemplate.Load());
        var cella = vista.Righe.Single(r => r.Riga == "Lettura guasto").Celle.Single(c => c.Colonna == "BCM");
        Assert.Equal("30 min · 2/2", cella.TestoVisualizzato);

        var bytes = ReportService.GeneraExcel(new ReportFilter(), state, "Op", configurazione);
        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var foglio = workbook.Worksheet("Matrice configurabile");
        var testi = foglio.CellsUsed().Select(c => c.GetString()).ToList();

        Assert.Contains(cella.TestoVisualizzato, testi);
        Assert.Contains(testi, t => t.Contains($"Totale: {vista.TotaleGenerale.Testo}", StringComparison.Ordinal));
        Assert.Contains(testi, t => t.StartsWith("Tempo totale = somma dei minuti delle operazioni eseguite con durata nota", StringComparison.Ordinal));
    }

    [Fact]
    public void ExcelReport_LegendaTempoSegueLaLingua()
    {
        var loc = LocalizationService.Instance;
        var originale = loc.Lingua;
        try
        {
            loc.Lingua = "en";
            var state = CreaStatoConDueRevisioni();
            var configurazione = new ConfigurazioneMatriceReport
            {
                CampiX = [DimensioniMatrice.Componente],
                CampiY = [DimensioniMatrice.Funzionalita],
                ModalitaCella = ModalitaCellaMatrice.TempoTotale,
            };
            var bytes = ReportService.GeneraExcel(new ReportFilter(), state, "Op", configurazione);
            using var stream = new MemoryStream(bytes);
            using var workbook = new XLWorkbook(stream);
            var testi = workbook.Worksheet("Matrice configurabile").CellsUsed().Select(c => c.GetString()).ToList();

            Assert.Contains(testi, t => t.StartsWith("Total time = sum of minutes from executed operations with a known duration", StringComparison.Ordinal));
            Assert.DoesNotContain(testi, t => t.Contains("Tempo = minuti", StringComparison.Ordinal));
        }
        finally { loc.Lingua = originale; }
    }

    [Fact]
    public void PdfReport_TestoCella_CoincideConLaVistaInTutteLeModalita()
    {
        var state = CreaStatoConDueRevisioni();
        foreach (var modalita in new[] { ModalitaCellaMatrice.Percentuale, ModalitaCellaMatrice.TempoTotale, ModalitaCellaMatrice.TempoMedio })
        {
            var configurazione = new ConfigurazioneMatriceReport
            {
                CampiX = [DimensioniMatrice.Componente],
                CampiY = [DimensioniMatrice.Funzionalita],
                ModalitaCella = modalita,
                BaseTempo = BaseTempoMatrice.TutteLeEsecuzioni,
            };
            var vista = ReportMatriceService.Genera(state.TestCases.GetAll(), configurazione, state.Master.Veicoli.Load(),
                state.Master.Funzionalita.Load(), state.Master.TestTemplate.Load());
            var celle = vista.Righe.SelectMany(r => r.Celle).ToList();

            Assert.NotEmpty(celle);
            foreach (var cella in celle)
                Assert.Equal(cella.TestoVisualizzato, PdfReportService.TestoCella(cella));
        }
    }

    private AppState CreaStatoConDueRevisioni()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily A", GammaNome = "LCV" } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["Daily A"] } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Lettura guasto", Categorie = ["Diagnostica"] } });
        var tc = new TestCase { Id = "T-PDF", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"], DataCreazione = Giorno, UltimaModifica = Giorno, Assegnatario = "Op" };
        tc.Esecuzioni.Add(Revisione(Giorno, 20));
        tc.Esecuzioni.Add(Revisione(Giorno.AddDays(1), 10));
        state.TestCases.Save(tc, isNew: true);
        return state;
    }

    private static Esecuzione Revisione(DateTime data, int minuti) => new()
    {
        Iterazione = 1,
        Data = data,
        Operatore = "Op",
        Esito = Esito.Conforme,
        Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.Conforme, Componente = new EcuSelection(["BCM"]), DurataMinuti = minuti }],
    };
}
