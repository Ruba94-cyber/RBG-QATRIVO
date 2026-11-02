using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Fogli Dati / Riepilogo / Info dell'export Report e file pivot: la tabella Dati porta il Test UID e le intestazioni in riga 1;
/// i riepiloghi sono formule che tornano con i dati; il file pivot contiene pivot reali sugli stessi dati.</summary>
public sealed class ReportExportArricchimentiTests
{
    private static readonly List<Funzionalita> Funz =
    [
        new() { Nome = "Lettura guasto", Categorie = ["Diag. Base"] },
        new() { Nome = "Flash", Categorie = ["Programming"] }
    ];

    private static readonly List<Veicolo> Veicoli =
    [
        new() { Nome = "Daily A", GammaNome = "LCV Diesel" },
        new() { Nome = "Daily B", GammaNome = "LCV BEV" },
    ];

    private static TestCase T(string id, string funz, string componente, string veicolo, Esito esito)
    {
        var tc = new TestCase { Id = id, DescrizioneBreve = "d-" + id, Componente = new EcuSelection([componente]), Veicoli = [veicolo], Funzionalita = [funz] };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(2026, 3, 1), Operatore = "Mario", Esito = esito,
            Condizioni = [new Condizione { Funzionalita = funz, Esito = esito, Componente = new EcuSelection([componente]) }]
        });
        return tc;
    }

    private static readonly ConfigurazioneMatriceReport EcuPerCategoria = new()
    {
        AsseX = DimensioniMatrice.Componente,
        AsseY = DimensioniMatrice.CategoriaFunzionalita
    };

    private static (List<TestCase> Test, List<ReportMatriceService.OrigineDato> Righe) Dati()
    {
        var test = new List<TestCase>
        {
            T("T-1", "Lettura guasto", "BCM", "Daily A", Esito.NonConforme),
            T("T-2", "Lettura guasto", "BCM", "Daily A", Esito.Conforme),
            T("T-3", "Flash", "EDC", "Daily B", Esito.Conforme),
            T("T-4", "Flash", "EDC", "Daily B", Esito.Conforme),
            T("T-5", "Flash", "EDC", "Daily B", Esito.NonConforme),
        };
        var (righe, _, _) = ReportMatriceService.Origine(test, EcuPerCategoria, Veicoli, Funz, [], 100);
        return (test, righe.ToList());
    }

    [Fact]
    public void FoglioDati_HaIntestazioniInRigaUnoFiltroETestUid()
    {
        var (test, righe) = Dati();
        using var wb = new XLWorkbook();
        ReportExportArricchimentiService.Scrivi(wb, righe, righe.Count, "Mario", new DateTime(2026, 3, 2, 10, 0, 0), CancellationToken.None);

        var ws = wb.Worksheet(LocalizationService.Instance.Traduci("ExpFoglioDati"));
        Assert.Equal("Test UID", ws.Cell(1, 1).GetString());
        Assert.Equal(ReportExportArricchimentiService.Intestazioni().Count, ws.LastColumnUsed()!.ColumnNumber());
        Assert.Equal(righe.Count + 1, ws.LastRowUsed()!.RowNumber());
        Assert.NotNull(ws.AutoFilter);
        var uidDelleRighe = Enumerable.Range(2, righe.Count).Select(r => ws.Cell(r, 1).GetString()).ToHashSet();
        Assert.All(righe.Select(x => x.TestUid), uid => Assert.Contains(uid.ToString(), uidDelleRighe));
        Assert.Subset(test.Select(t => t.Uid.ToString()).ToHashSet(), uidDelleRighe);
    }

    [Fact]
    public void Riepilogo_LeFormuleTornanoConIDati()
    {
        var (_, righe) = Dati();
        using var wb = new XLWorkbook();
        ReportExportArricchimentiService.Scrivi(wb, righe, righe.Count, "Mario", DateTime.Now, CancellationToken.None);

        var ws = wb.Worksheet(LocalizationService.Instance.Traduci("ExpFoglioRiepilogo"));
        var riepilogo = ws.CellsUsed().Where(c => c.GetString() == "LCV Diesel").Select(c => c.Address.RowNumber).First();
        var attesoDiesel = righe.Count(x => x.Gamma == "LCV Diesel");
        var attesoBev = righe.Count(x => x.Gamma == "LCV BEV");
        Assert.True(attesoDiesel > 0 && attesoBev > 0);
        Assert.Equal(attesoDiesel, (int)ws.Cell(riepilogo, 2).Value.GetNumber());
        var rigaBev = ws.CellsUsed().Where(c => c.GetString() == "LCV BEV").Select(c => c.Address.RowNumber).First();
        Assert.Equal(attesoBev, (int)ws.Cell(rigaBev, 2).Value.GetNumber());
        var determinantiBev = righe.Count(x => x.Gamma == "LCV BEV" && x.DeterminaStato);
        Assert.Equal(determinantiBev, (int)ws.Cell(rigaBev, 3).Value.GetNumber());
    }

    [Fact]
    public void Info_ElencaIFogliEIlNumeroDiRighe()
    {
        var (_, righe) = Dati();
        using var wb = new XLWorkbook();
        wb.Worksheets.Add("Grafici");
        ReportExportArricchimentiService.Scrivi(wb, righe, righe.Count + 7, "Mario", new DateTime(2026, 3, 2, 10, 0, 0), CancellationToken.None);

        var ws = wb.Worksheet(LocalizationService.Instance.Traduci("ExpFoglioInfo"));
        var testo = string.Join("|", ws.CellsUsed().Select(c => c.GetString()));
        Assert.Contains("02/03/2026 10:00", testo);
        Assert.Contains("Mario", testo);
        Assert.Contains("Grafici", testo);
        Assert.Contains((righe.Count + 7).ToString(), testo);
    }

    [Fact]
    public void PivotStato_CategorieMultipleENonApplicabile_NonDuplicanoCombinazioni()
    {
        var funzioni = new List<Funzionalita> { new() { Nome = "F", Categorie = ["A", "B"] } };
        var test = new[] { T("T1", "F", "BCM", "Daily A", Esito.Conforme), T("T2", "F", "BCM", "Daily A", Esito.NonConforme) };
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.CategoriaFunzionalita };
        var preparata = ReportMatriceService.Prepara(test, config, Veicoli, funzioni, []);
        var marcature = new[] { new CellaNonApplicabile { ValoreAsseX = "BCM", ValoreAsseY = "B" } };
        using var wb = new XLWorkbook();
        ReportExportArricchimentiService.ScriviPivotStato(wb, preparata, config, funzioni, marcature, CancellationToken.None);
        var loc = LocalizationService.Instance;
        var core = wb.Worksheet(loc.Scegli("Origine pivot", "Pivot source", "Source pivot"));
        var cat = wb.Worksheet(loc.Scegli("Origine categorie", "Category source", "Source catégories"));
        Assert.Equal(2, core.LastRowUsed()!.RowNumber());
        Assert.Equal(2, cat.LastRowUsed()!.RowNumber());
        Assert.Equal("A", cat.Cell(2, 4).GetString());
        foreach (var nome in ReportPivotStandardTests.NomiFogliStandard()) Assert.True(wb.TryGetWorksheet(nome, out _), nome);
    }

    [Fact]
    public void PivotStato_IncludeManualiEUniversoNonTestato()
    {
        var funzioni = new List<Funzionalita> { new() { Nome = "F", Categorie = ["A"] } };
        var manuali = new[] { new StatoFunzionalitaEcuVeicolo { Funzionalita = "F", Componente = "BCM", Veicolo = "Daily A", Esito = Esito.NonConforme, DataDecisione = new DateTime(2026, 8, 1) } };
        var componenti = new[] { new Componente { Nome = "BCM", VeicoliAssociati = ["Daily A", "Daily B"] } };
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.CategoriaFunzionalita };
        var preparata = ReportMatriceService.Prepara([], config, Veicoli, funzioni, [], componenti, manuali);
        using var wb = new XLWorkbook();
        ReportExportArricchimentiService.ScriviPivotStato(wb, preparata, config, funzioni, [], CancellationToken.None);
        var loc = LocalizationService.Instance;
        var ws = wb.Worksheet(loc.Scegli("Origine pivot", "Pivot source", "Source pivot"));
        var righe = ws.RowsUsed().Skip(1).ToDictionary(r => r.Cell(1).GetString(), r => r.Cell(5).GetString());
        Assert.Equal(Esito.NonConforme.Label(), righe["Daily A"]);
        // Il risultato manuale sull’altro veicolo rende propagabile questa combinazione.
        Assert.Equal(1, preparata.CalcolaTotaleGenerale().Stati.DaDecidere);
        Assert.Equal(ReportMatriceService.EtichettaStatoDisponibile(StatoCombinazioneMatrice.DaDecidere), righe["Daily B"]);
    }

    [Fact]
    public void PivotStato_Vuoto_HaTreTabelleStandardESorgenteZero()
    {
        var config = new ConfigurazioneMatriceReport();
        var preparata = ReportMatriceService.Prepara([], config, [], [], []);
        using var wb = new XLWorkbook();
        ReportExportArricchimentiService.ScriviPivotStato(wb, preparata, config, [], [], CancellationToken.None);
        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;
        using var riaperto = new XLWorkbook(stream);
        foreach (var nome in ReportPivotStandardTests.NomiFogliStandard()) Assert.True(riaperto.TryGetWorksheet(nome, out _), nome);
        var loc = LocalizationService.Instance;
        Assert.Equal(0, riaperto.Worksheet(loc.Scegli("Origine pivot", "Pivot source", "Source pivot")).Cell(2, 6).GetDouble());
    }

    [Fact]
    public void PivotStato_AnnullamentoArrestaLEnumerazioneDellUniverso()
    {
        var config = new ConfigurazioneMatriceReport();
        var funzioni = new[] { new Funzionalita { Nome = "F", Categorie = ["A"] } };
        var componente = new[] { new Componente { Nome = "BCM", VeicoliAssociati = ["Daily A"] } };
        var preparata = ReportMatriceService.Prepara([], config, Veicoli, funzioni, [], componente);
        using var annulla = new CancellationTokenSource();
        annulla.Cancel();
        Assert.Throws<OperationCanceledException>(() => preparata.CombinazioniPerExport([], annulla.Token).ToList());
    }

    [Fact]
    public void Prepara_UniversoMaterializzatoRispettaLimiteECancellazione()
    {
        var config = new ConfigurazioneMatriceReport { CampiX = [DimensioniMatrice.Componente, DimensioniMatrice.Funzionalita], CampiY = [DimensioniMatrice.CategoriaFunzionalita] };
        var funzioni = new[] { new Funzionalita { Nome = "F", Categorie = ["A"] } };
        var componente = new[] { new Componente { Nome = "BCM", VeicoliAssociati = ["Daily A", "Daily B"] } };
        Assert.Throws<LimiteUniversoExportSuperatoException>(() => ReportMatriceService.Prepara([], config, Veicoli, funzioni, [], componente, limiteUniversoExport: 1));
        using var annulla = new CancellationTokenSource();
        annulla.Cancel();
        Assert.Throws<OperationCanceledException>(() => ReportMatriceService.Prepara([], config, Veicoli, funzioni, [], componente, ct: annulla.Token));
    }

}
