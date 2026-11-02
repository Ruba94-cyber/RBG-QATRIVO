using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Difetto 6.4.2 (docs/PIANO_6.4.2.md §1): ogni JSON scritto dentro una WorkflowTransaction portava _ditmTx e
/// diventava leggibile solo se la receipt della transazione esisteva e TUTTI i file elencati nella receipt
/// avevano ancora l'hash finale (WorkflowTransactionVisibilityService.VerifyReceiptSettled, gate all-or-nothing
/// cross-file, rimosso). Appena uno di quei file veniva riscritto legittimamente da un salvataggio successivo,
/// la vecchia receipt non si assestava più: gli altri file della stessa transazione restavano "Pending" per
/// sempre, anche con un solo PC e nessuno che lavora in concorrenza. Riprodotto: (a) primitiva Test+NC in
/// transazione A, poi solo NC in transazione B → Test illeggibile; (b) flusso app con i repository reali
/// (salva Test+NC, modifica solo la NC, riavvio) → Test in FileConProblemi.
///
/// Decisione: un JSON di dominio sintatticamente valido è SEMPRE leggibile, nessun gate cross-file basato sulle
/// receipt. Un tombstone (_ditmDeleted=true) vale come eliminazione logica senza bisogno di receipt. Questi test
/// verificano che il difetto non si ripresenti e che un tombstone resti riconosciuto come tale anche quando la
/// receipt della sua transazione non potrà mai più assestarsi.
/// </summary>
public sealed class LetturaSenzaGateTransazionaleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_LetturaSenzaGate_" + Guid.NewGuid().ToString("N"));
    public LetturaSenzaGateTransazionaleTests() => new DataPaths(_root).EnsureStructure();
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void TestDellaTransazioneA_DopoCheLaNcVieneRiscrittaDaB()
    {
        var paths = new DataPaths(_root);
        const string dev = "pc-unico";
        var txA = Guid.NewGuid();
        var tc = new TestCase { Id = "TC-1", Descrizione = "x", DataCreazione = DateTime.Now };
        var pT = paths.TestCaseFile(tc.Componente, tc.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(pT)!);
        var tBytes = WorkflowTransactionVisibilityService.StampJson(JsonSerializer.SerializeToUtf8Bytes(tc, JsonIo.Options), txA, dev);
        var nc = new NonConformita { Id = "NC-1", TestCaseId = tc.Id, DataApertura = DateTime.Now };
        var pN = paths.NonConformitaFile(nc.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(pN)!);
        var nBytes = WorkflowTransactionVisibilityService.StampJson(JsonSerializer.SerializeToUtf8Bytes(nc, JsonIo.Options), txA, dev);
        File.WriteAllBytes(pT, tBytes); File.WriteAllBytes(pN, nBytes);
        Pubblica(paths, txA, dev, (pT, tBytes), (pN, nBytes));
        Assert.NotNull(JsonIo.TryRead<TestCase>(pT, out _));

        // Transazione B: modifica solo la NC. La receipt di A non si assesterà mai più su pN.
        var txB = Guid.NewGuid();
        nc.Descrizione = "modificata";
        var nBytes2 = WorkflowTransactionVisibilityService.StampJson(JsonSerializer.SerializeToUtf8Bytes(nc, JsonIo.Options), txB, dev);
        File.WriteAllBytes(pN, nBytes2);
        Pubblica(paths, txB, dev, (pN, nBytes2));

        Assert.NotNull(JsonIo.TryRead<NonConformita>(pN, out _));
        var letto = JsonIo.TryRead<TestCase>(pT, out var err);
        Assert.True(letto != null, "Test non leggibile: " + err);
    }

    [Fact]
    public void FlussoApp_SalvaTestConNc_PoiModificaSoloNc_PoiRiavvio()
    {
        using (var state = TestStateFactory.Create())
        {
            state.InizializzaCartella(_root);
            var tc = new TestCase { Id = "TC-APP", Descrizione = "x", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now };
            var nc = new NonConformita { Id = "NC-APP", TestCaseId = tc.Id, DataApertura = DateTime.Now };
            WorkflowTransactionService.Execute(state.Paths!, "save-test:TC-APP", () =>
            {
                state.TestCases.Save(tc, isNew: true);
                state.NonConformita.Save(nc);
            });
            var ncLetta = state.NonConformita.GetAll().Single(n => n.Id == "NC-APP");
            ncLetta.Descrizione = "modificata dalla pagina NC";
            state.NonConformita.Save(ncLetta, ncLetta.RevisioneDati);
        }
        using var riavvio = TestStateFactory.Create();
        riavvio.InizializzaCartella(_root);
        riavvio.TestCases.RefreshIndex();
        Assert.True(riavvio.TestCases.GetById("TC-APP") != null && riavvio.TestCases.FileConProblemi.Count == 0,
            "Problemi: " + string.Join(" | ", riavvio.TestCases.FileConProblemi.Select(p => p.Errore)));
    }

    /// <summary>
    /// Un tombstone la cui receipt di commit non si assesterà MAI PIÙ (un'altra entry della stessa transazione
    /// viene legittimamente riscritta da una transazione successiva) deve restare riconosciuto come tombstone e
    /// non comparire in FileConProblemi: 6.4.2 non gestisce più questo caso con una riclassificazione ad hoc
    /// (i vecchi IsSupersededHistoricalTombstone/IsSupersededByTerminalDeletion/IsStaleOrphanedTrashTombstone,
    /// rimossi), perché non esiste più alcun gate da scavalcare in primo luogo.
    /// </summary>
    [Fact]
    public void TombstoneConReceiptCheNonSiAssesteraMai_Resta_RiconosciutoComeTombstone()
    {
        var paths = new DataPaths(_root);
        const string dev = "pc-unico";
        var txDel = Guid.NewGuid();

        var tc = new TestCase { Id = "TC-DEL", Descrizione = "x", DataCreazione = DateTime.Now };
        var pT = paths.TestCaseFile(tc.Componente, tc.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(pT)!);
        var nc = new NonConformita { Id = "NC-DEL", TestCaseId = tc.Id, DataApertura = DateTime.Now };
        var pN = paths.NonConformitaFile(nc.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(pN)!);

        // txDel elimina il Test (tombstone) e, nella STESSA transazione, tocca anche la NC: la receipt di txDel
        // elenca entrambi i path.
        var tombstone = WorkflowTransactionVisibilityService.CreateTombstone(
            txDel, dev, Path.GetFileName(pT), JsonSerializer.SerializeToUtf8Bytes(tc, JsonIo.Options));
        var nBytes = WorkflowTransactionVisibilityService.StampJson(JsonSerializer.SerializeToUtf8Bytes(nc, JsonIo.Options), txDel, dev);
        File.WriteAllBytes(pT, tombstone);
        File.WriteAllBytes(pN, nBytes);
        Pubblica(paths, txDel, dev, (pT, tombstone), (pN, nBytes));

        // Una transazione successiva riscrive SOLO la NC: la receipt di txDel non potrà mai più assestarsi su
        // pN (hash sempre diverso), esattamente il pattern che generava il falso "in attesa di sincronizzazione".
        var txC = Guid.NewGuid();
        nc.Descrizione = "riscritta da un'altra transazione";
        var nBytes2 = WorkflowTransactionVisibilityService.StampJson(JsonSerializer.SerializeToUtf8Bytes(nc, JsonIo.Options), txC, dev);
        File.WriteAllBytes(pN, nBytes2);
        Pubblica(paths, txC, dev, (pN, nBytes2));

        Assert.True(WorkflowTransactionVisibilityService.IsCommittedTombstone(paths, pT),
            "Il tombstone deve restare riconosciuto come tale anche se la sua receipt non si assesta più.");
        Assert.Null(JsonIo.TryRead<TestCase>(pT, out var err));
        Assert.Contains("Tombstone", err);

        using var state = TestStateFactory.Create();
        state.InizializzaCartella(_root);
        state.TestCases.RefreshIndex();
        Assert.DoesNotContain(state.TestCases.FileConProblemi, p => p.Percorso.Equals(pT, StringComparison.OrdinalIgnoreCase));
        Assert.Null(state.TestCases.GetById("TC-DEL"));
    }

    private static void Pubblica(DataPaths paths, Guid tx, string dev, params (string Path, byte[] Bytes)[] e) =>
        WorkflowCommitLedgerService.Publish(paths, new WorkflowTransactionCommitReceipt
        {
            Id = tx, DeviceId = dev, Device = dev, StartedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow,
            Entries = e.Select(x => new WorkflowTransactionCommitEntry
            {
                RelativePath = Path.GetRelativePath(paths.Root, x.Path).Replace('\\', '/'),
                FinalSha256 = WorkflowTransactionVisibilityService.Hash(x.Bytes)
            }).ToList()
        });
}
