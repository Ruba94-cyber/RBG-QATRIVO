using TestLoom.App.Services;
using Xunit;

namespace TestLoom.App.Tests;

public sealed class SyncHandshakeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ditm_handshake_" + Guid.NewGuid().ToString("N"));
    private DateTime _ora = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    public SyncHandshakeServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private SyncHandshakeService Pc(string id) => new(_root, id, "op-" + id, () => _ora);

    [Fact]
    public void SenzaAltrePostazioni_ProvaSoloLocaleNonConfermata()
    {
        var a = Pc("A");
        a.Impulso();
        var report = a.Valuta();
        Assert.Equal(StatoHandshake.NessunaPostazione, report.Stato);
        Assert.Empty(report.Postazioni);
    }

    [Fact]
    public void ConfermaARoundTrip_AScriveBVedeBRispondeAVedeLaConferma()
    {
        var a = Pc("A");
        var b = Pc("B");
        a.Impulso();                       // A1
        b.Impulso();                       // B1, che legge A1
        a.Impulso();                       // A2, che legge B1

        Assert.Equal(StatoHandshake.InAttesa, a.Valuta().Stato);   // B non ha ancora confermato l'ultima sequenza di A
        Assert.True(b.RispondiSeNecessario());                      // B nota la nuova sequenza di A e conferma
        var report = a.Valuta();

        Assert.Equal(StatoHandshake.Confermata, report.Stato);
        Assert.Equal(1, report.Confermate);
        Assert.True(report.Postazioni.Single().HoRicevutoSuaSequenza);
    }

    [Fact]
    public void RispondiSeNecessario_NonScriveSeNonCiSonoNovita()
    {
        var a = Pc("A");
        var b = Pc("B");
        a.Impulso();
        Assert.True(b.RispondiSeNecessario());   // prima volta: vede A
        Assert.False(b.RispondiSeNecessario());  // nessuna novita'
    }

    [Fact]
    public void PostazioneInattivaOltreLaFinestra_NonBloccaLaConferma()
    {
        var a = Pc("A");
        var b = Pc("B");
        b.Impulso();
        a.Impulso();
        _ora += SyncHandshakeService.FinestraAttivaPredefinita + TimeSpan.FromMinutes(1);
        a.Impulso();

        var report = a.Valuta();

        Assert.Equal(StatoHandshake.NessunaPostazione, report.Stato);
        Assert.False(report.Postazioni.Single().Attiva);
    }

    [Fact]
    public void FileDellAltraPostazioneIllegibile_EIgnoratoSenzaEccezioni()
    {
        var a = Pc("A");
        a.Impulso();
        File.WriteAllText(Path.Combine(_root, "_ditm", "sync-handshake", "rotto.json"), "{ non json");
        File.WriteAllBytes(Path.Combine(_root, "_ditm", "sync-handshake", "vuoto.json"), []);

        var report = a.Valuta();
        Assert.Equal(StatoHandshake.NessunaPostazione, report.Stato);
        a.Impulso();
    }

    [Fact]
    public void CopiaDiConflittoDellaStessaPostazione_ContaUnaVoltaConLaSequenzaPiuAlta()
    {
        var a = Pc("A");
        var b = Pc("B");
        b.Impulso();
        b.Impulso();
        var dir = Path.Combine(_root, "_ditm", "sync-handshake");
        File.Copy(Path.Combine(dir, "B.json"), Path.Combine(dir, "B-PCNOME.json"));
        b.Impulso();
        a.Impulso();

        Assert.Single(a.Valuta().Postazioni);
    }

    [Fact]
    public async Task ImpulsoEAttendi_TerminaAlTimeoutConStatoInAttesaSeLaPostazioneNonRisponde()
    {
        var a = Pc("A");
        var b = Pc("B");
        b.Impulso();
        var passi = 0;

        var report = await a.ImpulsoEAttendiAsync(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5),
            () => _ora, (d, _) => { _ora += d; passi++; return Task.CompletedTask; });

        Assert.Equal(StatoHandshake.InAttesa, report.Stato);
        Assert.InRange(passi, 5, 7);
    }

    [Fact]
    public async Task ImpulsoEAttendi_TerminaSubitoQuandoLaPostazioneConferma()
    {
        var a = Pc("A");
        var b = Pc("B");
        b.Impulso();
        var passi = 0;

        var report = await a.ImpulsoEAttendiAsync(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5),
            () => _ora, (d, _) => { _ora += d; passi++; b.RispondiSeNecessario(); return Task.CompletedTask; });

        Assert.Equal(StatoHandshake.Confermata, report.Stato);
        Assert.Equal(1, passi);
    }
}
