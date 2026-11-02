using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Nessun contatto con servizi Microsoft. Verifica che la sola lettura persistente
/// agisca sui guard di dominio, non soltanto sulla schermata Impostazioni.
/// </summary>
public sealed class WorkspaceReadOnlyServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_Viewer_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_Viewer_Local_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Blocco_PersistePerWorkspace_EConservaDatiOriginali()
    {
        var data = new DataPaths(_root);
        data.EnsureStructure();
        File.WriteAllText(data.RequisitiFile, "[{\"id\":\"BASE-653\"}]");
        var before = File.ReadAllBytes(data.RequisitiFile);

        WorkspaceReadOnlyService.SetEnabled(_root, true, _local);
        Assert.True(WorkspaceReadOnlyService.IsEnabled(_root, _local));
        Assert.False(WorkspaceReadOnlyService.IsEnabled(_root + "_altro", _local));
        Assert.Equal(before, File.ReadAllBytes(data.RequisitiFile));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.lock", SearchOption.AllDirectories));

        // Il marker non vive né nello storage condiviso né fra gli indici locali.
        Assert.False(File.Exists(Path.Combine(_root, "read-only.lock")));

        WorkspaceReadOnlyService.SetEnabled(_root, false, _local);
        Assert.False(WorkspaceReadOnlyService.IsEnabled(_root, _local));
        Assert.Equal(before, File.ReadAllBytes(data.RequisitiFile));
    }

    [Fact]
    public void SolaLettura_ImpedisceScrittureAncheSeFilesystemLocaleEScrivibile()
    {
        Directory.CreateDirectory(_root);
        var paths = new DataPaths(_root);
        WorkspaceReadOnlyService.SetEnabled(_root, true);
        try
        {
            Assert.Equal(StatoCondivisione.SolaLettura,
                CondivisioneHealthService.VerificaDisponibilitaSenzaScrivere(_root).Stato);
            Assert.Equal(StatoCondivisione.SolaLettura,
                CondivisioneHealthService.Verifica(_root).Stato);
            Assert.False(WorkspaceWritePolicy.CanWrite(paths));
            Assert.Throws<IOException>(() =>
                WorkspaceWritePolicy.EnsureCanWrite(paths, "Salvataggio TestCase"));
        }
        finally { WorkspaceReadOnlyService.SetEnabled(_root, false); }

        Assert.Equal(StatoCondivisione.Operativa,
            CondivisioneHealthService.VerificaDisponibilitaSenzaScrivere(_root).Stato);
    }

    [Fact]
    public void BloccoDiUnWorkspace_NonBloccaUnSecondoWorkspace()
    {
        Directory.CreateDirectory(_root);
        var other = _root + "_other";
        Directory.CreateDirectory(other);
        try
        {
            WorkspaceReadOnlyService.SetEnabled(_root, true);
            Assert.True(WorkspaceReadOnlyService.IsEnabled(_root));
            Assert.False(WorkspaceReadOnlyService.IsEnabled(other));
            Assert.Equal(StatoCondivisione.Operativa,
                CondivisioneHealthService.VerificaDisponibilitaSenzaScrivere(other).Stato);
        }
        finally
        {
            WorkspaceReadOnlyService.SetEnabled(_root, false);
            try { Directory.Delete(other, true); } catch { }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_local, true); } catch { }
    }
}
