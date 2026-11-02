using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>6.6.0 (import ROLLBACK e retry idempotente): il Salvataggio dei guasti importati scrive gli archivi di TUTTI i
/// requisiti in UNA sola riga del journal Phase D. Un guasto alla riga k del set (fault injection) non lascia archivi
/// parziali nel journal e non cambia nessun archivio; il retry completa il set senza duplicare le voci.</summary>
[Collection("Localization")]
public sealed class DtcImportAtomicoTests : IDisposable
{
    private const int NumeroRequisiti = 60;
    // Oltre il vecchio confine di batch (25): il codice precedente avrebbe gia' pubblicato il primo batch prima del guasto.
    private const int RigaGuasto = 40;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_DtcAtomico_" + Guid.NewGuid().ToString("N"));
    private readonly List<AppState> _stati = new();

    public void Dispose()
    {
        PhaseDMasterRepositoryEngine<DtcArchivioRequisito>.FaultInjectionPerUid = null;
        foreach (var s in _stati) { try { s.Dispose(); } catch { } }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AppState Nuovo()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        _stati.Add(state);
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(TestOperatori.Sigilla(new Operatore { Nome = "Master Atomico", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") }));
        state.Master.Operatori.Save(ops);
        Assert.True(state.ImpostaOperatore("Master Atomico", "1234"));
        return state;
    }

    private static Requisito Extra(string id)
    {
        var r = new Requisito { Id = id, Testo = "t" };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        return r;
    }

    /// <summary>Crea <paramref name="n"/> requisiti Extra senza guasto e li rilegge dal repository (con Uid tecnico assegnato).</summary>
    private static List<Requisito> Crea(AppState s, int n)
    {
        s.Master.Requisiti.Save(Enumerable.Range(1, n).Select(i => Extra($"DA{i:D3}")).ToList());
        return s.Master.Requisiti.Load().Where(r => r.Id.StartsWith("DA", StringComparison.Ordinal))
            .OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    private static Requisito Req(AppState s, string id) => s.Master.Requisiti.Load().Single(r => r.Id == id);

    private static List<DtcRequisito> Voci(int n) =>
        Enumerable.Range(0, n).Select(i => new DtcRequisito { Codice = $"P{i:D4}", Componente = i % 2 == 0 ? "ECM" : "BCM" }).ToList();

    /// <summary>Numero di mutazioni nel journal Phase D del workspace (una riga per batch, molte mutazioni per riga).</summary>
    private static int Mutazioni(AppState s) => PhaseDMasterJournalService.ReadAllMutations(s.Paths.Root).Count;

    /// <summary>Simula un guasto (eccezione) alla <paramref name="riga"/>-esima mutazione elaborata dal motore guasto del workspace.</summary>
    private static void GuastoAllaRiga(AppState s, int riga)
    {
        var radice = s.Paths.Root;
        var contatore = 0;
        PhaseDMasterRepositoryEngine<DtcArchivioRequisito>.FaultInjectionPerUid = (root, _) =>
        {
            if (!string.Equals(root, radice, StringComparison.OrdinalIgnoreCase)) return;
            if (++contatore == riga) throw new IOException($"Guasto simulato alla riga {riga} del set guasto.");
        };
    }

    [Fact]
    public void Scrivi_GuastoAllaRigaK_NonLasciaArchiviParzialiNelJournal()
    {
        var s = Nuovo();
        var reqs = Crea(s, NumeroRequisiti);
        var mutazioniPrima = Mutazioni(s);
        var set = reqs.Select(r => new DtcScrittura(r, Voci(2), null)).ToList();

        GuastoAllaRiga(s, RigaGuasto);
        Assert.Throws<IOException>(() => s.Master.RequisitiDtc.Scrivi(set, "Test"));
        PhaseDMasterRepositoryEngine<DtcArchivioRequisito>.FaultInjectionPerUid = null;

        // Tutto-o-niente: nessuna riga journal aggiunta e nessun archivio visibile, nemmeno i primi requisiti del set.
        Assert.Equal(mutazioniPrima, Mutazioni(s));
        foreach (var r in reqs)
            Assert.Empty(s.Master.RequisitiDtc.Leggi(Req(s, r.Id)).Voci);
    }

    [Fact]
    public void Scrivi_RetryDopoGuasto_CompletaIlSetSenzaDuplicati()
    {
        var s = Nuovo();
        var reqs = Crea(s, NumeroRequisiti);
        var set = reqs.Select(r => new DtcScrittura(r, Voci(2), null)).ToList();

        GuastoAllaRiga(s, RigaGuasto);
        Assert.Throws<IOException>(() => s.Master.RequisitiDtc.Scrivi(set, "Test"));
        PhaseDMasterRepositoryEngine<DtcArchivioRequisito>.FaultInjectionPerUid = null;

        // Retry dello stesso set: una sola mutazione per requisito, nessuna voce duplicata.
        var mutazioniPrimaRetry = Mutazioni(s);
        s.Master.RequisitiDtc.Scrivi(set, "Test");
        Assert.Equal(mutazioniPrimaRetry + NumeroRequisiti, Mutazioni(s));
        foreach (var r in reqs)
            Assert.Equal(2, s.Master.RequisitiDtc.Leggi(Req(s, r.Id)).Voci.Count);

        // Secondo retry: idempotente, il journal non cresce.
        var mutazioniDopoRetry = Mutazioni(s);
        s.Master.RequisitiDtc.Scrivi(set, "Test");
        Assert.Equal(mutazioniDopoRetry, Mutazioni(s));
        foreach (var r in reqs)
            Assert.Equal(2, s.Master.RequisitiDtc.Leggi(Req(s, r.Id)).Voci.Count);
    }

    [Fact]
    public void Scrivi_GuastoAllaRigaK_SuArchiviEsistenti_LasciaOgniArchivioInvariato()
    {
        var s = Nuovo();
        var reqs = Crea(s, NumeroRequisiti);
        s.Master.RequisitiDtc.Scrivi(reqs.Select(r => new DtcScrittura(r, Voci(1), null)).ToList(), "Seed");
        var mutazioniPrima = Mutazioni(s);

        // Aggiornamento: ogni requisito passa da 1 a 2 voci, con la baseline letta all'apertura (come l'editor).
        var set = reqs.Select(r => new DtcScrittura(Req(s, r.Id), Voci(2), s.Master.RequisitiDtc.Leggi(Req(s, r.Id)).Documento)).ToList();
        GuastoAllaRiga(s, RigaGuasto);
        Assert.Throws<IOException>(() => s.Master.RequisitiDtc.Scrivi(set, "Test"));
        PhaseDMasterRepositoryEngine<DtcArchivioRequisito>.FaultInjectionPerUid = null;

        Assert.Equal(mutazioniPrima, Mutazioni(s));
        foreach (var r in reqs)
            Assert.Single(s.Master.RequisitiDtc.Leggi(Req(s, r.Id)).Voci);
    }
}
