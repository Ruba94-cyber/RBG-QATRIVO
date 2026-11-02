using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>TIME (6.6.1), review finding 2: filtri Home "Tempo operazione" e "Tempo totale Test". La query SQL sulla proiezione (schema 15)
/// e il percorso in memoria (DashboardFilter) danno gli stessi Test; una durata vuota non corrisponde mai; un valore diverso non riusa
/// una cache; una modifica della durata cambia il conteggio.</summary>
public sealed class HomeFiltroTempoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_HomeTempo_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Giorno = new(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static Condizione Op(string funzionalita, Esito esito, int? minuti, Guid? uid = null, Guid? origine = null, string guasto = "") => new()
    {
        Uid = uid ?? Guid.NewGuid(),
        Funzionalita = funzionalita,
        Esito = esito,
        Componente = new EcuSelection(["BCM"]),
        DurataMinuti = minuti,
        OperazioneOrigineUid = origine,
        Guasto = guasto,
    };

    private static Esecuzione Rev(int iterazione, params Condizione[] condizioni) => new()
    {
        Iterazione = iterazione,
        Data = Giorno.AddDays(iterazione),
        Operatore = "Mario",
        Esito = Esito.Conforme,
        Condizioni = [.. condizioni],
    };

    private static TestCase Test(string id, params Esecuzione[] revisioni)
    {
        var tc = new TestCase
        {
            Id = id, Uid = Guid.NewGuid(), DataCreazione = Giorno, UltimaModifica = Giorno,
            Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily"], Veicolo = "Daily", Funzionalita = ["F1", "F2"],
        };
        tc.Esecuzioni.AddRange(revisioni);
        return tc;
    }

    /// <summary>Dataset di prova. A: 30 + 5 = 35. B: la stessa operazione passa da 10 a 40 (ultima revisione): 40. C: nessuna durata nota.
    /// D: snapshot importato da UnitLogger con 99 (non conta). E: ripetizione nella stessa revisione (50 e 7): entrambe eseguite, totale 57.
    /// F: 3 minuti piu' una guasto da 60: 63 (la guasto conta nel totale e nel filtro).</summary>
    private static (TestCase A, TestCase B, TestCase C, TestCase D, TestCase E, TestCase F) Dataset()
    {
        var a = Test("A", Rev(1, Op("F1", Esito.Conforme, 30), Op("F2", Esito.Conforme, 5)));
        var uidB = Guid.NewGuid();
        var b = Test("B", Rev(1, Op("F1", Esito.Conforme, 10, uidB)), Rev(2, Op("F1", Esito.Conforme, 40, uidB)));
        var c = Test("C", Rev(1, Op("F1", Esito.Conforme, null)));
        var d = Test("D", Rev(1, Op("F1", Esito.Conforme, 99)));
        d.Origine = OrigineTest.ImportStatoUdt;
        var primaE = Op("F1", Esito.NonConforme, 50);
        var e = Test("E", Rev(1, primaE, Op("F1", Esito.Conforme, 7, origine: primaE.Uid)));
        var f = Test("F", Rev(1, Op("F1", Esito.Conforme, 3), Op("", Esito.Conforme, 60, guasto: "P0100")));
        return (a, b, c, d, e, f);
    }

    private static LocalTestIndexStore Indice(string nome, IEnumerable<TestCase> test)
    {
        var store = new LocalTestIndexStore(Path.Combine(nome, "data"), Path.Combine(nome, "cache"));
        foreach (var tc in test) store.Upsert(Path.Combine(nome, tc.Id + ".json"), Giorno, 100, tc);
        return store;
    }

    private static DashboardFilter Filtro(OperatoreDurata? operazione = null, OperatoreDurata? totale = null, int minuti = 0, int minutiTotale = 0) => new()
    {
        TempoOperazione = operazione is { } o ? new FiltroDurataMinuti(o, minuti) : null,
        TempoTotaleTest = totale is { } t ? new FiltroDurataMinuti(t, minutiTotale) : null,
    };

    /// <summary>Numero di Test dello snapshot Home (un esito per Test, quindi la somma dei conteggi per esito).</summary>
    private static long Conteggio(LocalHomeDashboardQueryService servizio, DashboardFilter filtro) =>
        servizio.GetSnapshot(filtro).OutcomeCounts.Values.Sum();

    [Theory]
    [InlineData(OperatoreDurata.Maggiore, 20, 3)]   // A (30), B (40), F (60 guasto): D escluso, C vuoto mai, E (7) no
    [InlineData(OperatoreDurata.Minore, 20, 3)]     // A (5), E (7), F (3): B no, C vuoto mai
    [InlineData(OperatoreDurata.Maggiore, 0, 4)]    // ogni durata nota e' > 0: A, B, E, F (C vuoto non corrisponde)
    [InlineData(OperatoreDurata.Minore, 1000, 4)]   // ogni durata nota e' < 1000: A, B, E, F (C vuoto non corrisponde)
    public void TempoOperazione_CountSnapshotUgualeAlPercorsoInMemoria(OperatoreDurata operatore, int minuti, int attesi)
    {
        var (a, b, c, d, e, f) = Dataset();
        var tutti = new[] { a, b, c, d, e, f };
        var store = Indice(Path.Combine(_root, "op" + operatore + minuti), tutti);
        var servizio = new LocalHomeDashboardQueryService(store.DatabasePath);
        var filtro = Filtro(operazione: operatore, minuti: minuti);

        Assert.Equal(attesi, Conteggio(servizio, filtro));
        Assert.Equal(attesi, filtro.Applica(tutti, _ => null).Count);   // stesso risultato del percorso in memoria
    }

    [Theory]
    [InlineData(OperatoreDurata.Maggiore, 40, 2)]   // E (50 + 7 = 57), F (63); A (35) no; B (40) non e' > 40
    [InlineData(OperatoreDurata.Minore, 40, 1)]     // A (35); B (40) non e' < 40; E (57) e F (63) no
    [InlineData(OperatoreDurata.Minore, 1, 0)]      // nessun totale sotto 1 minuto
    public void TempoTotaleTest_CountSnapshotUgualeAlPercorsoInMemoria(OperatoreDurata operatore, int minuti, int attesi)
    {
        var (a, b, c, d, e, f) = Dataset();
        var tutti = new[] { a, b, c, d, e, f };
        var store = Indice(Path.Combine(_root, "tot" + operatore + minuti), tutti);
        var servizio = new LocalHomeDashboardQueryService(store.DatabasePath);
        var filtro = Filtro(totale: operatore, minutiTotale: minuti);

        Assert.Equal(attesi, Conteggio(servizio, filtro));
        Assert.Equal(attesi, filtro.Applica(tutti, _ => null).Count);
    }

    [Fact]
    public void TempoTotaleTest_RipetizioneNellaStessaRevisioneOInUnaSuccessiva_SommaTuttiITentativi()
    {
        // Decisione utente: 20 minuti e una ripetizione da 10 danno 30, nella stessa revisione o in una revisione successiva.
        var provaStessa = Op("F1", Esito.Conforme, 20);
        var stessaRevisione = Test("R-STESSA", Rev(1, provaStessa, Op("F1", Esito.Conforme, 10, origine: provaStessa.Uid)));
        var provaSuccessiva = Op("F1", Esito.Conforme, 20);
        var revisioneSuccessiva = Test("R-SUCCESSIVA", Rev(1, provaSuccessiva), Rev(2, Op("F1", Esito.Conforme, 10, origine: provaSuccessiva.Uid)));
        var tutti = new[] { stessaRevisione, revisioneSuccessiva };
        Assert.Equal(30, ReportMatriceService.TotaleMinutiTest(stessaRevisione));
        Assert.Equal(30, ReportMatriceService.TotaleMinutiTest(revisioneSuccessiva));

        var store = Indice(Path.Combine(_root, "ripetizioni"), tutti);
        var servizio = new LocalHomeDashboardQueryService(store.DatabasePath);
        var sopraVenticinque = Filtro(totale: OperatoreDurata.Maggiore, minutiTotale: 25);   // 30 > 25
        var sottoTrenta = Filtro(totale: OperatoreDurata.Minore, minutiTotale: 30);          // 30 non e' < 30
        Assert.Equal(2, Conteggio(servizio, sopraVenticinque));
        Assert.Equal(0, Conteggio(servizio, sottoTrenta));
        Assert.Equal(2, sopraVenticinque.Applica(tutti, _ => null).Count);
        Assert.Equal(0, sottoTrenta.Applica(tutti, _ => null).Count);
    }

    [Fact]
    public void FiltriCombinati_RestringonoSoloDoveSonoAttivi()
    {
        var (a, b, c, d, e, f) = Dataset();
        var tutti = new[] { a, b, c, d, e, f };
        var store = Indice(Path.Combine(_root, "combinati"), tutti);
        var servizio = new LocalHomeDashboardQueryService(store.DatabasePath);
        // Operazione > 20 (A, B, F) e totale < 40 (solo A, 35): B (40) e F (63) no.
        var filtro = new DashboardFilter
        {
            TempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 20),
            TempoTotaleTest = new FiltroDurataMinuti(OperatoreDurata.Minore, 40),
        };

        Assert.Equal(1, Conteggio(servizio, filtro));
        Assert.Equal(1, filtro.Applica(tutti, _ => null).Count);
    }

    [Fact]
    public void ValoriDiversi_NonRiusanoLaCache_EUnaModificaCambiaIlConteggio()
    {
        var (a, b, c, d, e, f) = Dataset();
        var store = Indice(Path.Combine(_root, "cache"), new[] { a, b, c, d, e, f });
        var servizio = new LocalHomeDashboardQueryService(store.DatabasePath);

        // Stesso servizio (stessa cache): valori diversi danno conteggi diversi, e tornare indietro torna al primo risultato.
        var sopraVenti = Conteggio(servizio, Filtro(operazione: OperatoreDurata.Maggiore, minuti: 20));   // A, B, F
        var sottoQuattro = Conteggio(servizio, Filtro(operazione: OperatoreDurata.Minore, minuti: 4));    // solo F (3)
        Assert.NotEqual(sopraVenti, sottoQuattro);
        Assert.Equal(sopraVenti, Conteggio(servizio, Filtro(operazione: OperatoreDurata.Maggiore, minuti: 20)));

        // Correzione dell'ultima revisione di B: la durata sparisce, B non corrisponde piu' a "> 20".
        b.Esecuzioni[^1].Condizioni[0].DurataMinuti = null;
        store.Upsert(Path.Combine(_root, "cache", "B.json"), Giorno, 101, b);
        Assert.Equal(sopraVenti - 1, Conteggio(servizio, Filtro(operazione: OperatoreDurata.Maggiore, minuti: 20)));
    }
}
