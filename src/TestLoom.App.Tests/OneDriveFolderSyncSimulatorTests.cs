namespace TestLoom.App.Tests;

/// <summary>Test unitari del simulatore stesso (Block A), indipendenti dai processi MultiPcHost: verificano che
/// il motore di propagazione si comporti come dichiarato prima di fidarcisi negli scenari end-to-end.</summary>
public sealed class OneDriveFolderSyncSimulatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_SimTest_" + Guid.NewGuid().ToString("N"));
    private readonly string _a;
    private readonly string _b;

    public OneDriveFolderSyncSimulatorTests()
    {
        _a = Path.Combine(_root, "A");
        _b = Path.Combine(_root, "B");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void Propaga_FileNuovoSuA_AppareSuBDopoUnTick()
    {
        var sim = new OneDriveFolderSyncSimulator(_a, _b, seed: 1);
        File.WriteAllText(Path.Combine(_a, "x.json"), "{\"v\":1}");

        sim.Propaga();

        Assert.True(File.Exists(Path.Combine(_b, "x.json")));
        Assert.Equal("{\"v\":1}", File.ReadAllText(Path.Combine(_b, "x.json")));
    }

    [Fact]
    public void Propaga_ConRitardo_NonArrivaSubito_ArrivaDopoNTick()
    {
        var sim = new OneDriveFolderSyncSimulator(_a, _b, seed: 1);
        File.WriteAllText(Path.Combine(_a, "x.json"), "{\"v\":1}");

        sim.Propaga(ritardoTickPerNuoviCambiamenti: 3);
        Assert.False(File.Exists(Path.Combine(_b, "x.json")), "Non deve arrivare prima del ritardo configurato.");

        sim.Propaga();
        sim.Propaga();
        Assert.False(File.Exists(Path.Combine(_b, "x.json")), "Non ancora al tick 3: il ritardo era di 3 tick dal primo Propaga (tick 1), matura al tick 4.");
        sim.Propaga();
        Assert.True(File.Exists(Path.Combine(_b, "x.json")), "Deve arrivare al tick giusto.");
    }

    [Fact]
    public void Propaga_ScritturaAMeta_FileTroncatoFinoAlCompletamento()
    {
        var sim = new OneDriveFolderSyncSimulator(_a, _b, seed: 1);
        var contenuto = "{\"descrizione\":\"contenuto abbastanza lungo da poter essere troncato a meta\"}";
        File.WriteAllText(Path.Combine(_a, "x.json"), contenuto);

        sim.Propaga(scritturaAMetaBytesPerNuoviCambiamenti: 10);

        var parziale = File.ReadAllText(Path.Combine(_b, "x.json"));
        Assert.Equal(10, parziale.Length);
        Assert.NotEqual(contenuto, parziale);

        sim.CompletaScrittureAMeta();
        Assert.Equal(contenuto, File.ReadAllText(Path.Combine(_b, "x.json")));
    }

    [Fact]
    public void BloccaTemporaneamente_RimandaLaPropagazioneFinoAlloSblocco()
    {
        var sim = new OneDriveFolderSyncSimulator(_a, _b, seed: 1);
        sim.BloccaTemporaneamente("x.json", tick: 3);
        File.WriteAllText(Path.Combine(_a, "x.json"), "{\"v\":1}");

        sim.Propaga();
        sim.Propaga();
        Assert.False(File.Exists(Path.Combine(_b, "x.json")), "Deve restare bloccato finché lo sblocco non matura.");

        sim.PropagaFinoAConvergenza();
        Assert.True(File.Exists(Path.Combine(_b, "x.json")), "Deve sbloccarsi da solo, mai un errore permanente.");
    }

    [Fact]
    public void CancellazioneSuA_RitardataMaEventualmenteVisibileAncheSuB()
    {
        var sim = new OneDriveFolderSyncSimulator(_a, _b, seed: 1);
        File.WriteAllText(Path.Combine(_a, "x.json"), "{\"v\":1}");
        sim.PropagaFinoAConvergenza();
        Assert.True(File.Exists(Path.Combine(_b, "x.json")));

        File.Delete(Path.Combine(_a, "x.json"));
        sim.Propaga(ritardoTickPerNuoviCambiamenti: 2);
        Assert.True(File.Exists(Path.Combine(_b, "x.json")), "Cancellazione ritardata: non subito.");

        sim.PropagaFinoAConvergenza();
        Assert.False(File.Exists(Path.Combine(_b, "x.json")), "...ma eventualmente propagata.");
    }

    [Fact]
    public void EntrambiILatiModificanoLoStessoFileNellaStessaFinestra_NessunaSovrascritturaSilenziosa()
    {
        var sim = new OneDriveFolderSyncSimulator(_a, _b, seed: 1);
        File.WriteAllText(Path.Combine(_a, "x.json"), "{\"v\":0}");
        sim.PropagaFinoAConvergenza();
        Assert.Equal("{\"v\":0}", File.ReadAllText(Path.Combine(_b, "x.json")));

        // Entrambi i lati modificano lo stesso path PRIMA che il prossimo giro di propagazione parta.
        File.WriteAllText(Path.Combine(_a, "x.json"), "{\"v\":\"da A\"}");
        File.WriteAllText(Path.Combine(_b, "x.json"), "{\"v\":\"da B\"}");

        sim.PropagaFinoAConvergenza();

        // Nessuno dei due contenuti e' sparito: uno resta nel path canonico, l'altro come copia rinominata su
        // ENTRAMBI i lati (OneDrive genera la copia di conflitto su entrambe le postazioni coinvolte).
        var contenutiA = Directory.GetFiles(_a, "*.json").Select(File.ReadAllText).ToList();
        var contenutiB = Directory.GetFiles(_b, "*.json").Select(File.ReadAllText).ToList();
        Assert.Contains("{\"v\":\"da A\"}", contenutiA.Concat(contenutiB));
        Assert.Contains("{\"v\":\"da B\"}", contenutiA.Concat(contenutiB));
        Assert.True(Directory.GetFiles(_a, "*.json").Length >= 2 || Directory.GetFiles(_b, "*.json").Length >= 2,
            "Deve esistere almeno una copia di conflitto rinominata su uno dei due lati.");
    }
}
