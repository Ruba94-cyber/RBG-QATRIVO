using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Regressioni sulla distinzione tra il percorso legacy Save(items), che puo' proteggere liste filtrate,
/// e il percorso moderno Save(items, expectedSnapshot), dove lo snapshot esplicito descrive intenzionalmente
/// l'intero risultato autoritativo e deve quindi poter rappresentare anche una cancellazione reale.
/// </summary>
public sealed class MasterDataExplicitSnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterExplicit_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(_dir)) return;
        try { Directory.Delete(_dir, true); }
        catch { }
    }

    [Fact]
    public void RepositoryStrumenti_SaveConSnapshotEsplicito_PuoCancellareRigaArchiviata()
    {
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var repo = state.Master.Strumenti;
        repo.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = "Attivo", Archiviata = false, ModificatoDa = "Seed" },
            new() { Nome = "Storico archiviato", Archiviata = true, ModificatoDa = "Seed" }
        });

        var aperti = repo.Load();
        var snapshot = EntityStateFingerprint.Clone(aperti);
        var daSalvare = aperti.Where(s => !s.Archiviata).ToList();

        // Questa omissione e' intenzionale e protetta da snapshot: non deve essere reinterpretata come il
        // vecchio filtro UI del Test editor, altrimenti una cancellazione definitiva Master diventerebbe
        // impossibile proprio per il catalogo Strumenti.
        repo.Save(daSalvare, snapshot);

        var autorevole = repo.Load();
        Assert.Single(autorevole);
        Assert.Equal("Attivo", autorevole[0].Nome);
    }
}
