using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class WorkflowMalformedTransactionMarkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_MalformedTx_" + Guid.NewGuid().ToString("N"));

    public WorkflowMalformedTransactionMarkerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    /// <summary>
    /// Decisione 6.4.2 (docs/PIANO_6.4.2.md §1): un JSON di dominio sintatticamente valido è SEMPRE leggibile,
    /// indipendentemente dalla validità di _ditmTx/_ditmTxDevice (diagnostica, ignorati in deserializzazione).
    /// Il vecchio comportamento fail-closed "marker malformato -> Invalid -> illeggibile" copriva un gate
    /// cross-file che non esiste più (JsonConMarkerTransazionaleMalformato_NonDegradaALegacyVisible, rimosso):
    /// HasTransactionMarker/TryReadMetadata restano corretti come diagnostica di basso livello, ma non
    /// condizionano più la lettura del record.
    /// </summary>
    [Fact]
    public void JsonConMarkerTransazionaleMalformato_RestaComunqueLeggibile()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var path = Path.Combine(paths.TestCasesDir, "testcase_TX-BROKEN.json");
        var test = new TestCase
        {
            Id = "TX-BROKEN",
            Uid = Guid.NewGuid(),
            Descrizione = "leggibile anche con marker tecnico malformato"
        };
        var json = JsonSerializer.SerializeToNode(test, JsonIo.Options)!.AsObject();
        json[WorkflowTransactionVisibilityService.TransactionProperty] = "non-e-un-guid";
        json[WorkflowTransactionVisibilityService.TransactionDeviceProperty] = "device-a";
        File.WriteAllText(path, json.ToJsonString(JsonIo.Options));

        Assert.True(WorkflowTransactionVisibilityService.HasTransactionMarker(File.ReadAllBytes(path)));
        Assert.False(WorkflowTransactionVisibilityService.TryReadMetadata(File.ReadAllBytes(path), out _));

        var loaded = JsonIo.TryRead<TestCase>(path, out var error);
        Assert.NotNull(loaded);
        Assert.Null(error);
        Assert.Equal("leggibile anche con marker tecnico malformato", loaded!.Descrizione);
    }

    [Fact]
    public void JsonLegacySenzaMarker_RestaLeggibile()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var path = Path.Combine(paths.TestCasesDir, "testcase_LEGACY.json");
        var test = new TestCase { Id = "LEGACY", Uid = Guid.NewGuid(), Descrizione = "legacy valido" };
        File.WriteAllText(path, JsonSerializer.Serialize(test, JsonIo.Options));

        Assert.False(WorkflowTransactionVisibilityService.HasTransactionMarker(File.ReadAllBytes(path)));

        var loaded = JsonIo.TryRead<TestCase>(path, out var error);
        Assert.NotNull(loaded);
        Assert.Null(error);
        Assert.Equal("legacy valido", loaded!.Descrizione);
    }
}
