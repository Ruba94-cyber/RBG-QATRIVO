using System.Text.Json;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Copertura di integrità per l'esploratore: la consultazione non deve modificare
/// JSON autorevoli, bozze, marcatori locali né altre cartelle/workspace.
/// </summary>
public sealed class FailedSaveDraftInspectorTests : IDisposable
{
    private readonly string _shared = Path.Combine(Path.GetTempPath(), "TestLoom_InspectorData_" + Guid.NewGuid().ToString("N"));
    private readonly string _base = Path.Combine(Path.GetTempPath(), "TestLoom_InspectorLocal_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Elenco_PiuRecentePrima_IndipendentementeDalDominio()
    {
        // Regressione: il nome inizia con il dominio, l'ordinamento per nome metteva in coda le CR più recenti.
        var directory = FailedSaveDraftService.LocalDirectory(_shared, _base);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "segnalazione_20260101T080000000_" + Guid.NewGuid().ToString("N") + ".json"), "{}");
        File.WriteAllText(Path.Combine(directory, "nonconformita_20260102T080000000_" + Guid.NewGuid().ToString("N") + ".json"), "{}");
        var newest = Path.Combine(directory, "change-request_20260103T080000000_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(newest, "{}");

        var rows = FailedSaveDraftInspector.ListPage(_shared, 0, 2, _base);

        Assert.Equal(Path.GetFileName(newest), rows[0].FileName);
        Assert.StartsWith("nonconformita_", rows[1].FileName);
    }

    [Fact]
    public void ElencoEAnteprima_DiDueBozze_NonModificanoDati()
    {
        Directory.CreateDirectory(_shared);
        var authoritative = Path.Combine(_shared, "requisiti.json");
        File.WriteAllText(authoritative, "[{\"Id\":\"BASE-6.5.3\"}]");
        var baseline = File.ReadAllBytes(authoritative);
        var uid = Guid.NewGuid();
        var path1 = FailedSaveDraftService.Save(_shared, "change-request", uid, 12,
            "Operatore QA", new { Titolo = "Primo testo QA" }, "Errore di I/O", _base);
        var path2 = FailedSaveDraftService.Save(_shared, "segnalazione", Guid.NewGuid(), 7,
            "Operatore QA", new { Descrizione = "Secondo testo QA" }, "Conflitto", _base);
        var bytes1 = File.ReadAllBytes(path1);
        var bytes2 = File.ReadAllBytes(path2);

        var rows = FailedSaveDraftInspector.ListPage(_shared, 0, 50, _base);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsReadable));
        var cr = Assert.Single(rows.Where(r => r.Domain == "change-request"));
        Assert.Equal(uid, cr.EntityUid);
        Assert.Equal(12, cr.ExpectedRevision);
        Assert.Contains("Primo testo QA", FailedSaveDraftInspector.Preview(_shared, cr, _base));

        Assert.Equal(baseline, File.ReadAllBytes(authoritative));
        Assert.Equal(bytes1, File.ReadAllBytes(path1));
        Assert.Equal(bytes2, File.ReadAllBytes(path2));
    }

    [Fact]
    public void BozzaModificataDopoElenco_NonVieneMostrataComeVersioneOriginale()
    {
        var path = FailedSaveDraftService.Save(_shared, "change-request", Guid.NewGuid(), 4,
            "QA", new { Titolo = "VERSIONE_ORIGINALE" }, "Offline", _base);
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_shared, 0, 10, _base));
        Assert.True(row.IsReadable);

        // Simula un intervento esterno fra lista e clic sul pulsante Anteprima.
        var envelope = JsonIo.TryRead<FailedSaveDraftService.DraftEnvelope>(path, out var error)!;
        Assert.Null(error);
        envelope.PayloadJson = """{"Titolo":"CAMBIATO"}""";
        JsonIo.WriteAtomic(path, envelope);

        var preview = FailedSaveDraftInspector.Preview(_shared, row, _base);
        Assert.Contains("cambiata", preview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CAMBIATO", preview);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void FileCorrottoRestaVisibileMaNonPuòEssereConsideratoRecuperabile()
    {
        var path = Path.Combine(FailedSaveDraftService.LocalDirectory(_shared, _base),
            "change-request_corrupt.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ incomplete");

        var rows = FailedSaveDraftInspector.ListPage(_shared, 0, 20, _base);

        var row = Assert.Single(rows);
        Assert.False(row.IsReadable);
        Assert.Contains("danneggiata", FailedSaveDraftInspector.Preview(_shared, row, _base));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ElencoPaginated_NonScartaBozzeENonMescolaWorkspace()
    {
        for (var i = 0; i < 8; i++)
            FailedSaveDraftService.Save(_shared, "nonconformita", Guid.NewGuid(), i,
                "QA", new { Numero = i }, "Salvataggio fallito", _base);
        FailedSaveDraftService.Save(_shared + "-altro", "nonconformita", Guid.NewGuid(), 1,
            "QA", new { Numero = 99 }, "Offline", _base);

        var page1 = FailedSaveDraftInspector.ListPage(_shared, 0, 3, _base);
        var page2 = FailedSaveDraftInspector.ListPage(_shared, 3, 3, _base);
        var page3 = FailedSaveDraftInspector.ListPage(_shared, 6, 3, _base);

        Assert.Equal(3, page1.Count);
        Assert.Equal(3, page2.Count);
        Assert.Equal(2, page3.Count);
        Assert.Equal(8, page1.Concat(page2).Concat(page3).Select(r => r.FilePath).Distinct().Count());
        Assert.All(page1.Concat(page2).Concat(page3),
            r => Assert.StartsWith(FailedSaveDraftService.LocalDirectory(_shared, _base), r.FilePath));
    }

    [Fact]
    public void Anteprima_RifiutaBozzeEsterneAlWorkspace()
    {
        var path = FailedSaveDraftService.Save(_shared + "-estraneo", "change-request",
            Guid.NewGuid(), 0, "QA", new { Titolo = "Segreto" }, "Errore", _base);
        var row = Assert.Single(FailedSaveDraftInspector.ListPage(_shared + "-estraneo", 0, 5, _base));
        Assert.Throws<UnauthorizedAccessException>(() =>
            FailedSaveDraftInspector.Preview(_shared, row, _base));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void WorkspaceSenzaBozze_RestituisceElencoVuotoSenzaCreareDirectory()
    {
        Assert.Empty(FailedSaveDraftInspector.ListPage(_shared, 0, 30, _base));
        Assert.False(Directory.Exists(FailedSaveDraftService.LocalDirectory(_shared, _base)));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_shared)) Directory.Delete(_shared, true); } catch { }
        try { if (Directory.Exists(_base)) Directory.Delete(_base, true); } catch { }
    }
}
