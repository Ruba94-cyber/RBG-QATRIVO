using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Gate di regressione della source of truth master Phase D. I device remoti sono simulati come li vedrebbe
/// davvero una replica OneDrive: ciascuno consegna il proprio current.ndjson nella cartella condivisa, senza
/// modificare o impersonare DeviceIdentityService del PC che esegue i test. Il materializer deve quindi
/// riconciliare esclusivamente i byte condivisi e il proprio SQLite locale ricostruibile.
/// </summary>
public sealed class PhaseDMasterStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseD_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseD_Local_" + Guid.NewGuid().ToString("N"));

    public PhaseDMasterStorageTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_local, recursive: true); } catch { }
    }

    [Fact]
    public void CompatibilityGate_AttivaPhaseDEIlLegacyNonRestaSourceOfTruth()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        JsonIo.WriteAtomic(paths.RequisitiFile, new List<Requisito>
        {
            new() { Id = "REQ-PD-001", Testo = "baseline legacy", ModificatoDa = "legacy" }
        });

        var health = CondivisioneHealthService.Verifica(_root);
        Assert.Equal(StatoCondivisione.Operativa, health.Stato);

        var result = CompatibilitaVersioneService.Applica(paths, health, new Version(6, 1, 0));

        Assert.Equal(StatoCondivisione.Operativa, result.Stato);
        Assert.True(PhaseDMasterBootstrapService.IsReady(_root));

        var repo = new MasterDataRepository<Requisito>(paths.RequisitiFile);
        var fromPhaseD = repo.Load();
        Assert.Single(fromPhaseD);
        Assert.Equal("baseline legacy", fromPhaseD[0].Testo);

        // Dopo ready.json il monolite e' solo materiale legacy/import: persino una sua riscrittura non deve
        // alterare la vista autorevole restituita dal repository.
        JsonIo.WriteAtomic(paths.RequisitiFile, new List<Requisito>());
        var stillPhaseD = repo.Load();
        Assert.Single(stillPhaseD);
        Assert.Equal("REQ-PD-001", stillPhaseD[0].Id);
    }

    [Fact]
    public void Materializer_QuindiciDeviceDisgiunti_ConvergonoSenzaPerdite()
    {
        PreparePhaseD();
        var expected = new Dictionary<Guid, string>();

        for (var i = 0; i < 15; i++)
        {
            var device = Device(i);
            var entity = new Funzionalita { Uid = Guid.NewGuid(), Nome = $"FUN-PD-{i:D2}" };
            expected[entity.Uid] = entity.Nome;
            WriteCurrentBatch(device, 1, Upsert(entity, device, new Dictionary<string, long> { [device] = 1 }));
        }

        var materializer = new PhaseDMasterMaterializer(_root, _local);
        var snapshot = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");

        Assert.Equal(15, snapshot.Items.Count);
        Assert.Equal(15, snapshot.Heads.Count);
        foreach (var item in snapshot.Items)
            Assert.Equal(expected[item.Uid], item.Nome);
        Assert.All(snapshot.Heads.Values, head => Assert.False(head.HasConflict));
    }

    /// <summary>Regressione della cache mtime+length su current.ndjson (fix prestazioni 2026-09-24, navigazione
    /// lenta): il Refresh() non deve mai restituire dati stantii dopo una SCRITTURA LOCALE sullo stesso
    /// current.ndjson gia' fingerprintato in un Refresh precedente, anche quando lo skip-se-invariato e' attivo
    /// per quel file.</summary>
    [Fact]
    public void Refresh_ScritturaLocaleSuCurrentGiaFingerprintato_NonRestaStantio()
    {
        PreparePhaseD();
        var uid = Guid.NewGuid();
        var device = Device(0);

        WriteCurrentBatch(device, 1, Upsert(
            new Funzionalita { Uid = uid, Nome = "Prima-Versione" }, device, new Dictionary<string, long> { [device] = 1 }));

        var materializer = new PhaseDMasterMaterializer(_root, _local);
        var primo = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal("Prima-Versione", Assert.Single(primo.Items).Nome);

        // Refresh ripetuto SENZA alcuna scrittura: deve restare stabile (percorso skip-se-invariato).
        var ripetuto = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal("Prima-Versione", Assert.Single(ripetuto.Items).Nome);
        Assert.Equal(primo.SourceFingerprint, ripetuto.SourceFingerprint);

        // Scrittura locale successiva sullo STESSO file (stesso pattern di un Save applicativo): il fingerprint
        // mtime+length cambia, quindi il prossimo Refresh() deve rileggerlo e vedere il valore aggiornato.
        WriteCurrentBatch(device, 2, Upsert(
            new Funzionalita { Uid = uid, Nome = "Seconda-Versione" }, device, new Dictionary<string, long> { [device] = 2 }));

        var dopoScrittura = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal("Seconda-Versione", Assert.Single(dopoScrittura.Items).Nome);
        Assert.NotEqual(primo.SourceFingerprint, dopoScrittura.SourceFingerprint);
    }

    /// <summary>Stesso principio della precedente, ma la scrittura arriva da una SECONDA ISTANZA (un altro
    /// device/PC che condivide la stessa cartella dati, come da requisito multi-PC): il proprio current.ndjson e'
    /// un file diverso da quello del device locale, quindi il fingerprint per-file dello skip non lo nasconde.</summary>
    [Fact]
    public void Refresh_ScritturaDaSecondaIstanzaSullaStessaCartella_VieneVistaSenzaRitardo()
    {
        PreparePhaseD();
        var uidLocale = Guid.NewGuid();
        var uidRemota = Guid.NewGuid();
        var deviceLocale = Device(0);
        var deviceRemoto = Device(1);

        WriteCurrentBatch(deviceLocale, 1, Upsert(
            new Funzionalita { Uid = uidLocale, Nome = "Locale" }, deviceLocale, new Dictionary<string, long> { [deviceLocale] = 1 }));

        var materializer = new PhaseDMasterMaterializer(_root, _local);
        var primo = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Single(primo.Items);

        // Refresh senza cambiamenti: percorso skip attivo per il device locale.
        materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");

        // Una "seconda istanza" pubblica sul proprio current.ndjson (device diverso, come farebbe un altro PC
        // sulla stessa cartella OneDrive/SharePoint condivisa).
        WriteCurrentBatch(deviceRemoto, 1, Upsert(
            new Funzionalita { Uid = uidRemota, Nome = "Remota" }, deviceRemoto, new Dictionary<string, long> { [deviceRemoto] = 1 }));

        var dopo = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal(2, dopo.Items.Count);
        Assert.Contains(dopo.Items, x => x.Nome == "Remota");
        Assert.Contains(dopo.Items, x => x.Nome == "Locale");
    }

    [Fact]
    public void Materializer_StessaUidModificataInConcorrenza_EsponeConflittoNonLastWriterWins()
    {
        PreparePhaseD();
        var uid = Guid.NewGuid();
        var a = Device(1);
        var b = Device(2);

        WriteCurrentBatch(a, 1, Upsert(
            new Funzionalita { Uid = uid, Nome = "Valore-A" },
            a,
            new Dictionary<string, long> { [a] = 1 }));
        WriteCurrentBatch(b, 1, Upsert(
            new Funzionalita { Uid = uid, Nome = "Valore-B" },
            b,
            new Dictionary<string, long> { [b] = 1 }));

        var materializer = new PhaseDMasterMaterializer(_root, _local);

        // Contratto attuale: la lettura del catalogo NON lancia; la voce in conflitto compare con la testa deterministica
        // e il suo Uid e' esposto in ConflictedUids. Il dettaglio delle teste resta in LoadConflicts.
        var snapshot = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal(new[] { uid }, snapshot.ConflictedUids);
        Assert.Single(snapshot.Items);
        var conflicts = materializer.LoadConflicts(_root, "funzionalita");
        var conflict = Assert.Single(conflicts);
        Assert.Equal(uid, conflict.EntityUid);
        Assert.True(conflict.HasConflict);
        Assert.Equal(2, conflict.ConcurrentHeads.Count);
    }

    [Fact]
    public void Materializer_DeleteVsUpdateConcorrenti_RestaConflittoEsplicito()
    {
        PreparePhaseD();
        var uid = Guid.NewGuid();
        var a = Device(3);
        var b = Device(4);
        var c = Device(5);

        WriteCurrentBatch(a, 1, Upsert(
            new Funzionalita { Uid = uid, Nome = "Base" },
            a,
            new Dictionary<string, long> { [a] = 1 }));
        WriteCurrentBatch(b, 1, Upsert(
            new Funzionalita { Uid = uid, Nome = "Aggiornata" },
            b,
            new Dictionary<string, long> { [a] = 1, [b] = 1 }));
        WriteCurrentBatch(c, 1, Delete(
            uid,
            c,
            new Dictionary<string, long> { [a] = 1, [c] = 1 }));

        var materializer = new PhaseDMasterMaterializer(_root, _local);

        // Lettura non bloccata (vedi Materializer_StessaUidModificataInConcorrenza): la voce e' esposta come in conflitto.
        Assert.Equal(new[] { uid }, materializer.LoadCatalog<Funzionalita>(_root, "funzionalita").ConflictedUids);
        var conflict = Assert.Single(materializer.LoadConflicts(_root, "funzionalita"));
        Assert.Contains(conflict.ConcurrentHeads, x => x.Operation == PhaseDMasterMutationOperation.Upsert);
        Assert.Contains(conflict.ConcurrentHeads, x => x.Operation == PhaseDMasterMutationOperation.Delete);
    }

    [Fact]
    public void Materializer_DeleteCausaleCheHaOsservatoUpdate_DominaSenzaResurrezione()
    {
        PreparePhaseD();
        var uid = Guid.NewGuid();
        var a = Device(6);
        var b = Device(7);
        var c = Device(8);

        WriteCurrentBatch(a, 1, Upsert(
            new Funzionalita { Uid = uid, Nome = "Base" },
            a,
            new Dictionary<string, long> { [a] = 1 }));
        WriteCurrentBatch(b, 1, Upsert(
            new Funzionalita { Uid = uid, Nome = "Aggiornata" },
            b,
            new Dictionary<string, long> { [a] = 1, [b] = 1 }));
        WriteCurrentBatch(c, 1, Delete(
            uid,
            c,
            new Dictionary<string, long> { [a] = 1, [b] = 1, [c] = 1 }));

        var materializer = new PhaseDMasterMaterializer(_root, _local);
        var snapshot = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");

        Assert.Empty(snapshot.Items);
        var head = Assert.Single(snapshot.Heads).Value;
        Assert.Equal(uid, head.EntityUid);
        Assert.Equal(PhaseDMasterMutationOperation.Delete, head.Operation);
        Assert.False(head.HasConflict);
    }

    [Fact]
    public void Materializer_RenameConUidStabile_ConservaIdentitaESostituisceChiaveBusiness()
    {
        PreparePhaseD();
        var uid = Guid.NewGuid();
        var a = Device(9);
        var b = Device(10);

        WriteCurrentBatch(a, 1, Upsert(
            new Funzionalita { Uid = uid, Nome = "Nome vecchio" },
            a,
            new Dictionary<string, long> { [a] = 1 }));
        WriteCurrentBatch(b, 1, Upsert(
            new Funzionalita { Uid = uid, Nome = "Nome nuovo" },
            b,
            new Dictionary<string, long> { [a] = 1, [b] = 1 }));

        var materializer = new PhaseDMasterMaterializer(_root, _local);
        var snapshot = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");

        var item = Assert.Single(snapshot.Items);
        Assert.Equal(uid, item.Uid);
        Assert.Equal("Nome nuovo", item.Nome);
        var head = Assert.Single(snapshot.Heads).Value;
        Assert.Equal("Nome nuovo", head.BusinessKey);
        Assert.False(head.HasConflict);
    }

    [Fact]
    public void Checkpoint_ManifestCurrentArrivaPrimaDelloShard_UsaPreviousIntegrale()
    {
        PreparePhaseD();
        var device = Device(11);
        var uid = Guid.NewGuid();
        var previousMutation = Upsert(
            new Funzionalita { Uid = uid, Nome = "Previous-safe" },
            device,
            new Dictionary<string, long> { [device] = 1 });

        var previous = WriteCheckpointGeneration(device, 1, previousMutation);
        var missingGenerationId = Guid.NewGuid();
        var current = new PhaseDMasterCheckpointGeneration
        {
            GenerationId = missingGenerationId,
            CoveredThroughDeviceSequence = 2,
            CreatedUtc = DateTime.UtcNow,
            Shards = new Dictionary<int, PhaseDMasterCheckpointShardReference>
            {
                [0] = new()
                {
                    FileName = $"gen-{missingGenerationId:N}-shard-00.json.gz",
                    Sha256 = new string('A', 64),
                    Length = 123
                }
            }
        };
        Directory.CreateDirectory(PhaseDMasterJournalService.CheckpointDirectory(_root, device));
        JsonIo.WriteAtomic(PhaseDMasterJournalService.CheckpointManifestPath(_root, device),
            new PhaseDMasterCheckpointManifest
            {
                DeviceId = device,
                Current = current,
                Previous = previous
            });

        var materializer = new PhaseDMasterMaterializer(_root, _local);
        var snapshot = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");

        var item = Assert.Single(snapshot.Items);
        Assert.Equal(uid, item.Uid);
        Assert.Equal("Previous-safe", item.Nome);
    }

    [Fact]
    [Trait("Category", "Scale")]
    public void Save_100kPoiUnaModifica_PubblicaUnaSolaMutationNelNuovoCurrent()
    {
        PreparePhaseD();
        var engine = new PhaseDMasterRepositoryEngine<Funzionalita>(
            _root,
            "funzionalita",
            localBaseDirectory: _local);
        var initial = Enumerable.Range(0, 100_000)
            .Select(i => new Funzionalita
            {
                Uid = Guid.NewGuid(),
                Nome = $"FUN-SCALE-{i:D6}",
                ModificatoDa = "scale-seed"
            })
            .ToList();

        engine.Save(initial, loadedState: null, expectedSnapshot: null);
        var loaded = engine.Load();
        var edited = loaded.Items[50_000];
        edited.Nome += "-EDIT";
        edited.ModificatoDa = "scale-edit";
        engine.Save(loaded.Items, loaded.State, expectedSnapshot: null);

        var current = PhaseDMasterJournalService.CurrentPath(_root, DeviceIdentityService.GetOrCreate());
        Assert.True(File.Exists(current));
        var lines = File.ReadLines(current).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var line = Assert.Single(lines);
        var batch = JsonSerializer.Deserialize<PhaseDMasterMutationBatch>(line, JsonIo.CompactOptions);
        Assert.NotNull(batch);
        var mutation = Assert.Single(batch!.Mutations);
        Assert.Equal(edited.Uid, mutation.EntityUid);
        Assert.Equal(PhaseDMasterMutationOperation.Upsert, mutation.Operation);
    }

    /// <summary>
    /// Simula una mutation Phase D scritta PRIMA dell'aggiunta di un campo al modello (es. CreatoDa, 2026-09-18):
    /// il PayloadJson storico non contiene quel campo e PayloadSha256 e' l'hash di QUEL testo, esattamente come
    /// sarebbe realmente sul disco condiviso. Deserializzare oggi questo payload nel modello corrente popola il
    /// campo mancante con il default C# (stringa vuota per CreatoDa).
    /// </summary>
    [Fact]
    public void SavePatch_RigaConPayloadStoricoSenzaCampoNuovo_ModificaHaSuccessoEPersisteDopoRiapertura()
    {
        PreparePhaseD();
        var uid = Guid.NewGuid();
        var owner = Device(20);
        var seed = new Funzionalita { Uid = uid, Nome = "FUN-LEGACY-01", ModificatoDa = "R. Paolo" };
        WriteCurrentBatch(owner, 1, UpsertLegacyPayload(seed, owner, new Dictionary<string, long> { [owner] = 1 }));

        var engine = new PhaseDMasterRepositoryEngine<Funzionalita>(_root, "funzionalita", localBaseDirectory: _local);
        var loaded = engine.Load();
        var item = Assert.Single(loaded.Items);
        Assert.Equal("", item.CreatoDa);

        var baselineWindow = new List<Funzionalita> { Clone(item) };
        item.Nome = "FUN-LEGACY-01-EDIT";
        item.ModificatoDa = "R. Paolo";

        var ex = Record.Exception(() => engine.SavePatch(new List<Funzionalita> { item }, baselineWindow));
        Assert.Null(ex);

        var reloaded = engine.Load();
        var persisted = Assert.Single(reloaded.Items);
        Assert.Equal("FUN-LEGACY-01-EDIT", persisted.Nome);

        // Non solo la prima modifica: una seconda modifica consecutiva sulla stessa riga deve continuare a
        // funzionare (la testa e' ora scritta col modello corrente, ma il fix non deve dipendere da questo).
        var secondBaseline = new List<Funzionalita> { Clone(persisted) };
        persisted.Nome = "FUN-LEGACY-01-EDIT-2";
        var ex2 = Record.Exception(() => engine.SavePatch(new List<Funzionalita> { persisted }, secondBaseline));
        Assert.Null(ex2);
        Assert.Equal("FUN-LEGACY-01-EDIT-2", Assert.Single(engine.Load().Items).Nome);

        // Aggiunta di una voce nuova sullo stesso catalogo legacy deve continuare a funzionare normalmente.
        var nuova = new Funzionalita { Uid = Guid.NewGuid(), Nome = "FUN-LEGACY-01-NUOVA" };
        var ex3 = Record.Exception(() => engine.SavePatch(new List<Funzionalita> { nuova }, new List<Funzionalita>()));
        Assert.Null(ex3);
        Assert.Equal(2, engine.Load().Items.Count);
    }

    /// <summary>
    /// Regressione mirata all'aggiunta di Funzionalita.TutteLeEcu/EcuAssociate (feedback utente 2026-09-23):
    /// una riga scritta prima di questo campo non ha né TutteLeEcu né EcuAssociate nel payload storico. Il fix
    /// bfbd590 (normalizzazione hash storici) deve coprire QUALUNQUE campo aggiunto in seguito, non solo
    /// CreatoDa — questo test lo verifica esplicitamente sul campo nuovo di questa funzionalità: la modifica
    /// deve avere successo senza MasterDataConflictException, e TutteLeEcu deve leggersi true (default
    /// dell'initializer C#, non presente nel JSON storico) cosi' la funzionalità resta valida per tutti i componenti
    /// esattamente come il comportamento pre-esistente (nessuna sparisce dai filtri dopo l'aggiornamento).
    /// </summary>
    [Fact]
    public void SavePatch_RigaConPayloadStoricoSenzaTutteLeEcuNeEcuAssociate_ModificaHaSuccessoETutteLeEcuRestaTrue()
    {
        PreparePhaseD();
        var uid = Guid.NewGuid();
        var owner = Device(23);
        var seed = new Funzionalita { Uid = uid, Nome = "FUN-LEGACY-componente-01" };
        var mutation = UpsertLegacyPayload(seed, owner, new Dictionary<string, long> { [owner] = 1 }, fieldToStrip: "TutteLeEcu");
        // UpsertLegacyPayload rimuove un solo campo per chiamata: la riga storica reale non aveva nessuno dei
        // due, quindi il payload va corretto per rimuoverli entrambi mantenendo l'hash coerente col testo scritto.
        mutation = StripAdditionalField(mutation, "EcuAssociate");
        WriteCurrentBatch(owner, 1, mutation);

        var engine = new PhaseDMasterRepositoryEngine<Funzionalita>(_root, "funzionalita", localBaseDirectory: _local);
        var loaded = engine.Load();
        var item = Assert.Single(loaded.Items);
        Assert.True(item.TutteLeEcu);
        Assert.Empty(item.EcuAssociate);

        var baselineWindow = new List<Funzionalita> { Clone(item) };
        item.Nome = "FUN-LEGACY-componente-01-EDIT";

        var ex = Record.Exception(() => engine.SavePatch(new List<Funzionalita> { item }, baselineWindow));
        Assert.Null(ex);

        var reloaded = engine.Load();
        var persisted = Assert.Single(reloaded.Items);
        Assert.Equal("FUN-LEGACY-componente-01-EDIT", persisted.Nome);
        Assert.True(persisted.TutteLeEcu);
    }

    /// <summary>Rimuove un campo aggiuntivo da una mutation già serializzata (UpsertLegacyPayload ne rimuove
    /// solo uno), ricalcolando l'hash sul testo effettivamente scritto — stesso principio di UpsertLegacyPayload
    /// stesso, usato in sequenza per simulare una riga storica priva di DUE campi aggiunti in tempi diversi.</summary>
    private static PhaseDMasterMutation StripAdditionalField(PhaseDMasterMutation mutation, string fieldToStrip)
    {
        var node = JsonNode.Parse(mutation.PayloadJson)!.AsObject();
        node.Remove(fieldToStrip);
        mutation.PayloadJson = node.ToJsonString(JsonIo.CompactOptions);
        mutation.PayloadSha256 = PhaseDMasterMutation.HashPayload(mutation.PayloadJson);
        return mutation;
    }

    [Fact]
    public void Save_ListaCompletaConPayloadStoricoSenzaCampoNuovo_ModificaHaSuccessoEPersiste()
    {
        PreparePhaseD();
        var uid = Guid.NewGuid();
        var owner = Device(21);
        var seed = new Funzionalita { Uid = uid, Nome = "FUN-LEGACY-02" };
        WriteCurrentBatch(owner, 1, UpsertLegacyPayload(seed, owner, new Dictionary<string, long> { [owner] = 1 }));

        var engine = new PhaseDMasterRepositoryEngine<Funzionalita>(_root, "funzionalita", localBaseDirectory: _local);
        var loaded = engine.Load();
        var item = Assert.Single(loaded.Items);
        item.Nome = "FUN-LEGACY-02-EDIT";
        item.ModificatoDa = "R. Paolo";

        var ex = Record.Exception(() => engine.Save(loaded.Items, loaded.State, expectedSnapshot: null));
        Assert.Null(ex);

        var reloaded = engine.Load();
        Assert.Equal("FUN-LEGACY-02-EDIT", Assert.Single(reloaded.Items).Nome);
    }

    /// <summary>
    /// Il fix non deve allentare il rilevamento di un conflitto VERO: due editor partono dalla stessa riga
    /// legacy (payload storico senza CreatoDa), il primo salva con successo (la testa remota avanza), il
    /// secondo tenta di salvare una modifica diversa partendo dalla stessa baseline pre-modifica del primo.
    /// </summary>
    [Fact]
    public void SavePatch_ModificaConcorrenteVeraSuRigaConPayloadStorico_VieneAncoraRilevataComeConflitto()
    {
        PreparePhaseD();
        var uid = Guid.NewGuid();
        var owner = Device(22);
        var seed = new Funzionalita { Uid = uid, Nome = "FUN-LEGACY-03" };
        WriteCurrentBatch(owner, 1, UpsertLegacyPayload(seed, owner, new Dictionary<string, long> { [owner] = 1 }));

        var engine = new PhaseDMasterRepositoryEngine<Funzionalita>(_root, "funzionalita", localBaseDirectory: _local);
        var editorABaseline = Clone(Assert.Single(engine.Load().Items));
        var editorBBaseline = Clone(editorABaseline);

        var editorAItem = Clone(editorABaseline);
        editorAItem.Nome = "FUN-LEGACY-03-A";
        engine.SavePatch(new List<Funzionalita> { editorAItem }, new List<Funzionalita> { editorABaseline });

        var editorBItem = Clone(editorBBaseline);
        editorBItem.Nome = "FUN-LEGACY-03-B";
        var conflittoVero = Assert.Throws<MasterDataConflictException>(() =>
            engine.SavePatch(new List<Funzionalita> { editorBItem }, new List<Funzionalita> { editorBBaseline }));

        // Un vero conflitto concorrente (payload remoto leggibile ma diverso dalla baseline) deve conservare il
        // messaggio generico storico: non e' il caso "payload storico illeggibile" coperto dal fix successivo.
        Assert.IsType<MasterDataConflictException>(conflittoVero);
        Assert.Contains("altra postazione", conflittoVero.Message);

        // La riga resta quella pubblicata per prima: nessun merge silenzioso ha corrotto il risultato.
        Assert.Equal("FUN-LEGACY-03-A", Assert.Single(engine.Load().Items).Nome);
    }

    /// <summary>
    /// Il fix bfbd590 non deve mai far crashare SavePatch con un'eccezione tecnica quando un payload storico
    /// non e' piu' deserializzabile nel modello corrente (review F2): qui la testa remota candidata al confronto
    /// contiene JSON completamente incompatibile (non un oggetto). NormalizedSha deve intercettare l'eccezione
    /// di deserializzazione e ripiegare su un confronto raw, che a sua volta produce un MasterDataConflictException
    /// gestito — mai una JsonException/InvalidDataException non gestita che risalga fuori da SavePatch.
    ///
    /// Fix successivo (chiarezza messaggio): questo caso specifico non e' un vero conflitto concorrente, quindi
    /// il messaggio "e' stato modificato da un'altra postazione" e' fuorviante. SavePatch deve lanciare la
    /// sottoclasse MasterDataUnreadableHistoricalPayloadException, che resta un MasterDataConflictException
    /// (fail-closed, tutti i catch esistenti continuano a funzionare) ma con un messaggio che spiega la causa
    /// reale e come sbloccare.
    /// </summary>
    [Fact]
    public void SavePatch_TestaRemotaNonDeserializzabile_NonCrashaMaProducConflittoGestito()
    {
        PreparePhaseD();
        var uid = Guid.NewGuid();
        var owner = Device(25);
        var seed = new Funzionalita { Uid = uid, Nome = "FUN-CORROTTA-01" };
        WriteCurrentBatch(owner, 1, Upsert(seed, owner, new Dictionary<string, long> { [owner] = 1 }));

        var engine = new PhaseDMasterRepositoryEngine<Funzionalita>(_root, "funzionalita", localBaseDirectory: _local);
        var baseline = Clone(Assert.Single(engine.Load().Items));

        // Un'altra postazione pubblica una testa il cui payload non e' piu' un JSON deserializzabile in
        // Funzionalita (es. corruzione o formato radicalmente incompatibile): un caso ben oltre il semplice
        // "campo mancante" gia' gestito da NormalizedSha.
        var corrottoDevice = Device(26);
        var corrottoVector = new Dictionary<string, long> { [owner] = 1, [corrottoDevice] = 1 };
        WriteCurrentBatch(corrottoDevice, 1, UpsertRawPayload(
            uid, "funzionalita", "\"non e' un oggetto Funzionalita\"", seed.Nome, corrottoDevice, corrottoVector));

        var edit = Clone(baseline);
        edit.Nome = "FUN-CORROTTA-01-EDIT";

        // Deve restare un MasterDataConflictException gestito (fail-closed), mai un'eccezione tecnica non gestita,
        // ma con un messaggio specifico che NON parla di "altra postazione" e spiega come sbloccare.
        var ex = Assert.Throws<MasterDataUnreadableHistoricalPayloadException>(() =>
            engine.SavePatch(new List<Funzionalita> { edit }, new List<Funzionalita> { baseline }));
        Assert.IsAssignableFrom<MasterDataConflictException>(ex);
        Assert.DoesNotContain("altra postazione", ex.Message);
        Assert.Contains("funzionalita", ex.Message);
        Assert.Contains(uid.ToString("N"), ex.Message);
        Assert.Contains("aggiornare TestLoom", ex.Message);
        Assert.Contains("supporto", ex.Message);
    }

    /// <summary>
    /// MasterEntity ha [JsonExtensionData] (review F3): un campo scritto da una postazione PIU' NUOVA (es.
    /// 6.4.0, un campo che questa build 6.3.0 non conosce ancora) deve sopravvivere al round-trip
    /// deserializza/riserializza dentro NormalizedSha/CanonicalPayload. Qui si modifica un campo NOTO su una
    /// riga il cui payload storico porta gia' un campo ignoto: dopo Save+Load il campo ignoto deve restare
    /// presente in ExtensionData, altrimenti la sua perdita silenziosa potrebbe far apparire due varianti
    /// remote come identiche quando in realta' non lo sono.
    /// </summary>
    [Fact]
    public void SavePatch_CampoIgnotoFuturo_SopravviveAlRoundTripDopoModificaCampoNoto()
    {
        PreparePhaseD();
        var uid = Guid.NewGuid();
        var owner = Device(27);
        var seed = new Funzionalita { Uid = uid, Nome = "FUN-FUTURE-CAMPO-01" };
        var payloadConCampoIgnoto = WithExtraField(seed, "CampoFuturo", "valore-A");
        WriteCurrentBatch(owner, 1, UpsertRawPayload(
            uid, "funzionalita", payloadConCampoIgnoto, seed.Nome, owner, new Dictionary<string, long> { [owner] = 1 }));

        var engine = new PhaseDMasterRepositoryEngine<Funzionalita>(_root, "funzionalita", localBaseDirectory: _local);
        var loaded = engine.Load();
        var item = Assert.Single(loaded.Items);
        Assert.NotNull(item.ExtensionData);
        Assert.Equal("valore-A", item.ExtensionData!["CampoFuturo"].GetString());

        var baseline = Clone(item);
        item.Nome = "FUN-FUTURE-CAMPO-01-EDIT";
        var ex = Record.Exception(() => engine.SavePatch(new List<Funzionalita> { item }, new List<Funzionalita> { baseline }));
        Assert.Null(ex);

        var reloaded = Assert.Single(engine.Load().Items);
        Assert.Equal("FUN-FUTURE-CAMPO-01-EDIT", reloaded.Nome);
        Assert.NotNull(reloaded.ExtensionData);
        Assert.Equal("valore-A", reloaded.ExtensionData!["CampoFuturo"].GetString());
    }

    /// <summary>
    /// Review F3: due teste remote storiche che differiscono SOLO in un campo sconosciuto al modello corrente
    /// devono produrre sha normalizzati DIVERSI e il conflitto va rilevato — mai mascherato come "nessuna
    /// modifica remota rilevante" solo perche' i campi NOTI combaciano.
    /// </summary>
    [Fact]
    public void SavePatch_DueTesteStoricheDifferisconoSoloPerCampoIgnoto_ShaDiversiEConflittoRilevato()
    {
        PreparePhaseD();
        var uid = Guid.NewGuid();
        var owner = Device(28);
        var seed = new Funzionalita { Uid = uid, Nome = "FUN-FUTURE-CAMPO-02" };
        var payloadA = WithExtraField(seed, "CampoFuturo", "valore-A");
        WriteCurrentBatch(owner, 1, UpsertRawPayload(
            uid, "funzionalita", payloadA, seed.Nome, owner, new Dictionary<string, long> { [owner] = 1 }));

        var engine = new PhaseDMasterRepositoryEngine<Funzionalita>(_root, "funzionalita", localBaseDirectory: _local);
        var loaded = engine.Load();
        var localBaseline = Clone(Assert.Single(loaded.Items));
        var localEdit = Clone(localBaseline);
        localEdit.Nome = "FUN-FUTURE-CAMPO-02-EDIT-LOCALE";

        // Nel frattempo un'altra postazione PIU' NUOVA pubblica una head che cambia SOLO il campo ignoto
        // (A -> B): per i campi noti il payload "sembra" identico, ma non lo e' davvero.
        var altroDevice = Device(29);
        var vettorePiuNuovo = new Dictionary<string, long>(loaded.State.Entities[uid].VersionVector)
        {
            [altroDevice] = 1
        };
        var payloadB = WithExtraField(seed, "CampoFuturo", "valore-B");
        WriteCurrentBatch(altroDevice, 1, UpsertRawPayload(
            uid, "funzionalita", payloadB, seed.Nome, altroDevice, vettorePiuNuovo));

        // Il salvataggio locale deve rilevare il conflitto reale, non mascherarlo.
        Assert.Throws<MasterDataConflictException>(() =>
            engine.SavePatch(new List<Funzionalita> { localEdit }, new List<Funzionalita> { localBaseline }));

        // Nessun merge silenzioso: la riga resta quella pubblicata dall'altra postazione (payload B).
        var afterConflict = Assert.Single(engine.Load().Items);
        Assert.Equal("valore-B", afterConflict.ExtensionData!["CampoFuturo"].GetString());
    }

    /// <summary>
    /// Review F3, punto "impone a livello di tipo": la sopravvivenza dei campi ignoti verificata dai test sopra
    /// dipende interamente da [JsonExtensionData] su MasterEntity. Questo test non verifica il comportamento
    /// (gia' coperto sopra), ma la PRECONDIZIONE strutturale su OGNI tipo master concreto presente nell'assembly:
    /// se una sottoclasse futura di MasterEntity nascondesse ExtensionData con 'new' (o l'attributo sparisse per
    /// un refactor distratto), questo test fallisce esplicitamente invece di lasciare che NormalizedSha inizi a
    /// mascherare in silenzio conflitti veri su quel solo tipo.
    /// </summary>
    [Fact]
    public void TuttiITipiMasterEntity_HannoJsonExtensionDataPerPreservareCampiIgnoti()
    {
        var tipiMaster = typeof(MasterEntity).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(MasterEntity).IsAssignableFrom(t))
            .ToList();
        Assert.NotEmpty(tipiMaster);

        foreach (var tipo in tipiMaster)
        {
            var proprieta = tipo.GetProperty("ExtensionData", BindingFlags.Public | BindingFlags.Instance);
            Assert.True(proprieta != null,
                $"{tipo.Name} deve esporre una proprieta' pubblica 'ExtensionData' (ereditata o propria).");
            // Le nullable-reference-annotation non sopravvivono a runtime: il tipo CLR e' comunque
            // Dictionary<string, JsonElement> sia per 'Dictionary<...>' sia per 'Dictionary<...>?'.
            Assert.True(
                proprieta!.PropertyType == typeof(Dictionary<string, JsonElement>),
                $"{tipo.Name}.ExtensionData deve essere Dictionary<string, JsonElement>? (trovato {proprieta.PropertyType}).");
            var haAttributo = proprieta.GetCustomAttribute<JsonExtensionDataAttribute>() != null;
            Assert.True(haAttributo, $"{tipo.Name}.ExtensionData deve avere l'attributo [JsonExtensionData].");
        }
    }

    private static PhaseDMasterMutation UpsertRawPayload(
        Guid uid,
        string catalog,
        string payloadJson,
        string businessKey,
        string ownerDevice,
        Dictionary<string, long> vector)
    {
        if (!vector.ContainsKey(ownerDevice))
            throw new InvalidOperationException("Il version vector del test deve contenere il device proprietario del journal.");
        return new PhaseDMasterMutation
        {
            MutationId = Guid.NewGuid(),
            Catalog = catalog,
            EntityUid = uid,
            Operation = PhaseDMasterMutationOperation.Upsert,
            VersionVector = vector,
            PayloadJson = payloadJson,
            PayloadSha256 = PhaseDMasterMutation.HashPayload(payloadJson),
            BusinessKey = businessKey,
            TimestampUtc = DateTime.UtcNow
        };
    }

    private static string WithExtraField(Funzionalita entity, string fieldName, string value)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(entity, JsonIo.CompactOptions))!.AsObject();
        node[fieldName] = value;
        return node.ToJsonString(JsonIo.CompactOptions);
    }

    private static T Clone<T>(T item) where T : MasterEntity =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(item, JsonIo.CompactOptions), JsonIo.CompactOptions)!;

    private static PhaseDMasterMutation UpsertLegacyPayload(
        Funzionalita entity,
        string ownerDevice,
        Dictionary<string, long> vector,
        string fieldToStrip = "CreatoDa")
    {
        if (!vector.ContainsKey(ownerDevice))
            throw new InvalidOperationException("Il version vector del test deve contenere il device proprietario del journal.");
        var node = JsonNode.Parse(JsonSerializer.Serialize(entity, JsonIo.CompactOptions))!.AsObject();
        node.Remove(fieldToStrip);
        var payload = node.ToJsonString(JsonIo.CompactOptions);
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

    private void PreparePhaseD()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);
        Assert.True(PhaseDMasterBootstrapService.IsReady(_root));
    }

    private void WriteCurrentBatch(string deviceId, long sequence, params PhaseDMasterMutation[] mutations)
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
        var path = PhaseDMasterJournalService.CurrentPath(_root, deviceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(batch, JsonIo.CompactOptions) + Environment.NewLine);
    }

    private static PhaseDMasterMutation Upsert(
        Funzionalita entity,
        string ownerDevice,
        Dictionary<string, long> vector)
    {
        if (!vector.ContainsKey(ownerDevice))
            throw new InvalidOperationException("Il version vector del test deve contenere il device proprietario del journal.");
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

    private static PhaseDMasterMutation Delete(
        Guid uid,
        string ownerDevice,
        Dictionary<string, long> vector)
    {
        if (!vector.ContainsKey(ownerDevice))
            throw new InvalidOperationException("Il version vector del test deve contenere il device proprietario del journal.");
        return new PhaseDMasterMutation
        {
            MutationId = Guid.NewGuid(),
            Catalog = "funzionalita",
            EntityUid = uid,
            Operation = PhaseDMasterMutationOperation.Delete,
            VersionVector = vector,
            PayloadJson = "",
            PayloadSha256 = "",
            BusinessKey = "",
            TimestampUtc = DateTime.UtcNow
        };
    }

    private PhaseDMasterCheckpointGeneration WriteCheckpointGeneration(
        string deviceId,
        long coveredThrough,
        PhaseDMasterMutation mutation)
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
        return new PhaseDMasterCheckpointGeneration
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
    }

    private static string Device(int index) =>
        $"{index + 1:x32}";
}
