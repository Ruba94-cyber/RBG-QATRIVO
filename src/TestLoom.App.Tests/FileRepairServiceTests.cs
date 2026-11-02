using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class FileRepairServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_filerepair_" + Guid.NewGuid().ToString("N"));

    public FileRepairServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string ScriviFile(string contenuto)
    {
        var testDir = Path.Combine(_dir, "testcases", "ECM");
        Directory.CreateDirectory(testDir);
        var percorso = Path.Combine(testDir, "testcase_x.json");
        File.WriteAllText(percorso, contenuto);
        return percorso;
    }

    [Fact]
    public void ProvaRipara_VirgolaFinalePrimaDiChiusura_Ripara()
    {
        var percorso = ScriviFile("{\"Id\": \"A\", \"Note\": \"x\",}");

        var risultato = FileRepairService.ProvaRipara(percorso);

        Assert.True(risultato.Riuscita);
        Assert.Contains(".bak", risultato.Messaggio);
        var testo = File.ReadAllText(percorso);
        Assert.DoesNotContain(",}", testo);
    }

    [Fact]
    public void ProvaRipara_ParentesiGraffaMancante_Ripara()
    {
        var percorso = ScriviFile("{\"Id\": \"A\", \"Note\": \"x\"");

        var risultato = FileRepairService.ProvaRipara(percorso);

        Assert.True(risultato.Riuscita);
        var testo = File.ReadAllText(percorso);
        Assert.EndsWith("}", testo);
    }

    [Fact]
    public void ProvaRipara_SalvaBackupDelloOriginale()
    {
        var originale = "{\"Id\": \"A\",}";
        var percorso = ScriviFile(originale);

        var risultato = FileRepairService.ProvaRipara(percorso);

        Assert.True(risultato.Riuscita);
        Assert.True(File.Exists(risultato.Messaggio));
        Assert.Equal(originale, File.ReadAllText(risultato.Messaggio));
    }

    [Fact]
    public void ProvaRipara_NonRiparabile_NonTocaIlFileOriginale()
    {
        var originale = "questo non e' json ne' riparabile con virgole o parentesi";
        var percorso = ScriviFile(originale);

        var risultato = FileRepairService.ProvaRipara(percorso);

        Assert.False(risultato.Riuscita);
        Assert.Equal(originale, File.ReadAllText(percorso));
    }

    [Fact]
    public void ProvaRipara_JsonGiaValido_NonRiportaSuccesso()
    {
        var percorso = ScriviFile("{\"Id\": \"A\"}");

        var risultato = FileRepairService.ProvaRipara(percorso);

        Assert.False(risultato.Riuscita);
    }
}
