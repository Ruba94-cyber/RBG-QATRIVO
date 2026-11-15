using System.Net;
using System.Net.Http;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Badge, filtro per stato Ticketing dei requisiti nella lista test e gestione dei progetti nelle Impostazioni.</summary>
[Collection("Localization")]
public sealed class RequisitoPolarionUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_ReqPolUi_" + Guid.NewGuid().ToString("N"));
    private readonly AppState _state;
    private readonly Action<Action> _postatore = RequisitoPolarionCache.Postatore;

    public RequisitoPolarionUiTests()
    {
        RequisitoPolarionCache.Postatore = a => a();
        Directory.CreateDirectory(_root);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_root);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Ui", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        _state.Master.Operatori.Save([master]);
        Assert.True(_state.ImpostaOperatore(master.Nome, "1234"));
    }

    public void Dispose()
    {
        RequisitoPolarionCache.Postatore = _postatore;
        try { _state.Dispose(); } catch { }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static Requisito Req(string id, string? p = null, string? wi = null) => new() { Id = id, Testo = "t", PolarionProgetto = p, PolarionWorkItemId = wi };

    private void Prepara(params (Requisito R, string? Stato)[] voci)
    {
        var cfg = PolarionApiSettingsService.Load(_state.Paths);
        cfg.Progetti = new() { new() { Id = "REQ_PROJ", Nome = "Req" } };
        PolarionApiSettingsService.Save(_state.Paths, cfg, true);
        var store = new PolarionSnapshotStore(_state.Paths);
        foreach (var (r, stato) in voci)
            if (stato != null && r.PolarionWorkItemId != null)
                store.SaveIfNewer(new PolarionTicketSnapshot
                {
                    ProjectId = r.PolarionProgetto!, Id = r.PolarionWorkItemId, Status = stato,
                    Updated = DateTimeOffset.UtcNow.AddDays(-1), RetrievedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3)
                });
        RequisitoPolarionCache.Ricostruisci(_state.Paths, () => voci.Select(v => v.R).ToList());
    }

    // ---------- badge ----------

    [Fact]
    public void Badge_SoloPerRequisitiCollegati_ConColoreEdStato_EmaxQuattroPerRiga()
    {
        Prepara((Req("A", "REQ_PROJ", "REQ-1"), "open"), (Req("B", "REQ_PROJ", "REQ-2"), "closed"), (Req("C"), null),
                (Req("D", "REQ_PROJ", "REQ-4"), "in_progress"), (Req("E", "REQ_PROJ", "REQ-5"), "open"), (Req("F", "REQ_PROJ", "REQ-6"), "open"));
        var tc = new TestCase { RequisitoId = new() { "A", "B", "C", "D", "E", "F" } };
        var risultato = (List<RequisitoBadge>)RequisitoPolarionBadgeConverter.Instance.Convert(new object?[] { tc, _state.Paths, 0 }, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture)!;
        Assert.Equal(new[] { "A", "B", "D", "E" }, risultato.Select(b => b.Id).ToArray());   // C non collegato: nessun badge; tetto 4
        Assert.NotEqual(risultato[0].Pennello, risultato[1].Pennello);
        Assert.Contains("open", risultato[0].Testo);
    }

    [Fact]
    public void Badge_TestSenzaRequisitiCollegatiOCacheNonPronta_ListaVuota_SenzaEccezioni()
    {
        RequisitoPolarionCache.Invalida(_state.Paths);
        var tc = new TestCase { RequisitoId = new() { "A" } };
        var vuoto = (List<RequisitoBadge>)RequisitoPolarionBadgeConverter.Instance.Convert(new object?[] { tc, _state.Paths, 0 }, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture)!;
        Assert.Empty(vuoto);
        var senzaPaths = (List<RequisitoBadge>)RequisitoPolarionBadgeConverter.Instance.Convert(new object?[] { tc, null, 0 }, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture)!;
        Assert.Empty(senzaPaths);
        Prepara((Req("A"), null));
        Assert.Empty((List<RequisitoBadge>)RequisitoPolarionBadgeConverter.Instance.Convert(new object?[] { tc, _state.Paths, 0 }, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture)!);
        Assert.Equal(false, RequisitoPolarionBadgeConverter.Instance.Convert(new object?[] { "A", _state.Paths, 0 }, typeof(bool), "visibile", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Badge_CollegatoMaSenzaSnapshot_MostraNonDisponibile()
    {
        Prepara((Req("A", "REQ_PROJ", "REQ-1"), null));
        var b = RequisitoPolarionBadgeConverter.Crea(_state.Paths, "A");
        Assert.NotNull(b);
        Assert.Contains(LocalizationService.Instance.Scegli("non disponibile", "not available", "non disponible"), b!.Testo);
    }

    [Fact]
    public void CellaCatalogoRequisiti_StatoVuotoSpiegaCosaFare_EColonnaCompatta()
    {
        Prepara((Req("A", "REQ_PROJ", "REQ-1"), "open"), (Req("B"), null));
        string Testo(Requisito r, bool inModifica) => (string)RequisitoPolarionBadgeConverter.Instance.Convert(
            new object?[] { r, _state.Paths, 0, inModifica }, typeof(string), "testo", System.Globalization.CultureInfo.InvariantCulture)!;
        Assert.Equal(LocalizationService.Instance.Scegli("Collega…", "Link…", "Lier…"), Testo(Req("B"), true));
        Assert.Equal(LocalizationService.Instance.Scegli("Non collegato", "Not linked", "Non lié"), Testo(Req("B"), false));
        Assert.Equal("open", Testo(Req("A", "REQ_PROJ", "REQ-1"), false));   // solo lo stato: badge compatto
        // La colonna non sottrae spazio a "Testo requisito": larghezza al contenuto con tetto.
        var xaml = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestLoom.App", "Views", "RequisitiView.axaml")));
        Assert.Contains("Header=\"Ticketing\" Width=\"Auto\" MinWidth=\"96\"", xaml);
    }

    // ---------- filtro ----------

    private static void Sel(FiltroMultiSelect f, string valore)
    {
        f.DeselezionaTutto();
        f.Opzioni.Single(o => o.Valore == valore).Selezionato = true;
    }

    [Fact]
    public void Filtro_StatoPolarioneRequisito_RestringeIRequisitiDellaQuery()
    {
        Prepara((Req("A", "REQ_PROJ", "REQ-1"), "open"), (Req("B", "REQ_PROJ", "REQ-2"), "closed"), (Req("C"), null));
        var vm = new TestCaseListViewModel(_state, _ => { });
        Assert.Null(vm.RequisitiConFiltroPolarion(null));                 // nessun filtro: invariato
        Sel(vm.FiltroStatoPolarionRequisito, nameof(PolarionStatoClasse.Chiuso));
        Assert.Equal(new[] { "B" }, vm.RequisitiConFiltroPolarion(null));
        // Intersezione con il filtro per requisito esplicito.
        Assert.Equal(new[] { "B" }, vm.RequisitiConFiltroPolarion(new List<string> { "A", "B" }));
        Assert.Single(vm.RequisitiConFiltroPolarion(new List<string> { "A" })!);   // nessuna corrispondenza = sentinella (zero risultati)
        Assert.DoesNotContain("A", vm.RequisitiConFiltroPolarion(new List<string> { "A" })!);

        Sel(vm.FiltroStatoPolarionRequisito, nameof(PolarionStatoClasse.NonCollegato));
        Assert.Equal(new[] { "C" }, vm.RequisitiConFiltroPolarion(null));
    }

    [Fact]
    public void Filtro_ConCacheNonPronta_NonMostraRisultatiErrati_ERichiedeIlCaricamento()
    {
        RequisitoPolarionCache.Invalida(_state.Paths);
        var vm = new TestCaseListViewModel(_state, _ => { });
        Sel(vm.FiltroStatoPolarionRequisito, nameof(PolarionStatoClasse.Aperto));
        var r = vm.RequisitiConFiltroPolarion(null);
        Assert.Single(r!);
        Assert.StartsWith("\u0001", r![0]);
    }

    // ---------- impostazioni: progetti e sonda ----------

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public List<string> Url { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { lock (Url) Url.Add(request.RequestUri!.AbsolutePath); return Task.FromResult(f(request)); }
    }

    private void SalvaConfigBase()
    {
        var cfg = PolarionApiSettingsService.Load(_state.Paths);
        cfg.ApiRoot = "https://example.invalid/ticketing/rest/v1"; cfg.LetturaAbilitata = true; cfg.ImpostaToken("synthetic");
        PolarionApiSettingsService.Save(_state.Paths, cfg, true);
    }

    [Fact]
    public void Impostazioni_AggiungiSalvaERicarica_ProgettiConNome_ESenzaIdValidoNonSalva()
    {
        SalvaConfigBase();
        using var vm = new ImpostazioniViewModel(_state, () => { });
        vm.AggiungiProgettoPolarionCommand.Execute(null);
        var riga = vm.PolarionProgetti.Single();
        riga.Id = "1non valido";
        Assert.True(riga.HaErroreId);
        Assert.False(riga.PuoVerificare);
        Assert.True(vm.PolarionApiHasUnsavedChanges);
        vm.SalvaConfigurazionePolarionApiCommand.Execute(null);
        Assert.True(vm.HasPolarionApiError);
        Assert.Null(PolarionApiSettingsService.Load(_state.Paths).Progetti);

        riga.Id = "REQ_PROJ"; riga.Nome = "Requisiti";
        Assert.True(riga.PuoVerificare);
        vm.SalvaConfigurazionePolarionApiCommand.Execute(null);
        Assert.True(vm.HasPolarionApiSuccess, vm.PolarionApiFeedback);
        Assert.False(vm.PolarionApiHasUnsavedChanges);
        Assert.Equal("REQ_PROJ", Assert.Single(PolarionApiSettingsService.Load(_state.Paths).Progetti!).Id);

        using var vm2 = new ImpostazioniViewModel(_state, () => { });
        Assert.Equal("Requisiti", vm2.PolarionProgetti.Single().Nome);
        vm2.RimuoviProgettoPolarionCommand.Execute(vm2.PolarionProgetti.Single());
        Assert.True(vm2.PolarionApiHasUnsavedChanges);
        vm2.SalvaConfigurazionePolarionApiCommand.Execute(null);
        Assert.Null(PolarionApiSettingsService.Load(_state.Paths).Progetti);
        // L'TICKET predefinito non e' mai toccato.
        Assert.Equal("TICKET_Tickets", PolarionApiSettingsService.Load(_state.Paths).ProjectId);
    }

    [Fact]
    public async Task Impostazioni_SondaProgetto_SuccessoEFallimento401_ESoloLettura()
    {
        SalvaConfigBase();
        using var vm = new ImpostazioniViewModel(_state, () => { });
        var handler = new Handler(r => r.RequestUri!.AbsolutePath.Contains("/projects/OK_PROJ/")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        vm.PolarionHttpFactory = () => new HttpClient(handler);
        vm.AggiungiProgettoPolarionCommand.Execute(null);
        vm.AggiungiProgettoPolarionCommand.Execute(null);
        vm.PolarionProgetti[0].Id = "OK_PROJ";
        vm.PolarionProgetti[1].Id = "KO_PROJ";

        await vm.VerificaProgettoPolarionCommand.ExecuteAsync(vm.PolarionProgetti[0]);
        await vm.VerificaProgettoPolarionCommand.ExecuteAsync(vm.PolarionProgetti[1]);

        Assert.False(vm.PolarionProgetti[0].EsitoErrore);
        Assert.NotEqual("", vm.PolarionProgetti[0].EsitoVerifica);
        Assert.True(vm.PolarionProgetti[1].EsitoErrore);
        Assert.Contains("401", vm.PolarionProgetti[1].EsitoVerifica);
        Assert.Equal(2, handler.Url.Count);
    }

    [Fact]
    public async Task Impostazioni_SondaSenzaConfigurazioneSalvata_NonContattaLaRete()
    {
        using var vm = new ImpostazioniViewModel(_state, () => { });
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        vm.PolarionHttpFactory = () => new HttpClient(handler);
        vm.AggiungiProgettoPolarionCommand.Execute(null);
        vm.PolarionProgetti[0].Id = "REQ_PROJ";
        await vm.VerificaProgettoPolarionCommand.ExecuteAsync(vm.PolarionProgetti[0]);
        Assert.True(vm.PolarionProgetti[0].EsitoErrore);
        Assert.Empty(handler.Url);
    }
}
