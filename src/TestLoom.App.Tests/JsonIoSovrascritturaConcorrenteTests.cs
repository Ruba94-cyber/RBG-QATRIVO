using System.Text;
using TestLoom.App.Services;
using Xunit;

namespace TestLoom.App.Tests;

public sealed class JsonIoSovrascritturaConcorrenteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ditm_jsonio_conc_" + Guid.NewGuid().ToString("N"));

    public JsonIoSovrascritturaConcorrenteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        JsonIo.PrimaDelControlloDestinazioneTestHook = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed record Dato(int V);

    [Fact]
    public void FileSostituitoDaAltroPcPrimaDelRename_NonVieneSovrascrittoENonRestanoTemporanei()
    {
        var path = Path.Combine(_dir, "test.json");
        JsonIo.WriteAtomic(path, new Dato(1));
        var altroPc = Encoding.UTF8.GetBytes("{\"V\":2}");
        JsonIo.PrimaDelControlloDestinazioneTestHook = p =>
        {
            if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) File.WriteAllBytes(path, altroPc);
        };

        Assert.Throws<DestinazioneModificataDuranteScritturaException>(() => JsonIo.WriteAtomic(path, new Dato(3)));

        Assert.Equal(altroPc, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp*"));
    }

    [Fact]
    public void SovrascritturaSenzaInterferenze_RiesceEMantieneIlNuovoContenuto()
    {
        var path = Path.Combine(_dir, "test.json");
        JsonIo.WriteAtomic(path, new Dato(1));
        JsonIo.WriteAtomic(path, new Dato(5));
        Assert.Contains("5", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp*"));
    }

    [Fact]
    public void CreazioneDiFileNuovo_NonEInfluenzataDalControllo()
    {
        var path = Path.Combine(_dir, "nuovo.json");
        JsonIo.WriteAtomic(path, new Dato(7));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("a.json.tmp1a2b3c4d", true)]
    [InlineData("a.json.tmpDEADBEEF", true)]
    [InlineData("a.json.tmp", false)]
    [InlineData("a.json.tmp1a2b3c4", false)]
    [InlineData("a.json.tmpZZZZZZZZ", false)]
    [InlineData("a.json", false)]
    public void TemporaneoDiScrittura_ERiconosciutoSoloNelFormatoDiJsonIo(string nome, bool atteso) =>
        Assert.Equal(atteso, OneDriveSyncService.EraTemporaneoDiScrittura(nome));
}
