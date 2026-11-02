using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Un export verso un file aperto in Excel non mostra il messaggio tecnico dell'OS: l'esito dice cosa fare, e il file
/// gia' presente resta com'era.</summary>
public class EsportazioneFileInUsoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_EsportazioneInUso_" + Guid.NewGuid().ToString("N"));

    public EsportazioneFileInUsoTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public async Task EseguiAsync_FileAperto_InAltraApplicazione_DiceCosaFareERiportaIlFileOriginale()
    {
        var percorso = Path.Combine(_dir, "report.xlsx");
        File.WriteAllBytes(percorso, [1, 2, 3]);
        // Come Excel: lettura condivisa, ma niente sostituzione del file da parte di altri.
        using var aperto = new FileStream(percorso, FileMode.Open, FileAccess.Read, FileShare.Read);

        var esito = await new EsportazioneInBackground().EseguiAsync(percorso, (temporaneo, _) => File.WriteAllBytes(temporaneo, [9, 9]));

        Assert.Equal(StatoEsportazione.Fallita, esito.Stato);
        Assert.Equal(LocalizationService.Instance.Traduci("EsportazioneFileInUsoMsg"), esito.Errore?.Message);
        Assert.NotNull(esito.Errore?.InnerException);
        aperto.Dispose();
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(percorso));
    }

    [Fact]
    public async Task EseguiAsync_FileLibero_Completa()
    {
        var percorso = Path.Combine(_dir, "libero.xlsx");

        var esito = await new EsportazioneInBackground().EseguiAsync(percorso, (temporaneo, _) => File.WriteAllBytes(temporaneo, [7]));

        Assert.Equal(StatoEsportazione.Completata, esito.Stato);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(percorso));
    }
}
