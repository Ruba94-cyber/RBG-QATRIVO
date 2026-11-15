using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Snapshot immutabile e condiviso dei cataloghi master Phase D (<see cref="MasterCatalogSnapshotCache"/>): stessa istanza a
/// revisione invariata, invalidazione su salvataggio locale / modifica proveniente da un altro PC (journal) / risoluzione di
/// conflitto / ricreazione del read-model, isolamento delle copie modificabili di <c>Load()</c>, accesso concorrente,
/// budget di memoria e precaricamento.
///
/// Le asserzioni sul numero di letture usano <see cref="MasterCatalogSnapshotCache.Statistiche{T}"/> (per DB e tipo), non i
/// contatori globali di <see cref="PerfContatori"/>, che altri test paralleli possono incrementare.
/// </summary>
[Collection("MasterCatalogSnapshot")]
public sealed class MasterCatalogSnapshotTests : IDisposable
{
    private const string DeviceB = "b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_MasterSnapshot_" + Guid.NewGuid().ToString("N"));
    private readonly DataPaths _paths;
    private readonly long _limiteIniziale = MasterCatalogSnapshotCache.LimiteBytes;
    private long _sequenzaB;

    public MasterCatalogSnapshotTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new DataPaths(_root);
        new MasterDataRepository<Veicolo>(_paths.VeicoliFile).Save(new List<Veicolo>
        {
            new() { Nome = "V1", GammaNome = "g1" },
            new() { Nome = "V2", GammaNome = "g1" },
            new() { Nome = "V3", GammaNome = "g2" },
        });
        _paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(_paths);
    }

    public void Dispose()
    {
        MasterCatalogSnapshotCache.LimiteBytes = _limiteIniziale;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private MasterDataRepository<Veicolo> NuovoRepo() => new(_paths.VeicoliFile);

    private (long Costruzioni, long Riusi) Stat(MasterDataRepository<Veicolo> repo) =>
        MasterCatalogSnapshotCache.Statistiche<Veicolo>(repo.PercorsoReadModelLocale()!, "veicoli");

    private static List<string> Nomi(IEnumerable<Veicolo> v) => v.Select(x => x.Nome).OrderBy(x => x, StringComparer.Ordinal).ToList();

    /// <summary>Pubblica nel journal del device B (come se fosse arrivata da un altro PC via OneDrive) un Upsert della riga.</summary>
    private void PubblicaUpsertB(Veicolo riga, IReadOnlyDictionary<string, long> vvOsservato)
    {
        MasterEntityIdentityService.GetOrCreate(riga);
        var payload = JsonSerializer.Serialize(riga, JsonIo.CompactOptions);
        var mutazione = new PhaseDMasterMutation
        {
            MutationId = Guid.NewGuid(),
            Catalog = "veicoli",
            EntityUid = MasterEntityIdentityService.GetRequired(riga),
            Operation = PhaseDMasterMutationOperation.Upsert,
            VersionVector = PhaseDVersionVector.Increment(vvOsservato, DeviceB),
            PayloadJson = payload,
            PayloadSha256 = PhaseDMasterMutation.HashPayload(payload),
            BusinessKey = PhaseDMasterBusinessKeyService.Get(riga),
            TimestampUtc = DateTime.UtcNow
        };
        var batch = new PhaseDMasterMutationBatch
        {
            BatchId = Guid.NewGuid(),
            DeviceId = DeviceB,
            DeviceSequence = ++_sequenzaB,
            CreatedUtc = DateTime.UtcNow,
            Mutations = new List<PhaseDMasterMutation> { mutazione }
        };
        batch.Validate();
        var current = PhaseDMasterJournalService.CurrentPath(_root, DeviceB);
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.AppendAllText(current, JsonSerializer.Serialize(batch, JsonIo.CompactOptions) + Environment.NewLine);
    }

    private IReadOnlyDictionary<string, long> TestaVv(Veicolo riga) =>
        new PhaseDMasterMaterializer(_root).LoadCatalog<Veicolo>(_root, "veicoli")
            .Heads[MasterEntityIdentityService.GetRequired(riga)].VersionVector;

    [Fact]
    public void RevisioneInvariata_StessaIstanzaCondivisaFraRepositoryDiversi_SenzaRileggere()
    {
        var repo1 = NuovoRepo();
        var repo2 = NuovoRepo();

        var primo = repo1.LoadSnapshot();
        var dopoPrimo = Stat(repo1);
        var secondo = repo1.LoadSnapshot();
        var terzo = repo2.LoadSnapshot();

        Assert.Equal(new[] { "V1", "V2", "V3" }, Nomi(primo));
        Assert.Same(primo, secondo);
        Assert.Same(primo, terzo);
        Assert.Equal(dopoPrimo.Costruzioni, Stat(repo1).Costruzioni); // nessuna lettura completa dopo la prima
        Assert.True(Stat(repo1).Riusi >= dopoPrimo.Riusi + 2);
    }

    [Fact]
    public void Load_RestituisceCopiePrivate_LaMutazioneDelChiamanteNonAlteraLoSnapshot()
    {
        var repo = NuovoRepo();
        var snapshot = repo.CaricaSnapshot()!;
        var istanzeCondivise = snapshot.Items.ToList();

        var copia = repo.Load();
        Assert.Equal(3, copia.Count);
        foreach (var v in copia) { v.GammaNome = "MUTATA"; v.Archiviata = true; v.Nome += "-x"; }
        copia.Add(new Veicolo { Nome = "Extra" });
        copia.RemoveAt(0);

        // Le istanze condivise non sono quelle restituite a Load() e il loro contenuto e' intatto.
        Assert.DoesNotContain(copia, v => istanzeCondivise.Contains(v));
        Assert.True(snapshot.VerificaIntegrita());
        Assert.Equal(new[] { "g1", "g1", "g2" }, repo.LoadSnapshot().Select(v => v.GammaNome).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(repo.LoadSnapshot(), v => Assert.False(v.Archiviata));

        // Un secondo Load() parte di nuovo dal contenuto originale, e lo snapshot e' ancora la stessa istanza (nessuna rilettura).
        var costruzioni = Stat(repo).Costruzioni;
        var copia2 = repo.Load();
        Assert.Equal(new[] { "V1", "V2", "V3" }, Nomi(copia2));
        Assert.Same(snapshot.Items, repo.LoadSnapshot());
        Assert.Equal(costruzioni, Stat(repo).Costruzioni);
    }

    [Fact]
    public void SalvataggioLocale_InvalidaLoSnapshot_EIlVecchioRestaImmutabile()
    {
        var repo = NuovoRepo();
        var vecchio = repo.LoadSnapshot();
        var costruzioni = Stat(repo).Costruzioni;

        var lista = repo.Load();
        lista.Add(new Veicolo { Nome = "V4", GammaNome = "g3" });
        lista.Single(v => v.Nome == "V1").GammaNome = "g-nuova";
        repo.Save(lista);

        var nuovo = repo.LoadSnapshot();
        Assert.NotSame(vecchio, nuovo);
        Assert.Equal(new[] { "V1", "V2", "V3", "V4" }, Nomi(nuovo));
        Assert.Equal("g-nuova", nuovo.Single(v => v.Nome == "V1").GammaNome);
        Assert.Equal(costruzioni + 1, Stat(repo).Costruzioni);
        // Chi teneva il vecchio snapshot continua a vedere una fotografia coerente e invariata.
        Assert.Equal(new[] { "V1", "V2", "V3" }, Nomi(vecchio));
        Assert.Equal("g1", vecchio.Single(v => v.Nome == "V1").GammaNome);
        // E un secondo repository (altro servizio) vede subito il nuovo contenuto.
        Assert.Same(nuovo, NuovoRepo().LoadSnapshot());
    }

    [Fact]
    public void ModificaDaAltroPc_ArrivataNelJournal_VieneVistaAlPrimoAccessoSuccessivo()
    {
        var repo = NuovoRepo();
        var prima = repo.LoadSnapshot();
        var v2 = prima.Single(v => v.Nome == "V2");

        // Nuova riga + modifica di una riga esistente, pubblicate dal device B come farebbe la sync OneDrive.
        PubblicaUpsertB(new Veicolo { Nome = "VB", GammaNome = "gB" }, new Dictionary<string, long>());
        var modificata = JsonSerializer.Deserialize<Veicolo>(JsonSerializer.Serialize(v2, JsonIo.CompactOptions), JsonIo.Options)!;
        modificata.GammaNome = "gB-modificata";
        PubblicaUpsertB(modificata, TestaVv(v2));

        var dopo = repo.LoadSnapshot();
        Assert.NotSame(prima, dopo);
        Assert.Equal(new[] { "V1", "V2", "V3", "VB" }, Nomi(dopo));
        Assert.Equal("gB-modificata", dopo.Single(v => v.Nome == "V2").GammaNome);
        Assert.Equal("g1", prima.Single(v => v.Nome == "V2").GammaNome); // il vecchio snapshot non e' stato toccato
        // Anche Load() (copie modificabili) riflette la modifica esterna.
        Assert.Equal("gB-modificata", repo.Load().Single(v => v.Nome == "V2").GammaNome);

        // Nessuna ulteriore modifica => di nuovo la stessa istanza.
        Assert.Same(dopo, repo.LoadSnapshot());
    }

    [Fact]
    public void RisoluzioneConflitto_CambiaLaRevisione_EAggiornaLoSnapshot()
    {
        var repo = NuovoRepo();
        var v1 = repo.LoadSnapshot().Single(v => v.Nome == "V1");
        var uid = MasterEntityIdentityService.GetRequired(v1);
        var vvBase = TestaVv(v1);

        // PC locale modifica GammaNome; il device B (stessa base, offline) modifica Archiviata => teste concorrenti.
        var lista = repo.Load();
        lista.Single(v => v.Nome == "V1").GammaNome = "g-locale";
        repo.Save(lista);
        var altra = JsonSerializer.Deserialize<Veicolo>(JsonSerializer.Serialize(v1, JsonIo.CompactOptions), JsonIo.Options)!;
        altra.Archiviata = true;
        PubblicaUpsertB(altra, vvBase);

        var inConflitto = repo.CaricaSnapshot()!;
        Assert.Contains(uid, inConflitto.ConflictedUids);

        var conflitto = Assert.Single(PhaseDMasterConflictResolver.Elenca(_root), c => c.EntityUid == uid);
        var scelta = new PhaseDMasterMaterializer(_root).LoadConflicts(_root, "veicoli").Single(x => x.EntityUid == uid)
            .ConcurrentHeads.Single(m => JsonSerializer.Deserialize<Veicolo>(m.PayloadJson, JsonIo.Options)!.GammaNome == "g-locale");
        PhaseDMasterConflictResolver.Risolvi(_paths, "veicoli", uid,
            PhaseDMasterConflictResolver.VersionIdOf(scelta.Operation, scelta.PayloadSha256), "Mario", isMaster: true);

        var risolto = repo.CaricaSnapshot()!;
        Assert.NotSame(inConflitto, risolto);
        Assert.DoesNotContain(uid, risolto.ConflictedUids);
        Assert.Equal("g-locale", risolto.Items.Single(v => v.Nome == "V1").GammaNome);
        Assert.False(risolto.Items.Single(v => v.Nome == "V1").Archiviata);
        Assert.NotNull(conflitto);
    }

    [Fact]
    public void RicreazioneDelReadModelLocale_NonVieneConfusaConLaStessaRevisione()
    {
        var repo = NuovoRepo();
        var dbPath = repo.PercorsoReadModelLocale()!;
        var prima = repo.LoadSnapshot();
        var costruzioni = Stat(repo).Costruzioni;

        // Cancellazione esterna del read-model (ricostruibile): un nuovo repository ricrea il DB; le revisioni ripartono da
        // zero ma l'identita' del file e' diversa, quindi lo snapshot in cache non puo' essere riusato.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffisso in new[] { "", "-wal", "-shm" })
            try { File.Delete(dbPath + suffisso); } catch { }
        Thread.Sleep(50);

        var repoNuovo = NuovoRepo();
        var dopo = repoNuovo.LoadSnapshot();
        Assert.Equal(new[] { "V1", "V2", "V3" }, Nomi(dopo));
        Assert.NotSame(prima, dopo);
        Assert.True(Stat(repoNuovo).Costruzioni > costruzioni);
    }

    [Fact]
    public void AccessoConcorrente_ThreadUiEBackground_NessunaCorruzioneEUnaSolaLetturaPerRevisione()
    {
        var repo = NuovoRepo();
        MasterCatalogSnapshotCache.Svuota();
        var costruzioniIniziali = Stat(repo).Costruzioni;

        // Fase 1: 16 thread chiedono lo stesso snapshot a cache vuota => una sola lettura (single-flight).
        using (var via = new ManualResetEventSlim())
        {
            var istanze = new IReadOnlyList<Veicolo>[16];
            var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
            {
                via.Wait();
                istanze[i] = NuovoRepo().LoadSnapshot();
            })).ToArray();
            via.Set();
            Task.WaitAll(tasks);
            Assert.All(istanze, s => Assert.Same(istanze[0], s));
            Assert.Equal(costruzioniIniziali + 1, Stat(repo).Costruzioni);
        }

        // Fase 2: lettori (snapshot + copie modificabili) in parallelo a uno scrittore.
        var errori = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var stop = new CancellationTokenSource();
        var lettori = Enumerable.Range(0, 6).Select(i => Task.Run(() =>
        {
            var r = NuovoRepo();
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (i % 2 == 0)
                    {
                        var snapshot = r.CaricaSnapshot()!;
                        if (snapshot.Items.Count < 3) throw new InvalidOperationException("snapshot incompleto");
                        if (!snapshot.VerificaIntegrita()) throw new InvalidOperationException("snapshot alterato");
                    }
                    else
                    {
                        var copia = r.Load();
                        if (copia.Count < 3) throw new InvalidOperationException("copia incompleta");
                        foreach (var v in copia) v.GammaNome = "scribacchiata"; // non deve mai toccare lo snapshot
                    }
                }
                catch (Exception ex) { errori.Enqueue(ex); }
            }
        })).ToArray();

        var scrittore = NuovoRepo();
        for (var n = 0; n < 10; n++)
        {
            var lista = scrittore.Load();
            lista.Add(new Veicolo { Nome = "W" + n, GammaNome = "gw" });
            scrittore.Save(lista);
        }
        stop.Cancel();
        Task.WaitAll(lettori);

        Assert.Empty(errori);
        var finale = NuovoRepo().CaricaSnapshot()!;
        Assert.Equal(13, finale.Items.Count);
        Assert.True(finale.VerificaIntegrita());
        Assert.DoesNotContain(finale.Items, v => v.GammaNome == "scribacchiata");
    }

    [Fact]
    public void BudgetDiMemoria_UnCatalogoOltreIlLimiteNonVieneTrattenuto()
    {
        var repo = NuovoRepo();
        MasterCatalogSnapshotCache.Svuota();
        MasterCatalogSnapshotCache.LimiteBytes = 1; // nulla entra

        var a = repo.LoadSnapshot();
        var b = repo.LoadSnapshot();

        Assert.Equal(new[] { "V1", "V2", "V3" }, Nomi(a));
        Assert.NotSame(a, b); // comportamento di sempre: lettura integrale a ogni accesso
        Assert.Equal(0, MasterCatalogSnapshotCache.NumeroSnapshot);
    }

    [Fact]
    public async Task Precarica_RiportaProgressoRealeEPoiLeLettureSonoServiteDallaCache()
    {
        var store = new MasterDataStore(_paths);
        MasterCatalogSnapshotCache.Svuota();
        var passi = new System.Collections.Concurrent.ConcurrentQueue<MasterPrecaricamentoProgress>();

        await store.PrecaricaAsync(new SincronoProgress<MasterPrecaricamentoProgress>(p => passi.Enqueue(p)));

        var elenco = passi.ToList();
        Assert.Equal(13, elenco.Count);
        Assert.All(elenco, p => Assert.Equal(13, p.Totale));
        Assert.Equal(Enumerable.Range(1, 13), elenco.Select(p => p.Completati));
        Assert.Equal(3, elenco.Single(p => p.Catalogo == "Veicoli").Elementi);

        var costruzioni = Stat(store.Veicoli).Costruzioni;
        var snapshot = store.Veicoli.LoadSnapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(costruzioni, Stat(store.Veicoli).Costruzioni);
    }

    private sealed class SincronoProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SincronoProgress(Action<T> a) => _a = a;
        public void Report(T value) => _a(value);
    }
}
