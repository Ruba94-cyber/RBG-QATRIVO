using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Integrita' della risoluzione dei conflitti Test con unione delle REV:
/// A) riferimenti a una REV per NUMERO (NC, segnalazioni) dopo la rinumerazione;
/// B) stessa REV (stessa data e operatore) con contenuto diverso: nulla perso o sovrascritto in silenzio;
/// C) "Applica e salva" (caso T-BD-0012): copie divergenti di ogni forma, salvataggio fallito a meta', guardia mai permanente.
/// </summary>
[Collection("ConflictResolverUi")]
public sealed class TestRevisionMergeIntegritaTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Unspecified);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_RevInteg_" + Guid.NewGuid().ToString("N"));
    private readonly bool _flagOriginale = TestCaseAutoForkMergeService.AbilitaMergeAutomaticoFork;

    public void Dispose()
    {
        ConflictResolverUi.MergeHandler = null;
        ConflictResolverUi.WholeVersionHandler = null;
        TestCaseAutoForkMergeService.AbilitaMergeAutomaticoFork = _flagOriginale;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) { try { Directory.Delete(_root, true); } catch { } }
    }

    // ------------------------------------------------------------------ helper

    private static Esecuzione Rev(int it, int giorno, string op, string note = "", Guid? condizione = null)
    {
        var e = new Esecuzione { Iterazione = it, Data = T0.AddDays(giorno), Operatore = op, Note = note, Matricola = "Matricola" };
        if (condizione.HasValue) e.Condizioni.Add(new Condizione { Uid = condizione.Value });
        return e;
    }

    private static byte[] Bytes(Guid uid, params Esecuzione[] rev)
    {
        var tc = new TestCase { Id = "T-BD-0012", Uid = uid, Descrizione = "d", CreatoDa = "x", ModificatoDa = "x" };
        tc.Esecuzioni.AddRange(rev);
        return JsonSerializer.SerializeToUtf8Bytes(tc, JsonIo.Options);
    }

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

    /// <summary>Test con 3 REV comuni; A e B ne aggiungono di proprie (modificaA / modificaB) sullo stesso Test -> fork di journal.</summary>
    private (Postazione A, Postazione B, string Id) CreaForkConRev(Action<TestCase> modificaA, Action<TestCase> modificaB)
    {
        TestCaseAutoForkMergeService.AbilitaMergeAutomaticoFork = false;
        var a = CreaPostazione("A", "device-aaaa");
        var b = CreaPostazione("B", "device-bbbb");
        var tc = new TestCase
        {
            Id = "T-BD-0012", Componente = new EcuSelection { "BCM" }, Descrizione = "descrizione base", DescrizioneBreve = "breve base",
            CreatoDa = "Seed", ModificatoDa = "Seed"
        };
        tc.Esecuzioni.AddRange([Rev(1, 0, "Anna"), Rev(2, 1, "Anna"), Rev(3, 2, "Bruno")]);
        a.Repo.Save(tc, isNew: true);
        SincronizzaBidirezionale(a, b);
        a.Repo.RefreshIndex(); b.Repo.RefreshIndex();

        var tcA = a.Repo.LoadCurrent(a.Repo.GetById(tc.Id)!)!;
        modificaA(tcA);
        a.Repo.Save(tcA, isNew: false, revisioneAttesa: 1);
        var tcB = b.Repo.LoadCurrent(b.Repo.GetById(tc.Id)!)!;
        modificaB(tcB);
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

    private static TestCase Clona(TestCase tc) =>
        JsonSerializer.Deserialize<TestCase>(JsonSerializer.Serialize(tc, JsonIo.Options), JsonIo.Options)!;

    private static string CopiaVisibile(Postazione p, TestCase current, string suffisso, Action<TestCase> modifica)
    {
        var copia = Clona(current);
        modifica(copia);
        var path = p.Repo.FilePathOf(current).Replace(".json", $" ({suffisso}).json", StringComparison.OrdinalIgnoreCase);
        JsonIo.WriteAtomic(path, copia);
        return path;
    }

    private static MergeUiResult Unisci(MergeUiRequest req, RecordFieldChoice scelta = RecordFieldChoice.Mia) =>
        new(MergeUiEsito.Unisci, RecordMergeChoices.Tutti(scelta, req.Plan));

    private static string Testo(DomainRecoveryRecord r) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(r.PayloadBase64));

    // ------------------------------------------------------------------ A: riferimenti per numero di REV

    [Fact]
    public void A_NcCheRiferisceLaRevPerNumero_DopoLaRinumerazione_RestaRisolvibilePerIdentita()
    {
        var uid = Guid.NewGuid();
        var condMia = Guid.NewGuid();
        var condLoro = Guid.NewGuid();
        Esecuzione[] comuni = [Rev(1, 0, "Anna"), Rev(2, 1, "Anna"), Rev(3, 2, "Bruno")];
        // La REV 4 dell'altra parte e' anteriore alla mia REV 4: dopo l'unione la MIA REV diventa la 5.
        var mia = Bytes(uid, [.. comuni, Rev(4, 5, "Anna", "mia", condMia)]);
        var altra = Bytes(uid, [.. comuni, Rev(4, 3, "Bruno", "loro", condLoro)]);
        var plan = RecordMergeEngine.Prepare(RecordMergeKind.TestCase, mia, altra).Plan!;
        var unito = (TestCase)plan.Build(RecordMergeChoices.Tutti(RecordFieldChoice.Mia, plan), "Master").Model!;
        Assert.Equal(5, unito.Esecuzioni.Single(e => e.Note == "mia").Iterazione); // rinumerata

        // NC e segnalazione salvate PRIMA dell'unione, con il numero di REV di allora (4).
        var prova = new RiferimentoProva { TestId = unito.Id, TestUid = uid, EsecuzioneIterazione = 4, CondizioneUid = condMia };
        var risolta = AnomaliaWorkflowService.RisolviProva(unito, prova);

        Assert.NotNull(risolta);
        Assert.Equal("mia", risolta!.Value.Esecuzione.Note);
        Assert.Equal(condMia, risolta.Value.Condizione.Uid);
        // E il riferimento esatto (numero corretto) continua a funzionare come prima.
        var esatta = new RiferimentoProva { TestId = unito.Id, TestUid = uid, EsecuzioneIterazione = 4, CondizioneUid = condLoro };
        Assert.Equal("loro", AnomaliaWorkflowService.RisolviProva(unito, esatta)!.Value.Esecuzione.Note);
        Assert.True(AnomaliaWorkflowService.ProvaAppartieneAlTest(unito, prova));
    }

    [Fact]
    public void A_RiferimentoConCondizioneInesistente_NonViene_ForzatoSuUnaRevQualsiasi()
    {
        var uid = Guid.NewGuid();
        var tc = new TestCase { Id = "T-X", Uid = uid };
        tc.Esecuzioni.Add(Rev(1, 0, "Anna", "uno", Guid.NewGuid()));
        var prova = new RiferimentoProva { TestId = "T-X", TestUid = uid, EsecuzioneIterazione = 1, CondizioneUid = Guid.NewGuid() };

        Assert.Null(AnomaliaWorkflowService.RisolviProva(tc, prova));
    }

    [Fact]
    public void A_RiferimentoStoricoSenzaNumero_ContinuaAUsareLIdentita()
    {
        var uid = Guid.NewGuid();
        var cond = Guid.NewGuid();
        var tc = new TestCase { Id = "T-X", Uid = uid };
        tc.Esecuzioni.Add(Rev(1, 0, "Anna", "uno"));
        tc.Esecuzioni.Add(Rev(2, 1, "Anna", "due", cond));
        var prova = new RiferimentoProva { TestId = "T-X", TestUid = uid, EsecuzioneIterazione = 0, CondizioneUid = cond };

        Assert.Equal("due", AnomaliaWorkflowService.RisolviProva(tc, prova)!.Value.Esecuzione.Note);
    }

    [Fact]
    public void A_RevRinumerataNonAltera_LeCondizioniNeLeOperazioni_ENonPerdeNessunaRev()
    {
        var uid = Guid.NewGuid();
        var c1 = Guid.NewGuid(); var c2 = Guid.NewGuid(); var c3 = Guid.NewGuid();
        Esecuzione[] comuni = [Rev(1, 0, "Anna")];
        var mia = Bytes(uid, [.. comuni, Rev(2, 5, "Anna", "mia", c1)]);
        var altra = Bytes(uid, [.. comuni, Rev(2, 3, "Bruno", "loro-a", c2), Rev(3, 4, "Bruno", "loro-b", c3)]);
        var plan = RecordMergeEngine.Prepare(RecordMergeKind.TestCase, mia, altra).Plan!;
        var unito = (TestCase)plan.Build(RecordMergeChoices.Tutti(RecordFieldChoice.Mia, plan), "Master").Model!;

        Assert.Equal(4, unito.Esecuzioni.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, unito.Esecuzioni.Select(e => e.Iterazione));
        // Ogni condizione resta nella propria REV (identita' preservata): solo il numero d'ordine cambia.
        Assert.Equal(c2, unito.Esecuzioni[1].Condizioni.Single().Uid);
        Assert.Equal(c3, unito.Esecuzioni[2].Condizioni.Single().Uid);
        Assert.Equal(c1, unito.Esecuzioni[3].Condizioni.Single().Uid);
    }

    // ------------------------------------------------------------------ B: stessa REV, contenuto diverso / regola "nessun dato scartato"

    [Fact]
    public void B_DivergenzaSemplice_NessunaDecisioneManuale_ZeroDatiPersi()
    {
        var uid = Guid.NewGuid();
        Esecuzione[] comuni = [Rev(1, 0, "Anna"), Rev(2, 1, "Anna")];
        var plan = RecordMergeEngine.Prepare(RecordMergeKind.TestCase, Bytes(uid, [.. comuni, Rev(3, 4, "Anna", "A")]),
            Bytes(uid, [.. comuni, Rev(3, 3, "Bruno", "B")])).Plan!;

        Assert.Empty(plan.DecisioniManuali);
        Assert.NotNull(plan.RevisioniUnite);
        var rev = ((TestCase)plan.Build(new RecordMergeChoices(), "M").Model!).Esecuzioni;
        Assert.Equal(new[] { "", "", "B", "A" }, rev.Select(r => r.Note)); // per data di creazione; la piu' recente e' la corrente
        Assert.Equal(new[] { 1, 2, 3, 4 }, rev.Select(r => r.Iterazione));
        Assert.Equal(4, plan.RevisioniUnite!.MappaCopiaMia[3]); // la mia REV 3 e' diventata la 4
        Assert.Equal(3, plan.RevisioniUnite.MappaAltraCopia[3]);
        Assert.Contains("questo PC", plan.Nota);
        Assert.Contains("altro PC", plan.Nota);
    }

    [Fact]
    public void B_StessaIdentitaContenutoDiverso_EntrambeConservateInOrdineDiData()
    {
        var uid = Guid.NewGuid();
        // Stessa data e operatore ma contenuto diverso, piu' una REV solo nella mia: nessuna scelta richiesta, nulla scartato.
        var mia = Bytes(uid, Rev(1, 0, "Anna", "nota A"), Rev(2, 1, "Anna", "solo mia"));
        var altra = Bytes(uid, Rev(1, 0, "Anna", "nota B"));
        var plan = RecordMergeEngine.Prepare(RecordMergeKind.TestCase, mia, altra).Plan!;

        Assert.DoesNotContain(plan.Differences, d => d.Name == "Esecuzioni");
        Assert.Empty(plan.DecisioniManuali);
        var rev = ((TestCase)plan.Build(new RecordMergeChoices(), "M").Model!).Esecuzioni;
        Assert.Equal(3, rev.Count);
        Assert.Equal(new[] { 1, 2, 3 }, rev.Select(r => r.Iterazione));
        Assert.Equal(new[] { "nota A", "nota B" }, rev.Take(2).Select(r => r.Note).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("solo mia", rev[2].Note);
    }

    [Fact]
    public void B_DateUguali_OrdineDeterministicoIndipendenteDaChiSiaMia()
    {
        var uid = Guid.NewGuid();
        var x = Bytes(uid, Rev(1, 0, "Anna"), Rev(2, 5, "Zeno", "zeta"));
        var y = Bytes(uid, Rev(1, 0, "Anna"), Rev(2, 5, "Anna", "alfa"));
        var note1 = ((TestCase)RecordMergeEngine.Prepare(RecordMergeKind.TestCase, x, y).Plan!.Build(new RecordMergeChoices(), "M").Model!).Esecuzioni.Select(r => r.Note).ToList();
        var note2 = ((TestCase)RecordMergeEngine.Prepare(RecordMergeKind.TestCase, y, x).Plan!.Build(new RecordMergeChoices(), "M").Model!).Esecuzioni.Select(r => r.Note).ToList();
        Assert.Equal(note1, note2);
        Assert.Equal(new[] { "", "alfa", "zeta" }, note1);

        // Stessa data, stesso operatore, contenuto diverso: stabilita' garantita dall'hash del contenuto.
        var p = Bytes(uid, Rev(1, 0, "Anna", "uno"));
        var q = Bytes(uid, Rev(1, 0, "Anna", "due"));
        var r1 = ((TestCase)RecordMergeEngine.Prepare(RecordMergeKind.TestCase, p, q).Plan!.Build(new RecordMergeChoices(), "M").Model!).Esecuzioni.Select(r => r.Note).ToList();
        var r2 = ((TestCase)RecordMergeEngine.Prepare(RecordMergeKind.TestCase, q, p).Plan!.Build(new RecordMergeChoices(), "M").Model!).Esecuzioni.Select(r => r.Note).ToList();
        Assert.Equal(r1, r2);
    }

    [Fact]
    public void B_DataMancante_DecisioneManualeSegnalata_NienteUnioneInSilenzio()
    {
        var uid = Guid.NewGuid();
        var senzaData = Rev(2, 0, "Anna", "senza data");
        senzaData.Data = default;
        var mia = Bytes(uid, Rev(1, 0, "Anna"), senzaData);
        var altra = Bytes(uid, Rev(1, 0, "Anna"), Rev(2, 3, "Bruno", "loro"));

        var plan = RecordMergeEngine.Prepare(RecordMergeKind.TestCase, mia, altra).Plan!;

        Assert.Null(plan.RevisioniUnite);
        var d = Assert.Single(plan.DecisioniManuali, x => x.Campo == "Esecuzioni");
        Assert.Contains("senza data", d.ValoreCopiaMia);
        Assert.Contains("loro", d.ValoreAltraCopia);
        Assert.Contains("data di creazione", plan.Nota);
        Assert.Throws<RecordMergeException>(() => plan.Build(new RecordMergeChoices(), "M"));
    }

    [Fact]
    public void B_CampiDiTestataDivergenti_SonoLeUnicheDecisioniManuali_ConAutoreEData()
    {
        var uid = Guid.NewGuid();
        var m = JsonSerializer.Deserialize<TestCase>(Bytes(uid, Rev(1, 0, "Anna"), Rev(2, 4, "Anna")), JsonIo.Options)!;
        m.Descrizione = "mia descr"; m.ModificatoDa = "Anna"; m.UltimaModifica = T0.AddDays(10);
        var l = JsonSerializer.Deserialize<TestCase>(Bytes(uid, Rev(1, 0, "Anna"), Rev(2, 3, "Bruno")), JsonIo.Options)!;
        l.Descrizione = "loro descr"; l.ModificatoDa = "Bruno"; l.UltimaModifica = T0.AddDays(11);

        var plan = RecordMergeEngine.Prepare(RecordMergeKind.TestCase,
            JsonSerializer.SerializeToUtf8Bytes(m, JsonIo.Options), JsonSerializer.SerializeToUtf8Bytes(l, JsonIo.Options)).Plan!;

        var d = Assert.Single(plan.DecisioniManuali);
        Assert.Equal("Descrizione", d.Campo);
        Assert.Equal("mia descr", d.ValoreCopiaMia);
        Assert.Equal("loro descr", d.ValoreAltraCopia);
        Assert.Equal("Anna", d.AutoreCopiaMia);
        Assert.Equal("Bruno", d.AutoreAltraCopia);
        Assert.Equal(T0.AddDays(11), d.DataAltraCopia);
        Assert.NotNull(plan.RevisioniUnite); // le REV restano automatiche
    }

    [Fact]
    public void B_TreCopie_UnioneIterata_TutteLeRevTenute()
    {
        var uid = Guid.NewGuid();
        Esecuzione[] comuni = [Rev(1, 0, "Anna")];
        var a = Bytes(uid, [.. comuni, Rev(2, 5, "Anna", "A")]);
        var b = Bytes(uid, [.. comuni, Rev(2, 3, "Bruno", "B")]);
        var c = Bytes(uid, [.. comuni, Rev(2, 4, "Carla", "C"), Rev(3, 7, "Carla", "C2")]);
        var primo = RecordMergeEngine.Prepare(RecordMergeKind.TestCase, a, b).Plan!.Build(new RecordMergeChoices(), "M");
        var secondo = RecordMergeEngine.Prepare(RecordMergeKind.TestCase, primo.Bytes, c).Plan!.Build(new RecordMergeChoices(), "M");

        var rev = ((TestCase)secondo.Model!).Esecuzioni;
        Assert.Equal(new[] { "", "B", "C", "A", "C2" }, rev.Select(r => r.Note));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, rev.Select(r => r.Iterazione));
    }

    [Fact]
    public void B_RiferimentiNcSegnalazioniCr_VengonoRimappatiPerIdentita()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_root);
        state.InizializzaCartella(_root);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Master", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));

        var uid = Guid.NewGuid();
        var cond = Guid.NewGuid();
        var unito = new TestCase { Id = "T-RM-1", Uid = uid };
        unito.Esecuzioni.Add(Rev(1, 0, "Anna"));
        unito.Esecuzioni.Add(Rev(2, 3, "Bruno", "loro"));
        unito.Esecuzioni.Add(Rev(3, 5, "Anna", "mia", cond)); // era la REV 2 prima dell'unione
        var nc = new NonConformita
        {
            Id = "NC-RM-1", TestCaseId = "T-RM-1", TestUid = uid, EsecuzioneIterazione = 2, CondizioneOrigineUid = cond,
            Owner = "x", Descrizione = "d", DataApertura = DateTime.Now,
            ProveCollegate = { new RiferimentoProva { TestId = "T-RM-1", TestUid = uid, EsecuzioneIterazione = 2, CondizioneUid = cond } }
        };
        state.NonConformita.Save(nc);
        var seg = new SegnalazioneDaClassificare { TestId = "T-RM-1", TestUid = uid, CondizioneUid = cond, EsecuzioneIterazione = 2 };
        state.Segnalazioni.Save(seg);

        var esito = TestRevisionReferenceRemap.Riallinea(state, unito);

        Assert.Empty(esito.Errori);
        Assert.Equal(1, esito.NcAggiornate);
        Assert.Equal(1, esito.SegnalazioniAggiornate);
        var ncDopo = state.NonConformita.GetById("NC-RM-1")!;
        Assert.Equal(3, ncDopo.EsecuzioneIterazione);
        Assert.Equal(3, ncDopo.ProveCollegate.Single().EsecuzioneIterazione);
        Assert.Equal(3, state.Segnalazioni.GetAll().Single(x => x.Uid == seg.Uid).EsecuzioneIterazione);
        // Idempotente: una seconda esecuzione non riscrive nulla.
        var ancora = TestRevisionReferenceRemap.Riallinea(state, unito);
        Assert.Equal(0, ancora.NcAggiornate + ancora.SegnalazioniAggiornate + ancora.CrAggiornate);
    }

    [Fact]
    public async Task B_Pipeline_StessaRevDiversa_LaVersioneNonScelta_FinisceNelRecoveryBin_ESalvataggioFallito_LasciaTuttoIntatto()
    {
        var (a, _, _) = CreaForkConRev(
            t => t.Esecuzioni.Add(Rev(4, 5, "Anna", "A-quattro")),
            t => t.Esecuzioni.Add(Rev(4, 5, "Anna", "B-quattro"))); // stessa data e operatore, contenuto diverso
        var (fork, recovery, current) = CaricaFork(a);
        var percorso = a.Repo.FilePathOf(current);
        var prima = File.ReadAllBytes(percorso);

        // Salvataggio che fallisce: un'altra postazione scrive il canonico mentre il Master sceglie.
        var visteNote = new List<string>();
        ConflictResolverUi.MergeHandler = req =>
        {
            visteNote.Add(req.Plan.Nota ?? "");
            var intruso = Clona(current);
            intruso.DescrizioneBreve = "scritto nel frattempo";
            intruso.RevisioneDati = current.RevisioneDati;
            a.Repo.Save(intruso, isNew: false, revisioneAttesa: current.RevisioneDati);
            return Task.FromResult(Unisci(req, RecordFieldChoice.Loro));
        };
        await Assert.ThrowsAsync<ConflittoRevisioneException>(() =>
            TestCaseManualForkResolution.RisolviConEditorAsync(a.Repo, a.Paths, fork, current, recovery, "Master"));
        Assert.All(visteNote, n => Assert.Contains("REV unite", n));
        var dopoFallimento = a.Repo.LoadCurrent(a.Repo.GetById(current.Id)!)!;
        Assert.Equal("scritto nel frattempo", dopoFallimento.DescrizioneBreve);
        Assert.DoesNotContain(dopoFallimento.Esecuzioni, e => e.Note == "B-quattro"); // la scelta NON e' stata applicata a meta'
        Assert.Single(a.Repo.JournalForks);
        Assert.NotEqual(prima, File.ReadAllBytes(percorso));

        // Riprova su stato fresco: ora riesce e nessuna delle due versioni va persa (una e' tenuta, l'altra e' nel bin).
        a.Repo.RefreshIndex();
        var (fork2, recovery2, current2) = CaricaFork(a);
        ConflictResolverUi.MergeHandler = req => Task.FromResult(Unisci(req, RecordFieldChoice.Loro));
        var ok = await TestCaseManualForkResolution.RisolviConEditorAsync(a.Repo, a.Paths, fork2, current2, recovery2, "Master");
        Assert.NotNull(ok);
        var finale = a.Repo.LoadCurrent(a.Repo.GetById(current.Id)!)!;
        Assert.Contains(finale.Esecuzioni, e => e.Note == "A-quattro"); Assert.Contains(finale.Esecuzioni, e => e.Note == "B-quattro"); // entrambe tenute
        var bin = DomainRecoveryService.GetAll(a.Paths).Where(r => r.VersioneScartata && r.Category == DomainRecoveryCategory.TestCase).Select(Testo).ToList();
        Assert.Contains(bin, t => t.Contains("A-quattro"));
    }

    // ------------------------------------------------------------------ C: Applica e salva

    [Fact]
    public async Task C_UnioneRev_NuoveSuEntrambeLeParti_SiSalvaSenzaBloccoDiStoriaImmutabile()
    {
        // La REV 4 dell'altra parte e' anteriore alla mia: dopo l'unione la mia REV 4 e' la 5 (il prefisso persistito cambia).
        var (a, _, id) = CreaForkConRev(
            t => t.Esecuzioni.Add(Rev(4, 5, "Anna", "A-quattro")),
            t => { t.Esecuzioni.Add(Rev(4, 3, "Bruno", "B-quattro")); t.Esecuzioni.Add(Rev(5, 6, "Bruno", "B-cinque")); });
        var (fork, recovery, current) = CaricaFork(a);
        ConflictResolverUi.MergeHandler = req => Task.FromResult(Unisci(req));

        var risultato = await TestCaseManualForkResolution.RisolviConEditorAsync(a.Repo, a.Paths, fork, current, recovery, "Master");

        Assert.NotNull(risultato);
        var salvato = a.Repo.LoadCurrent(a.Repo.GetById(id)!)!;
        Assert.Equal(6, salvato.Esecuzioni.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, salvato.Esecuzioni.Select(e => e.Iterazione));
        Assert.Equal(new[] { "B-quattro", "A-quattro", "B-cinque" }, salvato.Esecuzioni.Skip(3).Select(e => e.Note));
        Assert.Empty(a.Repo.JournalForks.Where(f => f.EntityUid == salvato.Uid && !TestCaseForkResolution.GetResolvedKeys(salvato).Contains(TestCaseForkResolution.Key(f))));
    }

    [Fact]
    public async Task C_UnioneRev_SoloLeRevDifferiscono_LUnioneVieneApplicataEntraNelSalvato()
    {
        // Nessun altro campo diverso: prima il piano risultava "senza differenze" e la REV dell'altra parte andava persa.
        var (a, _, id) = CreaForkConRev(
            t => t.Esecuzioni.Add(Rev(4, 5, "Anna", "A-quattro")),
            t => t.Esecuzioni.Add(Rev(4, 6, "Bruno", "B-quattro")));
        var (fork, recovery, current) = CaricaFork(a);
        ConflictResolverUi.MergeHandler = req => Task.FromResult(Unisci(req));

        var risultato = await TestCaseManualForkResolution.RisolviConEditorAsync(a.Repo, a.Paths, fork, current, recovery, "Master");

        Assert.NotNull(risultato);
        var salvato = a.Repo.LoadCurrent(a.Repo.GetById(id)!)!;
        Assert.Contains(salvato.Esecuzioni, e => e.Note == "A-quattro");
        Assert.Contains(salvato.Esecuzioni, e => e.Note == "B-quattro");
        Assert.Equal(5, salvato.Esecuzioni.Count);
    }

    [Fact]
    public async Task C_CopiaUgualeAUnRamoDelJournal_NonBloccaEViene_Archiviata()
    {
        var (a, _, _) = CreaForkConRev(t => t.Descrizione = "ramo A", t => t.DescrizioneBreve = "ramo B");
        var (fork, recovery, current) = CaricaFork(a);
        var ramo = recovery.Available.First(b => !TestCaseFingerprint.Compute(b.TestCase).Equals(TestCaseFingerprint.Compute(current), StringComparison.OrdinalIgnoreCase));
        var copia = a.Repo.FilePathOf(current).Replace(".json", " (PC-B).json", StringComparison.OrdinalIgnoreCase);
        JsonIo.WriteAtomic(copia, ramo.TestCase);
        ConflictResolverUi.MergeHandler = req => Task.FromResult(Unisci(req, RecordFieldChoice.Loro));

        var risultato = await TestCaseManualForkResolution.RisolviConEditorAsync(a.Repo, a.Paths, fork, current, recovery, "Master");

        Assert.NotNull(risultato);
        Assert.False(File.Exists(copia));
        Assert.Contains(DomainRecoveryService.GetAll(a.Paths), r => r.VersioneScartata);
    }

    [Fact]
    public async Task C_TreOPiuCopieDivergenti_EntranoTutteNelConfrontoNessunaSiPerdeELaGuardiaNonBlocca()
    {
        var (a, _, id) = CreaForkConRev(t => t.Descrizione = "ramo A", t => t.DescrizioneBreve = "ramo B");
        var (fork, recovery, current) = CaricaFork(a);
        var c1 = CopiaVisibile(a, current, "PC-uno", t => t.Descrizione = "copia uno");
        var c2 = CopiaVisibile(a, current, "PC-due", t => t.Descrizione = "copia due");
        var c3 = CopiaVisibile(a, current, "PC-tre", t => t.Esecuzioni.Add(Rev(4, 9, "Carla", "REV-copia-tre")));
        var confronti = 0;
        ConflictResolverUi.MergeHandler = req => { confronti++; return Task.FromResult(Unisci(req, RecordFieldChoice.Loro)); };

        var risultato = await TestCaseManualForkResolution.RisolviConEditorAsync(a.Repo, a.Paths, fork, current, recovery, "Master");

        Assert.NotNull(risultato);
        Assert.True(confronti >= 4, $"ramo + 3 copie devono essere tutti confrontati (confronti={confronti})");
        Assert.All(new[] { c1, c2, c3 }, c => Assert.False(File.Exists(c)));
        var bin = DomainRecoveryService.GetAll(a.Paths).Where(r => r.VersioneScartata).Select(Testo).ToList();
        Assert.Contains(bin, t => t.Contains("copia uno"));
        Assert.Contains(bin, t => t.Contains("copia due"));
        Assert.Contains(bin, t => t.Contains("REV-copia-tre"));
        // La REV presente solo nella terza copia e' tenuta nel risultato (nessuna operazione persa).
        Assert.Contains(a.Repo.LoadCurrent(a.Repo.GetById(id)!)!.Esecuzioni, e => e.Note == "REV-copia-tre");
    }

    [Fact]
    public async Task C_CopiaConRevMancanti_NonCancellaLeRevDelCanonico()
    {
        var (a, _, id) = CreaForkConRev(
            t => t.Esecuzioni.Add(Rev(4, 5, "Anna", "A-quattro")),
            t => t.Descrizione = "ramo B");
        var (fork, recovery, current) = CaricaFork(a);
        // Copia vecchia con una sola REV (REV mancanti) e descrizione diversa.
        CopiaVisibile(a, current, "PC-vecchio", t => { t.Esecuzioni = [t.Esecuzioni[0]]; t.Descrizione = "copia con REV mancanti"; });
        ConflictResolverUi.MergeHandler = req => Task.FromResult(Unisci(req, RecordFieldChoice.Mia));

        var risultato = await TestCaseManualForkResolution.RisolviConEditorAsync(a.Repo, a.Paths, fork, current, recovery, "Master");

        Assert.NotNull(risultato);
        var salvato = a.Repo.LoadCurrent(a.Repo.GetById(id)!)!;
        Assert.True(salvato.Esecuzioni.Count >= current.Esecuzioni.Count, "le REV del canonico non possono diminuire");
        Assert.Contains(salvato.Esecuzioni, e => e.Note == "A-quattro");
    }

    [Fact]
    public async Task C_SalvataggioCheFalliscePrimaDellaScrittura_LasciaCopieEBlocchiIntattiENonBloccaPerSempre()
    {
        var (a, _, id) = CreaForkConRev(t => t.Descrizione = "ramo A", t => t.DescrizioneBreve = "ramo B");
        var (fork, recovery, current) = CaricaFork(a);
        var copia = CopiaVisibile(a, current, "PC-terzo", t => t.Descrizione = "terza versione");
        var bytesCopia = File.ReadAllBytes(copia);
        var bytesCanonico = File.ReadAllBytes(a.Repo.FilePathOf(current));

        // Fallimento: il canonico cambia sotto i piedi del Master durante la scelta (revisione attesa non piu' valida).
        ConflictResolverUi.MergeHandler = req =>
        {
            var intruso = Clona(current);
            intruso.DescrizioneBreve = "scritto nel frattempo";
            File.WriteAllBytes(a.Repo.FilePathOf(current), JsonSerializer.SerializeToUtf8Bytes(
                new Func<TestCase>(() => { intruso.RevisioneDati = current.RevisioneDati + 7; return intruso; })(), JsonIo.Options));
            return Task.FromResult(Unisci(req, RecordFieldChoice.Loro));
        };
        await Assert.ThrowsAnyAsync<IOException>(() =>
            TestCaseManualForkResolution.RisolviConEditorAsync(a.Repo, a.Paths, fork, current, recovery, "Master"));

        // La copia divergente non e' stata toccata e continua a proteggere il conflitto: una scrittura ordinaria resta BLOCCATA.
        Assert.True(File.Exists(copia));
        Assert.Equal(bytesCopia, File.ReadAllBytes(copia));
        var ordinaria = Clona(current);
        ordinaria.DescrizioneBreve = "scrittura ordinaria";
        var blocco = Assert.ThrowsAny<IOException>(() => a.Repo.Save(ordinaria, isNew: false, revisioneAttesa: current.RevisioneDati + 7));
        Assert.DoesNotContain("scrittura ordinaria", File.ReadAllText(a.Repo.FilePathOf(current)));
        Assert.NotNull(blocco);

        // Ripristino del canonico e nuovo tentativo: la risoluzione riesce (nessun blocco permanente, nessuna esenzione residua).
        File.WriteAllBytes(a.Repo.FilePathOf(current), bytesCanonico);
        a.Repo.RefreshIndex();
        var (fork2, recovery2, current2) = CaricaFork(a);
        ConflictResolverUi.MergeHandler = req => Task.FromResult(Unisci(req, RecordFieldChoice.Loro));
        var ok = await TestCaseManualForkResolution.RisolviConEditorAsync(a.Repo, a.Paths, fork2, current2, recovery2, "Master");
        Assert.NotNull(ok);
        Assert.False(File.Exists(copia));
        Assert.NotNull(id);
    }

    [Fact]
    public void C_Guardia_LEsenzioneDiRisoluzione_NonSopravvivealloScopeEBloccaOgniAltraCopia()
    {
        var dir = Path.Combine(_root, "guard");
        Directory.CreateDirectory(dir);
        var uid = Guid.NewGuid();
        var canonico = Path.Combine(dir, "T-G.json");
        JsonIo.WriteAtomic(canonico, new TestCase { Id = "T-G", Uid = uid, Descrizione = "canonico" });
        var c1 = Path.Combine(dir, "T-G (PC-uno).json");
        var c2 = Path.Combine(dir, "T-G (PC-due).json");
        JsonIo.WriteAtomic(c1, new TestCase { Id = "T-G", Uid = uid, Descrizione = "uno" });
        JsonIo.WriteAtomic(c2, new TestCase { Id = "T-G", Uid = uid, Descrizione = "due" });

        Assert.Throws<IOException>(() => VisibleConflictWriteGuard.EnsureNoVisibleFork(canonico, uid));
        using (VisibleConflictWriteGuard.BeginResolution([Path.GetFullPath(c1)]))
        {
            // c2 non e' esentata: continua a bloccare anche dentro l'ambito.
            Assert.Throws<IOException>(() => VisibleConflictWriteGuard.EnsureNoVisibleFork(canonico, uid));
        }
        using (VisibleConflictWriteGuard.BeginResolution([Path.GetFullPath(c1), Path.GetFullPath(c2)]))
            VisibleConflictWriteGuard.EnsureNoVisibleFork(canonico, uid); // tutte esentate: passa
        // Fuori dall'ambito (anche dopo un'eccezione) l'esenzione e' sparita.
        Assert.Throws<IOException>(() => VisibleConflictWriteGuard.EnsureNoVisibleFork(canonico, uid));
        try
        {
            using (VisibleConflictWriteGuard.BeginResolution([Path.GetFullPath(c1), Path.GetFullPath(c2)]))
                throw new InvalidOperationException("salvataggio fallito");
        }
        catch (InvalidOperationException) { }
        Assert.Throws<IOException>(() => VisibleConflictWriteGuard.EnsureNoVisibleFork(canonico, uid));
    }

    [Fact]
    public async Task C_CopiaIllegibileMaCorrotta_NonBloccaPerSempre_ViaRecoveryBin()
    {
        var (a, _, _) = CreaForkConRev(t => t.Descrizione = "ramo A", t => t.DescrizioneBreve = "ramo B");
        var (fork, recovery, current) = CaricaFork(a);
        var corrotta = a.Repo.FilePathOf(current).Replace(".json", " (PC-guasto).json", StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(corrotta, "{ non e' json valido");
        ConflictResolverUi.MergeHandler = req => Task.FromResult(Unisci(req, RecordFieldChoice.Loro));
        ConflictResolverUi.WholeVersionHandler = _ => Task.FromResult<WholeVersionChoice?>(WholeVersionChoice.TieniMia);

        var risultato = await TestCaseManualForkResolution.RisolviConEditorAsync(a.Repo, a.Paths, fork, current, recovery, "Master");

        Assert.NotNull(risultato);
        // I byte originali non vanno persi: sono nel recovery-bin.
        Assert.Contains(DomainRecoveryService.GetAll(a.Paths), r => Testo(r).Contains("non e' json valido"));
    }

    [Fact]
    public async Task B_ValoreNonScelto_FinisceNelRecoveryBin_ETracciatoNellaCronologia()
    {
        var (a, _, id) = CreaForkConRev(t => t.Descrizione = "descrizione ramo A", t => t.Descrizione = "descrizione ramo B");
        var (fork, recovery, current) = CaricaFork(a);
        ConflictResolverUi.MergeHandler = req => Task.FromResult(Unisci(req, RecordFieldChoice.Mia));

        var risultato = await TestCaseManualForkResolution.RisolviConEditorAsync(a.Repo, a.Paths, fork, current, recovery, "Master");

        Assert.NotNull(risultato);
        var salvato = a.Repo.LoadCurrent(a.Repo.GetById(id)!)!;
        Assert.Equal(current.Descrizione, salvato.Descrizione);
        var altra = current.Descrizione.EndsWith('A') ? "descrizione ramo B" : "descrizione ramo A";
        Assert.Contains(DomainRecoveryService.GetAll(a.Paths).Where(r => r.VersioneScartata).Select(Testo), t => t.Contains(altra));
        Assert.Contains(salvato.Changelog, c => c.ValoreNuovo?.Contains("recovery-bin") == true);
    }
}
