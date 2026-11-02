using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Regressioni del singleton condiviso Ticketing. Anche se non e' un master data tabellare, due sessioni Master
/// possono tenerlo aperto contemporaneamente su repliche OneDrive diverse: una fotografia obsoleta non deve
/// quindi trasformarsi in last-writer-wins. Revisione + fingerprint coprono anche writer legacy che non
/// incrementano ancora RevisioneDati.
/// </summary>
public sealed class PolarionConfigSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "TestLoom_PolarionSafety_" + Guid.NewGuid().ToString("N"));

    private DataPaths CreatePaths()
    {
        Directory.CreateDirectory(_root);
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        return paths;
    }

    [Fact]
    public void DueSessioniSullaStessaBaseline_LaSecondaNonSovrascriveLaPrima()
    {
        var paths = CreatePaths();
        var seed = new PolarionExportConfig { SezioniDiDefault = new List<string> { "A" } };
        PolarionConfigService.Save(paths, seed);
        Assert.Equal(1, seed.RevisioneDati);

        var sessioneA = PolarionConfigService.Load(paths);
        var sessioneB = PolarionConfigService.Load(paths);
        var revisioneA = sessioneA.RevisioneDati;
        var fingerprintA = sessioneA.SourceFingerprint;

        sessioneB.SezioniDiDefault = new List<string> { "B" };
        PolarionConfigService.Save(paths, sessioneB);
        Assert.Equal(2, sessioneB.RevisioneDati);

        sessioneA.SezioniDiDefault = new List<string> { "C" };
        Assert.Throws<ConflittoRevisioneException>(() => PolarionConfigService.Save(paths, sessioneA));

        // Il fallimento non deve sporcare la working copy con una revisione/fingerprint mai persistiti.
        Assert.Equal(revisioneA, sessioneA.RevisioneDati);
        Assert.Equal(fingerprintA, sessioneA.SourceFingerprint);

        var autorevole = PolarionConfigService.Load(paths);
        Assert.Equal(2, autorevole.RevisioneDati);
        Assert.Equal(new[] { "B" }, autorevole.SezioniDiDefault);
    }

    [Fact]
    public void WriterLegacyCambiaContenutoSenzaIncrementareRevisione_IlFingerprintBloccaIlSalvataggioObsoleto()
    {
        var paths = CreatePaths();
        var seed = new PolarionExportConfig { SezioniDiDefault = new List<string> { "A" } };
        PolarionConfigService.Save(paths, seed);

        var sessione = PolarionConfigService.Load(paths);
        Assert.Equal(1, sessione.RevisioneDati);
        Assert.False(string.IsNullOrWhiteSpace(sessione.SourceFingerprint));

        // Simula una build legacy che riscrive il singleton senza conoscere il protocollo di revisione.
        JsonIo.WriteAtomic(paths.PolarionConfigFile, new PolarionExportConfig
        {
            RevisioneDati = 1,
            SezioniDiDefault = new List<string> { "LEGACY-REMOTE" }
        });

        sessione.SezioniDiDefault = new List<string> { "LOCAL" };
        var ex = Assert.Throws<ConflittoRevisioneException>(() => PolarionConfigService.Save(paths, sessione));
        Assert.True(ex.ContenutoDifferenteAllaStessaRevisione);

        var autorevole = PolarionConfigService.Load(paths);
        Assert.Equal(1, autorevole.RevisioneDati);
        Assert.Equal(new[] { "LEGACY-REMOTE" }, autorevole.SezioniDiDefault);
    }

    [Fact]
    public void FileEsistenteCorrotto_NonVieneSostituitoDaiDefaultRuntime()
    {
        var paths = CreatePaths();
        const string corrotto = "{ ticketing non valido";
        File.WriteAllText(paths.PolarionConfigFile, corrotto);

        var fallback = PolarionConfigService.Load(paths);
        Assert.False(string.IsNullOrWhiteSpace(fallback.ErroreCaricamento));
        Assert.Null(fallback.SourceFingerprint);

        fallback.SezioniDiDefault = new List<string> { "NON-DEVE-SOVRASCRIVERE" };
        Assert.Throws<InvalidDataException>(() => PolarionConfigService.Save(paths, fallback));
        Assert.Equal(corrotto, File.ReadAllText(paths.PolarionConfigFile));
    }

    [Fact]
    public void FileRimossoDopoIlLoad_NonVieneRicreatoDaUnaFotografiaObsoleta()
    {
        var paths = CreatePaths();
        var seed = new PolarionExportConfig { SezioniDiDefault = new List<string> { "A" } };
        PolarionConfigService.Save(paths, seed);
        var sessione = PolarionConfigService.Load(paths);
        Assert.False(string.IsNullOrWhiteSpace(sessione.SourceFingerprint));

        File.Delete(paths.PolarionConfigFile);
        sessione.SezioniDiDefault = new List<string> { "LOCAL" };

        Assert.Throws<IOException>(() => PolarionConfigService.Save(paths, sessione));
        Assert.False(File.Exists(paths.PolarionConfigFile));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch { }
    }
}
