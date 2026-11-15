using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Secondo giro: riallineamento riferimenti (NC, export, CR), ricerca globale per identita', storico Audit senza duplicati,
/// numeri REV comuni fissi.</summary>
public sealed class TestRevisionMergeSecondoGiroTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Unspecified);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_RevSecondo_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) { try { Directory.Delete(_root, true); } catch { } }
    }

    private static Esecuzione Rev(int it, int giorno, string op, string note = "", Guid? condizione = null)
    {
        var e = new Esecuzione { Iterazione = it, Data = T0.AddDays(giorno), Operatore = op, Note = note, Matricola = "Matricola" };
        if (condizione.HasValue) e.Condizioni.Add(new Condizione { Uid = condizione.Value, DescrizioneProblema = "PROBLEMA-" + note });
        return e;
    }

    private static byte[] Bytes(Guid uid, params Esecuzione[] rev)
    {
        var tc = new TestCase { Id = "T-SG-1", Uid = uid, Descrizione = "d", CreatoDa = "x", ModificatoDa = "x" };
        tc.Esecuzioni.AddRange(rev);
        return JsonSerializer.SerializeToUtf8Bytes(tc, JsonIo.Options);
    }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_root);
        state.InizializzaCartella(_root);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Master", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    // ---- 1: export NC e avvisi

    [Fact]
    public void Punto1_DopoIlRiallineamento_LExportNcMostraIlNuovoNumeroRev_EGliErroriNonSonoIngoiati()
    {
        using var state = CreaStato();
        var uid = Guid.NewGuid();
        var cond = Guid.NewGuid();
        var unito = new TestCase { Id = "T-SG-1", Uid = uid };
        unito.Esecuzioni.Add(Rev(1, 0, "Anna"));
        unito.Esecuzioni.Add(Rev(2, 3, "Bruno", "loro"));
        unito.Esecuzioni.Add(Rev(3, 5, "Anna", "mia", cond));
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-SG-1", TestCaseId = "T-SG-1", TestUid = uid, EsecuzioneIterazione = 2, CondizioneOrigineUid = cond,
            Owner = "x", Descrizione = "d", DataApertura = DateTime.Now,
            ProveCollegate = { new RiferimentoProva { TestId = "T-SG-1", TestUid = uid, EsecuzioneIterazione = 2, CondizioneUid = cond } }
        });
        var colonna = NonConformitaExportService.Intestazioni.ToList().IndexOf("Iterazione test");

        var avviso = ConflictResolutionService.RiallineaRiferimentiRev(state, unito);

        Assert.Null(avviso);
        var riga = NonConformitaExportService.RigheDati(state.NonConformita.GetAll(), state, default, [unito]).Single();
        Assert.Equal(3, Convert.ToInt32(riga[colonna]));
        // Un errore di riallineamento arriva all'utente come avviso (e nel log), non viene ingoiato.
        Assert.NotNull(ConflictResolutionService.RiallineaRiferimentiRev(null!, unito));
    }

    // ---- 4: CR

    [Fact]
    public void Punto4_RiferimentoCrEsecuzioneValidante_SiRiallineaPerDataDellaRev()
    {
        using var state = CreaStato();
        var uid = Guid.NewGuid();
        var unito = new TestCase { Id = "T-SG-1", Uid = uid };
        unito.Esecuzioni.Add(Rev(1, 0, "Anna"));
        unito.Esecuzioni.Add(Rev(2, 3, "Bruno", "loro"));
        unito.Esecuzioni.Add(Rev(3, 5, "Anna", "mia")); // era la REV 2 prima dell'unione
        var cr = new ChangeRequest
        {
            Id = state.ChangeRequests.NextId(), Titolo = "CR", CreatoDa = "x",
            TestValidazione =
            {
                new CollegamentoValidazioneCr
                {
                    TestId = "T-SG-1", EsecuzioneValidante = 2,
                    SnapshotValidazione = new SnapshotValidazioneCr { EseguitaIl = T0.AddDays(5), Esito = Esito.Conforme }
                }
            }
        };
        state.ChangeRequests.Save(cr, null);

        var avviso = ConflictResolutionService.RiallineaRiferimentiRev(state, unito);

        Assert.Null(avviso);
        var dopo = state.ChangeRequests.GetAll(true).Single(c => c.Id == cr.Id);
        Assert.Equal(3, dopo.TestValidazione.Single().EsecuzioneValidante);
        Assert.Equal(0, TestRevisionReferenceRemap.Riallinea(state, unito).CrAggiornate); // idempotente
    }

    // ---- 2: ricerca globale

    [Theory]
    [InlineData(9)] // numero obsoleto dopo la rinumerazione
    [InlineData(0)] // record storico senza numero
    public void Punto2_RicercaGlobale_TrovaLaSegnalazionePerIdentitaAncheConNumeroRevObsoleto(int numeroSalvato)
    {
        using var state = CreaStato();
        var condizione = new Condizione
        {
            Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Frenata",
            DescrizioneProblema = "TERMINE-IDENTITA distintivo", Esito = Esito.NonConforme
        };
        var test = new TestCase
        {
            Id = "TEST-SG-SEARCH", Veicolo = "Truck A", Veicoli = { "Truck A" }, Componente = { "BCM" },
            Esecuzioni = { new Esecuzione { Iterazione = 3, Condizioni = { condizione } } }
        };
        state.TestCases.Save(test, true);
        state.Segnalazioni.Save(new SegnalazioneDaClassificare
        {
            TestId = test.Id, TestUid = test.Uid, EsecuzioneIterazione = numeroSalvato,
            CondizioneUid = condizione.Uid, Stato = StatoSegnalazione.DaClassificare
        });
        state.RicaricaTutto();
        var vm = new GlobalSearchViewModel(state, _ => { }, debounceMs: 0);

        vm.Query = "TERMINE-IDENTITA";

        var r = Assert.Single(vm.Risultati.Where(x => x.Destinazione == "segnalazione"));
        Assert.Contains("Frenata", r.Sottotitolo);
    }

    // ---- 3: Audit

    private static Guid IdVecchio(string testId, int iter, string req, string componente, string veicolo, DateTime data)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes($"test|{testId}|{iter}|{req}|{componente}|{veicolo}|{data:O}"));
        return new Guid(h.AsSpan(0, 16));
    }

    [Fact]
    public void Punto3_StoricoAudit_DopoRinumerazione_NonDuplicaGliEventiTestGiaSalvati_ENonNePerdeNessuno()
    {
        var data = T0.AddDays(5);
        var rev = Rev(3, 5, "Anna", "mia"); // prima dell'unione era la REV 2
        rev.Dettagli = new DettagliTestSnapshot { Componente = new EcuSelection(new[] { "BCM" }), Veicoli = { "Truck A" } };
        rev.PunteggiRequisiti.Add(new PunteggioRequisito { RequisitoId = "REQ-1", Valore = PunteggioConformita.Conforme });
        var test = new TestCase { Id = "T-SG-1", Uid = Guid.NewGuid() };
        test.Esecuzioni.Add(Rev(1, 0, "Anna"));
        test.Esecuzioni.Add(rev);
        // Evento salvato PRIMA dell'unione con il numero di allora (2) e un altro evento storico indipendente.
        var salvato = new SnapshotValutazioneAudit
        {
            EventId = IdVecchio("T-SG-1", 2, "REQ-1", "BCM", "Truck A", data), Origine = OrigineStoricoValutazioneAudit.Test,
            Punteggio = PunteggioConformita.Conforme, Data = data, Operatore = "Anna", TestCaseId = "T-SG-1", IterazioneTest = 2
        };
        var altro = new SnapshotValutazioneAudit
        {
            EventId = Guid.NewGuid(), Origine = OrigineStoricoValutazioneAudit.Manuale, Punteggio = PunteggioConformita.Conforme,
            Data = T0.AddDays(9), Operatore = "Master"
        };
        var risoluzione = new RisoluzioneConflittoAudit
        {
            Componente = "BCM", RequisitoId = "REQ-1", StoricoPunteggiManuali = { salvato, altro }
        };

        var storico = ValutazioneRequisitoEcuVeicoloService.CostruisciStoricoCella("REQ-1", "BCM", "Truck A", [test], [], [risoluzione]);

        Assert.Single(storico, e => e.Origine == OrigineStoricoValutazioneAudit.Test);
        Assert.Equal(3, storico.Single(e => e.Origine == OrigineStoricoValutazioneAudit.Test).IterazioneTest); // vince la ricostruzione attuale
        Assert.Contains(storico, e => e.EventId == altro.EventId);
        Assert.Equal(2, storico.Count);
    }

    // ---- 5: REV comuni fisse

    [Fact]
    public void Punto5_RevComuniMantengonoIlNumero_ConDateNonMonotone_ELeNuoveSeguono()
    {
        var uid = Guid.NewGuid();
        // Le comuni hanno date non monotone (REV 2 piu' vecchia della REV 1): l'ordinamento per data le rinumererebbe.
        Esecuzione[] comuni = [Rev(1, 5, "Anna", "c1"), Rev(2, 2, "Anna", "c2"), Rev(3, 7, "Anna", "c3")];
        var mia = Bytes(uid, [.. comuni, Rev(4, 9, "Anna", "mia")]);
        var altra = Bytes(uid, [.. comuni, Rev(4, 1, "Bruno", "loro-anteriore")]); // piu' vecchia di tutte le comuni

        var plan = RecordMergeEngine.Prepare(RecordMergeKind.TestCase, mia, altra).Plan!;
        var rev = ((TestCase)plan.Build(new RecordMergeChoices(), "M").Model!).Esecuzioni;

        Assert.Equal(new[] { "c1", "c2", "c3", "loro-anteriore", "mia" }, rev.Select(r => r.Note));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, rev.Select(r => r.Iterazione));
        Assert.Equal(1, plan.RevisioniUnite!.MappaCopiaMia[1]); // comuni invariate nelle mappe
        Assert.Equal(3, plan.RevisioniUnite.MappaAltraCopia[3]);
        Assert.Contains("data anteriore", plan.Nota);
    }
}
