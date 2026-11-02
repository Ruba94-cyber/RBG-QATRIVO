using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Regressione: <see cref="PhaseDMasterJournalService.NextDeviceSequence"/> indicizzava il contatore LOCALE per
/// hash del PERCORSO della cartella dati condivisa. Se lo STESSO device (stessa DeviceIdentityService) apre la
/// STESSA cartella Phase D da un percorso diverso (copia, spostamento, rinomina, zip riestratto, OneDrive che
/// crea "Cartella(1)"), il contatore locale ripartiva da zero anche quando il journal condiviso COPIATO conteneva
/// gia' un checkpoint di questo stesso device con CoveredThroughDeviceSequence alto: la mutation successiva
/// riceveva una DeviceSequence gia' coperta, il materializer la trattava come stantia (RemoveCoveredNonCheckpoint
/// Mappings) e il salvataggio falliva con PhaseDMasterConflictException, lasciando un batch orfano nel journal.
///
/// La sequenza deve invece essere sempre strettamente maggiore di ogni DeviceSequence che QUESTO device ha gia'
/// pubblicato nel journal condiviso DELLA CARTELLA (checkpoint incluso), oltre che del contatore locale.
/// </summary>
public sealed class PhaseDMasterJournalDeviceSequencePortabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_SeqPortability_Root_" + Guid.NewGuid().ToString("N"));
    private readonly string _copy = Path.Combine(Path.GetTempPath(), "TestLoom_SeqPortability_Copy_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_SeqPortability_Local_" + Guid.NewGuid().ToString("N"));

    public PhaseDMasterJournalDeviceSequencePortabilityTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDelete(_root);
        TryDelete(_copy);
        TryDelete(_local);
    }

    [Fact]
    public void CartellaCopiataStessoDevice_SalvataggioNonRiassegnaSequenzaGiaCopertaDalCheckpoint()
    {
        var deviceId = DeviceIdentityService.GetOrCreate();
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);

        // Simula lo stato reale segnalato: un checkpoint di QUESTO device gia' avanti a 149, prodotto da
        // scritture/rotazioni/compattazioni precedenti sul percorso originale (vedi PhaseDMasterStorageTests
        // per lo stesso pattern di sintesi diretta del checkpoint, che evita di dover scrivere >2MB di dati
        // reali solo per forzare RotateAfterBytes/CompactAfterSegments).
        var seedUid = Guid.NewGuid();
        var seedMutation = Upsert(new Funzionalita { Uid = seedUid, Nome = "FUN-SEQ-SEED" }, deviceId,
            new Dictionary<string, long> { [deviceId] = 149 });
        WriteCheckpointGeneration(deviceId, coveredThrough: 149, seedMutation);

        // Sul percorso ORIGINALE il contatore locale e' coerente col checkpoint: nessun bug su questo percorso.
        WriteLocalSequenceCounter(_local, _root, deviceId, 149);

        CopyDirectory(_root, _copy);

        // Stesso device, stesso %LOCALAPPDATA% (_local), ma percorso della cartella dati DIVERSO: la chiave del
        // contatore locale (hash del percorso) e' quindi nuova, come nel difetto reale.
        var engineOnCopy = new PhaseDMasterRepositoryEngine<Funzionalita>(_copy, "funzionalita", localBaseDirectory: _local);
        var loaded = engineOnCopy.Load();
        var seed = Assert.Single(loaded.Items);
        Assert.Equal("FUN-SEQ-SEED", seed.Nome);

        var nuova = new Funzionalita { Uid = Guid.NewGuid(), Nome = "FUN-SEQ-NUOVA" };

        // Prima del fix questa chiamata falliva con PhaseDMasterConflictException: la nuova mutation veniva
        // pubblicata con DeviceSequence=1 (contatore locale ripartito da zero sul nuovo percorso) e il
        // materializer la scartava perche' <= CoveredThroughDeviceSequence=149 del checkpoint copiato.
        var ex = Record.Exception(() => engineOnCopy.SavePatch(new List<Funzionalita> { nuova }, new List<Funzionalita>()));
        Assert.Null(ex);

        var afterSave = engineOnCopy.Load();
        Assert.Equal(2, afterSave.Items.Count);
        Assert.Contains(afterSave.Items, x => x.Nome == "FUN-SEQ-NUOVA");

        // La DeviceSequence realmente assegnata deve superare quanto gia' coperto dal checkpoint copiato: mai
        // un riuso di un numero gia' pubblicato da questo device.
        var batches = ReadDeviceBatches(_copy, deviceId);
        var batch = Assert.Single(batches);
        Assert.True(batch.DeviceSequence > 149,
            $"DeviceSequence {batch.DeviceSequence} non e' strettamente maggiore del checkpoint copiato (149).");

        // Una seconda postazione (device diverso) che scrive sulla STESSA cartella copiata deve continuare a
        // vedere e convergere correttamente, senza che il fix sul contatore locale del primo device interferisca.
        var deviceRemoto = "22222222222222222222222222222222";
        var remotoUid = Guid.NewGuid();
        WriteCurrentBatch(_copy, deviceRemoto, 1, Upsert(
            new Funzionalita { Uid = remotoUid, Nome = "FUN-SEQ-REMOTA" }, deviceRemoto,
            new Dictionary<string, long> { [deviceRemoto] = 1 }));

        var materializer = new PhaseDMasterMaterializer(_copy, _local);
        var snapshot = materializer.LoadCatalog<Funzionalita>(_copy, "funzionalita");
        Assert.Equal(3, snapshot.Items.Count);
        Assert.Contains(snapshot.Items, x => x.Nome == "FUN-SEQ-REMOTA");
        Assert.All(snapshot.Heads.Values, h => Assert.False(h.HasConflict));
    }

    /// <summary>
    /// Caso residuo documentato dal commit af41038 (fix "la sequenza device Phase D non riparte piu' da zero se
    /// la cartella dati viene copiata/spostata"): quel fix rende NextDeviceSequence portabile fra percorsi
    /// DIVERSI dello stesso device, ma non introduce alcuna coordinazione CROSS-PROCESSO oltre al file di lock
    /// per-percorso di NextDeviceSequence stesso — <see cref="PhaseDMasterJournalService.Append"/> serializza le
    /// proprie scritture su current.ndjson solo con un <c>lock</c> .NET statico (in-process), non con un lock su
    /// file. Due processi realmente distinti (stesso device, stessa identita' DeviceIdentityService, es. l'utente
    /// che avvia l'app due volte sulla stessa macchina, o un'istanza precedente non terminata) che aprono in
    /// parallelo la STESSA cartella condivisa non sono quindi protetti dal lock statico, che vive solo nel
    /// singolo processo .NET.
    ///
    /// Questo test simula la stessa identita' di device (DeviceIdentityService.GetOrCreate() e' cached per
    /// processo: qui e' inevitabilmente lo stesso device sia per la "postazione 1" che per la "postazione 2",
    /// esattamente come richiesto) con DUE PhaseDMasterRepositoryEngine distinti sulla STESSA cartella radice,
    /// e chiama SavePatch concorrentemente da due thread sincronizzati da un Barrier. Verifica il comportamento
    /// REALE osservato: se emerge una collisione (eccezione non gestita diversa da quelle di conflitto note, o
    /// perdita di una delle due entita' salvate), il test viene lasciato a testimonianza del bug con
    /// [Trait("Category","BugAperto")] e un commento con l'evidenza osservata, SENZA correggere il codice
    /// applicativo — la decisione su come/se correggere spetta a chi rivede la segnalazione.
    /// </summary>
    [Fact]
    [Trait("Category", "Concorrenza")]
    public async Task DueIstanzeStessoDeviceStessaCartellaCondivisa_SavePatchConcorrente_NessunaPerditaENessunaEccezioneNonGestita()
    {
        var deviceId = DeviceIdentityService.GetOrCreate();
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);

        // Stesso device (stesso processo => stesso DeviceIdentityService cached), stessa cartella radice, stessa
        // localBaseDirectory: la coppia di engine qui sotto e' l'equivalente piu' fedele, dentro un solo processo
        // di test, di "due istanze dell'app aperte in parallelo sulla stessa macchina sulla stessa cartella".
        var engine1 = new PhaseDMasterRepositoryEngine<Funzionalita>(_root, "funzionalita", localBaseDirectory: _local);
        var engine2 = new PhaseDMasterRepositoryEngine<Funzionalita>(_root, "funzionalita", localBaseDirectory: _local);

        var nuova1 = new Funzionalita { Uid = Guid.NewGuid(), Nome = "FUN-DUEISTANZE-001" };
        var nuova2 = new Funzionalita { Uid = Guid.NewGuid(), Nome = "FUN-DUEISTANZE-002" };

        var barrier = new Barrier(2);
        Exception? ex1 = null, ex2 = null;

        var t1 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            try { engine1.SavePatch(new List<Funzionalita> { nuova1 }, new List<Funzionalita>()); }
            catch (Exception ex) { ex1 = ex; }
        });
        var t2 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            try { engine2.SavePatch(new List<Funzionalita> { nuova2 }, new List<Funzionalita>()); }
            catch (Exception ex) { ex2 = ex; }
        });
        await Task.WhenAll(t1, t2);

        // Le uniche eccezioni "gestite" attese per una scrittura concorrente sono i conflitti Phase D noti
        // (PhaseDMasterConflictException/PhaseDMasterBusinessKeyConflictException/IOException di sharing sul
        // file di lock, con retry gia' incorporato in NextDeviceSequence): qualunque ALTRA eccezione, o un esito
        // "nessuna eccezione ma una delle due entita' e' andata perduta", e' la collisione residua documentata
        // da af41038 e va lasciata a testimonianza, non corretta qui.
        var eccezioniInattese = new[] { ex1, ex2 }
            .Where(e => e != null)
            .Where(e => e is not PhaseDMasterConflictException && e is not PhaseDMasterBusinessKeyConflictException && e is not IOException)
            .ToList();

        var materializer = new PhaseDMasterMaterializer(_root, _local);
        var snapshotFinale = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        var entrambeSalvate = snapshotFinale.Items.Any(x => x.Nome == "FUN-DUEISTANZE-001") &&
                              snapshotFinale.Items.Any(x => x.Nome == "FUN-DUEISTANZE-002");
        var esattamenteUnaFallita = (ex1 == null) != (ex2 == null);

        if (eccezioniInattese.Count > 0 || (!entrambeSalvate && ex1 == null && ex2 == null))
        {
            Assert.Fail(
                "BUG APERTO (residuo af41038, vedi commento sul test): SavePatch concorrente da due istanze " +
                "dello stesso device sulla stessa cartella condivisa ha prodotto un esito anomalo. " +
                $"ex1={DescriviEccezione(ex1)}; ex2={DescriviEccezione(ex2)}; " +
                $"FUN-DUEISTANZE-001 presente={snapshotFinale.Items.Any(x => x.Nome == "FUN-DUEISTANZE-001")}; " +
                $"FUN-DUEISTANZE-002 presente={snapshotFinale.Items.Any(x => x.Nome == "FUN-DUEISTANZE-002")}; " +
                "-> se questo Assert.Fail scatta in modo riproducibile, ri-etichettare il [Fact] con " +
                "[Trait(\"Category\",\"BugAperto\")] e riportare qui i valori osservati, senza modificare " +
                "PhaseDMasterJournalService/PhaseDMasterRepositoryEngine.");
        }

        // Nessuna perdita silenziosa: se entrambe le SavePatch sono riuscite, entrambe le entita' devono essere
        // visibili dopo la materializzazione; se una e' stata rifiutata con un conflitto noto, va bene che sia
        // assente (l'operatore ripeterebbe l'operazione), ma MAI un'eccezione sconosciuta né una perdita muta.
        if (ex1 == null && ex2 == null) Assert.True(entrambeSalvate, "Entrambe le SavePatch sono riuscite ma una delle due entita' e' assente dopo la materializzazione.");
        else Assert.True(esattamenteUnaFallita || (ex1 != null && ex2 != null), "Stato di fallimento inatteso.");
    }

    private static string DescriviEccezione(Exception? ex) =>
        ex == null ? "(nessuna)" : $"{ex.GetType().Name}: {ex.Message}";

    [Fact]
    public void NextDeviceSequence_ContatoreLocalePiuAvantiDelJournal_NonTornaMaiIndietro()
    {
        var deviceId = DeviceIdentityService.GetOrCreate();
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);

        // Checkpoint condiviso indietro rispetto al contatore locale (es. un append locale precedente e' andato
        // a buon fine sul contatore ma la compattazione/il checkpoint remoto non e' ancora arrivato tramite
        // OneDrive): il floor del journal (5) e' PIU' BASSO del contatore locale gia' persistito (10).
        var seedMutation = Upsert(new Funzionalita { Uid = Guid.NewGuid(), Nome = "FUN-SEQ-LOCALE-AVANTI" }, deviceId,
            new Dictionary<string, long> { [deviceId] = 5 });
        WriteCheckpointGeneration(deviceId, coveredThrough: 5, seedMutation);
        WriteLocalSequenceCounter(_local, _root, deviceId, 10);

        var next = PhaseDMasterJournalService.NextDeviceSequence(_root, _local);

        Assert.Equal(11, next);
    }

    [Fact]
    public void NextDeviceSequence_CartellaNuovaSenzaJournalDelDevice_PartePrimaSequenzaDaUno()
    {
        var deviceId = DeviceIdentityService.GetOrCreate();
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);

        // Nessun checkpoint, nessun current.ndjson, nessun contatore locale per questo device: il comportamento
        // di base (cartella davvero nuova) non deve cambiare rispetto a prima del fix.
        var next = PhaseDMasterJournalService.NextDeviceSequence(_root, _local);

        Assert.Equal(1, next);
    }

    private static void WriteLocalSequenceCounter(string localBaseDirectory, string root, string deviceId, long value)
    {
        var dir = Path.Combine(localBaseDirectory, WorkspaceKey(root), deviceId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sequence.txt"), value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Replica ESATTA di PhaseDMasterJournalService.NormalizeRoot (privato): il test deve scrivere il
    /// contatore locale nella stessa directory che il codice di produzione leggera' per lo stesso percorso.</summary>
    private static string WorkspaceKey(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalized = OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))[..32];
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destinationDir, Path.GetRelativePath(sourceDir, dir)));
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destinationDir, Path.GetRelativePath(sourceDir, file)), overwrite: false);
    }

    private static void WriteCurrentBatch(string root, string deviceId, long sequence, params PhaseDMasterMutation[] mutations)
    {
        var batch = new PhaseDMasterMutationBatch
        {
            BatchId = Guid.NewGuid(),
            DeviceId = deviceId,
            DeviceSequence = sequence,
            CreatedUtc = DateTime.UtcNow,
            Mutations = mutations.ToList()
        };
        batch.Validate();
        var path = PhaseDMasterJournalService.CurrentPath(root, deviceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(batch, JsonIo.CompactOptions) + Environment.NewLine);
    }

    private static List<PhaseDMasterMutationBatch> ReadDeviceBatches(string root, string deviceId)
    {
        var path = PhaseDMasterJournalService.CurrentPath(root, deviceId);
        var result = new List<PhaseDMasterMutationBatch>();
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            result.Add(JsonSerializer.Deserialize<PhaseDMasterMutationBatch>(line, JsonIo.CompactOptions)!);
        }
        return result;
    }

    /// <summary>Race stesso device/stesso processo: NextDeviceSequence legge current.ndjson (floor) mentre un'altra
    /// thread vi sta appendendo. Prima del fix la lettura non era coperta dal lock di Append e vedeva una riga
    /// parziale o una violazione di condivisione (FileShare.Read del writer) -> InvalidDataException fail-closed
    /// spuria. Il journal non era corrotto (Append e' serializzato): il floor deve attendere l'append in corso.</summary>
    [Fact]
    public async Task NextDeviceSequenceConcorrenteConAppend_NonVedeMaiAppendInCorso()
    {
        var deviceId = DeviceIdentityService.GetOrCreate();
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);

        const int appends = 400;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var allocated = new System.Collections.Concurrent.ConcurrentBag<long>();
        using var barrier = new Barrier(2);

        void Worker()
        {
            barrier.SignalAndWait();
            for (var i = 0; i < appends; i++)
            {
                try
                {
                    var seq = PhaseDMasterJournalService.NextDeviceSequence(_root, _local);
                    allocated.Add(seq);
                    var uid = Guid.NewGuid();
                    var m = Upsert(new Funzionalita { Uid = uid, Nome = "FUN-RACE-" + uid.ToString("N") }, deviceId,
                        new Dictionary<string, long> { [deviceId] = seq });
                    PhaseDMasterJournalService.Append(paths, new PhaseDMasterMutationBatch
                    {
                        DeviceId = deviceId, DeviceSequence = seq, Mutations = new List<PhaseDMasterMutation> { m }
                    });
                }
                catch (Exception ex) { errors.Enqueue(ex); }
            }
        }

        var t1 = Task.Run(Worker);
        var t2 = Task.Run(Worker);
        await Task.WhenAll(t1, t2);

        Assert.True(errors.IsEmpty, errors.TryPeek(out var first) ? first.ToString() : "");
        Assert.Equal(2 * appends, allocated.Distinct().Count());
        // Il journal finale e' integro e il contatore successivo supera tutto cio' che e' stato pubblicato.
        Assert.True(PhaseDMasterJournalService.NextDeviceSequence(_root, _local) > allocated.Max());
    }

    private static PhaseDMasterMutation Upsert(Funzionalita entity, string ownerDevice, Dictionary<string, long> vector)
    {
        var payload = JsonSerializer.Serialize(entity, JsonIo.CompactOptions);
        return new PhaseDMasterMutation
        {
            MutationId = Guid.NewGuid(),
            Catalog = "funzionalita",
            EntityUid = entity.Uid,
            Operation = PhaseDMasterMutationOperation.Upsert,
            VersionVector = vector,
            PayloadJson = payload,
            PayloadSha256 = PhaseDMasterMutation.HashPayload(payload),
            BusinessKey = entity.Nome,
            TimestampUtc = DateTime.UtcNow
        };
    }

    private void WriteCheckpointGeneration(string deviceId, long coveredThrough, PhaseDMasterMutation mutation)
    {
        var generationId = Guid.NewGuid();
        var checkpointDir = PhaseDMasterJournalService.CheckpointDirectory(_root, deviceId);
        Directory.CreateDirectory(checkpointDir);
        var fileName = $"gen-{generationId:N}-shard-00.json.gz";
        var path = Path.Combine(checkpointDir, fileName);
        var shard = new PhaseDMasterCheckpointShard
        {
            DeviceId = deviceId,
            GenerationId = generationId,
            CoveredThroughDeviceSequence = coveredThrough,
            Shard = 0,
            CompactedUtc = DateTime.UtcNow,
            LatestByEntity = new Dictionary<string, PhaseDMasterMutation>(StringComparer.Ordinal)
            {
                [$"{mutation.Catalog}:{mutation.EntityUid:N}"] = mutation
            }
        };

        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
            JsonSerializer.Serialize(gzip, shard, JsonIo.Options);

        var bytes = File.ReadAllBytes(path);
        var generation = new PhaseDMasterCheckpointGeneration
        {
            GenerationId = generationId,
            CoveredThroughDeviceSequence = coveredThrough,
            CreatedUtc = DateTime.UtcNow,
            Shards = new Dictionary<int, PhaseDMasterCheckpointShardReference>
            {
                [0] = new()
                {
                    FileName = fileName,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    Length = bytes.LongLength
                }
            }
        };

        JsonIo.WriteAtomic(PhaseDMasterJournalService.CheckpointManifestPath(_root, deviceId),
            new PhaseDMasterCheckpointManifest
            {
                DeviceId = deviceId,
                Current = generation,
                Previous = null
            });
    }

    private static void TryDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
