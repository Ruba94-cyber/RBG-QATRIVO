using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Due postazioni (PC-A, PC-B) con cartelle condivise separate, sincronizzate da una replica OneDrive semplificata
/// (<see cref="ReplicaOneDrive"/>): i cataloghi JSON in radice vengono sovrascritti in modo piatto (l'ultimo che
/// scrive vince sul file intero), il contenuto di _ditm (manifest, ready.json, journal per device) viene copiato
/// con newer-wins per file.
///
/// Limite del simulatore: <see cref="DeviceIdentityService.GetOrCreate"/> e' in cache per processo. PC-A usa quindi
/// il device reale del processo (repository ed engine reali); PC-B usa il device sintetico <see cref="DeviceB"/> e le
/// sue mutation Phase D vengono pubblicate nel suo journal con lo stesso formato di PhaseDMasterRepositoryEngine
/// (Upsert, version vector = testa osservata + proprio contatore). Le letture di entrambi i PC passano dai
/// MasterDataRepository reali.
///
/// Scenari: S1 = aggiunte offline di due righe diverse; S2 = modifiche offline di due campi diversi della stessa
/// riga; S3 = archiviazione su un PC mentre l'altro modifica la stessa riga. Ogni scenario e' eseguito sul JSON
/// legacy (nessun ready.json: il JSON e' la source of truth) e su Phase D (ready.json pubblicato, come fa
/// CompatibilitaVersioneService.Applica). I casi con perdita silenziosa sul JSON legacy sono caratterizzazioni di un
/// limite noto, marcate Category=LimiteNoto (vedi la sezione legacy e RELEASE_NOTES_6.6.0.md, "Limiti noti").
/// </summary>
public sealed class MasterCatalogMultiPcTests : IDisposable
{
    // Device id sintetico del PC-B: quello reale del processo e' unico per processo (vedi DeviceIdentityService).
    private const string DeviceB = "b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0";
    private const string RigaBase = "BASE";
    private const string ValoreModificato = "modifica-campo-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_MasterMultiPc_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record Postazione(string Nome, DataPaths Paths, string Locale)
    {
        public string Cartella => Paths.Root;
    }

    private Postazione NuovaPostazione(string nome)
    {
        var cartella = Path.Combine(_root, nome);
        var locale = Path.Combine(_root, "local-" + nome);
        Directory.CreateDirectory(cartella);
        Directory.CreateDirectory(locale);
        return new Postazione(nome, new DataPaths(cartella), locale);
    }

    /// <summary>Modifica di un campo (o archiviazione) e la sua verifica sul contenuto della riga.</summary>
    private sealed record Modifica<T>(Action<T> Applica, Func<T, bool> Verifica);

    /// <summary>Catalogo sotto test: file, costruttore di righe e due modifiche su campi diversi, piu' l'archiviazione.</summary>
    private sealed record Catalogo<T>(
        string Nome,
        Func<DataPaths, string> PercorsoFile,
        Func<string, T> Crea,
        Modifica<T> Campo1,
        Modifica<T> Campo2,
        Modifica<T> Archivia) where T : MasterEntity;

    private static Catalogo<Requisito> Requisiti() => new(
        "requisiti",
        p => p.RequisitiFile,
        id => new Requisito { Id = id, Testo = "testo " + id },
        new Modifica<Requisito>(r => r.Testo = ValoreModificato, r => r.Testo == ValoreModificato),
        new Modifica<Requisito>(r => r.ApplicabileBanco = true, r => r.ApplicabileBanco),
        new Modifica<Requisito>(r => r.Archiviata = true, r => r.Archiviata));

    // Veicolo ha, oltre alla chiave Nome, solo GammaNome e Archiviata: per S2 il PC-B modifica Archiviata
    // (Campo2 coincide con Archivia). Per S3 il PC-A archivia e il PC-B modifica GammaNome.
    private static Catalogo<Veicolo> Veicoli() => new(
        "veicoli",
        p => p.VeicoliFile,
        nome => new Veicolo { Nome = nome, GammaNome = "gamma-base" },
        new Modifica<Veicolo>(v => v.GammaNome = ValoreModificato, v => v.GammaNome == ValoreModificato),
        new Modifica<Veicolo>(v => v.Archiviata = true, v => v.Archiviata),
        new Modifica<Veicolo>(v => v.Archiviata = true, v => v.Archiviata));

    private static string Chiave<T>(T riga) where T : MasterEntity => PhaseDMasterBusinessKeyService.Get(riga);

    private static List<string> Chiavi<T>(MasterDataRepository<T> repo) where T : MasterEntity =>
        repo.Load().Select(r => Chiave(r)).ToList();

    private static List<string> WorkingUnitDi(MasterDataRepository<RevisioneUdt> repo) =>
        repo.Load().Select(r => r.WorkingUnitRev).ToList();

    private static void AsserisciPresenti(IReadOnlyCollection<string> presenti, string postazione, params string[] attese)
    {
        foreach (var attesa in attese)
            Assert.True(presenti.Contains(attesa),
                $"{postazione}: la voce '{attesa}' non e' visibile dopo la sincronizzazione. Presenti: [{string.Join(", ", presenti)}].");
    }

    /// <summary>
    /// Replica OneDrive semplificata da <paramref name="da"/> verso <paramref name="a"/>. Cataloghi JSON in radice:
    /// sovrascrittura piatta, nessuna fusione. Contenuto di _ditm: newer-wins per file (a parita' di mtime vince il
    /// file piu' grande, utile per i journal append-only).
    /// </summary>
    private static void ReplicaOneDrive(Postazione da, Postazione a)
    {
        foreach (var file in Directory.EnumerateFiles(da.Cartella, "*.json", SearchOption.TopDirectoryOnly))
            CopiaJson(file, Path.Combine(a.Cartella, Path.GetFileName(file)));

        var testloom = Path.Combine(da.Cartella, "_ditm");
        if (!Directory.Exists(testloom)) return;
        foreach (var file in Directory.EnumerateFiles(testloom, "*", SearchOption.AllDirectories))
        {
            var destinazione = Path.Combine(a.Cartella, Path.GetRelativePath(da.Cartella, file));
            if (File.Exists(destinazione) && !PiuRecente(file, destinazione)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destinazione)!);
            File.Copy(file, destinazione, overwrite: true);
        }
    }

    /// <summary>
    /// Il JSON copiato riceve un mtime strettamente crescente, come un download OneDrive: la cache per-mtime di
    /// MasterDataRepository.Load non puo' servire la versione precedente se due versioni cadono nello stesso tick.
    /// </summary>
    private static void CopiaJson(string sorgente, string destinazione)
    {
        var precedente = File.Exists(destinazione) ? File.GetLastWriteTimeUtc(destinazione) : DateTime.MinValue;
        File.Copy(sorgente, destinazione, overwrite: true);
        var ora = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(destinazione, ora > precedente ? ora : precedente.AddMilliseconds(1));
    }

    private static bool PiuRecente(string sorgente, string destinazione)
    {
        var mSorgente = File.GetLastWriteTimeUtc(sorgente);
        var mDestinazione = File.GetLastWriteTimeUtc(destinazione);
        return mSorgente > mDestinazione ||
               (mSorgente == mDestinazione && new FileInfo(sorgente).Length > new FileInfo(destinazione).Length);
    }

    /// <summary>
    /// Attivazione Phase D come la fa l'app (CompatibilitaVersioneService.Applica -> EnsureReady) su PC-A, poi
    /// replica su PC-B. Da qui in poi entrambi i repository leggono dal journal e il JSON non viene piu' letto.
    /// </summary>
    private static void AttivaPhaseD(Postazione a, Postazione b)
    {
        Assert.NotEqual(DeviceB, DeviceIdentityService.GetOrCreate());
        a.Paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(a.Paths);
        ReplicaOneDrive(a, b);
        Assert.True(PhaseDMasterBootstrapService.IsReady(b.Cartella), "PC-B deve ricevere manifest e ready.json dalla replica.");
    }

    /// <summary>Riga base scritta nel JSON legacy prima della migrazione, come nel workspace preesistente.</summary>
    private static void SeminaRigaBaseLegacy<T>(Catalogo<T> c, Postazione a) where T : MasterEntity =>
        new MasterDataRepository<T>(c.PercorsoFile(a.Paths)).Save(new List<T> { c.Crea(RigaBase) });

    /// <summary>Mutation Upsert nel formato di PhaseDMasterRepositoryEngine.CreateUpsert, firmata dal device B.</summary>
    private static PhaseDMasterMutation MutazioneUpsertB<T>(string catalogo, T riga, IReadOnlyDictionary<string, long> vvOsservato)
        where T : MasterEntity
    {
        var payload = JsonSerializer.Serialize(riga, JsonIo.CompactOptions);
        return new PhaseDMasterMutation
        {
            MutationId = Guid.NewGuid(),
            Catalog = catalogo,
            EntityUid = MasterEntityIdentityService.GetRequired(riga),
            Operation = PhaseDMasterMutationOperation.Upsert,
            VersionVector = PhaseDVersionVector.Increment(vvOsservato, DeviceB),
            PayloadJson = payload,
            PayloadSha256 = PhaseDMasterMutation.HashPayload(payload),
            BusinessKey = PhaseDMasterBusinessKeyService.Get(riga),
            TimestampUtc = DateTime.UtcNow
        };
    }

    /// <summary>Pubblica un batch del device B nel journal di <paramref name="pc"/> (stesso formato di Append).</summary>
    private static void PubblicaBatchB(Postazione pc, long sequenza, params PhaseDMasterMutation[] mutazioni)
    {
        var batch = new PhaseDMasterMutationBatch
        {
            BatchId = Guid.NewGuid(),
            DeviceId = DeviceB,
            DeviceSequence = sequenza,
            CreatedUtc = DateTime.UtcNow,
            Mutations = mutazioni.ToList()
        };
        batch.Validate();
        var current = PhaseDMasterJournalService.CurrentPath(pc.Cartella, DeviceB);
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.AppendAllText(current, JsonSerializer.Serialize(batch, JsonIo.CompactOptions) + Environment.NewLine);
    }

    /// <summary>Version vector della testa della riga come la vede <paramref name="pc"/>: base della modifica offline.</summary>
    private static IReadOnlyDictionary<string, long> VersioneTesta<T>(Postazione pc, string catalogo, T riga) where T : MasterEntity
    {
        var snapshot = new PhaseDMasterMaterializer(pc.Cartella, pc.Locale).LoadCatalog<T>(pc.Cartella, catalogo);
        return snapshot.Heads[MasterEntityIdentityService.GetRequired(riga)].VersionVector;
    }

    // ---------------------------------------------------------------------------------------------------------
    // JSON legacy (nessun ready.json): la sovrascrittura piatta del file e' l'unico meccanismo di sincronizzazione.
    // LIMITE NOTO (Category=LimiteNoto): questi test caratterizzano la perdita silenziosa del formato JSON legacy
    // pre-Phase D. Il formato non e' raggiungibile con la cartella dati 6.6.0 (ricostruita in Phase D): alla prima apertura
    // scrivibile l'app migra alla Phase D (CompatibilitaVersioneService.Applica -> PhaseDMasterBootstrapService.EnsureReady)
    // e da li' il journal e' la source of truth. Il limite e' documentato in RELEASE_NOTES_6.6.0.md, "Limiti noti".
    // Le asserzioni fissano il comportamento OSSERVATO, non quello desiderato: se cambia, rivedere il limite noto.
    // I casi Phase D corrispondenti restano nella sezione seguente e non sono toccati.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// S1 legacy, caratterizzazione del limite noto. Comportamento osservato: PC-B sovrascrive il JSON di PC-A con la
    /// propria versione, la riga A1 sparisce da entrambi i PC e il reload non segnala nulla.
    /// </summary>
    private void LegacyS1<T>(Catalogo<T> c) where T : MasterEntity
    {
        var a = NuovaPostazione("PC-A");
        var b = NuovaPostazione("PC-B");
        SeminaRigaBaseLegacy(c, a);
        ReplicaOneDrive(a, b);

        var repoA = new MasterDataRepository<T>(c.PercorsoFile(a.Paths));
        var repoB = new MasterDataRepository<T>(c.PercorsoFile(b.Paths));
        var listaA = repoA.Load();
        listaA.Add(c.Crea("A1"));
        repoA.Save(listaA);
        var listaB = repoB.Load();
        listaB.Add(c.Crea("B1"));
        repoB.Save(listaB);

        ReplicaOneDrive(b, a); // OneDrive: il file di PC-B sovrascrive quello di PC-A
        ReplicaOneDrive(a, b);

        // Caratterizzazione: A1 non compare piu' in nessuno dei due PC; nessun conflitto viene segnalato.
        Assert.Equal(new[] { "B1", RigaBase }, Chiavi(repoA).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { "B1", RigaBase }, Chiavi(repoB).OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// S2/S3 legacy, caratterizzazione del limite noto: due modifiche offline sulla stessa riga (campi diversi, oppure
    /// archiviazione contro modifica). Comportamento osservato: dopo la sovrascrittura del JSON la modifica di PC-A
    /// viene persa senza conflitto, mentre quella di PC-B resta.
    /// </summary>
    private void LegacyConcorrenza<T>(Catalogo<T> c, Modifica<T> modificaA, Modifica<T> modificaB) where T : MasterEntity
    {
        var a = NuovaPostazione("PC-A");
        var b = NuovaPostazione("PC-B");
        SeminaRigaBaseLegacy(c, a);
        ReplicaOneDrive(a, b);

        var repoA = new MasterDataRepository<T>(c.PercorsoFile(a.Paths));
        var repoB = new MasterDataRepository<T>(c.PercorsoFile(b.Paths));
        var listaA = repoA.Load();
        modificaA.Applica(listaA.Single(r => Chiave(r) == RigaBase));
        repoA.Save(listaA);
        var listaB = repoB.Load();
        modificaB.Applica(listaB.Single(r => Chiave(r) == RigaBase));
        repoB.Save(listaB);

        ReplicaOneDrive(b, a);
        ReplicaOneDrive(a, b);

        var finale = repoA.Load().Single(r => Chiave(r) == RigaBase);
        // Caratterizzazione: la modifica di PC-A viene sovrascritta, quella di PC-B resta. Se questo test fallisce il
        // comportamento del JSON legacy e' cambiato: rivedere il limite noto in RELEASE_NOTES_6.6.0.md.
        Assert.False(modificaA.Verifica(finale),
            "Caratterizzazione: la modifica di PC-A doveva essere sovrascritta dal JSON di PC-B (limite noto, formato legacy).");
        Assert.True(modificaB.Verifica(finale),
            "Caratterizzazione: la modifica di PC-B deve restare nel JSON dopo la sincronizzazione.");
    }

    /// <summary>Legacy WU, caratterizzazione del limite noto: aggiunte offline tramite RevisioneUdtService, poi sovrascrittura
    /// piatta del JSON. Comportamento osservato: la WU aggiunta da PC-A sparisce, quella di PC-B resta.</summary>
    private void LegacyRevisioniUdtS1()
    {
        var a = NuovaPostazione("PC-A");
        var b = NuovaPostazione("PC-B");
        RevisioneUdtService.Sincronizza(new MasterDataRepository<RevisioneUdt>(a.Paths.RevisioniUdtFile), TestConWu("T-BASE", "WU-BASE"), "Mario");
        ReplicaOneDrive(a, b);

        var repoA = new MasterDataRepository<RevisioneUdt>(a.Paths.RevisioniUdtFile);
        var repoB = new MasterDataRepository<RevisioneUdt>(b.Paths.RevisioniUdtFile);
        RevisioneUdtService.Sincronizza(repoA, TestConWu("T-A", "WU-ALFA"), "Mario");
        RevisioneUdtService.Sincronizza(repoB, TestConWu("T-B", "WU-BETA"), "Luigi");

        ReplicaOneDrive(b, a);
        ReplicaOneDrive(a, b);

        // Caratterizzazione: WU-ALFA (PC-A) non compare piu'; restano la base e la WU di PC-B.
        Assert.Equal(new[] { "WU-BASE", "WU-BETA" }, WorkingUnitDi(repoA).OrderBy(x => x, StringComparer.Ordinal));
    }

    // Caratterizzazione del limite noto (formato JSON legacy, vedi intestazione della sezione): ogni test fissa il
    // comportamento osservato, non l'obiettivo di correttezza. Gli esiti Phase D corrispondenti non sono cambiati.
    [Fact]
    [Trait("Category", "LimiteNoto")]
    public void LegacyJson_S1_Requisiti_AggiunteOffline_SovrascritturaJsonPerdeRigaDiA_LimiteNoto() =>
        LegacyS1(Requisiti());

    [Fact]
    [Trait("Category", "LimiteNoto")]
    public void LegacyJson_S1_Veicoli_AggiunteOffline_SovrascritturaJsonPerdeRigaDiA_LimiteNoto() =>
        LegacyS1(Veicoli());

    [Fact]
    [Trait("Category", "LimiteNoto")]
    public void LegacyJson_S2_Requisiti_CampiDiversiOffline_SovrascritturaJsonPerdeModificaDiA_LimiteNoto()
    {
        var c = Requisiti();
        LegacyConcorrenza(c, c.Campo1, c.Campo2);
    }

    [Fact]
    [Trait("Category", "LimiteNoto")]
    public void LegacyJson_S2_Veicoli_CampiDiversiOffline_SovrascritturaJsonPerdeModificaDiA_LimiteNoto()
    {
        var c = Veicoli();
        LegacyConcorrenza(c, c.Campo1, c.Campo2);
    }

    [Fact]
    [Trait("Category", "LimiteNoto")]
    public void LegacyJson_S3_Requisiti_ArchiviaVsModifica_SovrascritturaJsonAnnullaArchiviazione_LimiteNoto()
    {
        var c = Requisiti();
        LegacyConcorrenza(c, c.Archivia, c.Campo1);
    }

    [Fact]
    [Trait("Category", "LimiteNoto")]
    public void LegacyJson_S3_Veicoli_ArchiviaVsModifica_SovrascritturaJsonAnnullaArchiviazione_LimiteNoto()
    {
        var c = Veicoli();
        LegacyConcorrenza(c, c.Archivia, c.Campo1);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Phase D (ready.json pubblicato su entrambi i PC): il journal per device e' la source of truth.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// S1 Phase D. Esito atteso: PRESERVATO. Le due righe nuove hanno chiavi diverse e vivono nei journal dei rispettivi
    /// device: dopo la replica del journal in entrambe le direzioni sono visibili su entrambi i PC.
    /// </summary>
    private void PhaseDS1<T>(Catalogo<T> c) where T : MasterEntity
    {
        var a = NuovaPostazione("PC-A");
        var b = NuovaPostazione("PC-B");
        SeminaRigaBaseLegacy(c, a);
        AttivaPhaseD(a, b);

        var repoA = new MasterDataRepository<T>(c.PercorsoFile(a.Paths));
        var repoB = new MasterDataRepository<T>(c.PercorsoFile(b.Paths));

        var listaA = repoA.Load();
        listaA.Add(c.Crea("A1"));
        repoA.Save(listaA); // PC-A: engine reale, device del processo

        var rigaB = c.Crea("B1");
        MasterEntityIdentityService.GetOrCreate(rigaB);
        PubblicaBatchB(b, 1, MutazioneUpsertB(c.Nome, rigaB, new Dictionary<string, long>())); // PC-B: device B

        ReplicaOneDrive(a, b);
        ReplicaOneDrive(b, a);

        AsserisciPresenti(Chiavi(repoA), "PC-A", RigaBase, "A1", "B1");
        AsserisciPresenti(Chiavi(repoB), "PC-B", RigaBase, "A1", "B1");
    }

    /// <summary>
    /// S2/S3 Phase D. Esito atteso: CONFLITTO RILEVATO, nessuna perdita silenziosa. Phase D non applica
    /// last-writer-wins: su entrambi i PC il catalogo resta LEGGIBILE, la voce in conflitto compare con la testa
    /// deterministica (ConflictedUids) e il journal conserva entrambe le teste (LoadConflicts). La scelta Master e'
    /// coperta dai test PhaseD_Conflitto_*.
    /// </summary>
    private void PhaseDConcorrenza<T>(Catalogo<T> c, Modifica<T> modificaA, Modifica<T> modificaB) where T : MasterEntity
    {
        var a = NuovaPostazione("PC-A");
        var b = NuovaPostazione("PC-B");
        SeminaRigaBaseLegacy(c, a);
        AttivaPhaseD(a, b);

        var repoA = new MasterDataRepository<T>(c.PercorsoFile(a.Paths));
        var repoB = new MasterDataRepository<T>(c.PercorsoFile(b.Paths));

        var listaA = repoA.Load();
        modificaA.Applica(listaA.Single(r => Chiave(r) == RigaBase));
        repoA.Save(listaA); // PC-A: engine reale

        var rigaB = repoB.Load().Single(r => Chiave(r) == RigaBase);
        modificaB.Applica(rigaB);
        PubblicaBatchB(b, 1, MutazioneUpsertB(c.Nome, rigaB, VersioneTesta(b, c.Nome, rigaB))); // PC-B: offline, stessa base

        ReplicaOneDrive(a, b);
        ReplicaOneDrive(b, a);

        // Lettura non bloccata: il catalogo si carica su entrambi i PC e la voce in conflitto e' esposta come tale.
        Assert.Single(repoA.Load(), r => Chiave(r) == RigaBase);
        Assert.Single(repoB.Load(), r => Chiave(r) == RigaBase);
        Assert.Single(new PhaseDMasterMaterializer(a.Cartella, a.Locale).LoadCatalog<T>(a.Cartella, c.Nome).ConflictedUids);
        Assert.Single(new PhaseDMasterMaterializer(b.Cartella, b.Locale).LoadCatalog<T>(b.Cartella, c.Nome).ConflictedUids);

        var conflitto = Assert.Single(new PhaseDMasterMaterializer(a.Cartella, a.Locale).LoadConflicts(a.Cartella, c.Nome));
        Assert.Equal(2, conflitto.ConcurrentHeads.Count);
        var teste = conflitto.ConcurrentHeads
            .Select(m => JsonSerializer.Deserialize<T>(m.PayloadJson, JsonIo.Options)!)
            .ToList();
        Assert.Contains(teste, t => modificaA.Verifica(t));
        Assert.Contains(teste, t => modificaB.Verifica(t));
    }

    [Fact]
    public void PhaseD_S1_Requisiti_AggiunteOffline_JournalConvergente_TutteLeRigheVisibili() =>
        PhaseDS1(Requisiti());

    [Fact]
    public void PhaseD_S1_Veicoli_AggiunteOffline_JournalConvergente_TutteLeRigheVisibili() =>
        PhaseDS1(Veicoli());

    [Fact]
    public void PhaseD_S2_Requisiti_CampiDiversiOffline_ConflittoRilevatoTesteConservate()
    {
        var c = Requisiti();
        PhaseDConcorrenza(c, c.Campo1, c.Campo2);
    }

    [Fact]
    public void PhaseD_S2_Veicoli_CampiDiversiOffline_ConflittoRilevatoTesteConservate()
    {
        var c = Veicoli();
        PhaseDConcorrenza(c, c.Campo1, c.Campo2);
    }

    [Fact]
    public void PhaseD_S3_Requisiti_ArchiviaVsModifica_ConflittoRilevatoTesteConservate()
    {
        var c = Requisiti();
        PhaseDConcorrenza(c, c.Archivia, c.Campo1);
    }

    [Fact]
    public void PhaseD_S3_Veicoli_ArchiviaVsModifica_ConflittoRilevatoTesteConservate()
    {
        var c = Veicoli();
        PhaseDConcorrenza(c, c.Archivia, c.Campo1);
    }

    /// <summary>
    /// Esito atteso: PRESERVATO. Dopo la migrazione il JSON in radice non viene piu' letto: una sua sovrascrittura
    /// (qui con contenuto vuoto, come un file stale copiato da un PC con build precedente) non altera la lettura.
    /// </summary>
    [Fact]
    public void PhaseD_JsonSovrascrittoDaOneDrive_NonInfluisceSullaLetturaDalJournal()
    {
        var a = NuovaPostazione("PC-A");
        var b = NuovaPostazione("PC-B");
        var c = Requisiti();
        SeminaRigaBaseLegacy(c, a);
        AttivaPhaseD(a, b);

        File.WriteAllText(a.Paths.RequisitiFile, "[]");
        ReplicaOneDrive(a, b);

        AsserisciPresenti(Chiavi(new MasterDataRepository<Requisito>(a.Paths.RequisitiFile)), "PC-A", RigaBase);
        AsserisciPresenti(Chiavi(new MasterDataRepository<Requisito>(b.Paths.RequisitiFile)), "PC-B", RigaBase);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Conflitti dei cataloghi master: lettura non bloccante, guardia per voce, risoluzione Master.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Mutation Delete nel formato di PhaseDMasterRepositoryEngine.CreateDelete, firmata dal device B.</summary>
    private static PhaseDMasterMutation MutazioneDeleteB(string catalogo, Guid uid, IReadOnlyDictionary<string, long> vvOsservato) =>
        new()
        {
            MutationId = Guid.NewGuid(),
            Catalog = catalogo,
            EntityUid = uid,
            Operation = PhaseDMasterMutationOperation.Delete,
            VersionVector = PhaseDVersionVector.Increment(vvOsservato, DeviceB),
            PayloadJson = "",
            PayloadSha256 = "",
            BusinessKey = "",
            TimestampUtc = DateTime.UtcNow
        };

    /// <summary>Versione concorrente della voce riconosciuta dal contenuto (finche' le teste sono due non c'e' altro modo).</summary>
    private static PhaseDMasterMutation VersioneConcorrente<T>(Postazione pc, string catalogo, Guid uid, Func<T, bool> criterio)
        where T : MasterEntity
    {
        var conflitto = Assert.Single(
            new PhaseDMasterMaterializer(pc.Cartella, pc.Locale).LoadConflicts(pc.Cartella, catalogo),
            x => x.EntityUid == uid);
        return conflitto.ConcurrentHeads.Single(m => criterio(JsonSerializer.Deserialize<T>(m.PayloadJson, JsonIo.Options)!));
    }

    private static string IdVersione(PhaseDMasterMutation mutazione) =>
        PhaseDMasterConflictResolver.VersionIdOf(mutazione.Operation, mutazione.PayloadSha256);

    /// <summary>Conflitto su RigaBase di Requisiti: PC-A modifica Testo con l'engine reale, PC-B modifica
    /// ApplicabileBanco offline partendo dalla stessa testa. Dopo la replica nei due sensi le teste sono concorrenti.</summary>
    private (Postazione A, Postazione B, Guid Uid) CreaConflittoRequisiti()
    {
        var c = Requisiti();
        var a = NuovaPostazione("PC-A");
        var b = NuovaPostazione("PC-B");
        SeminaRigaBaseLegacy(c, a);
        AttivaPhaseD(a, b);

        var repoA = new MasterDataRepository<Requisito>(a.Paths.RequisitiFile);
        var listaA = repoA.Load();
        c.Campo1.Applica(listaA.Single(r => Chiave(r) == RigaBase));
        repoA.Save(listaA);

        var repoB = new MasterDataRepository<Requisito>(b.Paths.RequisitiFile);
        var rigaB = repoB.Load().Single(r => Chiave(r) == RigaBase);
        c.Campo2.Applica(rigaB);
        PubblicaBatchB(b, 1, MutazioneUpsertB(c.Nome, rigaB, VersioneTesta(b, c.Nome, rigaB)));

        ReplicaOneDrive(a, b);
        ReplicaOneDrive(b, a);
        return (a, b, MasterEntityIdentityService.GetRequired(rigaB));
    }

    [Fact]
    public void PhaseD_Conflitto_LetturaDelCatalogoNonSiBloccaEEsponeLaVoceInConflitto()
    {
        var (a, b, uid) = CreaConflittoRequisiti();

        Assert.Single(new MasterDataRepository<Requisito>(a.Paths.RequisitiFile).Load(), r => Chiave(r) == RigaBase);
        Assert.Single(new MasterDataRepository<Requisito>(b.Paths.RequisitiFile).Load(), r => Chiave(r) == RigaBase);

        foreach (var pc in new[] { a, b })
        {
            var snapshot = new PhaseDMasterMaterializer(pc.Cartella, pc.Locale).LoadCatalog<Requisito>(pc.Cartella, "requisiti");
            Assert.Equal(new[] { uid }, snapshot.ConflictedUids);
            Assert.Contains(snapshot.Items, r => MasterEntityIdentityService.GetRequired(r) == uid);
        }
    }

    [Fact]
    public void PhaseD_Conflitto_SalvareUnaltraVoceFunzionaELaVoceInConflittoResta()
    {
        var (a, _, uid) = CreaConflittoRequisiti();
        var repoA = new MasterDataRepository<Requisito>(a.Paths.RequisitiFile);
        var lista = repoA.Load();
        lista.Add(new Requisito { Id = "ALTRA", Testo = "voce non in conflitto" });
        repoA.Save(lista);

        var letta = new MasterDataRepository<Requisito>(a.Paths.RequisitiFile).Load();
        AsserisciPresenti(Chiavi(new MasterDataRepository<Requisito>(a.Paths.RequisitiFile)), "PC-A", "ALTRA", RigaBase);
        Assert.Contains(letta, r => MasterEntityIdentityService.GetRequired(r) == uid);
        Assert.Equal(new[] { uid },
            new PhaseDMasterMaterializer(a.Cartella, a.Locale).LoadCatalog<Requisito>(a.Cartella, "requisiti").ConflictedUids);
    }

    [Fact]
    public void PhaseD_Conflitto_SalvareLaVoceConflittuaLanciaPhaseDMasterConflictException()
    {
        var (a, _, uid) = CreaConflittoRequisiti();
        var repoA = new MasterDataRepository<Requisito>(a.Paths.RequisitiFile);
        var lista = repoA.Load();
        lista.Single(r => MasterEntityIdentityService.GetRequired(r) == uid).Testo = "modifica su voce in conflitto";

        Assert.Throws<PhaseDMasterConflictException>(() => repoA.Save(lista));

        // Nulla e' stato scritto: la voce resta in conflitto e il catalogo resta leggibile.
        Assert.Single(new PhaseDMasterMaterializer(a.Cartella, a.Locale).LoadConflicts(a.Cartella, "requisiti"));
    }

    [Fact]
    public void PhaseD_Conflitto_ElencoConVersioniECampiDiversi()
    {
        var (a, _, uid) = CreaConflittoRequisiti();

        var conflitto = Assert.Single(PhaseDMasterConflictResolver.Elenca(a.Cartella, a.Locale));
        Assert.Equal(uid, conflitto.EntityUid);
        Assert.Equal("requisiti", conflitto.Catalogo);
        Assert.Equal(RigaBase, conflitto.BusinessKey);
        Assert.Equal(2, conflitto.Versioni.Count);
        Assert.Single(conflitto.Versioni, v => v.Visualizzata);
        var nonVisualizzata = Assert.Single(conflitto.Versioni, v => !v.Visualizzata);
        // Le due modifiche toccano campi diversi: la versione non mostrata differisce dalla mostrata su entrambi.
        Assert.Contains("Testo", nonVisualizzata.CampiDiversi);
        Assert.Contains("ApplicabileBanco", nonVisualizzata.CampiDiversi);
    }

    [Fact]
    public void PhaseD_Conflitto_RisoluzioneSuPcA_SincronizzataSuPcB_UnaSolaTestaUgualeAllaScelta()
    {
        var (a, b, uid) = CreaConflittoRequisiti();
        var scelta = VersioneConcorrente<Requisito>(a, "requisiti", uid, r => r.Testo == ValoreModificato);

        var esito = PhaseDMasterConflictResolver.Risolvi(a.Paths, "requisiti", uid, IdVersione(scelta),
            "Mario", isMaster: true, localBaseDirectory: a.Locale);
        Assert.Equal(PhaseDMasterMutationOperation.Upsert, esito.Operazione);

        ReplicaOneDrive(a, b);
        ReplicaOneDrive(b, a);

        foreach (var pc in new[] { a, b })
        {
            Assert.Empty(PhaseDMasterConflictResolver.Elenca(pc.Cartella, pc.Locale));
            var snapshot = new PhaseDMasterMaterializer(pc.Cartella, pc.Locale).LoadCatalog<Requisito>(pc.Cartella, "requisiti");
            Assert.Empty(snapshot.ConflictedUids);
            var testa = snapshot.Heads[uid];
            Assert.False(testa.HasConflict);
            Assert.Equal(PhaseDMasterMutationOperation.Upsert, testa.Operation);
            Assert.Equal(scelta.PayloadSha256, testa.PayloadSha256, ignoreCase: true);
            var voce = Assert.Single(snapshot.Items, r => MasterEntityIdentityService.GetRequired(r) == uid);
            Assert.Equal(ValoreModificato, voce.Testo);
            Assert.False(voce.ApplicabileBanco);
        }
    }

    [Fact]
    public void PhaseD_Conflitto_RisoluzioneConservaTuttiIPayloadOriginaliNelJournal()
    {
        var (a, b, uid) = CreaConflittoRequisiti();
        var teste = new PhaseDMasterMaterializer(a.Cartella, a.Locale).LoadConflicts(a.Cartella, "requisiti")
            .Single(x => x.EntityUid == uid).ConcurrentHeads.Select(m => m.PayloadSha256).ToList();
        Assert.Equal(2, teste.Count);
        var scelta = VersioneConcorrente<Requisito>(a, "requisiti", uid, r => r.Testo == ValoreModificato);

        PhaseDMasterConflictResolver.Risolvi(a.Paths, "requisiti", uid, IdVersione(scelta), "Mario", isMaster: true, localBaseDirectory: a.Locale);

        ReplicaOneDrive(a, b);
        ReplicaOneDrive(b, a);

        var shaNelJournal = PhaseDMasterJournalService.ReadAllMutations(a.Cartella)
            .Where(m => m.EntityUid == uid && m.Operation == PhaseDMasterMutationOperation.Upsert)
            .Select(m => m.PayloadSha256)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var sha in teste) Assert.Contains(sha, shaNelJournal);
    }

    [Fact]
    public void PhaseD_Conflitto_RisoluzioneSceltaEliminazioneRimuoveLaVoceSuEntrambiIPc()
    {
        var c = Requisiti();
        var a = NuovaPostazione("PC-A");
        var b = NuovaPostazione("PC-B");
        SeminaRigaBaseLegacy(c, a);
        AttivaPhaseD(a, b);

        var repoA = new MasterDataRepository<Requisito>(a.Paths.RequisitiFile);
        var listaA = repoA.Load();
        c.Campo1.Applica(listaA.Single(r => Chiave(r) == RigaBase));
        repoA.Save(listaA); // PC-A modifica

        var repoB = new MasterDataRepository<Requisito>(b.Paths.RequisitiFile);
        var rigaB = repoB.Load().Single(r => Chiave(r) == RigaBase);
        var uid = MasterEntityIdentityService.GetRequired(rigaB);
        PubblicaBatchB(b, 1, MutazioneDeleteB(c.Nome, uid, VersioneTesta(b, c.Nome, rigaB))); // PC-B elimina, offline

        ReplicaOneDrive(a, b);
        ReplicaOneDrive(b, a);

        var conflitto = Assert.Single(PhaseDMasterConflictResolver.Elenca(a.Cartella, a.Locale));
        var eliminazione = conflitto.Versioni.Single(v => v.Operazione == PhaseDMasterMutationOperation.Delete);
        var esito = PhaseDMasterConflictResolver.Risolvi(a.Paths, "requisiti", uid, eliminazione.VersionId,
            "Mario", isMaster: true, localBaseDirectory: a.Locale);
        Assert.Equal(PhaseDMasterMutationOperation.Delete, esito.Operazione);

        ReplicaOneDrive(a, b);
        ReplicaOneDrive(b, a);

        foreach (var pc in new[] { a, b })
        {
            Assert.Empty(PhaseDMasterConflictResolver.Elenca(pc.Cartella, pc.Locale));
            var snapshot = new PhaseDMasterMaterializer(pc.Cartella, pc.Locale).LoadCatalog<Requisito>(pc.Cartella, "requisiti");
            Assert.Equal(PhaseDMasterMutationOperation.Delete, snapshot.Heads[uid].Operation);
            Assert.DoesNotContain(snapshot.Items, r => MasterEntityIdentityService.GetRequired(r) == uid);
            Assert.DoesNotContain(new MasterDataRepository<Requisito>(pc.Paths.RequisitiFile).Load(), r => Chiave(r) == RigaBase);
        }
    }

    [Fact]
    public void PhaseD_Conflitto_NonMasterNonPuoRisolvere()
    {
        var (a, _, uid) = CreaConflittoRequisiti();
        var scelta = VersioneConcorrente<Requisito>(a, "requisiti", uid, r => r.Testo == ValoreModificato);
        var mutazioniPrima = PhaseDMasterJournalService.ReadAllMutations(a.Cartella).Count;

        Assert.Throws<UnauthorizedAccessException>(() =>
            PhaseDMasterConflictResolver.Risolvi(a.Paths, "requisiti", uid, IdVersione(scelta), "Luigi", isMaster: false, localBaseDirectory: a.Locale));

        Assert.Equal(mutazioniPrima, PhaseDMasterJournalService.ReadAllMutations(a.Cartella).Count);
        Assert.Single(PhaseDMasterConflictResolver.Elenca(a.Cartella, a.Locale));
    }

    [Fact]
    public void PhaseD_Conflitto_WorkspaceInSolaLetturaRifiutaLaRisoluzione()
    {
        var (a, _, uid) = CreaConflittoRequisiti();
        var scelta = VersioneConcorrente<Requisito>(a, "requisiti", uid, r => r.Testo == ValoreModificato);
        var mutazioniPrima = PhaseDMasterJournalService.ReadAllMutations(a.Cartella).Count;

        WorkspaceReadOnlyService.SetEnabled(a.Cartella, true);
        try
        {
            Assert.Throws<IOException>(() =>
                PhaseDMasterConflictResolver.Risolvi(a.Paths, "requisiti", uid, IdVersione(scelta), "Mario", isMaster: true, localBaseDirectory: a.Locale));
        }
        finally
        {
            WorkspaceReadOnlyService.SetEnabled(a.Cartella, false);
        }

        Assert.Equal(mutazioniPrima, PhaseDMasterJournalService.ReadAllMutations(a.Cartella).Count);
        Assert.Single(PhaseDMasterConflictResolver.Elenca(a.Cartella, a.Locale));
    }

    /// <summary>Il contratto guasto resta fail-closed sulla singola voce; la lettura/enumerazione del catalogo no.</summary>
    [Fact]
    public void PhaseD_Conflitto_LetturaSingolaVoceFailClosedEEnumerazioneCoerenteConLoad()
    {
        var (a, _, uid) = CreaConflittoRequisiti();
        var lettore = new PhaseDMasterScalableReader(a.Cartella, a.Locale);

        Assert.Throws<PhaseDMasterConflictException>(() => lettore.LoadActiveEntity<Requisito>("requisiti", uid));
        var enumerati = lettore.EnumerateCatalog<Requisito>("requisiti").ToList();
        Assert.Contains(enumerati, r => MasterEntityIdentityService.GetRequired(r) == uid);
        Assert.Equal(new PhaseDMasterMaterializer(a.Cartella, a.Locale).LoadCatalog<Requisito>(a.Cartella, "requisiti").Items.Count,
            enumerati.Count);
    }

    /// <summary>Test con una sola Pacchetto nella condizione (come WU-only in RevisioneUdtServiceTests).</summary>
    private static TestCase TestConWu(string id, string workingUnit) => new()
    {
        Id = id,
        Componente = "BCM",
        Esecuzioni = new() { new Esecuzione
        {
            Iterazione = 1,
            Data = new DateTime(2026, 9, 1),
            Operatore = "Mario",
            Condizioni = new() { new Condizione { Esito = Esito.Conforme, ValoriStrumenti = new() { [RevisioneUdtService.ChiaveCampoNome] = workingUnit } } }
        } }
    };
}
