using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Il Test editor storico usa ancora Load -> modifica della List originale -> Save per l'aggiunta rapida
/// Strumento. Il repository puo' riconciliare automaticamente quel solo caso, ma deve rifiutare qualunque
/// modifica/cancellazione di righe che esistevano nella fotografia di apertura.
/// </summary>
public sealed class LegacyInstrumentAdditiveMergeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_LegacyInstrumentMerge_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(_dir)) return;
        try { Directory.Delete(_dir, true); }
        catch { }
    }

    [Fact]
    public void DueAggiunteRapideConcorrenzialiDistinte_VengonoFuseSenzaLostUpdate()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var repo = state.Master.Strumenti;
        repo.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = "UnitLogger", ModificatoDa = "Seed" }
        });

        var pcA = repo.Load();
        var pcB = repo.Load();

        pcA.Add(new StrumentoDiagnostico { Nome = "CaptureTool", ModificatoDa = "PC-A" });
        repo.Save(pcA);

        pcB.Add(new StrumentoDiagnostico { Nome = "CANoe", ModificatoDa = "PC-B" });
        // La fingerprint di pcB rileva il cambio remoto; il fallback additivo confronta pcB con i byte esatti
        // letti all'apertura e puo' dimostrare che l'unica differenza locale e' la nuova chiave CANoe.
        repo.Save(pcB);

        var autorevole = repo.Load();
        Assert.Equal(3, autorevole.Count);
        Assert.Contains(autorevole, s => s.Nome == "UnitLogger");
        Assert.Contains(autorevole, s => s.Nome == "CaptureTool");
        Assert.Contains(autorevole, s => s.Nome == "CANoe");
    }

    [Fact]
    public void ConflittoConEditDiRigaPreesistente_NonVieneMascheratoComeMergeAdditivo()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var repo = state.Master.Strumenti;
        repo.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = "UnitLogger", ModificatoDa = "Seed" }
        });

        var pcA = repo.Load();
        var pcB = repo.Load();

        pcA.Add(new StrumentoDiagnostico { Nome = "CaptureTool", ModificatoDa = "PC-A" });
        repo.Save(pcA);

        pcB[0].Campi.Add(new DefinizioneCampoStrumento { Nome = "Campo locale non ancora salvato" });
        pcB.Add(new StrumentoDiagnostico { Nome = "CANoe", ModificatoDa = "PC-B" });

        Assert.Throws<MasterDataConflictException>(() => repo.Save(pcB));

        var autorevole = repo.Load();
        Assert.Equal(2, autorevole.Count);
        var unitlogger = Assert.Single(autorevole, s => s.Nome == "UnitLogger");
        Assert.Empty(unitlogger.Campi);
        Assert.Contains(autorevole, s => s.Nome == "CaptureTool");
        Assert.DoesNotContain(autorevole, s => s.Nome == "CANoe");
    }
}
