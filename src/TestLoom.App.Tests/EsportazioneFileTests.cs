using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Scrittura atomica dei file esportati e runner in background: il file compare solo a scrittura completa,
/// l'annullamento e cooperativo e non lascia mai un export parziale ne temporanei sul disco.</summary>
public sealed class EsportazioneFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_EsportFile_" + Guid.NewGuid().ToString("N"));

    public EsportazioneFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private string Percorso(string nome) => Path.Combine(_dir, nome);

    private void NessunTemporaneoRimasto() => Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

    [Fact]
    public void ScriviAtomico_SpostaIlFileCompletoSulPercorsoScelto()
    {
        var destinazione = Percorso("completo.txt");

        EsportazioneFile.ScriviAtomico(destinazione, temporaneo => File.WriteAllText(temporaneo, "contenuto completo"));

        Assert.Equal("contenuto completo", File.ReadAllText(destinazione));
        NessunTemporaneoRimasto();
    }

    [Fact]
    public void ScriviAtomico_ScritturaFallita_LasciaLaVersionePrecedenteESenzaTemporanei()
    {
        var destinazione = Percorso("fallito.txt");
        File.WriteAllText(destinazione, "versione precedente");

        Assert.Throws<InvalidOperationException>(() => EsportazioneFile.ScriviAtomico(destinazione, temporaneo =>
        {
            File.WriteAllText(temporaneo, "parziale");
            throw new InvalidOperationException("scrittura interrotta simulata");
        }));

        Assert.Equal("versione precedente", File.ReadAllText(destinazione));
        NessunTemporaneoRimasto();
    }

    [Fact]
    public void ScriviAtomico_AnnullatoPrimaDellaScrittura_NonCreaNulla()
    {
        var destinazione = Percorso("annullato_prima.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            EsportazioneFile.ScriviAtomico(destinazione, temporaneo => File.WriteAllText(temporaneo, "x"), cts.Token));

        Assert.False(File.Exists(destinazione));
        NessunTemporaneoRimasto();
    }

    [Fact]
    public void ScriviAtomico_AnnullatoDopoLaScrittura_LasciaLaVersionePrecedente()
    {
        var destinazione = Percorso("annullato_dopo.txt");
        File.WriteAllText(destinazione, "versione precedente");
        using var cts = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => EsportazioneFile.ScriviAtomico(destinazione, temporaneo =>
        {
            File.WriteAllText(temporaneo, "completo ma annullato prima dello spostamento");
            cts.Cancel();
        }, cts.Token));

        Assert.Equal("versione precedente", File.ReadAllText(destinazione));
        NessunTemporaneoRimasto();
    }

    [Fact]
    public void ConAnnullamento_FermaLEnumerazioneAlPrimoAnnullamento()
    {
        using var cts = new CancellationTokenSource();
        var visti = new List<int>();

        Assert.Throws<OperationCanceledException>(() =>
        {
            foreach (var n in Enumerable.Range(1, 10).ConAnnullamento(cts.Token))
            {
                visti.Add(n);
                if (n == 3) cts.Cancel();
            }
        });

        Assert.Equal(new[] { 1, 2, 3 }, visti);
    }

    [Fact]
    public async Task EseguiAsync_Completata_ScriveIlFileSulPercorso()
    {
        var destinazione = Percorso("background_ok.txt");
        var runner = new EsportazioneInBackground();

        var esito = await runner.EseguiAsync(destinazione, (temporaneo, ct) => File.WriteAllText(temporaneo, "ok"));

        Assert.Equal(StatoEsportazione.Completata, esito.Stato);
        Assert.Equal("ok", File.ReadAllText(destinazione));
        Assert.False(runner.InCorso);
        NessunTemporaneoRimasto();
    }

    [Fact]
    public async Task EseguiAsync_AnnullataSubitoDopoLAvvio_NonLasciaNessunFile()
    {
        var destinazione = Percorso("background_annullato.txt");
        File.WriteAllText(destinazione, "precedente");
        var runner = new EsportazioneInBackground();

        var attesa = runner.EseguiAsync(destinazione, (temporaneo, ct) =>
        {
            File.WriteAllText(temporaneo, "parziale");
            ct.WaitHandle.WaitOne(); // lavoro che non finisce da solo: si ferma solo per annullamento
            ct.ThrowIfCancellationRequested();
        });
        runner.Annulla();
        var esito = await attesa;

        Assert.Equal(StatoEsportazione.Annullata, esito.Stato);
        Assert.Equal("precedente", File.ReadAllText(destinazione));
        NessunTemporaneoRimasto();
        Assert.False(runner.InCorso);
    }

    [Fact]
    public async Task EseguiAsync_ErroreDelGeneratore_RiportaFallitaSenzaToccareLaDestinazione()
    {
        var destinazione = Percorso("background_errore.txt");
        File.WriteAllText(destinazione, "precedente");
        var runner = new EsportazioneInBackground();

        var esito = await runner.EseguiAsync(destinazione, (temporaneo, ct) =>
        {
            File.WriteAllText(temporaneo, "parziale");
            throw new IOException("errore simulato");
        });

        Assert.Equal(StatoEsportazione.Fallita, esito.Stato);
        Assert.IsType<IOException>(esito.Errore);
        Assert.Equal("precedente", File.ReadAllText(destinazione));
        NessunTemporaneoRimasto();
    }

    [Fact]
    public async Task EseguiAsync_SecondaEsportazioneMentreUnaELInCorso_NonParte()
    {
        var destinazione = Percorso("background_doppio.txt");
        var runner = new EsportazioneInBackground();
        var rilascio = new TaskCompletionSource();

        var primo = runner.EseguiAsync(destinazione, (temporaneo, ct) =>
        {
            rilascio.Task.Wait(); // il primo export resta in corso finche il test non lo rilascia
            File.WriteAllText(temporaneo, "primo");
        });
        Assert.True(runner.InCorso);

        var secondo = await runner.EseguiAsync(destinazione, (temporaneo, ct) => File.WriteAllText(temporaneo, "secondo"));
        Assert.Equal(StatoEsportazione.Fallita, secondo.Stato);

        rilascio.SetResult();
        Assert.Equal(StatoEsportazione.Completata, (await primo).Stato);
        Assert.Equal("primo", File.ReadAllText(destinazione));
    }
}
