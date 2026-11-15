using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Fork fra postazioni risolti con la schermata grafica sul record reale (non sul JSON): Test (journal condiviso) e NC (journal dei
/// record). Verifica unione campo per campo, valore manuale, annullamento senza scritture, versioni originali nel recovery-bin
/// e chiusura del fork solo dopo il salvataggio.
/// </summary>
[Collection("ConflictResolverUi")]
public sealed class ForkConEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_ForkEditor_" + Guid.NewGuid().ToString("N"));
    private readonly string _localB = Path.Combine(Path.GetTempPath(), "TestLoom_ForkEditorB_" + Guid.NewGuid().ToString("N"));
    private readonly bool _flagOriginale = TestCaseAutoForkMergeService.AbilitaMergeAutomaticoFork;

    public void Dispose()
    {
        ConflictResolverUi.MergeHandler = null;
        ConflictResolverUi.WholeVersionHandler = null;
        TestCaseAutoForkMergeService.AbilitaMergeAutomaticoFork = _flagOriginale;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _root, _localB, FailedSaveDraftService.LocalDirectory(_root) })
            if (Directory.Exists(path)) { try { Directory.Delete(path, true); } catch { } }
    }

    // ------------------------------------------------------------------ Test (journal condiviso)

    private sealed class Postazione
    {
        public required DataPaths Paths;
        public required TestCaseRepository Repo;
    }

    private Postazione CreaPostazione(string nome, string deviceId)
    {
        var sharedRoot = Path.Combine(_root, "shared_" + nome);
        var paths = new DataPaths(sharedRoot);
        paths.EnsureStructure();
        var store = new LocalTestIndexStore(sharedRoot, Path.Combine(_root, "local_" + nome));
        return new Postazione { Paths = paths, Repo = new TestCaseRepository(paths, store, deviceIdOverride: deviceId) };
    }

    private static void Sincronizza(Postazione da, Postazione a, string sotto)
    {
        var origine = Path.Combine(da.Paths.Root, sotto);
        var destinazione = Path.Combine(a.Paths.Root, sotto);
        if (!Directory.Exists(origine)) return;
        foreach (var file in Directory.EnumerateFiles(origine, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destinazione, Path.GetRelativePath(origine, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void SincronizzaBidirezionale(Postazione x, Postazione y)
    {
        foreach (var sotto in new[] { "testcases", Path.Combine("_ditm", "journal"), Path.Combine("_ditm", "conflicts") })
        {
            Sincronizza(x, y, sotto);
            Sincronizza(y, x, sotto);
        }
    }

    private (Postazione A, Postazione B, string Id) CreaForkTest()
    {
        TestCaseAutoForkMergeService.AbilitaMergeAutomaticoFork = false;
        var a = CreaPostazione("A", "device-aaaa");
        var b = CreaPostazione("B", "device-bbbb");
        var tc = new TestCase
        {
            Id = "TC-EDIT-01", Componente = new EcuSelection { "BCM" }, Descrizione = "descrizione base", DescrizioneBreve = "breve base",
            CreatoDa = "Seed", ModificatoDa = "Seed"
        };
        a.Repo.Save(tc, isNew: true);
        SincronizzaBidirezionale(a, b);
        a.Repo.RefreshIndex(); b.Repo.RefreshIndex();

        var tcA = a.Repo.LoadCurrent(a.Repo.GetById(tc.Id)!)!;
        tcA.Descrizione = "modifica ramo A";
        a.Repo.Save(tcA, isNew: false, revisioneAttesa: 1);
        var tcB = b.Repo.LoadCurrent(b.Repo.GetById(tc.Id)!)!;
        tcB.DescrizioneBreve = "modifica ramo B";
        b.Repo.Save(tcB, isNew: false, revisioneAttesa: 1);
        SincronizzaBidirezionale(a, b);
        a.Repo.RefreshIndex(); b.Repo.RefreshIndex();
        a.Repo.RefreshJournalMetadata(); b.Repo.RefreshJournalMetadata();
        SincronizzaBidirezionale(a, b);
        a.Repo.RefreshJournalMetadata(); b.Repo.RefreshJournalMetadata();
        return (a, b, tc.Id);
    }

    private static (JournalForkConflict Fork, ForkRecoveryLoadResult Recovery, TestCase Current) CaricaFork(Postazione a)
    {
        a.Repo.RefreshJournalMetadata();
        var fork = Assert.Single(a.Repo.JournalForks);
        var copie = a.Repo.GetAll(includeEliminati: true).Where(t => t.Uid == fork.EntityUid).ToList();
        var current = a.Repo.LoadCurrent(copie.Where(t => !t.Eliminato).OrderByDescending(t => t.RevisioneDati).First())!;
        var recovery = DelayedForkRecoveryService.LoadAllBranches(a.Paths, fork, current, copie);
        Assert.True(recovery.Complete);
        return (fork, recovery, current);
    }

    [Fact]
    public async Task Test_UnioneGrafica_TuttiCampiDallAltraVersione_RisultatoSalvatoEOriginaliNelRecoveryBin()
    {
        var (a, _, id) = CreaForkTest();
        var (fork, recovery, current) = CaricaFork(a);
        MergeUiRequest? visto = null;
        ConflictResolverUi.MergeHandler = req =>
        {
            visto = req;
            return Task.FromResult(new MergeUiResult(MergeUiEsito.Unisci, RecordMergeChoices.Tutti(RecordFieldChoice.Loro, req.Plan)));
        };

        var risultato = await TestCaseManualForkResolution.RisolviConEditorAsync(a.Repo, a.Paths, fork, current, recovery, "Master");

        Assert.NotNull(risultato);
        Assert.NotNull(visto);
        Assert.All(visto!.Plan.Fields, f => Assert.DoesNotContain("{", f.MiaTesto + f.LoroTesto));
        var salvato = a.Repo.LoadCurrent(a.Repo.GetById(id)!)!;
        Assert.Equal(current.RevisioneDati + 1, salvato.RevisioneDati);
        Assert.NotEmpty(TestCaseForkResolution.GetResolvedKeys(salvato));
        var bin = DomainRecoveryService.GetAll(a.Paths).Where(r => r.VersioneScartata && r.Category == DomainRecoveryCategory.TestCase).ToList();
        Assert.True(bin.Count >= 2, "il ramo e la versione corrente prima dell'unione devono essere archiviati");
    }

    [Fact]
    public async Task Test_CopiaOneDriveVisibileNonCoincidenteConUnRamo_NonBloccaPiuLaRisoluzione_RegressioneTBD0012()
    {
        var (a, _, id) = CreaForkTest();
        var (fork, recovery, current) = CaricaFork(a);
        // Terza versione lasciata da OneDrive accanto al canonico, diversa da entrambi i rami del journal.
        var terza = JsonSerializer.Deserialize<TestCase>(JsonSerializer.Serialize(current, JsonIo.Options), JsonIo.Options)!;
        terza.Descrizione = "terza versione sulla copia OneDrive";
        var copia = a.Repo.FilePathOf(current).Replace(".json", " (PC-terzo).json", StringComparison.OrdinalIgnoreCase);
        JsonIo.WriteAtomic(copia, terza);
        // Prima della correzione: Save -> EnsureNoVisibleFork -> IOException "versioni OneDrive divergenti".
        var visti = new List<string>();
        ConflictResolverUi.MergeHandler = req =>
        {
            visti.AddRange(req.Plan.Differences.Select(d => d.Name));
            return Task.FromResult(new MergeUiResult(MergeUiEsito.Unisci, RecordMergeChoices.Tutti(RecordFieldChoice.Loro, req.Plan)));
        };

        var risultato = await TestCaseManualForkResolution.RisolviConEditorAsync(a.Repo, a.Paths, fork, current, recovery, "Master");

        Assert.NotNull(risultato);
        Assert.Contains("Descrizione", visti); // la terza versione e' entrata nel confronto
        Assert.False(File.Exists(copia));
        Assert.Contains(DomainRecoveryService.GetAll(a.Paths), r => r.VersioneScartata &&
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(r.PayloadBase64)).Contains("terza versione"));
        // Dopo la risoluzione il Test e' di nuovo modificabile.
        var dopo = a.Repo.LoadCurrent(a.Repo.GetById(id)!)!;
        dopo.DescrizioneBreve = "modifica normale";
        a.Repo.Save(dopo, isNew: false, revisioneAttesa: dopo.RevisioneDati);
    }

    [Fact]
    public async Task Test_CopiaInQuarantenaNonCoincidenteConUnRamo_NonBloccaLaRisoluzione()
    {
        var (a, _, _) = CreaForkTest();
        var (fork, recovery, current) = CaricaFork(a);
        var terza = JsonSerializer.Deserialize<TestCase>(JsonSerializer.Serialize(current, JsonIo.Options), JsonIo.Options)!;
        terza.Descrizione = "terza in quarantena";
        var copia = a.Repo.FilePathOf(current).Replace(".json", " (PC-terzo).json", StringComparison.OrdinalIgnoreCase);
        JsonIo.WriteAtomic(copia, terza);
        Assert.Equal(1, ConflictCopyQuarantineService.Run(a.Paths, "Master").Ritirate);
        ConflictResolverUi.MergeHandler = req =>
            Task.FromResult(new MergeUiResult(MergeUiEsito.Unisci, RecordMergeChoices.Tutti(RecordFieldChoice.Mia, req.Plan)));

        var risultato = await TestCaseManualForkResolution.RisolviConEditorAsync(a.Repo, a.Paths, fork, current, recovery, "Master");

        Assert.NotNull(risultato);
        Assert.Empty(ConflictCopyQuarantineService.List(a.Paths.Root));
    }

    [Fact]
    public async Task Test_Annulla_NonScriveNulla()
    {
        var (a, _, id) = CreaForkTest();
        var (fork, recovery, current) = CaricaFork(a);
        var prima = File.ReadAllBytes(a.Repo.FilePathOf(current));
        ConflictResolverUi.MergeHandler = _ => Task.FromResult(new MergeUiResult(MergeUiEsito.Annulla, null));

        var risultato = await TestCaseManualForkResolution.RisolviConEditorAsync(a.Repo, a.Paths, fork, current, recovery, "Master");

        Assert.Null(risultato);
        Assert.Equal(prima, File.ReadAllBytes(a.Repo.FilePathOf(current)));
        Assert.Empty(DomainRecoveryService.GetAll(a.Paths));
        Assert.Single(a.Repo.JournalForks);
        Assert.NotNull(id);
    }

    // ------------------------------------------------------------------ NC (journal dei record)

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

    private (NonConformita Nc, RecordFork Fork, string Path) CreaForkNc(AppState state)
    {
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-EDIT-1", Owner = "Base", Descrizione = "KO", Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.Confermata, DataApertura = DateTime.Now
        });
        var path = state.NonConformita.FilePathOf(state.NonConformita.GetById("NC-EDIT-1")!);
        var baseBytes = File.ReadAllBytes(path);
        var mia = state.NonConformita.GetById("NC-EDIT-1")!.Clona();
        mia.Owner = "A";
        state.NonConformita.Save(mia, mia.RevisioneDati);

        File.WriteAllBytes(path, baseBytes);
        var journalB = new DomainRecordJournalService(state.Paths, _localB, "PC-B-TEST");
        var repoB = new NonConformitaRepository(state.Paths) { RecordJournal = journalB };
        repoB.RefreshPaths(new[] { path });
        var loro = repoB.GetById("NC-EDIT-1")!.Clona();
        loro.Owner = "B";
        repoB.Save(loro, loro.RevisioneDati);
        state.NonConformita.RefreshPaths(new[] { path });
        journalB.Refresh(); // il PC B pubblica il payload del proprio ramo (come fa TestLoom aprendosi dopo la sincronizzazione)

        var fork = Assert.Single(state.RecordJournal!.Refresh());
        return (state.NonConformita.GetById("NC-EDIT-1")!, fork, path);
    }

    [Fact]
    public async Task Nc_ValoreManuale_SalvaChiudeIlForkEArchiviaLeVersioni()
    {
        using var state = CreaStato();
        var (_, fork, path) = CreaForkNc(state);
        ConflictResolverUi.MergeHandler = req =>
        {
            var scelte = new RecordMergeChoices();
            foreach (var f in req.Plan.Differences)
            {
                Assert.Equal("Owner", f.Name);
                scelte.Scelte[f.Name] = RecordFieldChoice.Manuale;
                scelte.ValoriManuali[f.Name] = "Carla";
            }
            return Task.FromResult(new MergeUiResult(MergeUiEsito.Unisci, scelte));
        };

        var esito = await RecordForkResolutionService.RisolviConEditorAsync(state, fork);

        Assert.NotNull(esito);
        state.NonConformita.RefreshPaths(new[] { path });
        Assert.Equal("Carla", state.NonConformita.GetById("NC-EDIT-1")!.Owner);
        Assert.Empty(state.RecordJournal!.Refresh());
        var bin = DomainRecoveryService.GetAll(state.Paths).Where(r => r.VersioneScartata).ToList();
        Assert.True(bin.Count >= 2);
        Assert.Contains(bin, r => Encoding.UTF8.GetString(Convert.FromBase64String(r.PayloadBase64)).Contains("\"A\""));
        Assert.Contains(bin, r => Encoding.UTF8.GetString(Convert.FromBase64String(r.PayloadBase64)).Contains("\"B\""));
    }

    [Fact]
    public async Task Nc_Annulla_ForkResteAperto_NulloScritto()
    {
        using var state = CreaStato();
        var (_, fork, path) = CreaForkNc(state);
        var prima = File.ReadAllBytes(path);
        ConflictResolverUi.MergeHandler = _ => Task.FromResult(new MergeUiResult(MergeUiEsito.Annulla, null));

        var esito = await RecordForkResolutionService.RisolviConEditorAsync(state, fork);

        Assert.Null(esito);
        Assert.Equal(prima, File.ReadAllBytes(path));
        Assert.Single(state.RecordJournal!.Refresh());
        Assert.Empty(DomainRecoveryService.GetAll(state.Paths));
    }

    [Fact]
    public async Task Nc_VersioneIntera_TieniLaMia_ChiudeIlForkSenzaModificareIlRecord()
    {
        using var state = CreaStato();
        var (_, fork, path) = CreaForkNc(state);
        var prima = File.ReadAllBytes(path);
        ConflictResolverUi.MergeHandler = _ => Task.FromResult(new MergeUiResult(MergeUiEsito.VersioneIntera, null));
        ConflictResolverUi.WholeVersionHandler = _ => Task.FromResult<WholeVersionChoice?>(WholeVersionChoice.TieniMia);

        var esito = await RecordForkResolutionService.RisolviConEditorAsync(state, fork);

        Assert.NotNull(esito);
        Assert.Equal(prima, File.ReadAllBytes(path));
        Assert.Empty(state.RecordJournal!.Refresh());
        Assert.True(DomainRecoveryService.GetAll(state.Paths).Count(r => r.VersioneScartata) >= 1);
    }
}
