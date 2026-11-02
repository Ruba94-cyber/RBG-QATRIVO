using System.Threading;

namespace TestLoom.App.Services;

/// <summary>
/// Contatori diagnostici minimali per il collaudo prestazioni in-process (Piano 6.4.0, banco a piena scala
/// 2026-09-25): solo <see cref="Interlocked"/> su campi statici, zero allocazioni, nessun cambio di
/// comportamento nei percorsi di produzione che li incrementano di una riga. Non sostituiscono
/// PerformanceDiagnosticsService (che scrive su disco un journal navigazione/costruzione): questi contano
/// invece QUANTE VOLTE certi percorsi caldi vengono davvero eseguiti in un giro di navigazione, per capire se
/// un tempo alto viene da un lavoro ripetuto inutilmente (es. più Load() dello stesso catalogo, più
/// PhaseDMasterMaterializer.Refresh non accorpati da MasterLetturaScope) invece che da un singolo lavoro
/// pesante. Lette/azzerate solo da PerfInProcessPagineTests: nessun altro consumer in produzione.
/// </summary>
public static class PerfContatori
{
    private static long _masterLoadCount;
    private static long _phaseDRefreshEseguiti;
    private static long _phaseDRefreshAccorpati;
    private static long _phaseDRefreshMsX10;
    private static long _loadCatalogCount;
    private static long _testCaseGetAllCount;
    private static long _graficoPngGeneratoCount;
    private static long _graficoPngRiusatoCount;
    private static long _totaliMatriceRicalcolatiCount;
    private static long _totaliMatriceRiusatiCount;
    private static long _fingerprintCalcolatiCount;
    private static long _fingerprintRiusatiCount;
    private static long _homeLetturaSommarioCount;
    private static long _homeLetturaCoperturaCount;
    private static long _homeLetturaListeCount;
    private static long _homeLetturaFiltriCount;
    private static long _homeLetturaGraficiCount;
    private static long _homeSondaIndiceCount;
    private static long _wuIndiceCostruitoCount;

    public static void IncrementaMasterLoad() => Interlocked.Increment(ref _masterLoadCount);
    public static void IncrementaPhaseDRefreshEseguito(double ms)
    {
        Interlocked.Increment(ref _phaseDRefreshEseguiti);
        Interlocked.Add(ref _phaseDRefreshMsX10, (long)(ms * 10));
    }
    public static void IncrementaPhaseDRefreshAccorpato() => Interlocked.Increment(ref _phaseDRefreshAccorpati);
    public static void IncrementaLoadCatalog() => Interlocked.Increment(ref _loadCatalogCount);
    public static void IncrementaTestCaseGetAll() => Interlocked.Increment(ref _testCaseGetAllCount);

    /// <summary>Un grafico Home (Copertura/Andamento/Donut) ha rigenerato il PNG SkiaSharp perché l'input
    /// (bucket/aggregati/titolo) e' cambiato rispetto alla cache in HomeViewModel, o perché non c'era ancora
    /// una cache (prima visita/riselezione dopo deselezione).</summary>
    public static void IncrementaGraficoPngGenerato() => Interlocked.Increment(ref _graficoPngGeneratoCount);

    /// <summary>Un grafico Home ha riusato il Bitmap già decodificato invece di rigenerare il PNG, perché
    /// l'input rispetto alla rivisita precedente era identico.</summary>
    public static void IncrementaGraficoPngRiusato() => Interlocked.Increment(ref _graficoPngRiusatoCount);

    /// <summary>Revisione indipendente 2026-09-26 (addendum totali di intestazione): MatricePreparata.
    /// CalcolaTotali (O(combinazioni core), non O(Test)) e' stato RIESEGUITO perché la firma della marcatura
    /// N.A. e' cambiata rispetto all'ultima chiamata, o non c'era ancora una cache (prima Finestra/Completa
    /// su questa MatricePreparata).</summary>
    public static void IncrementaTotaliMatriceRicalcolati() => Interlocked.Increment(ref _totaliMatriceRicalcolatiCount);

    /// <summary>La chiamata a Finestra/Completa ha RIUSATO i totali già calcolati (stessa firma N.A. della
    /// chiamata precedente sulla stessa MatricePreparata, es. solo un cambio di pagina/finestra).</summary>
    public static void IncrementaTotaliMatriceRiusati() => Interlocked.Increment(ref _totaliMatriceRiusatiCount);

    /// <summary>Un fingerprint di TestCase e' stato RICALCOLATO serializzando il Test due volte (TestCaseFingerprint.Compute).
    /// Conta il lavoro che il warm start puo' evitare riusando l'hash gia' memorizzato nell'indice locale.</summary>
    public static void IncrementaFingerprintCalcolato() => Interlocked.Increment(ref _fingerprintCalcolatiCount);

    /// <summary>Un fingerprint di TestCase e' stato RIUSATO dall'hash memorizzato nell'indice SQLite locale (nessuna serializzazione).</summary>
    public static void IncrementaFingerprintRiusato() => Interlocked.Increment(ref _fingerprintRiusatiCount);

    /// <summary>La Home ha letto dall'indice il riepilogo/testata (Riepilogo, Avvisi, KPI). Una sezione nascosta non lo fa.</summary>
    public static void IncrementaHomeLetturaSommario() => Interlocked.Increment(ref _homeLetturaSommarioCount);

    /// <summary>La Home ha calcolato dall'indice la copertura requisiti (KPI).</summary>
    public static void IncrementaHomeLetturaCopertura() => Interlocked.Increment(ref _homeLetturaCoperturaCount);

    /// <summary>La Home ha letto dall'indice una pagina di Da fare o di Fatti (sezione Liste).</summary>
    public static void IncrementaHomeLetturaListe() => Interlocked.Increment(ref _homeLetturaListeCount);

    /// <summary>La Home ha letto dall'indice gli elenchi dei filtri del grafici (componente e campi custom).</summary>
    public static void IncrementaHomeLetturaFiltri() => Interlocked.Increment(ref _homeLetturaFiltriCount);

    /// <summary>La Home ha eseguito la query dei grafici (snapshot, copertura e donut) sull'indice.</summary>
    public static void IncrementaHomeLetturaGrafici() => Interlocked.Increment(ref _homeLetturaGraficiCount);

    /// <summary>Lettura minima dell'indice eseguita solo per tenere aggiornato il banner di indisponibilita' quando nessuna sezione visibile legge.</summary>
    public static void IncrementaHomeSondaIndice() => Interlocked.Increment(ref _homeSondaIndiceCount);

    /// <summary>Indice delle occorrenze Pacchetto costruito con un passaggio sui Test (pagina WU Status: a caricamento
    /// della pagina e, a click con cache non valida, in background). Un click con cache valida non lo incrementa.</summary>
    public static void IncrementaWuIndiceCostruito() => Interlocked.Increment(ref _wuIndiceCostruitoCount);

    public static void Reset()
    {
        Interlocked.Exchange(ref _masterLoadCount, 0);
        Interlocked.Exchange(ref _phaseDRefreshEseguiti, 0);
        Interlocked.Exchange(ref _phaseDRefreshAccorpati, 0);
        Interlocked.Exchange(ref _phaseDRefreshMsX10, 0);
        Interlocked.Exchange(ref _loadCatalogCount, 0);
        Interlocked.Exchange(ref _testCaseGetAllCount, 0);
        Interlocked.Exchange(ref _graficoPngGeneratoCount, 0);
        Interlocked.Exchange(ref _graficoPngRiusatoCount, 0);
        Interlocked.Exchange(ref _totaliMatriceRicalcolatiCount, 0);
        Interlocked.Exchange(ref _totaliMatriceRiusatiCount, 0);
        Interlocked.Exchange(ref _fingerprintCalcolatiCount, 0);
        Interlocked.Exchange(ref _fingerprintRiusatiCount, 0);
        Interlocked.Exchange(ref _homeLetturaSommarioCount, 0);
        Interlocked.Exchange(ref _homeLetturaCoperturaCount, 0);
        Interlocked.Exchange(ref _homeLetturaListeCount, 0);
        Interlocked.Exchange(ref _homeLetturaFiltriCount, 0);
        Interlocked.Exchange(ref _homeLetturaGraficiCount, 0);
        Interlocked.Exchange(ref _homeSondaIndiceCount, 0);
        Interlocked.Exchange(ref _wuIndiceCostruitoCount, 0);
    }

    public readonly record struct Snapshot(
        long MasterLoad, long PhaseDRefreshEseguiti, long PhaseDRefreshAccorpati,
        double PhaseDRefreshMs, long LoadCatalog, long TestCaseGetAll,
        long GraficoPngGenerato, long GraficoPngRiusato,
        long TotaliMatriceRicalcolati, long TotaliMatriceRiusati,
        long FingerprintCalcolati, long FingerprintRiusati,
        long HomeLetturaSommario, long HomeLetturaCopertura, long HomeLetturaListe,
        long HomeLetturaFiltri, long HomeLetturaGrafici, long HomeSondaIndice,
        long WuIndiceCostruito);

    public static Snapshot LeggiSnapshot() => new(
        Interlocked.Read(ref _masterLoadCount),
        Interlocked.Read(ref _phaseDRefreshEseguiti),
        Interlocked.Read(ref _phaseDRefreshAccorpati),
        Interlocked.Read(ref _phaseDRefreshMsX10) / 10.0,
        Interlocked.Read(ref _loadCatalogCount),
        Interlocked.Read(ref _testCaseGetAllCount),
        Interlocked.Read(ref _graficoPngGeneratoCount),
        Interlocked.Read(ref _graficoPngRiusatoCount),
        Interlocked.Read(ref _totaliMatriceRicalcolatiCount),
        Interlocked.Read(ref _totaliMatriceRiusatiCount),
        Interlocked.Read(ref _fingerprintCalcolatiCount),
        Interlocked.Read(ref _fingerprintRiusatiCount),
        Interlocked.Read(ref _homeLetturaSommarioCount),
        Interlocked.Read(ref _homeLetturaCoperturaCount),
        Interlocked.Read(ref _homeLetturaListeCount),
        Interlocked.Read(ref _homeLetturaFiltriCount),
        Interlocked.Read(ref _homeLetturaGraficiCount),
        Interlocked.Read(ref _homeSondaIndiceCount),
        Interlocked.Read(ref _wuIndiceCostruitoCount));
}
