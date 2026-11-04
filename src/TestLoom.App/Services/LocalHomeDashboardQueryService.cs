using TestLoom.App.Models;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Aggregati della dashboard Home sul read-model SQLite. È separato dalla lista perché la dashboard usa il
/// range DataCreazione e aggregazioni, non pagine di record. Nessun metodo deserializza TestCase completi.
/// </summary>
public sealed class LocalHomeDashboardQueryService
{
    private const int BusyTimeoutMs = 500;
    private readonly string _connectionString;

    /// <summary>Veicoli × esito dalla proiezione test_vehicle (v14), con il solo periodo della Home. Stesso ordine e stessa
    /// grafia della query originale in GetSnapshot: vedi il commento di quel ramo.</summary>
    private const string SqlVeicoliProiezione = """
        SELECT v.value, v.esito_home, COUNT(*)
        FROM test_vehicle v INDEXED BY ix_test_vehicle_home
        WHERE v.eliminato = 0 AND v.origine_test = 0
            AND v.data_creazione_ticks >= $homeDateFrom AND v.data_creazione_ticks < $homeDateTo
        GROUP BY v.value COLLATE NOCASE, v.esito_home
        ORDER BY v.value COLLATE NOCASE, v.esito_home;
        """;

    /// <summary>Generazione dell'indice a cui l'istanza e' legata; vedi LocalTestArchiveQueryService.Generazione.</summary>
    public long Generazione { get; }

    private readonly object _cacheLock = new();
    private (long Generazione, long Revisione, DateTime? DataDa, DateTime? DataA, LocalDefaultDashboardSnapshot Snapshot)? _cachedSnapshot;
    private (long Generazione, long Revisione, DateTime? DataDa, DateTime? DataA, int RequisitiHash, CoverageBucket Bucket)? _cachedCoverage;
    // Un'unica fotografia per le due modalita' TIME. Solo filtro data (nessuna dimensione):
    // evita due scansioni di test_operation_attempt quando si passa Totale <-> Medio.
    // Revisione e generazione garantiscono invalidazione dopo ogni scrittura dell'indice.
    private (long Generazione, long Revisione, DateTime? DataDa, DateTime? DataA,
        ValoreTempo Totale, ValoreTempo SoloConformi)? _cachedTimeSummary;

    /// <summary>Hook interno per verificare che le due query coverage condividano lo stesso snapshot.</summary>
    internal Action? RequirementCoverageStandardReadObserver { get; set; }

    public LocalHomeDashboardQueryService(string databasePath) : this(databasePath, null) { }

    public LocalHomeDashboardQueryService(string databasePath, LocalIndexRecoveryCoordinator? coordinatore)
    {
        Generazione = coordinatore?.Generazione ?? 0;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = true
        }.ToString();
    }

    private long _operatoriSaltati;

    /// <summary>Spia dei test: quante volte la query degli operatori e' stata saltata perche' non esistono revisioni.</summary>
    internal long OperatoriSaltati => Interlocked.Read(ref _operatoriSaltati);

    private long _veicoliDaProiezione;

    /// <summary>Spia dei test: quante volte la query Veicoli × esito e' stata letta dalla proiezione test_vehicle (solo periodo Home).</summary>
    internal long VeicoliDaProiezione => Interlocked.Read(ref _veicoliDaProiezione);

    public LocalDefaultDashboardSnapshot GetSnapshot(DateTime? dataDa, DateTime? dataA) =>
        GetSnapshot(new DashboardFilter { DataDa = dataDa, DataA = dataA });

    /// <summary>
    /// Restituisce gli aggregati Home applicando esattamente le stesse condizioni di <see cref="DashboardFilter"/>
    /// sul read-model locale. Gamma e categoria sono dimensioni derivate dai cataloghi master: il chiamante
    /// passa quindi i veicoli/funzionalita' gia' risolti, senza introdurre una seconda fonte dati nell'indice.
    /// </summary>
    public LocalDefaultDashboardSnapshot GetSnapshot(
        DashboardFilter filter,
        IReadOnlyCollection<string>? vehiclesForSelectedGamma = null,
        IReadOnlyCollection<string>? functionalityForSelectedCategory = null,
        long? knownNonExecutedTestCount = null,
        long? knownNonExecutedRevisione = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var dataDa = filter.DataDa;
        var dataA = filter.DataA;
        var cacheable = !HasDimensionalFilters(filter);
        // LocalTestArchiveQueryService può attestare, nello stesso snapshot SQLite, che non esiste alcuna
        // revisione e fornire il conteggio attivo TestLoom già necessario alla Home. In questo caso esito/KPI e
        // operatori sono deterministici (tutti non eseguiti/zero); il riuso è ammesso solo senza filtri o date.
        var connection = Open();
        using (connection)
        using (var lettura = new LocalIndexSnapshot(connection))
        {
            var knownNoRevisions = knownNonExecutedTestCount.HasValue &&
                knownNonExecutedRevisione == lettura.Revisione && dataDa == null && dataA == null && !HasDimensionalFilters(filter);
            lock (_cacheLock)
            {
                if (cacheable && lettura.Cacheabile && _cachedSnapshot.HasValue &&
                    _cachedSnapshot.Value.Generazione == Generazione &&
                    _cachedSnapshot.Value.Revisione == lettura.Revisione &&
                    _cachedSnapshot.Value.DataDa == dataDa &&
                    _cachedSnapshot.Value.DataA == dataA)
                {
                    return CloneSnapshot(_cachedSnapshot.Value.Snapshot);
                }
            }

            // Tutti gli aggregati descrivono lo stesso commit locale, anche durante un refresh remoto: sono letti nello snapshot.
            var transaction = lettura.Transazione;
            // Decisione utente 6.4.0 ("Test importati: solo stato finale"): questo snapshot alimenta SOLO i
            // conteggi operativi della Home (test/esecuzioni/non eseguiti/carico/attività) — i Test con
            // OrigineTest.ImportStatoUdt ne restano fuori (excludeImportStatoUdt: true), a differenza di
            // GetRequirementCoverage più sotto, che alimenta la copertura ed è volutamente invariata.
            var (where, whereParameters) = FilterWhere(filter, vehiclesForSelectedGamma, functionalityForSelectedCategory, "s", excludeImportStatoUdt: true);
            // vehicles/operators: JOIN con la condizione nell'ON, non un WHERE...IN (SELECT...) verso
            // test_summary (stesso motivo già documentato in GetRequirementCoverage/DateJoinClause: a 100k
            // righe con selettività quasi totale, IN (SELECT...) spingeva SQLite verso un piano peggiore che
            // il JOIN diretto, Addendum prestazioni 2026-09-20, feedback utente "navigazione fluida 700ms").
            var (filterOn, joinParameters) = FilterJoinClause(filter, vehiclesForSelectedGamma, functionalityForSelectedCategory, excludeImportStatoUdt: true);
            var snapshot = new LocalDefaultDashboardSnapshot();

            // Percorso veloce (v14): con il solo periodo della Home (entrambe le date, nessun filtro dimensionale) la query legge
            // la proiezione test_vehicle con indice di copertura, senza JOIN verso test_summary. Ordine e grafia restano quelli
            // della query originale: la grafia di un gruppo (veicolo NOCASE, esito) e' quella della prima riga nell'ordine
            // dell'indice (data_creazione_ticks, test_uid), lo stesso con cui la query originale scorre ix_test_summary_home_v8
            // con il periodo. Senza l'indice (DB non ancora migrato) resta la query originale.
            var proiezioneVeicoli = dataDa.HasValue && dataA.HasValue && !HasDimensionalFilters(filter) &&
                IndiceEsiste(connection, transaction, "ix_test_vehicle_home");
            if (proiezioneVeicoli) Interlocked.Increment(ref _veicoliDaProiezione);
            using (var vehicleOutcomes = connection.CreateCommand())
            {
                vehicleOutcomes.Transaction = transaction;
                vehicleOutcomes.CommandText = proiezioneVeicoli ? SqlVeicoliProiezione : knownNoRevisions
                    ? $"""
                        SELECT v.value, $notExecuted, COUNT(*)
                        FROM test_vehicle v
                        JOIN test_summary s ON s.uid = v.test_uid {filterOn}
                        GROUP BY v.value COLLATE NOCASE
                        ORDER BY v.value COLLATE NOCASE;
                        """
                    : $"""
                        SELECT v.value, COALESCE(s.current_outcome, $notExecuted), COUNT(*)
                        FROM test_vehicle v
                        JOIN test_summary s ON s.uid = v.test_uid {filterOn}
                        GROUP BY v.value COLLATE NOCASE, COALESCE(s.current_outcome, $notExecuted)
                        ORDER BY v.value COLLATE NOCASE, COALESCE(s.current_outcome, $notExecuted);
                        """;
                if (proiezioneVeicoli)
                {
                    vehicleOutcomes.Parameters.AddWithValue("$homeDateFrom", dataDa!.Value.ToUniversalTime().Ticks);
                    vehicleOutcomes.Parameters.AddWithValue("$homeDateTo", DataAInclusiva.FineEsclusa(dataA!.Value).ToUniversalTime().Ticks);
                }
                else
                {
                    AddParameters(vehicleOutcomes, joinParameters);
                    vehicleOutcomes.Parameters.AddWithValue("$notExecuted", (int)Esito.NonEseguitoNA);
                }
                using var reader = vehicleOutcomes.ExecuteReader();
                while (reader.Read())
                {
                    var vehicle = reader.GetString(0);
                    var count = reader.GetInt64(2);
                    snapshot.VehicleOutcomeRows.Add((vehicle, (Esito)reader.GetInt32(1), count));
                    // Ogni riga di VehicleOutcomeRows appartiene a un gruppo disgiunto per esito.
                    // Sommarle ricostruisce esattamente il conteggio per veicolo senza ripetere la
                    // scansione e il JOIN di test_vehicle/test_summary appena eseguiti.
                    snapshot.VehicleCounts[vehicle] = snapshot.VehicleCounts.GetValueOrDefault(vehicle) + count;
                }
            }
            if (knownNoRevisions)
            {
                if (knownNonExecutedTestCount!.Value > 0)
                    snapshot.OutcomeCounts[Esito.NonEseguitoNA] = knownNonExecutedTestCount.Value;
                snapshot.IterationKpi = new KpiIterazioniConforme();
            }
            else
            {
                // Indice forzato solo se esiste nel database: su uno schema senza l'indice la query fallirebbe invece di ricadere.
                var indiceEsiti = IndiceEsiste(connection, transaction, "ix_test_summary_outcome") ? "INDEXED BY ix_test_summary_outcome" : "";
                using (var outcomesAndKpi = connection.CreateCommand())
                {
                    outcomesAndKpi.Transaction = transaction;
                    // Esiti e KPI leggono gli stessi test filtrati: aggregarli per esito evita una seconda
                    // scansione completa di test_summary all'apertura fredda della Home ad archivi grandi.
                    outcomesAndKpi.CommandText = $"""
                        SELECT COALESCE(s.current_outcome, $notExecuted), COUNT(*),
                            SUM(CASE WHEN s.executed_revision_count > 0 AND s.first_conforme_revision IS NOT NULL THEN 1 ELSE 0 END),
                            SUM(CASE WHEN s.executed_revision_count > 0 AND s.first_conforme_revision IS NULL THEN 1 ELSE 0 END),
                            SUM(CASE WHEN s.first_conforme_revision IS NOT NULL THEN s.first_conforme_revision ELSE 0 END),
                            SUM(CASE WHEN s.first_conforme_revision IS NOT NULL THEN 1 ELSE 0 END)
                        FROM test_summary s {indiceEsiti}
                        {where}
                        GROUP BY COALESCE(s.current_outcome, $notExecuted)
                        ORDER BY COALESCE(s.current_outcome, $notExecuted);
                        """;
                    AddParameters(outcomesAndKpi, whereParameters);
                    outcomesAndKpi.Parameters.AddWithValue("$notExecuted", (int)Esito.NonEseguitoNA);
                    long casiRaggiunti = 0, casiMaiRaggiunti = 0, sommaIterazioni = 0, testConformeConteggiabile = 0;
                    using var reader = outcomesAndKpi.ExecuteReader();
                    while (reader.Read())
                    {
                        snapshot.OutcomeCounts[(Esito)reader.GetInt32(0)] = reader.GetInt64(1);
                        casiRaggiunti += reader.GetInt64(2);
                        casiMaiRaggiunti += reader.GetInt64(3);
                        sommaIterazioni += reader.GetInt64(4);
                        testConformeConteggiabile += reader.GetInt64(5);
                    }
                    snapshot.IterationKpi = new KpiIterazioniConforme
                    {
                        MediaIterazioni = testConformeConteggiabile == 0 ? 0 : (double)sommaIterazioni / testConformeConteggiabile,
                        CasiRaggiuntiConforme = casiRaggiunti,
                        CasiMaiRaggiuntiConforme = casiMaiRaggiunti
                    };
                }
            }

            // Senza alcuna revisione la query degli operatori non puo' restituire righe: si evita la scansione del JOIN.
            if (!knownNoRevisions && !HaRevisioni(connection, transaction)) Interlocked.Increment(ref _operatoriSaltati);
            else if (!knownNoRevisions) using (var operators = connection.CreateCommand())
            {
                operators.Transaction = transaction;
                operators.CommandText = $"""
                    SELECT r.operatore, SUM(r.unit_count)
                    FROM test_revision r
                    JOIN test_summary s ON s.uid = r.test_uid {filterOn}
                    WHERE TRIM(r.operatore) <> '' AND r.raw_attempt_count > 0 AND r.origine <> 1
                    GROUP BY r.operatore COLLATE NOCASE
                    ORDER BY SUM(r.unit_count) DESC, r.operatore COLLATE NOCASE;
                    """;
                AddParameters(operators, joinParameters);
                using var reader = operators.ExecuteReader();
                while (reader.Read()) snapshot.ExecutionCountsByOperator[reader.GetString(0)] = reader.GetInt64(1);
            }
            if (cacheable) lock (_cacheLock)
            {
                if (lettura.Cacheabile) _cachedSnapshot = (Generazione, lettura.Revisione, dataDa, dataA, CloneSnapshot(snapshot));
            }

            return snapshot;
        }
    }

    /// <summary>Riepilogo TIME della Home sullo stesso read-model locale e con gli stessi
    /// filtri del donut: non deserializza TestCase. Come Report (base tutte le esecuzioni),
    /// conta una volta l'ultima revisione di ogni tentativo realmente eseguito;
    /// ripetizioni e guasto entrano nella somma, gli snapshot UnitLogger no.
    /// In modo Tempo medio (soloConformi), il predicato riguarda l'esito della
    /// singola operazione, NON lo stato complessivo del Test.</summary>
    public ValoreTempo GetTimeSummary(DashboardFilter filter,
        IReadOnlyCollection<string>? vehiclesForSelectedGamma = null,
        IReadOnlyCollection<string>? functionalityForSelectedCategory = null,
        bool soloConformi = false)
    {
        ArgumentNullException.ThrowIfNull(filter);
        using var connection = Open();
        using var lettura = new LocalIndexSnapshot(connection);
        var cacheable = !HasDimensionalFilters(filter);
        if (cacheable && lettura.Cacheabile) lock (_cacheLock)
        {
            if (_cachedTimeSummary is { } cache &&
                cache.Generazione == Generazione && cache.Revisione == lettura.Revisione &&
                cache.DataDa == filter.DataDa && cache.DataA == filter.DataA)
                return soloConformi ? cache.SoloConformi : cache.Totale;
        }

        var (filterOn, parameters) = FilterJoinClause(filter, vehiclesForSelectedGamma,
            functionalityForSelectedCategory, excludeImportStatoUdt: true);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = lettura.Transazione;
        // Una scansione restituisce ENTRAMBE le modalita'. Per la media le operazioni
        // con esito NOK/deviazioni non contribuiscono ne' ai minuti ne' agli eleggibili.
        // La completezza comprende i conformi con durata mancante, ma la media no.
        cmd.CommandText = $"""
            SELECT COALESCE(SUM(a.duration_minutes), 0),
                   COUNT(a.duration_minutes), COUNT(*),
                   COALESCE(SUM(CASE WHEN a.outcome = {(int)Esito.Conforme} THEN a.duration_minutes ELSE 0 END), 0),
                   COUNT(CASE WHEN a.outcome = {(int)Esito.Conforme} THEN a.duration_minutes END),
                   COALESCE(SUM(CASE WHEN a.outcome = {(int)Esito.Conforme} THEN 1 ELSE 0 END), 0)
            FROM test_operation_attempt a
            JOIN test_summary s ON s.uid = a.test_uid {filterOn}
            WHERE {SqlTentativiEseguiti};
            """;
        AddParameters(cmd, parameters);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return default;
        var totale = new ValoreTempo(reader.GetInt64(0),
            (int)Math.Min(int.MaxValue, reader.GetInt64(1)),
            (int)Math.Min(int.MaxValue, reader.GetInt64(2)));
        var conformi = new ValoreTempo(reader.GetInt64(3),
            (int)Math.Min(int.MaxValue, reader.GetInt64(4)),
            (int)Math.Min(int.MaxValue, reader.GetInt64(5)));
        if (cacheable && lettura.Cacheabile) lock (_cacheLock)
            _cachedTimeSummary = (Generazione, lettura.Revisione, filter.DataDa, filter.DataA, totale, conformi);
        return soloConformi ? conformi : totale;
    }

    /// <summary>CoverageService.PerRequisito con lo stesso filtro DataCreazione della DashboardFilter.</summary>
    public CoverageBucket GetRequirementCoverage(IReadOnlyList<Requisito> requisiti, DateTime? dataDa, DateTime? dataA) =>
        GetRequirementCoverage(requisiti, new DashboardFilter { DataDa = dataDa, DataA = dataA });

    /// <summary>Coverage applicata al medesimo insieme di Test del snapshot filtrato.</summary>
    public CoverageBucket GetRequirementCoverage(
        IReadOnlyList<Requisito> requisiti,
        DashboardFilter filter,
        IReadOnlyCollection<string>? vehiclesForSelectedGamma = null,
        IReadOnlyCollection<string>? functionalityForSelectedCategory = null)
    {
        ArgumentNullException.ThrowIfNull(requisiti);
        ArgumentNullException.ThrowIfNull(filter);
        var dataDa = filter.DataDa;
        var dataA = filter.DataA;
        var cacheable = !HasDimensionalFilters(filter);

        // Vedi RequisitiCoverageCacheKey: hash condiviso con LocalTestArchiveQueryService.GetHomeAggiornaBatch,
        // cosi' le due cache non possono divergere su quali campi del Requisito le invalidano.
        var reqHash = RequisitiCoverageCacheKey.ComputeHash(requisiti);

        var connection = Open();
        using (connection)
        using (var lettura = new LocalIndexSnapshot(connection))
        {
            lock (_cacheLock)
            {
                if (cacheable && lettura.Cacheabile && _cachedCoverage.HasValue &&
                    _cachedCoverage.Value.Generazione == Generazione &&
                    _cachedCoverage.Value.Revisione == lettura.Revisione &&
                    _cachedCoverage.Value.DataDa == dataDa &&
                    _cachedCoverage.Value.DataA == dataA &&
                    _cachedCoverage.Value.RequisitiHash == reqHash)
                {
                    return CloneCoverage(_cachedCoverage.Value.Bucket);
                }
            }
        // Il filtro eliminato/data deve stare nell'ON del join verso s, non in un WHERE dopo la catena di
        // CROSS JOIN: altrimenti SQLite risolve la ricerca di s con l'indice (eliminato, data) - una range
        // scan su ~70k righe per ciascuna delle ~169 righe di rq - invece dell'uguaglianza su uid (ricerca
        // diretta). Verificato con EXPLAIN QUERY PLAN: stessa query, ON vs WHERE, 1ms contro oltre 2000ms
        // (feedback utente 2026-09-17, volumi 100k).
        // Decisione utente 6.4.0 ("Test importati: solo stato finale"): la copertura NON esclude i Test con
        // OrigineTest.ImportStatoUdt (excludeImportStatoUdt: false) — a differenza dei conteggi operativi di
        // GetSnapshot sopra, contribuiscono allo stato finale/copertura esattamente come un Test TestLoom.
        var (filterOn, parameters) = FilterJoinClause(filter, vehiclesForSelectedGamma, functionalityForSelectedCategory, excludeImportStatoUdt: false);
        var standard = new Dictionary<string, Esito>(StringComparer.OrdinalIgnoreCase);
        var extra = new Dictionary<(string Requirement, string Vehicle), Esito>(RequirementVehicleComparer.Instance);

        // Le mappe standard e Extra formano una sola fotografia logica. Un refresh concorrente fra i due
        // command non deve combinare risultati di commit diversi; lo snapshot (deferred) mantiene il writer libero in WAL.
        var transaction = lettura.Transazione;

        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = $"""
                SELECT EXISTS(
                    SELECT 1 FROM test_revision r
                    JOIN test_summary s ON s.uid = r.test_uid {filterOn}
                    LIMIT 1
                );
                """;
            AddParameters(exists, parameters);
            var hasRelevantRevisions = Convert.ToInt64(exists.ExecuteScalar()) != 0;

            // Una copertura filtrata senza revisioni applicabili non può avere esiti. Evita di classificare
            // tutte le associazioni Test×Requisito×Veicolo solo per ottenere due mappe vuote, restando nello
            // stesso snapshot e mantenendo NonTestati come risultato per le righe del catalogo.
            if (hasRelevantRevisions)
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = $"""
                        WITH ranked AS (
                            SELECT rq.value requisito, r.outcome,
                                   ROW_NUMBER() OVER (
                                        PARTITION BY rq.value COLLATE NOCASE
                                        ORDER BY r.data_ticks DESC, r.revision_index DESC, r.test_uid
                                   ) rn
                            FROM test_requirement rq
                            CROSS JOIN test_summary s ON s.uid = rq.test_uid {filterOn}
                            CROSS JOIN test_revision r ON r.test_uid = s.uid
                        )
                        SELECT requisito, outcome FROM ranked WHERE rn = 1;
                        """;
                    AddParameters(command, parameters);
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) standard[reader.GetString(0)] = (Esito)reader.GetInt32(1);
                }

                RequirementCoverageStandardReadObserver?.Invoke();

                var hasApplicableD4D = requisiti.Any(r => !r.Archiviata && RequisitoApplicabilita.ED4D(r));
                if (hasApplicableD4D)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $"""
                        WITH ranked AS (
                            SELECT rq.value requisito, v.value veicolo, r.outcome,
                                   ROW_NUMBER() OVER (
                                        PARTITION BY rq.value COLLATE NOCASE, v.value COLLATE NOCASE
                                        ORDER BY r.data_ticks DESC, r.revision_index DESC, r.test_uid
                                   ) rn
                            FROM test_requirement rq
                            CROSS JOIN test_summary s ON s.uid = rq.test_uid {filterOn}
                            CROSS JOIN test_vehicle v ON v.test_uid = s.uid
                            CROSS JOIN test_revision r ON r.test_uid = s.uid
                        )
                        SELECT requisito, veicolo, outcome FROM ranked WHERE rn = 1;
                        """;
                    AddParameters(command, parameters);
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) extra[(reader.GetString(0), reader.GetString(1))] = (Esito)reader.GetInt32(2);
                }
            }
        }

        var bucket = new CoverageBucket();
        foreach (var requisito in requisiti.Where(r => !r.Archiviata))
        {
            if (!RequisitoApplicabilita.ED4D(requisito))
            {
                Accumulate(bucket, standard.TryGetValue(requisito.Id, out var current) ? current : null);
                continue;
            }

            var vehicles = RequisitoApplicabilita.SottoveicoliDi(requisito);
            if (vehicles.Count == 0)
            {
                // 6.5.0: sottoveicoli facoltativi -> Extra applicabile a tutti, unita cross come uno Standard.
                Accumulate(bucket, standard.TryGetValue(requisito.Id, out var currentCross) ? currentCross : null);
                continue;
            }
            foreach (var vehicle in vehicles)
                Accumulate(bucket, extra.TryGetValue((requisito.Id, vehicle), out var current) ? current : null);
        }

            if (cacheable) lock (_cacheLock)
            {
                if (lettura.Cacheabile) _cachedCoverage = (Generazione, lettura.Revisione, dataDa, dataA, reqHash, CloneCoverage(bucket));
            }

            return bucket;
        }
    }

    private static LocalDefaultDashboardSnapshot CloneSnapshot(LocalDefaultDashboardSnapshot s)
    {
        var clone = new LocalDefaultDashboardSnapshot();
        foreach (var kvp in s.VehicleCounts) clone.VehicleCounts[kvp.Key] = kvp.Value;
        foreach (var kvp in s.OutcomeCounts) clone.OutcomeCounts[kvp.Key] = kvp.Value;
        clone.VehicleOutcomeRows.AddRange(s.VehicleOutcomeRows);
        foreach (var kvp in s.ExecutionCountsByOperator) clone.ExecutionCountsByOperator[kvp.Key] = kvp.Value;
        clone.IterationKpi = new KpiIterazioniConforme
        {
            MediaIterazioni = s.IterationKpi.MediaIterazioni,
            CasiRaggiuntiConforme = s.IterationKpi.CasiRaggiuntiConforme,
            CasiMaiRaggiuntiConforme = s.IterationKpi.CasiMaiRaggiuntiConforme
        };
        return clone;
    }

    private static CoverageBucket CloneCoverage(CoverageBucket b) => new CoverageBucket
    {
        Conformi = b.Conformi,
        ConformiConDeviazioni = b.ConformiConDeviazioni,
        NonConformi = b.NonConformi,
        NonTestati = b.NonTestati
    };

    /// <summary>Vero se l'indice esiste nel database, letto nello stesso snapshot della transazione.</summary>
    private static bool IndiceEsiste(SqliteConnection connection, SqliteTransaction transaction, string nome)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $nome);";
        command.Parameters.AddWithValue("$nome", nome);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    /// <summary>Vero se esiste almeno una revisione nel read-model: nello stesso snapshot della transazione.</summary>
    private static bool HaRevisioni(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM test_revision LIMIT 1);";
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    /// <summary>Vero se il filtro restringe i Test oltre al periodo. I filtri TIME (durata) sono dimensionali: non passano per le
    /// cache di snapshot e di copertura (chiavi: periodo + data version + generazione), quindi un valore diverso non riusa un risultato.</summary>
    private static bool HasDimensionalFilters(DashboardFilter filter) =>
        filter.Gamma.Count > 0 || filter.Veicolo.Count > 0 || filter.Componente.Count > 0 ||
        filter.Funzionalita.Count > 0 || filter.CategoriaFunzionalita.Count > 0 ||
        filter.Requisito.Count > 0 || filter.Strumento.Count > 0 || filter.Operatore.Count > 0 ||
        filter.Assegnatario.Count > 0 || filter.TestTemplateUid.Count > 0 || filter.Esito.Count > 0 ||
        filter.CampiCustom.Values.Any(v => v.Count > 0) || filter.TempoOperazione != null || filter.TempoTotaleTest != null;

    /// <summary>Tentativi realmente eseguiti, ciascuno una volta: l'ULTIMA revisione in cui compare (test_uid + condition_uid), con l'esito
    /// di quella revisione. Ripetizioni incluse (decisione utente: 20 piu una ripetizione da 10 danno 30); le operazioni guasto contano.
    /// Come ReportMatriceService.TotaleMinutiTest. Il bind e' sull'alias "a" dell'operazione.</summary>
    private static readonly string SqlTentativiEseguiti =
        $"a.outcome IN ({(int)Esito.Conforme}, {(int)Esito.NonConforme}, {(int)Esito.ConformeConDeviazioni}) "
        + "AND NOT EXISTS (SELECT 1 FROM test_operation_attempt n WHERE n.test_uid = a.test_uid AND n.condition_uid = a.condition_uid "
        + "AND n.revision_index > a.revision_index)";

    /// <summary>Operazioni correnti effettive, base del filtro "Tempo operazione": tentativi eseguiti dell'ultima revisione che sono anche
    /// effettivi nella loro revisione (is_effective, cioe' CondizioniEffettive). Come ReportMatriceService.OperazioniCorrentiEffettive.</summary>
    private static readonly string SqlOperazioniCorrenti = SqlTentativiEseguiti + " AND a.is_effective = 1";

    /// <summary>"Tempo operazione" (Test): almeno una operazione corrente effettiva con durata nota che soddisfa il criterio. Una durata
    /// vuota non corrisponde mai (NULL non e' mai > o < di un valore); un Test importato da UnitLogger non corrisponde (come HaOperazioneConDurata).</summary>
    private static string ClausolaTempoOperazione(string alias, FiltroDurataMinuti filtro, Dictionary<string, object> parameters)
    {
        parameters["homeTempoOperazioneMinuti"] = filtro.Minuti;
        return $"{alias}.origine_test = 0 AND EXISTS (SELECT 1 FROM test_operation_attempt a "
            + $"WHERE a.test_uid = {alias}.uid AND {SqlOperazioniCorrenti} AND a.duration_minutes {OperatoreSql(filtro)} $homeTempoOperazioneMinuti)";
    }

    /// <summary>"Tempo totale Test" (Test): somma delle durate note di tutti i tentativi eseguiti del Test, ciascuno una volta (ultima revisione
    /// per operazione, ripetizioni incluse, guasto incluse; snapshot UnitLogger esclusi). Senza alcuna durata nota il Test non corrisponde (come
    /// TotaleMinutiTest che ritorna null).</summary>
    private static string ClausolaTempoTotaleTest(string alias, FiltroDurataMinuti filtro, Dictionary<string, object> parameters)
    {
        parameters["homeTempoTotaleMinuti"] = filtro.Minuti;
        return $"{alias}.origine_test = 0 AND EXISTS (SELECT 1 FROM (SELECT COUNT(a.duration_minutes) AS conosciuti, "
            + $"SUM(a.duration_minutes) AS totale FROM test_operation_attempt a WHERE a.test_uid = {alias}.uid AND {SqlTentativiEseguiti}) t "
            + $"WHERE t.conosciuti > 0 AND t.totale {OperatoreSql(filtro)} $homeTempoTotaleMinuti)";
    }

    private static string OperatoreSql(FiltroDurataMinuti filtro) => filtro.Operatore == OperatoreDurata.Maggiore ? ">" : "<";

    private static (string Where, Dictionary<string, object> Parameters) FilterWhere(
        DashboardFilter filter,
        IReadOnlyCollection<string>? vehiclesForSelectedGamma,
        IReadOnlyCollection<string>? functionalityForSelectedCategory,
        string alias,
        bool excludeImportStatoUdt)
    {
        var clauses = new List<string> { $"{alias}.eliminato = 0" };
        // Decisione utente 6.4.0 ("Test importati: solo stato finale"): solo i chiamanti che calcolano
        // conteggi operativi (GetSnapshot) passano true — GetRequirementCoverage passa false, la copertura
        // resta invariata.
        if (excludeImportStatoUdt) clauses.Add($"{alias}.origine_test = 0");
        var parameters = new Dictionary<string, object>();
        var parameterIndex = 0;

        void AddValues(string prefix, IEnumerable<string> values, Action<List<string>> addClause)
        {
            var names = new List<string>();
            foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var name = prefix + parameterIndex++;
                names.Add("$" + name);
                parameters[name] = value.Trim();
            }
            if (names.Count == 0) clauses.Add("0");
            else addClause(names);
        }

        void AddExists(string prefix, IReadOnlyCollection<string> values, string table)
        {
            if (values.Count == 0) return;
            AddValues(prefix, values, names => clauses.Add(
                $"EXISTS (SELECT 1 FROM {table} x WHERE x.test_uid = {alias}.uid AND x.value COLLATE NOCASE IN ({string.Join(',', names)}))"));
        }

        AddExists("homeVehicle", filter.Veicolo, "test_vehicle");
        AddExists("homeEcu", filter.Componente, "test_ecu");
        AddExists("homeFunctionality", filter.Funzionalita, "test_functionality");
        AddExists("homeRequirement", filter.Requisito, "test_requirement");
        AddExists("homeInstrument", filter.Strumento, "test_instrument");
        if (filter.Operatore.Count > 0)
            AddValues("homeOperator", filter.Operatore, names => clauses.Add(
                $"EXISTS (SELECT 1 FROM test_revision x WHERE x.test_uid = {alias}.uid AND x.raw_attempt_count > 0 AND x.origine <> 1 AND x.operatore COLLATE NOCASE IN ({string.Join(',', names)}))"));

        if (filter.Gamma.Count > 0)
            AddValues("homeGammaVehicle", vehiclesForSelectedGamma ?? Array.Empty<string>(), names => clauses.Add(
                $"EXISTS (SELECT 1 FROM test_vehicle x WHERE x.test_uid = {alias}.uid AND x.value COLLATE NOCASE IN ({string.Join(',', names)}))"));
        if (filter.CategoriaFunzionalita.Count > 0)
            AddValues("homeCategoryFunctionality", functionalityForSelectedCategory ?? Array.Empty<string>(), names => clauses.Add(
                $"EXISTS (SELECT 1 FROM test_functionality x WHERE x.test_uid = {alias}.uid AND x.value COLLATE NOCASE IN ({string.Join(',', names)}))"));

        if (filter.Assegnatario.Count > 0)
            AddValues("homeAssignee", filter.Assegnatario, names => clauses.Add($"{alias}.assegnatario COLLATE NOCASE IN ({string.Join(',', names)})"));
        if (filter.TestTemplateUid.Count > 0)
            AddValues("homeTemplate", filter.TestTemplateUid.Select(x => x.ToString("N")), names => clauses.Add($"{alias}.test_template_uid IN ({string.Join(',', names)})"));
        if (filter.Esito.Count > 0)
        {
            var names = new List<string>();
            foreach (var esito in filter.Esito.Distinct())
            {
                var name = "homeOutcome" + parameterIndex++;
                names.Add("$" + name);
                parameters[name] = (int)esito;
            }
            clauses.Add($"{alias}.current_outcome IN ({string.Join(',', names)})");
        }

        foreach (var custom in filter.CampiCustom.Where(kv => kv.Value.Count > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var keyName = "homeCustomKey" + parameterIndex++;
            parameters[keyName] = custom.Key;
            AddValues("homeCustomValue", custom.Value, names => clauses.Add(
                $"EXISTS (SELECT 1 FROM test_custom_field x WHERE x.test_uid = {alias}.uid AND x.field_key = ${keyName} AND x.value COLLATE NOCASE IN ({string.Join(',', names)}))"));
        }

        // TIME (6.6.1): durata delle operazioni, a livello di Test (vedi ClausolaTempoOperazione / ClausolaTempoTotaleTest).
        if (filter.TempoOperazione is { } tempoOperazione) clauses.Add(ClausolaTempoOperazione(alias, tempoOperazione, parameters));
        if (filter.TempoTotaleTest is { } tempoTotaleTest) clauses.Add(ClausolaTempoTotaleTest(alias, tempoTotaleTest, parameters));
        if (filter.DataDa.HasValue)
        {
            clauses.Add($"{alias}.data_creazione_ticks >= $homeDateFrom");
            parameters["homeDateFrom"] = filter.DataDa.Value.ToUniversalTime().Ticks;
        }
        if (filter.DataA.HasValue)
        {
            clauses.Add($"{alias}.data_creazione_ticks < $homeDateTo");
            parameters["homeDateTo"] = DataAInclusiva.FineEsclusa(filter.DataA.Value).ToUniversalTime().Ticks;
        }
        return ("WHERE " + string.Join(" AND ", clauses), parameters);
    }

    /// <summary>Le condizioni restano nell'ON del join, cosi' SQLite mantiene la ricerca diretta per UID.
    /// Vedi <see cref="GetRequirementCoverage"/> per il motivo del vincolo di piano.</summary>
    private static (string OnClause, Dictionary<string, object> Parameters) FilterJoinClause(
        DashboardFilter filter,
        IReadOnlyCollection<string>? vehiclesForSelectedGamma,
        IReadOnlyCollection<string>? functionalityForSelectedCategory,
        bool excludeImportStatoUdt)
    {
        var (where, parameters) = FilterWhere(filter, vehiclesForSelectedGamma, functionalityForSelectedCategory, "s", excludeImportStatoUdt);
        return ("AND " + where["WHERE ".Length..], parameters);
    }

    private static void AddParameters(SqliteCommand command, IReadOnlyDictionary<string, object> parameters)
    {
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue("$" + name, value);
    }

    private static void Accumulate(CoverageBucket bucket, Esito? esito)
    {
        switch (esito)
        {
            case Esito.Conforme: bucket.Conformi++; break;
            case Esito.ConformeConDeviazioni: bucket.ConformiConDeviazioni++; break;
            case Esito.NonConforme: bucket.NonConformi++; break;
            default: bucket.NonTestati++; break;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        // Cache=Private + una connessione per chiamata: senza queste pragma ogni query riparte con una
        // cache pagine minuscola (default ~2MB) su un file da centinaia di MB, rileggendo dal disco le
        // stesse pagine di test_summary/test_revision già toccate dalla query precedente nella stessa
        // apertura pagina (feedback utente 2026-09-17, volumi 100k: misurato ~800-1200ms per singola
        // query di aggregazione altrimenti banale). mmap_size affida la cache all'OS, che la mantiene
        // tra le connessioni; sola lettura, nessuna implicazione sulla durabilità dei dati condivisi.
        command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA query_only=ON; PRAGMA cache_size=-131072; PRAGMA mmap_size=536870912;";
        command.ExecuteNonQuery();
        return connection;
    }

    private sealed class RequirementVehicleComparer : IEqualityComparer<(string Requirement, string Vehicle)>
    {
        public static RequirementVehicleComparer Instance { get; } = new();
        public bool Equals((string Requirement, string Vehicle) x, (string Requirement, string Vehicle) y) =>
            string.Equals(x.Requirement, y.Requirement, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Vehicle, y.Vehicle, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Requirement, string Vehicle) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Requirement),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Vehicle));
    }
}



