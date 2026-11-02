using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Copre AttachmentService con immagini reali (non solo byte a caso con estensione giusta): un PNG
/// 1x1 valido, così il test esercita lo stesso identico percorso di codice di un vero allegato caricato
/// dall'utente, non un file finto che capita ad avere l'estensione giusta.</summary>
public class AttachmentServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly DataPaths _paths;
    private readonly TestCase _tc;

    // PNG 1x1 trasparente reale e valido (non byte casuali con estensione .png).
    private static readonly byte[] PngReale = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public AttachmentServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoomAttachmentTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new DataPaths(_dir);
        _paths.EnsureStructure();
        _tc = new TestCase { Id = "Componente-TEST-BCM-20260101-OPA-01", Componente = "Componente-TEST-BCM" };
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string ScriviSorgente(string nomeFile, byte[]? contenuto = null)
    {
        var percorso = Path.Combine(_dir, nomeFile);
        File.WriteAllBytes(percorso, contenuto ?? PngReale);
        return percorso;
    }

    [Fact]
    public void Salva_ConImmaginePngReale_CopiaIlFileECreaMetadatiCorretti()
    {
        var sorgente = ScriviSorgente("screenshot_dtc_banco.png");

        var allegato = AttachmentService.Salva(_paths, _tc, sorgente, "Operatore Prova A");

        Assert.Equal("screenshot_dtc_banco.png", allegato.NomeFile);
        Assert.Equal("Operatore Prova A", allegato.CaricatoDa);
        Assert.Equal(PngReale.Length, allegato.DimensioneByte);
        var destinazione = AttachmentService.PercorsoCompleto(_paths, _tc, allegato);
        Assert.True(File.Exists(destinazione));
        Assert.Equal(PngReale, File.ReadAllBytes(destinazione));
    }

    [Fact]
    public void Salva_ConJpgReale_Accettato()
    {
        var sorgente = ScriviSorgente("foto_vin_veicolo.jpg");
        var allegato = AttachmentService.Salva(_paths, _tc, sorgente, "Operatore Prova A");
        Assert.True(File.Exists(AttachmentService.PercorsoCompleto(_paths, _tc, allegato)));
    }

    [Fact]
    public void Salva_StessoNomeFileDueVolte_RinominaInvecediSovrascrivere()
    {
        var sorgente = ScriviSorgente("screenshot.png");

        var primo = AttachmentService.Salva(_paths, _tc, sorgente, "Operatore Prova A");
        var secondo = AttachmentService.Salva(_paths, _tc, sorgente, "Operatore Prova B");

        Assert.NotEqual(primo.NomeFile, secondo.NomeFile);
        Assert.True(File.Exists(AttachmentService.PercorsoCompleto(_paths, _tc, primo)));
        Assert.True(File.Exists(AttachmentService.PercorsoCompleto(_paths, _tc, secondo)));
    }

    [Fact]
    public void Salva_EstensioneNonSupportata_LanciaEccezioneChiaraSenzaCopiareNulla()
    {
        var sorgente = ScriviSorgente("programma.exe", "formato eseguibile non ammesso"u8.ToArray());

        var ex = Assert.Throws<InvalidOperationException>(() => AttachmentService.Salva(_paths, _tc, sorgente, "Operatore Prova A"));
        Assert.Contains("non supportato", ex.Message, StringComparison.OrdinalIgnoreCase);

        var dir = _paths.AllegatiDir(_tc);
        Assert.True(!Directory.Exists(dir) || Directory.GetFiles(dir).Length == 0);
    }

    [Fact]
    public void Salva_ConPercorsoSorgenteInesistente_LanciaEccezioneInveceDiCorrompersiInSilenzio()
    {
        var sorgenteInesistente = Path.Combine(_dir, "non_esiste.png");
        Assert.ThrowsAny<IOException>(() => AttachmentService.Salva(_paths, _tc, sorgenteInesistente, "Operatore Prova A"));
    }

    [Fact]
    public void Elimina_RimuoveIlFileFisico()
    {
        var sorgente = ScriviSorgente("da_rimuovere.png");
        var allegato = AttachmentService.Salva(_paths, _tc, sorgente, "Operatore Prova A");
        var destinazione = AttachmentService.PercorsoCompleto(_paths, _tc, allegato);
        Assert.True(File.Exists(destinazione));

        AttachmentService.Elimina(_paths, _tc, allegato);

        Assert.False(File.Exists(destinazione));
    }

    [Fact]
    public void Elimina_AllegatoGiaAssente_NonLanciaEccezioni()
    {
        var fantasma = new Allegato { NomeFile = "mai_esistito.png", DataCaricamento = DateTime.Now, CaricatoDa = "x" };
        var ex = Record.Exception(() => AttachmentService.Elimina(_paths, _tc, fantasma));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("foto.PNG")]
    [InlineData("foto.Jpg")]
    [InlineData("archivio.ZIP")]
    [InlineData("messaggio.MSG")]
    public void EstensioneConsentita_NonDistingueMaiuscoleMinuscole(string nomeFile) =>
        Assert.True(AttachmentService.EstensioneConsentita(nomeFile));
}
