using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Matrice multi-campo (6.6.1) lato ViewModel e lato Excel: limiti dei campi per asse, asse senza campi, duplicati,
/// persistenza di modalita' e gruppi compressi, intestazioni unite, colori di stato, elenco piatto con filtro automatico.</summary>
public sealed class ReportMatriceCampiExportTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public ReportMatriceCampiExportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoom_MatriceCampi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_dir);
        _state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily A", GammaNome = "LCV" }, new() { Nome = "Daily B", GammaNome = "LCV" } });
        _state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "BCM", VeicoliAssociati = ["Daily A", "Daily B"] },
            new() { Nome = "EDC", VeicoliAssociati = ["Daily A"] },
        });
        _state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Lettura guasto", Categorie = ["Diagnostica"] },
            new() { Nome = "Programmazione", Categorie = ["Programming"] },
        });
        SalvaTest("T-A", "BCM", "Daily A", "Lettura guasto", Esito.Conforme, new DateTime(2026, 1, 1));
        SalvaTest("T-B", "BCM", "Daily B", "Lettura guasto", Esito.NonConforme, new DateTime(2026, 1, 2));
        SalvaTest("T-C", "EDC", "Daily A", "Programmazione", Esito.DaEseguire, new DateTime(2026, 1, 3));
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private void SalvaTest(string id, string componente, string veicolo, string funzionalita, Esito esito, DateTime data)
    {
        var tc = new TestCase { Id = id, Componente = new EcuSelection([componente]), Veicoli = [veicolo], Funzionalita = [funzionalita] };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = data,
            Condizioni = [new Condizione { Funzionalita = funzionalita, Esito = esito, Componente = new EcuSelection([componente]) }],
        });
        _state.TestCases.Save(tc, isNew: true);
    }

    private ReportViewModel NuovoViewModel() => new(_state);

    [Fact]
    public void Campi_massimoTreParAsseEMassimoUnoMinimoNellaMatrice()
    {
        var vm = NuovoViewModel();
        vm.AggiungiCampoMatrice(righe: false);
        vm.AggiungiCampoMatrice(righe: false);
        vm.AggiungiCampoMatrice(righe: false);
        vm.AggiungiCampoMatrice(righe: false);
        Assert.Equal(ConfigurazioneMatriceReport.MassimoCampiPerAsse, vm.CampiXMatrice.Count);

        // Si puo' svuotare un asse, ma non l'intera matrice: l'ultimo campo resta.
        while (vm.CampiXMatrice.Count > 0) vm.RimuoviCampoMatrice(vm.CampiXMatrice[0]);
        vm.RimuoviCampoMatrice(vm.CampiYMatrice[0]);
        Assert.Single(vm.CampiYMatrice.Concat(vm.CampiXMatrice));
    }

    [Fact]
    public void Campi_dimensioneDuplicataVieneRifiutata()
    {
        var vm = NuovoViewModel();
        var componente = vm.OpzioniDimensioniMatrice.First(d => d.Id == DimensioniMatrice.Componente);
        var primoCampoX = vm.CampiXMatrice[0];
        vm.CampiYMatrice[0].Dimensione = componente; // gia' usata sull'asse X: la scelta torna a quella precedente
        Assert.Equal(DimensioniMatrice.CategoriaFunzionalita, vm.CampiYMatrice[0].Dimensione.Id);
        Assert.Same(primoCampoX, vm.CampiXMatrice[0]);
    }

    [Fact]
    public void Campi_assoSenzaCampiSalvatoERiaperto()
    {
        var vm = NuovoViewModel();
        vm.RimuoviCampoMatrice(vm.CampiXMatrice[0]); // asse X senza campi: colonna unica "Stato"
        Assert.Empty(vm.CampiXMatrice);
        Assert.True(vm.SenzaCampiXMatrice);
        Assert.False(string.IsNullOrEmpty(vm.EtichettaColonnaMatrice));
        vm.AggiungiCampoMatrice(righe: true); // secondo campo sull'asse Y: l'asse X resta senza campi
        Assert.Empty(vm.CampiXMatrice);

        var riaperto = NuovoViewModel();
        Assert.Empty(riaperto.CampiXMatrice);
        Assert.Equal(vm.CampiYMatrice.Select(c => c.Dimensione.Id), riaperto.CampiYMatrice.Select(c => c.Dimensione.Id));
    }

    [Fact]
    public void Modalita_eGruppiCompressiSonoPreferenzeLocaliRiaperte()
    {
        var vm = NuovoViewModel();
        vm.IndiceModalitaMatrice = 1;
        Assert.Equal(ModalitaCellaMatrice.Stato, vm.ModalitaMatrice);
        vm.AlternaGruppoMatrice(ReportMatriceService.ChiaveCompressione(true, "Daily A"));
        Assert.Contains(ReportMatriceService.ChiaveCompressione(true, "Daily A"), vm.GruppiCompressiMatrice);

        vm.IndiceModalitaMatrice = 2;
        Assert.Equal(ModalitaCellaMatrice.TestPercentuale, vm.ModalitaMatrice);

        var riaperto = NuovoViewModel();
        Assert.Equal(ModalitaCellaMatrice.TestPercentuale, riaperto.ModalitaMatrice);
        Assert.Contains(ReportMatriceService.ChiaveCompressione(true, "Daily A"), riaperto.GruppiCompressiMatrice);

        ViewStateService.Scrivi(_state, "Report.ModalitaMatrice", "percentuale"); // persisted 6.6.1 preference remains Stato %.
        Assert.Equal(ModalitaCellaMatrice.Percentuale, NuovoViewModel().ModalitaMatrice);
    }

    [Fact]
    public void CambiareModalitaMatrice_MantieneAssiAnnidatiOrdineFiltriEVistaPersonale()
    {
        var vm = NuovoViewModel();
        DimensioneMatriceOpzione Dimensione(string id) => vm.OpzioniDimensioniMatrice.Single(d => d.Id == id);

        vm.AggiungiCampoMatrice(righe: false);
        vm.CampiXMatrice[1].Dimensione = Dimensione(DimensioniMatrice.Sottoveicolo);
        vm.AggiungiCampoMatrice(righe: false);
        vm.CampiXMatrice[2].Dimensione = Dimensione(DimensioniMatrice.Gamma);
        vm.AggiungiCampoMatrice(righe: true);
        vm.CampiYMatrice[1].Dimensione = Dimensione(DimensioniMatrice.Funzionalita);
        vm.AggiungiCampoMatrice(righe: true);
        vm.CampiYMatrice[2].Dimensione = Dimensione(DimensioniMatrice.Template);
        vm.SpostaCampoMatrice(vm.CampiXMatrice[2], -1);
        vm.SpostaCampoMatrice(vm.CampiYMatrice[2], -1);

        var filtroEsito = vm.FiltriMatrice.Single(f => f.Dimensione.Id == DimensioniMatrice.Esito).Filtro;
        filtroEsito.Opzioni.Single(o => o.Valore == nameof(Esito.Conforme)).Selezionato = true;
        vm.FiltroVeicolo = "Daily A";

        var assiX = vm.CampiXMatrice.Select(c => c.Dimensione.Id).ToArray();
        var assiY = vm.CampiYMatrice.Select(c => c.Dimensione.Id).ToArray();
        var esitiSelezionati = filtroEsito.Selezionati.ToArray();
        foreach (var modo in new[] { 1, 2, 0 })
        {
            vm.IndiceModalitaMatrice = modo;
            Assert.Equal(assiX, vm.CampiXMatrice.Select(c => c.Dimensione.Id));
            Assert.Equal(assiY, vm.CampiYMatrice.Select(c => c.Dimensione.Id));
            Assert.Equal(esitiSelezionati, filtroEsito.Selezionati);
            Assert.Equal("Daily A", vm.FiltroVeicolo);
        }

        var riaperto = NuovoViewModel();
        Assert.Equal(assiX, riaperto.CampiXMatrice.Select(c => c.Dimensione.Id));
        Assert.Equal(assiY, riaperto.CampiYMatrice.Select(c => c.Dimensione.Id));
        Assert.Equal(esitiSelezionati, riaperto.FiltriMatrice.Single(f => f.Dimensione.Id == DimensioniMatrice.Esito).Filtro.Selezionati);
        Assert.Equal("Daily A", riaperto.FiltroVeicolo);
        Assert.Equal(ModalitaCellaMatrice.Percentuale, riaperto.ModalitaMatrice);
    }

    [Fact]
    public void FiltroEsito_TestPercentualeUsaStatoCorrenteEMantieneSelezioniNonPiuPresenti()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_EsitoModalita_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(dir);
        state.Master.Veicoli.Save([new Veicolo { Nome = "V1" }]);
        state.Master.Componente.Save([new Componente { Nome = "BCM", VeicoliAssociati = ["V1"] }]);
        state.Master.Funzionalita.Save([new Funzionalita { Nome = "F", EcuAssociate = ["BCM"] }]);
        var test = new TestCase { Id = "T-DEV-ACC", Componente = new EcuSelection(["BCM"]), Veicoli = ["V1"], Funzionalita = ["F"] };
        test.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(2026, 1, 1),
            Esito = Esito.Conforme,
            EsitoSovrascritto = true,
            MotivazioneOverride = "Deviazione accettata",
            Condizioni = [new Condizione { Funzionalita = "F", Componente = new EcuSelection(["BCM"]), Esito = Esito.ConformeConDeviazioni }],
        });
        state.TestCases.Save(test, isNew: true);

        try
        {
            var vm = new ReportViewModel(state);
            var filtro = vm.FiltriMatrice.Single(f => f.Dimensione.Id == DimensioniMatrice.Esito).Filtro;
            var deviazione = Esito.ConformeConDeviazioni.Label();
            Assert.Contains(filtro.Opzioni, o => o.Valore == Esito.ConformeConDeviazioni.Label());
            filtro.Opzioni.Single(o => o.Valore == Esito.ConformeConDeviazioni.Label()).Selezionato = true;

            vm.IndiceModalitaMatrice = 2;

            Assert.Contains(filtro.Opzioni, o => o.Valore == Esito.Conforme.Label());
            Assert.Contains(deviazione, filtro.Selezionati);
            filtro.Opzioni.Single(o => o.Valore == deviazione).Selezionato = false;
            filtro.Opzioni.Single(o => o.Valore == Esito.Conforme.Label()).Selezionato = true;

            Assert.Equal(1, vm.TotaleGeneraleMatrice.StatiPercentuale.Ok);
            Assert.Equal(1, vm.TotaleGeneraleMatrice.StatiPercentuale.Totale);
        }
        finally
        {
            state.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Excel_UnCampoPerAsse_NessunaUnioneEFogliaInvariato()
    {
        var config = new ConfigurazioneMatriceReport { CampiX = [DimensioniMatrice.Componente], CampiY = [DimensioniMatrice.Funzionalita] };
        using var wb = new XLWorkbook(new MemoryStream(ReportService.GeneraExcel(new ReportFilter(), _state, "Operatore Prova A", config)));
        var ws = wb.Worksheet("Matrice configurabile");
        Assert.Empty(ws.MergedRanges);
        Assert.Equal("BCM", ws.Cell(4, 2).GetString().Split('\n')[0]);
        Assert.Equal("Lettura guasto", ws.Cell(5, 1).GetString().Split('\n')[0]);
        Assert.Contains("%", ws.Cell(5, 2).GetString());
    }

    [Fact]
    public void Excel_TestPercentuale_MostraLaDistribuzioneEIlNumeroDiTestPerLaStessaMatrice()
    {
        var config = new ConfigurazioneMatriceReport
        {
            CampiX = [DimensioniMatrice.Componente],
            CampiY = [DimensioniMatrice.Funzionalita],
            ModalitaCella = ModalitaCellaMatrice.TestPercentuale,
        };
        using var workbook = new XLWorkbook(new MemoryStream(ReportService.GeneraExcel(new ReportFilter(), _state,
            "Operatore Prova A", config)));
        var ws = workbook.Worksheet("Matrice configurabile");
        var cellText = ws.Cell(5, 2).GetString();

        Assert.Contains("Test %", ws.Cell(1, 1).GetString());
        Assert.Contains("test)", cellText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("da eseg.", cellText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("100%", cellText);
    }

    [Fact]
    public void Excel_DueCampiInX_IntestazioneDiGruppoUnitaEColoriStato()
    {
        var config = new ConfigurazioneMatriceReport
        {
            CampiX = [DimensioniMatrice.Componente, DimensioniMatrice.Sottoveicolo],
            CampiY = [DimensioniMatrice.Funzionalita],
            ModalitaCella = ModalitaCellaMatrice.Stato,
        };
        using var wb = new XLWorkbook(new MemoryStream(ReportService.GeneraExcel(new ReportFilter(), _state, "Operatore Prova A", config)));
        var ws = wb.Worksheet("Matrice configurabile");
        // Gruppo "BCM" sulla prima riga di intestazione, unito su Daily A e Daily B.
        Assert.Equal("BCM", ws.Cell(4, 2).GetString().Split('\n')[0]);
        Assert.True(ws.Cell(4, 2).IsMerged());
        // Lettura guasto su BCM × Daily A: conforme; su BCM × Daily B: non conforme (colori dell'esito piu' recente).
        Assert.Equal(XLColor.FromHtml("#DCFCE7"), ws.Cell(6, 2).Style.Fill.BackgroundColor);
        Assert.Equal(XLColor.FromHtml("#FEE2E2"), ws.Cell(6, 3).Style.Fill.BackgroundColor);
    }

    [Fact]
    public void Excel_ModalitaPercentuale_NessunColoreVerdeSoloConDeviazioniAccettate()
    {
        // EDC x Lettura guasto: solo "Conforme con deviazioni" = 100% Dev. BCM x Lettura guasto: misto (conforme + non conforme).
        // La vista in modalita' percentuale non colora le celle: l'Excel non deve mostrare il verde di Conformi/Totali.
        SalvaTest("T-D", "EDC", "Daily A", "Lettura guasto", Esito.ConformeConDeviazioni, new DateTime(2026, 1, 4));
        var config = new ConfigurazioneMatriceReport
        {
            CampiX = [DimensioniMatrice.Componente],
            CampiY = [DimensioniMatrice.Funzionalita],
            ModalitaCella = ModalitaCellaMatrice.Percentuale,
        };
        using var wb = new XLWorkbook(new MemoryStream(ReportService.GeneraExcel(new ReportFilter(), _state, "Operatore Prova A", config)));
        var ws = wb.Worksheet("Matrice configurabile");
        // Le celle hanno testo su piu' righe (nome, poi percentuali): si cerca per prefisso del primo rigo, senza un layout fisso.
        int Colonna(string componente) => Enumerable.Range(2, 6).First(c => Enumerable.Range(1, 8).Any(r => ws.Cell(r, c).GetString().StartsWith(componente, StringComparison.Ordinal)));
        int Riga(string funzionalita) => Enumerable.Range(1, 40).First(r => ws.Cell(r, 1).GetString().StartsWith(funzionalita, StringComparison.Ordinal));

        var soloDeviazioni = ws.Cell(Riga("Lettura guasto"), Colonna("EDC"));
        var misto = ws.Cell(Riga("Lettura guasto"), Colonna("BCM"));
        Assert.Contains("100%", soloDeviazioni.GetString());
        Assert.Equal(XLFillPatternValues.None, soloDeviazioni.Style.Fill.PatternType);
        Assert.Equal(XLFillPatternValues.None, misto.Style.Fill.PatternType);
    }

    [Fact]
    public async Task Excel_EsportaTuttiGliStatiAncheSeLaVistaNeNascondeUno()
    {
        var vm = NuovoViewModel();
        vm.ModalitaMatrice = ModalitaCellaMatrice.Stato;
        vm.NascondiNokMatrice = true;
        var percorso = Path.Combine(_dir, "report-stati-completi.xlsx");

        await vm.EsportaExcelVerso(percorso);

        using var wb = new XLWorkbook(percorso);
        var matrice = wb.Worksheet("Matrice configurabile");
        var testi = matrice.CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains(testi, testo => testo.Contains(Esito.NonConforme.Label(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Excel_AssenzaDiCampiInColonna_ElencoPiattoConFiltroAutomatico()
    {
        var config = new ConfigurazioneMatriceReport
        {
            CampiX = [],
            CampiY = [DimensioniMatrice.Funzionalita, DimensioniMatrice.Componente],
            ModalitaCella = ModalitaCellaMatrice.Stato,
        };
        using var wb = new XLWorkbook(new MemoryStream(ReportService.GeneraExcel(new ReportFilter(), _state, "Operatore Prova A", config)));
        var ws = wb.Worksheet("Matrice configurabile");
        Assert.Empty(ws.MergedRanges);
        // Intestazioni: una colonna per livello (Funzionalita, componente) e la colonna Stato; righe = combinazioni presenti o catalogo.
        Assert.Equal(3, ws.Row(4).CellsUsed().Count());
        Assert.Equal("Lettura guasto", ws.Cell(5, 1).GetString());
        Assert.Equal("BCM", ws.Cell(5, 2).GetString());
        Assert.True(ws.AutoFilter.IsEnabled);
    }

}
