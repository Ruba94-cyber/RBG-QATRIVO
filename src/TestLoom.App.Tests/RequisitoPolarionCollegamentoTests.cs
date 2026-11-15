using System.Text.Json;
using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Collegamento Requisito -> work item Ticketing: parsing/validazione, persistenza compatibile, merge/import-export.</summary>
[Collection("Localization")]
public sealed class RequisitoPolarionCollegamentoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReqPol_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;
    private readonly List<AppState> _stati = new();

    public void Dispose()
    {
        ConfirmService.Handler = _confirmOriginale;
        foreach (var s in _stati) { try { s.Dispose(); } catch { } }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStato(params Requisito[] requisiti)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        _stati.Add(state);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Pol", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList(); ops.Add(op); state.Master.Operatori.Save(ops);
        Assert.True(state.ImpostaOperatore("Master Pol", "1234"));
        if (requisiti.Length > 0) state.Master.Requisiti.Save(requisiti.ToList());
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        return state;
    }

    private static Requisito Req(string id, string? progetto = null, string? wi = null) => new()
    {
        Id = id, Testo = "Testo " + id, ApplicabileBanco = true, UltimaModifica = DateTime.Now, ModificatoDa = "Test",
        PolarionProgetto = progetto, PolarionWorkItemId = wi
    };

    private string Excel(params string[][] righe)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Requisiti");
        var intest = new[] { "Id", "Testo", "Banco", "Veicolo", "Veicolo-componente Extra", "Ticketing" };
        for (var c = 0; c < intest.Length; c++) ws.Cell(1, c + 1).Value = intest[c];
        for (var r = 0; r < righe.Length; r++)
            for (var c = 0; c < righe[r].Length; c++) ws.Cell(r + 2, c + 1).Value = righe[r][c];
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xlsx");
        wb.SaveAs(p);
        return p;
    }

    private static RequisitiViewModel Importa(AppState state, string percorso)
    {
        var vm = new RequisitiViewModel(state, TipoRequisito.Standard);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));
        return vm;
    }

    // ---------- parsing / validazione ----------

    [Theory]
    [InlineData("REQ_PROJ/REQ-123", null, "REQ_PROJ", "REQ-123")]
    [InlineData("  REQ_PROJ / req-9 ", null, "REQ_PROJ", "REQ-9")]
    [InlineData("REQ-77", "REQ_PROJ", "REQ_PROJ", "REQ-77")]
    [InlineData("https://ticketing.example/ticketing/#/project/REQ_PROJ/workitem?id=REQ-5", null, "REQ_PROJ", "REQ-5")]
    [InlineData("https://ticketing.example/ticketing/redirect/project/ALTRO/workitem?foo=1&id=SYS-10", null, "ALTRO", "SYS-10")]
    public void TryParse_FormatiValidi(string testo, string? predefinito, string progetto, string id)
    {
        Assert.True(RequisitoPolarionRegole.TryParse(testo, predefinito, out var link, out var errore));
        Assert.Null(errore);
        Assert.Equal(new RequisitoPolarionLink(progetto, id), link);
    }

    [Theory]
    [InlineData("REQ-77", null, "formato")]          // ID senza progetto e senza predefinito
    [InlineData("1BAD/REQ-1", null, "progetto")]      // il progetto deve iniziare con una lettera
    [InlineData("REQ PROJ/REQ-1", null, "progetto")]
    [InlineData("REQ_PROJ/REQ1", null, "id")]         // manca il numero
    [InlineData("REQ_PROJ/REQ-1/extra", null, "formato")]
    [InlineData("REQ_PROJ/../x-1", null, "formato")]
    public void TryParse_FormatiNonValidi(string testo, string? predefinito, string codiceErrore)
    {
        Assert.False(RequisitoPolarionRegole.TryParse(testo, predefinito, out var link, out var errore));
        Assert.Null(link);
        Assert.Equal(codiceErrore, errore);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_TestoVuoto_EquivaleANessunCollegamento(string? testo)
    {
        Assert.True(RequisitoPolarionRegole.TryParse(testo, null, out var link, out var errore));
        Assert.Null(link);
        Assert.Null(errore);
    }

    [Fact]
    public void Di_RichiedeProgettoEIdEntrambiValidi()
    {
        Assert.Null(RequisitoPolarionRegole.Di(null));
        Assert.Null(RequisitoPolarionRegole.Di(Req("R1")));
        Assert.Null(RequisitoPolarionRegole.Di(Req("R1", "REQ_PROJ", null)));
        Assert.Null(RequisitoPolarionRegole.Di(Req("R1", null, "REQ-1")));
        Assert.Null(RequisitoPolarionRegole.Di(Req("R1", "REQ_PROJ", "non valido")));
        Assert.Equal(new RequisitoPolarionLink("REQ_PROJ", "REQ-1"), RequisitoPolarionRegole.Di(Req("R1", " REQ_PROJ ", "req-1")));
    }

    [Fact]
    public void UrlBrowser_SoloHttps_ConIdEscapati()
    {
        var url = RequisitoPolarionRegole.UrlBrowser("https://pol.example:8443/ticketing/rest/v1", new("REQ_PROJ", "REQ-1"));
        Assert.Equal("https://pol.example:8443/ticketing/redirect/project/REQ_PROJ/workitem?id=REQ-1", url!.AbsoluteUri);
        Assert.Null(RequisitoPolarionRegole.UrlBrowser("http://pol.example/ticketing/rest/v1", new("REQ_PROJ", "REQ-1")));
        Assert.Null(RequisitoPolarionRegole.UrlBrowser("", new("REQ_PROJ", "REQ-1")));
        Assert.Null(RequisitoPolarionRegole.UrlBrowser("https://pol.example", new("a/b", "REQ-1")));
    }

    [Fact]
    public void CollegamentiAttivi_EscludeArchiviatiIncompletiEDuplicati()
    {
        var archiviato = Req("R3", "REQ_PROJ", "REQ-3"); archiviato.Archiviata = true;
        var elenco = RequisitoPolarionRegole.CollegamentiAttivi(new[]
        {
            Req("R1", "REQ_PROJ", "REQ-2"), Req("R2", "REQ_PROJ", "REQ-1"), Req("R2b", "req_proj", "req-1"),
            archiviato, Req("R4"), Req("R5", "REQ_PROJ", null)
        });
        Assert.Equal(new[] { "REQ_PROJ/REQ-1", "REQ_PROJ/REQ-2" }, elenco.Select(l => l.ToString()).ToArray());
    }

    [Theory]
    [InlineData("Open", PolarionStatoClasse.Aperto)]
    [InlineData("draft", PolarionStatoClasse.Aperto)]
    [InlineData("In Progress", PolarionStatoClasse.InLavorazione)]
    [InlineData("in review", PolarionStatoClasse.InLavorazione)]
    [InlineData("Closed", PolarionStatoClasse.Chiuso)]
    [InlineData("verified", PolarionStatoClasse.Chiuso)]
    [InlineData("approved", PolarionStatoClasse.Chiuso)]
    [InlineData("", PolarionStatoClasse.NonDisponibile)]
    [InlineData(null, PolarionStatoClasse.NonDisponibile)]
    public void Classificatore_StatiPolarion(string? stato, PolarionStatoClasse atteso) =>
        Assert.Equal(atteso, PolarionStatoClassificatore.Classifica(stato));

    // ---------- persistenza compatibile ----------

    [Fact]
    public void Requisito_SenzaCollegamento_NonScriveCampiNelJson_EDatiVecchiSiLeggonoComeNull()
    {
        var json = JsonSerializer.Serialize(Req("R1"), JsonIo.Options);
        Assert.DoesNotContain("Ticketing", json);
        var vecchio = JsonSerializer.Deserialize<Requisito>("{\"Id\":\"R0\",\"Testo\":\"t\",\"ApplicabileBanco\":true}", JsonIo.Options)!;
        Assert.Null(vecchio.PolarionProgetto);
        Assert.Null(vecchio.PolarionWorkItemId);
        Assert.Null(RequisitoPolarionRegole.Di(vecchio));
    }

    [Fact]
    public void Requisito_ConCollegamento_RoundTripJson_EVuotiDiventanoNull()
    {
        var json = JsonSerializer.Serialize(Req("R1", "REQ_PROJ", "REQ-1"), JsonIo.Options);
        var letto = JsonSerializer.Deserialize<Requisito>(json, JsonIo.Options)!;
        Assert.Equal("REQ_PROJ", letto.PolarionProgetto);
        Assert.Equal("REQ-1", letto.PolarionWorkItemId);
        var vuoti = JsonSerializer.Deserialize<Requisito>("{\"Id\":\"R\",\"PolarionProgetto\":\"  \",\"PolarionWorkItemId\":\"\"}", JsonIo.Options)!;
        Assert.Null(vuoti.PolarionProgetto);
        Assert.Null(vuoti.PolarionWorkItemId);
        Assert.DoesNotContain("Ticketing", JsonSerializer.Serialize(vuoti, JsonIo.Options));
    }

    /// <summary>Un client piu vecchio non conosce i campi: li conserva in ExtensionData e li riscrive senza perderli.</summary>
    private sealed class RequisitoVecchioClient : MasterEntity { public string Id { get; set; } = ""; public string Testo { get; set; } = ""; }

    [Fact]
    public void ClientVecchio_ConservaICampiPolarionInExtensionData()
    {
        var json = JsonSerializer.Serialize(Req("R1", "REQ_PROJ", "REQ-1"), JsonIo.Options);
        var vecchio = JsonSerializer.Deserialize<RequisitoVecchioClient>(json, JsonIo.Options)!;
        Assert.NotNull(vecchio.ExtensionData);
        Assert.True(vecchio.ExtensionData!.ContainsKey("PolarionProgetto"));
        var riscritto = JsonSerializer.Serialize(vecchio, JsonIo.Options);
        var nuovo = JsonSerializer.Deserialize<Requisito>(riscritto, JsonIo.Options)!;
        Assert.Equal(new RequisitoPolarionLink("REQ_PROJ", "REQ-1"), RequisitoPolarionRegole.Di(nuovo));
    }

    [Fact]
    public void Repository_SalvaECaricaIlCollegamento_ESenzaCollegamentoRestaInvariato()
    {
        var state = CreaStato(Req("R1", "REQ_PROJ", "REQ-1"), Req("R2"));
        var caricati = state.Master.Requisiti.Load();
        Assert.Equal("REQ_PROJ/REQ-1", RequisitoPolarionRegole.Di(caricati.Single(r => r.Id == "R1"))!.Value.ToString());
        Assert.Null(RequisitoPolarionRegole.Di(caricati.Single(r => r.Id == "R2")));
    }

    // ---------- Excel ----------

    [Fact]
    public void Export_ScriveColonnaPolarion_EImportRoundTrip()
    {
        var file = Path.Combine(_dir, "export.xlsx");
        Directory.CreateDirectory(_dir);
        RequisitiExcelExportService.Esporta(new[] { Req("R1", "REQ_PROJ", "REQ-1"), Req("R2") }, file);
        using var wb = new XLWorkbook(file);
        var ws = wb.Worksheet(RequisitiExcelExportService.NomeFoglio);
        Assert.Equal("Ticketing", ws.Cell(1, 6).GetString());
        Assert.Equal("REQ_PROJ/REQ-1", ws.Cell(2, 6).GetString());
        Assert.Equal("", ws.Cell(3, 6).GetString());
        // Le prime cinque colonne restano quelle storiche.
        Assert.Equal("Id", ws.Cell(1, 1).GetString());
        Assert.Equal("Veicolo-componente Extra", ws.Cell(1, 5).GetString());

        var state = CreaStato();
        var vm = Importa(state, file);
        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.Equal("REQ_PROJ/REQ-1", RequisitoPolarionRegole.Di(vm.Items.Single(x => x.Id == "R1"))!.Value.ToString());
        Assert.Null(RequisitoPolarionRegole.Di(vm.Items.Single(x => x.Id == "R2")));
    }

    [Fact]
    public void Import_FileSenzaColonnaPolarion_NonTocca_IlCollegamentoEsistente()
    {
        var state = CreaStato(Req("R1", "REQ_PROJ", "REQ-1"));
        var vm = Importa(state, Excel(new[] { "R1", "Nuovo testo", "si", "no" }));
        Assert.False(vm.HaMessaggioErrore);
        Assert.Equal("REQ_PROJ/REQ-1", RequisitoPolarionRegole.Di(vm.Items.Single(x => x.Id == "R1"))!.Value.ToString());
    }

    [Fact]
    public void Import_NonSovrascriveUnCollegamentoEsistente_MaRiempieQuelloMancante()
    {
        var state = CreaStato(Req("R1", "REQ_PROJ", "REQ-1"), Req("R2"));
        var vm = Importa(state, Excel(
            new[] { "R1", "x", "si", "no", "", "REQ_PROJ/REQ-99" },
            new[] { "R2", "x", "si", "no", "", "ALTRO/SYS-5" }));
        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.Equal("REQ_PROJ/REQ-1", RequisitoPolarionRegole.Di(vm.Items.Single(x => x.Id == "R1"))!.Value.ToString());
        Assert.Equal("ALTRO/SYS-5", RequisitoPolarionRegole.Di(vm.Items.Single(x => x.Id == "R2"))!.Value.ToString());
    }

    [Fact]
    public void Import_CollegamentoNonValido_BloccaTuttoSenzaModifiche()
    {
        var state = CreaStato(Req("R1"));
        var vm = Importa(state, Excel(
            new[] { "R5", "ok", "si", "no", "", "REQ_PROJ/REQ-5" },
            new[] { "R6", "ko", "si", "no", "", "REQ_PROJ/non-valido" }));
        Assert.True(vm.HaMessaggioErrore);
        Assert.DoesNotContain(vm.Items, x => x.Id == "R5");
        Assert.DoesNotContain(vm.Items, x => x.Id == "R6");
    }

    // ---------- unione master ----------

    [Fact]
    public void Unisci_ScartatoConCollegamento_LoTrasferisceAllaVoceCorrettaSenzaCollegamento()
    {
        var state = CreaStato(Req("R1", "REQ_PROJ", "REQ-1"), Req("R2"));
        var avvisi = new List<string>();
        MasterUnioneService.Unisci(state, state.Master.Requisiti, "Requisiti", "R1", "R2", r => r.Id, avvisi);
        var rimasto = state.Master.Requisiti.Load().Single(r => r.Id == "R2");
        Assert.Equal("REQ_PROJ/REQ-1", RequisitoPolarionRegole.Di(rimasto)!.Value.ToString());
        Assert.Empty(avvisi);
    }

    [Fact]
    public void Unisci_CollegamentiDiversi_VinceQuelloDellaVoceCorretta_ConAvviso()
    {
        var state = CreaStato(Req("R1", "REQ_PROJ", "REQ-1"), Req("R2", "REQ_PROJ", "REQ-2"));
        var avvisi = new List<string>();
        MasterUnioneService.Unisci(state, state.Master.Requisiti, "Requisiti", "R1", "R2", r => r.Id, avvisi);
        Assert.Equal("REQ_PROJ/REQ-2", RequisitoPolarionRegole.Di(state.Master.Requisiti.Load().Single(r => r.Id == "R2"))!.Value.ToString());
        Assert.Contains(avvisi, a => a.Contains("Ticketing"));
    }
}
