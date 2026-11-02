using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Regressione del difetto "indice stantio" (gate 6.4.2-indice-stantio, evidenza reale riportata dall'utente:
/// tools/data-fix-6.4.2 su una cartella di output cancellata e ricreata con contenuto diverso, stesso percorso
/// — AppState riportava 0 Test ImportStatoUdt letti dall'indice SQLite del giro precedente, non dal contenuto
/// reale su disco). WorkspaceLocalPaths deriva la cartella locale (indice Test, indice NC, journal) SOLO dal
/// percorso condiviso (vedi WorkspaceLocalPaths.ComputeWorkspaceKey): se la cartella dati viene sostituita allo
/// STESSO percorso, prima della correzione AppState.InizializzaCartella riapriva l'indice locale del giro
/// precedente credendolo ancora valido. Tutti i test usano TestLocalAppDataFixture (vedi il ModuleInitializer
/// di quell'assembly) per non scrivere mai nel vero %LOCALAPPDATA% della macchina.
/// </summary>
public sealed class WorkspaceIdentityStaleIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_WorkspaceIdentity_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private static void SeedTestCases(DataPaths paths, string prefix, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var id = $"{prefix}-{i:D4}";
            JsonIo.WriteAtomic(paths.TestCaseFile("BCM", id), new TestCase { Id = id, Componente = "BCM" });
        }
    }

    private static void SeedNonConformita(DataPaths paths, string prefix, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var id = $"{prefix}-{i:D4}";
            var nc = new NonConformita { Id = id, DaSegnalazioneRete = true };
            JsonIo.WriteAtomic(paths.NonConformitaFile(id), nc);
        }
    }

    /// <summary>Riproduzione diretta del difetto: cartella dati A in P, AppState completo aperto e chiuso su P
    /// (indice locale costruito), poi il CONTENUTO di P viene sostituito con una cartella dati B diversa (Test e
    /// NC in numero diverso) — stesso percorso P. Riaprendo AppState su P, prima della correzione l'app vedeva
    /// ancora lo stato di A (letto dall'indice SQLite locale, chiave derivata solo dal percorso); dopo la
    /// correzione deve vedere B, ricostruendo l'indice interamente da disco.</summary>
    [Fact]
    public void SostituzioneCartellaAllostessoPercorso_RicostruisceIndiceEMostraContenutoReale()
    {
        var pathsA = new DataPaths(_root);
        pathsA.EnsureStructure();
        SeedTestCases(pathsA, "TC-A", 5);
        SeedNonConformita(pathsA, "NC-A", 3);

        using (var stateA = TestStateFactory.Create())
        {
            stateA.ImpostaCartella(_root);
            Assert.True(stateA.InizializzazioneWorkspaceCompletata);
            Assert.Equal(5, stateA.TestCases.GetAll().Count);
            Assert.Equal(3, stateA.NonConformita.GetAll().Count);
        }

        // La cartella dati A viene cancellata e ricreata con un contenuto B DIVERSO, esattamente allo stesso
        // percorso P — lo scenario reale riprodotto con tools/data-fix-6.4.2 (cartella di output riutilizzata
        // fra due esecuzioni) e con un rilascio della cartella dati OneDrive/SharePoint copiato sullo stesso path.
        Directory.Delete(_root, recursive: true);
        var pathsB = new DataPaths(_root);
        pathsB.EnsureStructure();
        SeedTestCases(pathsB, "TC-B", 2);
        SeedNonConformita(pathsB, "NC-B", 7);

        using var stateB = TestStateFactory.Create();
        stateB.ImpostaCartella(_root);

        Assert.True(stateB.InizializzazioneWorkspaceCompletata);
        var testIds = stateB.TestCases.GetAll().Select(t => t.Id).OrderBy(x => x).ToList();
        Assert.Equal(2, testIds.Count);
        Assert.All(testIds, id => Assert.StartsWith("TC-B", id));
        Assert.DoesNotContain(testIds, id => id.StartsWith("TC-A"));

        var ncIds = stateB.NonConformita.GetAll().Select(n => n.Id).OrderBy(x => x).ToList();
        Assert.Equal(7, ncIds.Count);
        Assert.All(ncIds, id => Assert.StartsWith("NC-B", id));
        Assert.DoesNotContain(ncIds, id => id.StartsWith("NC-A"));
    }

    /// <summary>Stessa sostituzione, ma la cartella B non ha _ditm/journal (come fa tools/data-fix-6.4.2, che lo
    /// rimuove esplicitamente dalla copia di output): la ricostruzione deve avvenire comunque, non solo quando
    /// il journal condiviso è presente.</summary>
    [Fact]
    public void SostituzioneCartella_SenzaJournalCondiviso_RicostruisceComunque()
    {
        var pathsA = new DataPaths(_root);
        pathsA.EnsureStructure();
        SeedTestCases(pathsA, "TC-A", 4);

        using (var stateA = TestStateFactory.Create())
        {
            stateA.ImpostaCartella(_root);
            Assert.Equal(4, stateA.TestCases.GetAll().Count);
        }

        Directory.Delete(_root, recursive: true);
        var pathsB = new DataPaths(_root);
        pathsB.EnsureStructure();
        SeedTestCases(pathsB, "TC-B", 1);
        var journalDir = Path.Combine(_root, "_ditm", "journal");
        Assert.False(Directory.Exists(journalDir), "precondizione del test: la cartella B ricreata non deve avere _ditm/journal.");

        using var stateB = TestStateFactory.Create();
        stateB.ImpostaCartella(_root);

        Assert.True(stateB.InizializzazioneWorkspaceCompletata);
        var testIds = stateB.TestCases.GetAll().Select(t => t.Id).ToList();
        Assert.Single(testIds);
        Assert.Equal("TC-B-0000", testIds[0]);
    }

    /// <summary>Cartella condivisa senza id (dato "vecchio", creato prima di questa correzione, aperto per la
    /// prima volta col codice nuovo): l'id va creato e la ricostruzione forzata una volta sola. Una volta creato
    /// l'id e allineato il marcatore locale, riaperture successive SENZA modifiche non devono più ricostruire
    /// l'indice — verificato contando gli eventi "testcases.full-reconciliation" in performance.ndjson, non solo
    /// il risultato finale (contenuto corretto anche con un indice riusato senza modifiche).</summary>
    [Fact]
    public void CartellaCondivisaSenzaId_CreaIdERicostruisceUnaVoltaSola_PoiNonPiu()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        SeedTestCases(paths, "TC", 6);

        string? workspaceDir;
        using (var state0 = TestStateFactory.Create())
        {
            state0.ImpostaCartella(_root);
            Assert.Equal(6, state0.TestCases.GetAll().Count);
            workspaceDir = state0.TestCases.LocalWorkspaceDirectory;
        }
        Assert.False(string.IsNullOrWhiteSpace(workspaceDir));

        // Simula un workspace "vecchio": id condiviso e marcatore locale non esistevano ancora (creati per la
        // prima volta da questa correzione). L'indice SQLite locale e il journal restano quelli già costruiti
        // sopra — esattamente come un PC che aggiorna TestLoom senza che la cartella dati sia mai cambiata.
        File.Delete(Path.Combine(_root, "_ditm", "workspace-id.json"));
        File.Delete(Path.Combine(workspaceDir!, "workspace-id.local.json"));

        var eventiPrimaDelPrimoAvvioConCodiceNuovo = ContaRicostruzioniComplete(workspaceDir!);

        using (var state1 = TestStateFactory.Create())
        {
            state1.ImpostaCartella(_root);
            Assert.True(state1.InizializzazioneWorkspaceCompletata);
            Assert.Equal(6, state1.TestCases.GetAll().Count);
        }

        Assert.True(File.Exists(Path.Combine(_root, "_ditm", "workspace-id.json")),
            "l'id condiviso doveva essere creato al primo avvio col codice nuovo su una cartella priva di id.");
        var dopoIlPrimoAvvio = ContaRicostruzioniComplete(workspaceDir!);
        // Dalla 6.4.2 (WorkspaceIdentityService.SvuotaIndiciLocali) la cartella locale del workspace viene
        // svuotata prima della ricostruzione, diagnostica compresa: il conteggio riparte da zero e deve
        // registrare almeno la ricostruzione completa di questo avvio.
        _ = eventiPrimaDelPrimoAvvioConCodiceNuovo;
        Assert.True(dopoIlPrimoAvvio >= 1,
            "il primo avvio su una cartella senza id deve forzare una ricostruzione completa dell'indice.");

        // Riapertura successiva, contenuto invariato: NESSUNA nuova ricostruzione completa (solo lettura dei due
        // file id, costo trascurabile) — l'id condiviso ora coincide col marcatore locale appena scritto.
        using (var state2 = TestStateFactory.Create())
        {
            state2.ImpostaCartella(_root);
            Assert.Equal(6, state2.TestCases.GetAll().Count);
        }
        var dopoLaSecondaRiapertura = ContaRicostruzioniComplete(workspaceDir!);
        Assert.Equal(dopoIlPrimoAvvio, dopoLaSecondaRiapertura);
    }

    /// <summary>Id condiviso invariato fra due riaperture successive con contenuto identico: nessuna
    /// ricostruzione completa inutile (stesso spirito del test sopra, ma senza il salto "workspace vecchio":
    /// copre il percorso normale, più comune, in cui l'id esiste già dalla prima apertura).</summary>
    [Fact]
    public void IdInvariato_NessunaRicostruzioneInutileTraDueRiaperture()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        SeedTestCases(paths, "TC", 3);

        string? workspaceDir;
        using (var state0 = TestStateFactory.Create())
        {
            state0.ImpostaCartella(_root);
            workspaceDir = state0.TestCases.LocalWorkspaceDirectory;
        }
        var prima = ContaRicostruzioniComplete(workspaceDir!);

        using (var state1 = TestStateFactory.Create())
        {
            state1.ImpostaCartella(_root);
            Assert.Equal(3, state1.TestCases.GetAll().Count);
        }

        Assert.Equal(prima, ContaRicostruzioniComplete(workspaceDir!));
    }

    /// <summary>Race multi-PC: due scritture "concorrenti" dell'id condiviso sulla stessa cartella dati (stesso
    /// processo di test, due thread) devono convergere su UN SOLO id finale — mai due id permanenti diversi per
    /// la stessa cartella condivisa.</summary>
    [Fact]
    public void EnsureOrReadSharedId_DueScrittureConcorrenti_ConvergonoSuUnSoloId()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();

        Guid? risultato1 = null, risultato2 = null;
        var barriera = new Barrier(2);
        var t1 = new Thread(() =>
        {
            barriera.SignalAndWait();
            risultato1 = WorkspaceIdentityService.EnsureOrReadSharedId(paths, allowWrite: true);
        });
        var t2 = new Thread(() =>
        {
            barriera.SignalAndWait();
            risultato2 = WorkspaceIdentityService.EnsureOrReadSharedId(paths, allowWrite: true);
        });
        t1.Start(); t2.Start();
        t1.Join(TimeSpan.FromSeconds(10));
        t2.Join(TimeSpan.FromSeconds(10));

        Assert.NotNull(risultato1);
        Assert.NotNull(risultato2);
        Assert.Equal(risultato1, risultato2);

        var suDisco = WorkspaceIdentityService.EnsureOrReadSharedId(paths, allowWrite: false);
        Assert.Equal(risultato1, suDisco);
        // Nessun file temporaneo residuo: la race non lascia scritture parziali nella cartella condivisa.
        var testloomDir = Path.Combine(_root, "_ditm");
        Assert.Single(Directory.GetFiles(testloomDir, "workspace-id*"));
    }

    /// <summary>6.7.1 (fix rete): se la destinazione non può essere sostituita, il temporaneo non deve restare nella cartella
    /// condivisa e non si deve inventare un id.</summary>
    [Fact]
    public void EnsureOrReadSharedId_DestinazioneNonSostituibile_NonLasciaTemporanei()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var testloomDir = Path.Combine(_root, "_ditm");
        Directory.CreateDirectory(testloomDir);
        // Una cartella al posto del file di destinazione fa fallire il rename: il temporaneo scritto prima deve essere eliminato.
        Directory.CreateDirectory(Path.Combine(testloomDir, "workspace-id.json"));

        var risultato = WorkspaceIdentityService.EnsureOrReadSharedId(paths, allowWrite: true);

        Assert.Null(risultato);
        Assert.Empty(Directory.GetFiles(testloomDir, "workspace-id*"));
    }

    private static int ContaRicostruzioniComplete(string workspaceDirectory) =>
        PerformanceDiagnosticsService.ReadRecent(workspaceDirectory)
            .Count(x => x.Operation == "testcases.full-reconciliation");

    /// <summary>6.4.2: sostituendo la cartella nello stesso percorso con una cartella che ha cataloghi Phase D
    /// DIVERSI (stessa componente con identità diversa), prima l'indice locale dei cataloghi mescolava le due cartelle e
    /// l'app si bloccava all'avvio (PhaseDMasterBusinessKeyConflictException, procedura manuale guida §13.2). Ora
    /// gli indici locali vengono rimossi prima dell'apertura e ricostruiti: il catalogo è quello della nuova
    /// cartella e i backup locali restano.</summary>
    [Fact]
    public void SostituzioneCartellaConCataloghiDiversi_IndiciLocaliRicostruiti_BackupConservati()
    {
        Directory.CreateDirectory(_root);
        string backupLocale;
        using (var stateA = TestStateFactory.Create())
        {
            stateA.ImpostaCartella(_root);
            Assert.True(stateA.InizializzazioneWorkspaceCompletata);
            var componente = stateA.Master.Componente.Load();
            componente.Add(new Componente { Nome = "Componente-CONDIVISA" });
            componente.Add(new Componente { Nome = "Componente-SOLO-A" });
            stateA.Master.Componente.Save(componente);
            backupLocale = WorkspaceLocalPaths.GetBackupDirectory(_root);
            Directory.CreateDirectory(backupLocale);
            File.WriteAllText(Path.Combine(backupLocale, "TestLoom_snapshot_20260101.zip"), "backup");
        }

        // Cartella B preparata altrove (identità e journal dei cataloghi propri), poi messa nello stesso percorso.
        var altrove = _root + "_B";
        using (var stateB0 = TestStateFactory.Create())
        {
            Directory.CreateDirectory(altrove);
            stateB0.ImpostaCartella(altrove);
            var componente = stateB0.Master.Componente.Load();
            componente.Add(new Componente { Nome = "Componente-CONDIVISA" });
            componente.Add(new Componente { Nome = "Componente-SOLO-B" });
            stateB0.Master.Componente.Save(componente);
        }
        Directory.Delete(_root, recursive: true);
        Directory.Move(altrove, _root);

        using var stateB = TestStateFactory.Create();
        stateB.ImpostaCartella(_root);
        Assert.True(stateB.InizializzazioneWorkspaceCompletata);
        var nomi = stateB.Master.Componente.Load().Select(e => e.Nome).ToList();
        Assert.Contains("Componente-SOLO-B", nomi);
        Assert.DoesNotContain("Componente-SOLO-A", nomi);
        Assert.Equal(1, nomi.Count(n => n == "Componente-CONDIVISA"));
        Assert.True(File.Exists(Path.Combine(backupLocale, "TestLoom_snapshot_20260101.zip")), "I backup locali non vanno toccati.");
    }
}