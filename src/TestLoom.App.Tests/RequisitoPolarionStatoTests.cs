using System.Net;
using System.Net.Http;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Stato Ticketing dei requisiti: builder puro, cache in memoria, lettura non TICKET e sincronizzazione in background.</summary>
[Collection("Localization")]
public sealed class RequisitoPolarionStatoTests : IDisposable
{
    private const string ApiRoot = "https://example.invalid/ticketing/rest/v1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_ReqPolStato_" + Guid.NewGuid().ToString("N"));
    private readonly List<AppState> _stati = new();
    private readonly Action<Action> _postatoreOriginale = RequisitoPolarionCache.Postatore;

    public RequisitoPolarionStatoTests() { RequisitoPolarionCache.Postatore = a => a(); }

    public void Dispose()
    {
        RequisitoPolarionCache.Postatore = _postatoreOriginale;
        foreach (var s in _stati) { try { s.Dispose(); } catch { } }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static Requisito Req(string id, string? progetto = null, string? wi = null, bool archiviato = false) =>
        new() { Id = id, Testo = "t", Archiviata = archiviato, PolarionProgetto = progetto, PolarionWorkItemId = wi };

    private static PolarionTicketSnapshot Snap(string progetto, string id, string? stato, string? risoluzione = null,
        DateTimeOffset? letto = null, int commenti = 0) => new()
    {
        ProjectId = progetto, Id = id, Status = stato, Resolution = risoluzione, Title = "Titolo " + id,
        Updated = DateTimeOffset.Parse("2026-10-01T10:00:00Z"),
        RetrievedAtUtc = letto ?? DateTimeOffset.UtcNow.AddMinutes(-5),
        Comments = Enumerable.Range(0, commenti).Select(i => new PolarionCommentSnapshot { Id = $"{progetto}/{id}/{i}", Text = "c" + i }).ToList()
    };

    private DataPaths Paths()
    {
        Directory.CreateDirectory(_root);
        var p = new DataPaths(_root);
        p.EnsureStructure();
        return p;
    }

    // ---------- builder ----------

    [Fact]
    public void Builder_SenzaCollegamento_NonCollegato_EnessunaLetturaDellaCache()
    {
        var letture = 0;
        var stato = RequisitoPolarionStatoBuilder.Costruisci(Req("R1"), _ => true, (_, _) => { letture++; return null; }, DateTimeOffset.UtcNow);
        Assert.False(stato.Collegato);
        Assert.Equal(PolarionStatoClasse.NonCollegato, stato.Classe);
        Assert.Equal(0, letture);
        // Collegamento incompleto = nessun collegamento.
        Assert.Equal(PolarionStatoClasse.NonCollegato,
            RequisitoPolarionStatoBuilder.Costruisci(Req("R2", "REQ_PROJ", null), _ => true, (_, _) => null, DateTimeOffset.UtcNow).Classe);
    }

    [Fact]
    public void Builder_ProgettoNonConfigurato_NonLeggeLaCache_EsegnalaIlMotivo()
    {
        var letture = 0;
        var stato = RequisitoPolarionStatoBuilder.Costruisci(Req("R1", "REQ_PROJ", "REQ-1"), _ => false, (_, _) => { letture++; return null; }, DateTimeOffset.UtcNow);
        Assert.Equal(PolarionStatoClasse.NonDisponibile, stato.Classe);
        Assert.True(stato.ProgettoNonConfigurato);
        Assert.Equal(0, letture);
        Assert.NotEqual("", stato.TestoBreve(LocalizationService.Instance));
    }

    [Fact]
    public void Builder_NonSincronizzatoOCacheIllegibile_NonDisponibile_SenzaCrash()
    {
        var nonSincronizzato = RequisitoPolarionStatoBuilder.Costruisci(Req("R1", "REQ_PROJ", "REQ-1"), _ => true, (_, _) => null, DateTimeOffset.UtcNow);
        Assert.Equal(PolarionStatoClasse.NonDisponibile, nonSincronizzato.Classe);
        Assert.False(nonSincronizzato.ProgettoNonConfigurato);
        foreach (Exception ex in new Exception[] { new IOException(), new InvalidDataException(), new UnauthorizedAccessException() })
        {
            var s = RequisitoPolarionStatoBuilder.Costruisci(Req("R1", "REQ_PROJ", "REQ-1"), _ => true, (_, _) => throw ex, DateTimeOffset.UtcNow);
            Assert.Equal(PolarionStatoClasse.NonDisponibile, s.Classe);
        }
    }

    [Fact]
    public void Builder_ConSnapshot_ClasseStatoRisoluzioneCommentiEObsoleto()
    {
        var ora = DateTimeOffset.Parse("2026-11-01T12:00:00Z");
        var fresco = RequisitoPolarionStatoBuilder.Costruisci(Req("R1", "REQ_PROJ", "REQ-1"), _ => true,
            (_, _) => Snap("REQ_PROJ", "REQ-1", "in_progress", "none", ora.AddHours(-1), 3), ora);
        Assert.Equal(PolarionStatoClasse.InLavorazione, fresco.Classe);
        Assert.Equal("in_progress", fresco.Stato);
        Assert.Equal(3, fresco.Commenti);
        Assert.False(fresco.Obsoleto);
        Assert.Equal("in_progress / none", fresco.TestoBreve(LocalizationService.Instance));

        var vecchio = RequisitoPolarionStatoBuilder.Costruisci(Req("R1", "REQ_PROJ", "REQ-1"), _ => true,
            (_, _) => Snap("REQ_PROJ", "REQ-1", "closed", "closed", ora.AddHours(-30)), ora);
        Assert.Equal(PolarionStatoClasse.Chiuso, vecchio.Classe);
        Assert.True(vecchio.Obsoleto);
        Assert.StartsWith("⚠", vecchio.TestoBreve(LocalizationService.Instance));
    }

    // ---------- cache in memoria ----------

    [Fact]
    public void Cache_NonCaricata_TryGetEIdPerClassiRestituisconoNull()
    {
        var paths = Paths();
        Assert.False(RequisitoPolarionCache.Caricata(paths));
        Assert.Null(RequisitoPolarionCache.TryGet(paths, "R1"));
        Assert.Null(RequisitoPolarionCache.IdPerClassi(paths, new[] { PolarionStatoClasse.Chiuso }));
        Assert.Null(RequisitoPolarionCache.TryGet(null, "R1"));
    }

    [Fact]
    public void Cache_Ricostruisci_LeggeSoloLaCacheCondivisa_EClassificaTuttiIRequisiti()
    {
        var paths = Paths();
        var cfg = PolarionApiSettingsService.Load(paths);
        cfg.Progetti = new() { new() { Id = "REQ_PROJ", Nome = "Req" } };
        PolarionApiSettingsService.Save(paths, cfg, true);
        var store = new PolarionSnapshotStore(paths);
        store.SaveIfNewer(Snap("REQ_PROJ", "REQ-1", "open"));
        store.SaveIfNewer(Snap("REQ_PROJ", "REQ-2", "closed", "done"));
        // File di cache corrotto per REQ-3: il requisito risulta non disponibile, gli altri si caricano comunque.
        Directory.CreateDirectory(Path.Combine(paths.PolarionSnapshotDir, "REQ_PROJ"));
        File.WriteAllText(Path.Combine(paths.PolarionSnapshotDir, "REQ_PROJ", "REQ-3.json"), "{non json");

        var elenco = new List<Requisito>
        {
            Req("A", "REQ_PROJ", "REQ-1"), Req("B", "REQ_PROJ", "REQ-2"), Req("C", "REQ_PROJ", "REQ-3"),
            Req("D"), Req("E", "ALTRO", "SYS-1"), Req("F", "REQ_PROJ", "REQ-1")
        };
        var eventi = 0;
        void Conta() => eventi++;
        RequisitoPolarionCache.Aggiornato += Conta;
        try { RequisitoPolarionCache.Ricostruisci(paths, () => elenco); }
        finally { RequisitoPolarionCache.Aggiornato -= Conta; }

        Assert.Equal(1, eventi);
        Assert.Equal(PolarionStatoClasse.Aperto, RequisitoPolarionCache.TryGet(paths, "A")!.Classe);
        Assert.Equal(PolarionStatoClasse.Chiuso, RequisitoPolarionCache.TryGet(paths, "B")!.Classe);
        Assert.Equal(PolarionStatoClasse.NonDisponibile, RequisitoPolarionCache.TryGet(paths, "C")!.Classe);
        Assert.Equal(PolarionStatoClasse.NonCollegato, RequisitoPolarionCache.TryGet(paths, "D")!.Classe);
        var e = RequisitoPolarionCache.TryGet(paths, "E")!;
        Assert.Equal(PolarionStatoClasse.NonDisponibile, e.Classe);
        Assert.True(e.ProgettoNonConfigurato);
        Assert.Equal(PolarionStatoClasse.Aperto, RequisitoPolarionCache.TryGet(paths, "F")!.Classe);

        Assert.Equal(new[] { "A", "F" }, RequisitoPolarionCache.IdPerClassi(paths, new[] { PolarionStatoClasse.Aperto })!.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "D" }, RequisitoPolarionCache.IdPerClassi(paths, new[] { PolarionStatoClasse.NonCollegato })!.ToArray());
        Assert.Equal(new[] { "C", "E" }, RequisitoPolarionCache.IdPerClassi(paths, new[] { PolarionStatoClasse.NonDisponibile })!.OrderBy(x => x).ToArray());

        RequisitoPolarionCache.Invalida(paths);
        Assert.False(RequisitoPolarionCache.Caricata(paths));
    }

    // ---------- client: lettura non TICKET ----------

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Chiamate { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Chiamate) Chiamate.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string RequisitoJson =
        """{"data":{"id":"REQ_PROJ/REQ-5","type":"workitems","attributes":{"type":"requirement","title":"Il sistema deve","status":"approved","resolution":"done","updated":"2026-10-29T10:30:00Z"},"relationships":{"linkedWorkItems":{"data":[{"type":"workitems","id":"REQ_PROJ/REQ-5/parent/REQ_PROJ/REQ-1"}],"meta":{"totalCount":250}}}}}""";

    [Fact]
    public async Task Client_GetRequirement_NonPretendeRelazioniComplete_NeTipoEsta_EusaSoloGet()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal)
            ? Json("""{"data":[{"id":"REQ_PROJ/REQ-5/1","attributes":{"text":{"type":"text/html","value":"nota"},"created":"2026-10-29T10:30:00Z"}}]}""")
            : Json(RequisitoJson));
        var client = new PolarionReadClient(new HttpClient(handler), new Uri(ApiRoot), "fake-test-token");

        var snap = await client.GetRequirementAsync("REQ_PROJ", "REQ-5");
        Assert.Equal("REQ_PROJ", snap.ProjectId);
        Assert.Equal("REQ-5", snap.Id);
        Assert.Equal("approved", snap.Status);
        Assert.Equal("done", snap.Resolution);
        Assert.Equal("nota", Assert.Single(snap.Comments).Text);
        Assert.Empty(snap.LinkedWorkItems);
        Assert.All(handler.Chiamate, c => Assert.StartsWith("GET ", c));

        // La lettura TICKET resta rigorosa: relazione troncata = errore (nessuna regressione).
        var rigido = new PolarionReadClient(new HttpClient(new StubHandler(_ => Json(RequisitoJson))), new Uri(ApiRoot), "fake-test-token");
        await Assert.ThrowsAsync<InvalidDataException>(() => rigido.GetTicketAsync("REQ_PROJ", "REQ-5"));
    }

    [Fact]
    public async Task Client_GetRequirement_ErroriParzialiOIdNonValido_Rifiutati()
    {
        var parziale = new PolarionReadClient(new HttpClient(new StubHandler(_ => Json(
            """{"data":{"id":"REQ_PROJ/REQ-5","attributes":{"status":"open"}},"meta":{"errors":[{"status":"403"}]}}"""))), new Uri(ApiRoot), "fake-test-token");
        await Assert.ThrowsAsync<InvalidDataException>(() => parziale.GetRequirementAsync("REQ_PROJ", "REQ-5"));
        var client = new PolarionReadClient(new HttpClient(new StubHandler(_ => Json("{}"))), new Uri(ApiRoot), "fake-test-token");
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetRequirementAsync("../x", "REQ-5"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetRequirementAsync("REQ_PROJ", "REQ5"));
    }

    // ---------- sincronizzazione in background ----------

    [Fact]
    public void CollectRequirementLinks_SoloProgettiConfigurati_AttiviEsenzaDuplicatiEsta()
    {
        var settings = new PolarionApiSettings { ProjectId = "TICKET_Tickets", Progetti = new() { new() { Id = "REQ_PROJ", Nome = "n" } } };
        var links = PolarionBackgroundSyncService.CollectRequirementLinks(new[]
        {
            Req("A", "REQ_PROJ", "REQ-1"), Req("B", "ALTRO", "SYS-1"), Req("C", "REQ_PROJ", "REQ-2", archiviato: true),
            Req("D", "TICKET_Tickets", "TICKET-5"), Req("E", "TICKET_Tickets", "TICKET-6"), Req("F")
        }, settings, new[] { "TICKET-5" });
        Assert.Equal(new[] { "TICKET_Tickets/TICKET-6", "REQ_PROJ/REQ-1" }, links.Select(l => l.ToString()).OrderBy(x => x).ToArray());
    }

    private AppState CreaStatoMaster(Action<PolarionApiSettings> configura, params Requisito[] requisiti)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_root);
        state.InizializzaCartella(_root);
        _stati.Add(state);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Sync", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save([master]);
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        var cfg = PolarionApiSettingsService.Load(state.Paths);
        cfg.ApiRoot = ApiRoot; cfg.LetturaAbilitata = true; cfg.ImpostaToken("synthetic-token");
        configura(cfg);
        PolarionApiSettingsService.Save(state.Paths, cfg, true);
        state.Master.Requisiti.Save(requisiti.ToList());
        return state;
    }

    [Fact]
    public async Task Sync_LeggeIRequisitiCollegati_NelProgettoConfigurato_EscriveLaCacheCondivisa()
    {
        var state = CreaStatoMaster(c => c.Progetti = new() { new() { Id = "REQ_PROJ", Nome = "Req" } },
            Req("R1", "REQ_PROJ", "REQ-5"), Req("R2", "ALTRO", "SYS-1"), Req("R3"));
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal)
            ? Json("""{"data":[]}""") : Json(RequisitoJson));
        using var sync = new PolarionBackgroundSyncService(state, httpFactory: () => new HttpClient(handler));

        var result = await sync.RefreshOnceAsync(force: true);

        Assert.Equal(PolarionSyncStatus.Success, result.Status);
        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Refreshed);
        // Il progetto non configurato non genera alcuna richiesta di rete.
        Assert.DoesNotContain(handler.Chiamate, c => c.Contains("ALTRO", StringComparison.OrdinalIgnoreCase));
        Assert.All(handler.Chiamate, c => Assert.StartsWith("GET ", c));
        var snap = new PolarionSnapshotStore(state.Paths).Load("REQ_PROJ", "REQ-5");
        Assert.Equal("approved", snap!.Status);

        // La lista usa la cache condivisa appena scritta.
        RequisitoPolarionCache.Ricostruisci(state.Paths, () => state.Master.Requisiti.Load());
        Assert.Equal(PolarionStatoClasse.Chiuso, RequisitoPolarionCache.TryGet(state.Paths, "R1")!.Classe);
        Assert.Equal(PolarionStatoClasse.NonDisponibile, RequisitoPolarionCache.TryGet(state.Paths, "R2")!.Classe);
        Assert.Equal(PolarionStatoClasse.NonCollegato, RequisitoPolarionCache.TryGet(state.Paths, "R3")!.Classe);
    }

    [Fact]
    public async Task Sync_RequisitoInesistente404_NonFermaIlPassoNeLanciaEccezioni()
    {
        var state = CreaStatoMaster(c => c.Progetti = new() { new() { Id = "REQ_PROJ", Nome = "Req" } },
            Req("R1", "REQ_PROJ", "REQ-404"), Req("R2", "REQ_PROJ", "REQ-5"));
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("REQ-404", StringComparison.Ordinal) ? Json("{}", HttpStatusCode.NotFound)
            : req.RequestUri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal) ? Json("""{"data":[]}""") : Json(RequisitoJson));
        using var sync = new PolarionBackgroundSyncService(state, httpFactory: () => new HttpClient(handler));

        var result = await sync.RefreshOnceAsync(force: true);

        Assert.Equal(PolarionSyncStatus.PartialFailure, result.Status);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Refreshed);
        Assert.NotNull(new PolarionSnapshotStore(state.Paths).Load("REQ_PROJ", "REQ-5"));
    }

    [Fact]
    public async Task Sync_SenzaRequisitiNeTicketConfigurati_NoTickets_EnessunaRichiesta()
    {
        var state = CreaStatoMaster(c => { }, Req("R1"), Req("R2", "REQ_PROJ", "REQ-5"));   // REQ_PROJ non configurato
        var handler = new StubHandler(_ => Json("{}"));
        using var sync = new PolarionBackgroundSyncService(state, httpFactory: () => new HttpClient(handler));
        var result = await sync.RefreshOnceAsync(force: true);
        Assert.Equal(PolarionSyncStatus.NoTickets, result.Status);
        Assert.Empty(handler.Chiamate);
    }
}
