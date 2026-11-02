using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Contratto condiviso della riparazione della coda NDJSON (ledger receipt, notifiche, journal test): solo l'ultima riga
/// non terminata può essere scartata, copiata prima in current.ndjson.torn-*, e i file laterali sono potati.
/// </summary>
public sealed class NdjsonCodaRiparazioneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_NdjsonCoda_" + Guid.NewGuid().ToString("N"));
    private string Current => Path.Combine(_dir, "current.ndjson");

    public NdjsonCodaRiparazioneTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    private static bool RigaValida(string riga) =>
        riga.StartsWith("{\"ok\":", StringComparison.Ordinal) && riga.EndsWith('}');

    [Fact]
    public void RiparaSeCodaParziale_UltimaRigaIncompleta_CopiaInTornETroncaSoloLaCoda()
    {
        File.WriteAllText(Current, "{\"ok\":1}\n{\"ok\":2}\n{\"ok\":3");

        var esito = NdjsonCodaRiparazione.RiparaSeCodaParziale(Current, RigaValida, "test");

        Assert.Equal(NdjsonCodaRiparazione.Esito.Riparata, esito);
        Assert.Equal("{\"ok\":1}\n{\"ok\":2}\n", File.ReadAllText(Current));
        var laterale = Assert.Single(Directory.GetFiles(_dir, "current.ndjson.torn-*"));
        Assert.Equal("{\"ok\":3", File.ReadAllText(laterale));
    }

    [Fact]
    public void RiparaSeCodaParziale_UltimaRigaValidaSenzaNewline_NonLaScarta()
    {
        File.WriteAllText(Current, "{\"ok\":1}\n{\"ok\":2}");

        var esito = NdjsonCodaRiparazione.RiparaSeCodaParziale(Current, RigaValida, "test");

        Assert.Equal(NdjsonCodaRiparazione.Esito.Integra, esito);
        Assert.Equal("{\"ok\":1}\n{\"ok\":2}", File.ReadAllText(Current));
        Assert.Empty(Directory.GetFiles(_dir, "current.ndjson.torn-*"));
    }

    [Fact]
    public void RiparaSeCodaParziale_RigaIntermediaNonValida_NonModificaNulla()
    {
        const string testo = "{\"ok\":1}\n{\"ko\n{\"ok\":3}\n";
        File.WriteAllText(Current, testo);

        var esito = NdjsonCodaRiparazione.RiparaSeCodaParziale(Current, RigaValida, "test");

        Assert.Equal(NdjsonCodaRiparazione.Esito.Irriparabile, esito);
        Assert.Equal(testo, File.ReadAllText(Current));
        Assert.Empty(Directory.GetFiles(_dir, "current.ndjson.torn-*"));
    }

    [Fact]
    public void PreparaAccodamento_UltimaRigaValidaSenzaNewline_ChiudeLaRigaPrimaDelNuovoEvento()
    {
        File.WriteAllText(Current, "{\"ok\":1}");

        var prefisso = NdjsonCodaRiparazione.PreparaAccodamento(Current, RigaValida, "test", "irriparabile");

        Assert.Equal(Environment.NewLine, prefisso);
        Assert.Equal("{\"ok\":1}", File.ReadAllText(Current));
    }

    [Fact]
    public void PotaTornObsoleti_ConservaSoloIFileLateraliPiuRecenti()
    {
        var totale = NdjsonCodaRiparazione.TornConservati + 3;
        for (var i = 0; i < totale; i++)
            File.WriteAllText(Current + ".torn-20261014000000" + i.ToString("D3"), "coda");

        NdjsonCodaRiparazione.PotaTornObsoleti(Current);

        var rimasti = Directory.GetFiles(_dir, "current.ndjson.torn-*");
        Assert.Equal(NdjsonCodaRiparazione.TornConservati, rimasti.Length);
        // I tre più vecchi (timestamp più bassi) sono quelli eliminati.
        for (var i = 0; i < 3; i++)
            Assert.False(File.Exists(Current + ".torn-20261014000000" + i.ToString("D3")));
        Assert.True(File.Exists(Current + ".torn-20261014000000" + (totale - 1).ToString("D3")));
    }
}
