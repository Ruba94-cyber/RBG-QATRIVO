using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Decisione utente 6.4.1 "Avviso + merge automatico", punto B — decisione orchestratore: base del fork
/// persistita SOLO in journal.db locale del PC (mai nella cartella condivisa), merge automatico applicato solo
/// da un PC autore di uno dei due rami che possiede localmente quella base, e solo dal DeviceId ordinale minore
/// fra i due autori.
///
/// Simula due postazioni con due cartelle CONDIVISE FISICAMENTE SEPARATE (due <see cref="DataPaths"/> distinti,
/// due <see cref="TestCaseRepository"/> con DeviceId diverso) e propaga i cambiamenti copiando i file fra le due
/// come farebbe OneDrive — stesso approccio concettuale dei test multi-PC già esistenti nel progetto
/// (<c>TestCaseConcurrentSaveTwoInstancesTests</c>, <c>ResolvedJournalForkTests</c>), qui applicato a due radici
/// fisicamente distinte perché il fork richiede che le due Save NON si vedano a vicenda mentre avvengono.
/// </summary>
public sealed class TestCaseAutoForkMergeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_AutoForkMerge_" + Guid.NewGuid().ToString("N"));
    private readonly bool _flagOriginale = TestCaseAutoForkMergeService.AbilitaMergeAutomaticoFork;

    public void Dispose()
    {
        TestCaseAutoForkMergeService.AbilitaMergeAutomaticoFork = _flagOriginale;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Postazione
    {
        public required DataPaths Paths;
        public required TestCaseRepository Repo;
        public required string DeviceId;
    }

    private Postazione CreaPostazione(string nome, string deviceId)
    {
        var sharedRoot = Path.Combine(_root, "shared_" + nome);
        var localDir = Path.Combine(_root, "local_" + nome);
        var paths = new DataPaths(sharedRoot);
        paths.EnsureStructure();
        var store = new LocalTestIndexStore(sharedRoot, localDir);
        var repo = new TestCaseRepository(paths, store, deviceIdOverride: deviceId);
        return new Postazione { Paths = paths, Repo = repo, DeviceId = deviceId };
    }

    /// <summary>Copia (sovrascrivendo) i file di una sottocartella condivisa da una postazione all'altra —
    /// simula il ritardo/consegna di sync OneDrive/SharePoint per quella sottocartella.</summary>
    private static void Sincronizza(Postazione da, Postazione a, string sottocartella)
    {
        var origine = Path.Combine(da.Paths.Root, sottocartella);
        var destinazione = Path.Combine(a.Paths.Root, sottocartella);
        if (!Directory.Exists(origine)) return;
        Directory.CreateDirectory(destinazione);
        foreach (var file in Directory.EnumerateFiles(origine, "*", SearchOption.AllDirectories))
        {
            var relativo = Path.GetRelativePath(origine, file);
            var target = Path.Combine(destinazione, relativo);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>Sync bidirezionale completo delle cartelle rilevanti al journal/fork (non delle Basi locali,
    /// che per definizione restano SOLO locali e non vengono mai sincronizzate).</summary>
    private static void SincronizzaBidirezionale(Postazione x, Postazione y)
    {
        foreach (var sotto in new[] { "testcases", Path.Combine("_ditm", "journal"), Path.Combine("_ditm", "conflicts") })
        {
            Sincronizza(x, y, sotto);
            Sincronizza(y, x, sotto);
        }
    }

    private static TestCase Base(string id) => new()
    {
        Id = id,
        Componente = new EcuSelection { "BCM" },
        Descrizione = "descrizione base",
        DescrizioneBreve = "breve base",
        CreatoDa = "Seed",
        ModificatoDa = "Seed"
    };

    /// <summary>Crea il Test base su A, lo propaga su B, e ritorna l'Id e la revisione base (sempre 1).</summary>
    private static string CreaBaseCondivisa(Postazione a, Postazione b, string id)
    {
        var tc = Base(id);
        a.Repo.Save(tc, isNew: true);
        SincronizzaBidirezionale(a, b);
        b.Repo.RefreshIndex();
        a.Repo.RefreshIndex();
        return tc.Id;
    }

    /// <summary>Ogni postazione salva la propria modifica sulla PROPRIA copia (nessuna delle due vede l'altra
    /// finché non viene chiamato SincronizzaBidirezionale): è esattamente la finestra che produce un fork reale.
    /// Dopo il salvataggio di entrambe, sincronizza e aggiorna i metadati del journal su entrambe.</summary>
    private static void SalvaDueRamiDivergentiESincronizza(Postazione a, Postazione b, string id,
        Action<TestCase> mutaA, Action<TestCase> mutaB)
    {
        var tcA = a.Repo.LoadCurrent(a.Repo.GetById(id)!)!;
        mutaA(tcA);
        a.Repo.Save(tcA, isNew: false, revisioneAttesa: 1);

        var tcB = b.Repo.LoadCurrent(b.Repo.GetById(id)!)!;
        mutaB(tcB);
        b.Repo.Save(tcB, isNew: false, revisioneAttesa: 1);

        // Il file autorevole "vince" a chi arriva per ultimo nella copia fisica: con l'ordine (a, b) usato da
        // SincronizzaBidirezionale il contenuto del ramo A resta quello visibile su ENTRAMBE le copie dopo il
        // sync. Il merge automatico deve comunque funzionare a prescindere da quale dei due ha fisicamente
        // "vinto" il file corrente, perché ricostruisce ENTRAMBI i rami dagli snapshot _ditm/conflicts, non dal
        // solo file corrente.
        SincronizzaBidirezionale(a, b);

        a.Repo.RefreshIndex();
        b.Repo.RefreshIndex();
        a.Repo.RefreshJournalMetadata(); // rileva il fork e pubblica il proprio ramo in _ditm/conflicts
        b.Repo.RefreshJournalMetadata();
        SincronizzaBidirezionale(a, b); // propaga gli snapshot _ditm/conflicts appena pubblicati da entrambe
        a.Repo.RefreshJournalMetadata();
        b.Repo.RefreshJournalMetadata();
    }

    [Fact]
    public void CampiDiversi_MergeAutomatico_ContenutoContieneEntrambeLeModifiche()
    {
        var a = CreaPostazione("A", "device-aaaa");
        var b = CreaPostazione("B", "device-bbbb"); // ordinale maggiore: A applica
        var id = CreaBaseCondivisa(a, b, "TC-AUTO-0001");

        SalvaDueRamiDivergentiESincronizza(a, b, id,
            mutaA: tc => tc.Descrizione = "modifica ramo A",
            mutaB: tc => tc.DescrizioneBreve = "modifica ramo B");

        Assert.Single(a.Repo.JournalForks);

        var report = TestCaseAutoForkMergeService.EseguiSuForkNoti(a.Paths, a.Repo);
        Assert.Equal(1, report.Applicati);

        var risultato = a.Repo.LoadCurrent(a.Repo.GetById(id)!)!;
        Assert.Equal("modifica ramo A", risultato.Descrizione);
        Assert.Equal("modifica ramo B", risultato.DescrizioneBreve);
        Assert.Contains(risultato.Changelog, c => c.Campo == "merge automatico di due postazioni");
        Assert.NotEmpty(TestCaseForkResolution.GetResolvedKeys(risultato));
    }

    /// <summary>Review 6.4.1: Changelog è escluso dal merge a tre vie, quindi il risultato deve unire le voci di
    /// ENTRAMBI i rami (come la risoluzione manuale del Master), non conservare quelle di uno solo.</summary>
    [Fact]
    public void CampiDiversi_MergeAutomatico_ConservaIlChangelogDiEntrambiIRami()
    {
        var a = CreaPostazione("A", "device-aaaa");
        var b = CreaPostazione("B", "device-bbbb");
        var id = CreaBaseCondivisa(a, b, "TC-AUTO-CHLOG");

        SalvaDueRamiDivergentiESincronizza(a, b, id,
            mutaA: tc =>
            {
                tc.Descrizione = "modifica ramo A";
                tc.Changelog.Add(new ChangelogEntry { Data = DateTime.Now, Operatore = "Operatore A", Campo = "Descrizione", ValorePrecedente = "descrizione base", ValoreNuovo = "modifica ramo A" });
            },
            mutaB: tc =>
            {
                tc.DescrizioneBreve = "modifica ramo B";
                tc.Changelog.Add(new ChangelogEntry { Data = DateTime.Now, Operatore = "Operatore B", Campo = "DescrizioneBreve", ValorePrecedente = "breve base", ValoreNuovo = "modifica ramo B" });
            });

        Assert.Equal(1, TestCaseAutoForkMergeService.EseguiSuForkNoti(a.Paths, a.Repo).Applicati);

        var risultato = a.Repo.LoadCurrent(a.Repo.GetById(id)!)!;
        Assert.Contains(risultato.Changelog, c => c.Operatore == "Operatore A" && c.Campo == "Descrizione");
        Assert.Contains(risultato.Changelog, c => c.Operatore == "Operatore B" && c.Campo == "DescrizioneBreve");
        Assert.Single(risultato.Changelog, c => c.Campo == "merge automatico di due postazioni");
    }

    [Fact]
    public void CampiDiversi_MergeAutomatico_ChiudeIlForkENonNeCreaUnoNuovo()
    {
        var a = CreaPostazione("A", "device-aaaa");
        var b = CreaPostazione("B", "device-bbbb");
        var id = CreaBaseCondivisa(a, b, "TC-AUTO-0002");

        SalvaDueRamiDivergentiESincronizza(a, b, id,
            mutaA: tc => tc.Descrizione = "modifica ramo A",
            mutaB: tc => tc.DescrizioneBreve = "modifica ramo B");

        TestCaseAutoForkMergeService.EseguiSuForkNoti(a.Paths, a.Repo);
        a.Repo.RefreshJournalMetadata();
        Assert.Empty(a.Repo.JournalForks);

        // Propaga il risultato del merge alla postazione B e verifica che anche lì il fork sia chiuso, senza
        // che il refresh journal di B ne generi uno nuovo (il proprio ramo B è ormai "storia" rispetto al
        // risultato del merge, che discende dalla stessa base + contiene entrambe le modifiche).
        SincronizzaBidirezionale(a, b);
        b.Repo.RefreshIndex();
        b.Repo.RefreshJournalMetadata();
        Assert.Empty(b.Repo.JournalForks);
    }

    [Fact]
    public void StessoCampoModificatoDaEntrambi_NessunMergeAutomatico_RestaAlMaster()
    {
        var a = CreaPostazione("A", "device-aaaa");
        var b = CreaPostazione("B", "device-bbbb");
        var id = CreaBaseCondivisa(a, b, "TC-AUTO-0003");

        SalvaDueRamiDivergentiESincronizza(a, b, id,
            mutaA: tc => tc.Descrizione = "versione A",
            mutaB: tc => tc.Descrizione = "versione B"); // STESSO campo, valori diversi: conflitto vero

        Assert.Single(a.Repo.JournalForks);
        var report = TestCaseAutoForkMergeService.EseguiSuForkNoti(a.Paths, a.Repo);

        Assert.Equal(0, report.Applicati);
        Assert.Equal(1, report.NonEleggibili);
        Assert.Single(a.Repo.JournalForks); // il fork resta aperto, esattamente come nel flusso Master esistente
    }

    [Fact]
    public void RevisioniAggiunteDaEntrambeLePartiOEsecuzioniDivergenti_NessunMergeAutomatico()
    {
        var a = CreaPostazione("A", "device-aaaa");
        var b = CreaPostazione("B", "device-bbbb");
        var id = CreaBaseCondivisa(a, b, "TC-AUTO-0004");

        SalvaDueRamiDivergentiESincronizza(a, b, id,
            mutaA: tc => tc.Esecuzioni.Add(new Esecuzione { Iterazione = 2, Data = DateTime.Now, Operatore = "A", Esito = Esito.Conforme }),
            mutaB: tc => tc.Esecuzioni.Add(new Esecuzione { Iterazione = 2, Data = DateTime.Now, Operatore = "B", Esito = Esito.NonConforme }));

        var report = TestCaseAutoForkMergeService.EseguiSuForkNoti(a.Paths, a.Repo);

        Assert.Equal(0, report.Applicati);
        Assert.Single(a.Repo.JournalForks);
    }

    [Fact]
    public void PostazioneSenzaBaseLocale_NonApplicaIlMerge()
    {
        var a = CreaPostazione("A", "device-aaaa"); // minore: sarebbe eleggibile per il tie-break...
        var b = CreaPostazione("B", "device-bbbb");
        var id = CreaBaseCondivisa(a, b, "TC-AUTO-0005");

        SalvaDueRamiDivergentiESincronizza(a, b, id,
            mutaA: tc => tc.Descrizione = "modifica ramo A",
            mutaB: tc => tc.DescrizioneBreve = "modifica ramo B");

        // ...ma la sua cartella dati locale (journal.db) è stata persa/mai esistita: simulato ricreando il
        // repository di A su una directory locale VUOTA (stesso root condiviso, nessuna base_snapshot).
        var storeVuoto = new LocalTestIndexStore(a.Paths.Root, Path.Combine(_root, "local_A_senza_base"));
        var repoASenzaBase = new TestCaseRepository(a.Paths, storeVuoto, deviceIdOverride: "device-aaaa");
        repoASenzaBase.RefreshIndex();
        repoASenzaBase.RefreshJournalMetadata();

        var report = TestCaseAutoForkMergeService.EseguiSuForkNoti(a.Paths, repoASenzaBase);

        Assert.Equal(0, report.Applicati);
        Assert.Equal(1, report.NonEleggibili);
        Assert.Single(repoASenzaBase.JournalForks); // resta al Master
    }

    [Fact]
    public void SoloIlDeviceIdOrdinaleMinoreApplica_LAltraPostazioneNonFaNulla()
    {
        var a = CreaPostazione("A", "device-zzzz"); // maggiore
        var b = CreaPostazione("B", "device-aaaa"); // minore: deve applicare lei
        var id = CreaBaseCondivisa(a, b, "TC-AUTO-0006");

        SalvaDueRamiDivergentiESincronizza(a, b, id,
            mutaA: tc => tc.Descrizione = "modifica ramo A",
            mutaB: tc => tc.DescrizioneBreve = "modifica ramo B");

        // La postazione con DeviceId maggiore (A) NON deve applicare nulla, anche se ha la base localmente.
        var reportA = TestCaseAutoForkMergeService.EseguiSuForkNoti(a.Paths, a.Repo);
        Assert.Equal(0, reportA.Applicati);
        Assert.Single(a.Repo.JournalForks);

        // La postazione con DeviceId minore (B) applica.
        var reportB = TestCaseAutoForkMergeService.EseguiSuForkNoti(b.Paths, b.Repo);
        Assert.Equal(1, reportB.Applicati);

        var risultato = b.Repo.LoadCurrent(b.Repo.GetById(id)!)!;
        Assert.Equal("modifica ramo A", risultato.Descrizione);
        Assert.Equal("modifica ramo B", risultato.DescrizioneBreve);
    }

    [Fact]
    public void SecondaEsecuzioneSulloStessoFork_ENoOp_Idempotente()
    {
        var a = CreaPostazione("A", "device-aaaa");
        var b = CreaPostazione("B", "device-bbbb");
        var id = CreaBaseCondivisa(a, b, "TC-AUTO-0007");

        SalvaDueRamiDivergentiESincronizza(a, b, id,
            mutaA: tc => tc.Descrizione = "modifica ramo A",
            mutaB: tc => tc.DescrizioneBreve = "modifica ramo B");

        var primo = TestCaseAutoForkMergeService.EseguiSuForkNoti(a.Paths, a.Repo);
        Assert.Equal(1, primo.Applicati);
        var revisioneDopoPrimoMerge = a.Repo.LoadCurrent(a.Repo.GetById(id)!)!.RevisioneDati;

        // Rilancio immediato sulla stessa postazione: il fork ora è chiuso (RefreshJournalMetadata lo toglie
        // dai fork noti), quindi il secondo giro non trova nulla da fare.
        a.Repo.RefreshJournalMetadata();
        var secondo = TestCaseAutoForkMergeService.EseguiSuForkNoti(a.Paths, a.Repo);
        Assert.Equal(0, secondo.Applicati);

        var revisioneFinale = a.Repo.LoadCurrent(a.Repo.GetById(id)!)!.RevisioneDati;
        Assert.Equal(revisioneDopoPrimoMerge, revisioneFinale); // nessuna scrittura aggiuntiva
    }

    [Fact]
    public void FlagDisattivato_NonApplicaMai()
    {
        var a = CreaPostazione("A", "device-aaaa");
        var b = CreaPostazione("B", "device-bbbb");
        var id = CreaBaseCondivisa(a, b, "TC-AUTO-0008");

        SalvaDueRamiDivergentiESincronizza(a, b, id,
            mutaA: tc => tc.Descrizione = "modifica ramo A",
            mutaB: tc => tc.DescrizioneBreve = "modifica ramo B");

        TestCaseAutoForkMergeService.AbilitaMergeAutomaticoFork = false;
        var report = TestCaseAutoForkMergeService.EseguiSuForkNoti(a.Paths, a.Repo);

        Assert.Equal(0, report.TotaleForkEsaminati);
        Assert.Single(a.Repo.JournalForks);
    }
}
