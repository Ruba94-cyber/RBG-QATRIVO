using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.6.1 (d, e): perdita di scrittura sulla cartella condivisa → sola lettura → recupero con prova reale, e cartella
/// non raggiungibile all'avvio → riapertura quando torna. Nessuna scrittura avviene sulla cartella durante la perdita.</summary>
[Collection("CondivisioneRete")]
public class SharedWriteLossTests : IDisposable
{
    private readonly string _radice = TestRete.NuovaDirectoria();

    /// <summary>Questi test descrivono una cartella di rete: il monitor di sola lettura vale solo in modalità Rete (decisione B).</summary>
    private readonly OverrideModalitaScope _modalita;

    public SharedWriteLossTests()
    {
        _modalita = new OverrideModalitaScope(ModalitaCondivisioneService.ValoreRete, _radice);
    }

    public void Dispose()
    {
        _modalita.Dispose();
        try { Directory.Delete(_radice, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ErroreDiRetePerdutaSiDistingueDaUnConflittoLocale()
    {
        Assert.True(SharedWriteLossMonitor.EErrorePerditaCondivisione(new UnauthorizedAccessException("negato")));
        Assert.True(SharedWriteLossMonitor.EErrorePerditaCondivisione(new DirectoryNotFoundException("manca")));
        Assert.True(SharedWriteLossMonitor.EErrorePerditaCondivisione(new IOException("rete", unchecked((int)0x80070040)))); // NETNAME_DELETED
        Assert.True(SharedWriteLossMonitor.EErrorePerditaCondivisione(new IOException("rete", unchecked((int)0x80070043)))); // BAD_NET_NAME
        Assert.True(SharedWriteLossMonitor.EErrorePerditaCondivisione(new IOException("rete", unchecked((int)0x80070035)))); // BAD_NETPATH
        Assert.False(SharedWriteLossMonitor.EErrorePerditaCondivisione(new IOException("in uso", unchecked((int)0x80070020)))); // sharing violation
        Assert.False(SharedWriteLossMonitor.EErrorePerditaCondivisione(new IOException("disco pieno", unchecked((int)0x80070070))));
        Assert.False(SharedWriteLossMonitor.EErrorePerditaCondivisione(new FileNotFoundException("file")));
        Assert.False(SharedWriteLossMonitor.EErrorePerditaCondivisione(new InvalidOperationException("altro")));
    }

    [Theory]
    [InlineData(55)]   // ERROR_DEV_NOT_EXIST: risorsa di rete non più disponibile (VPN caduta)
    [InlineData(59)]   // ERROR_UNEXP_NET_ERR
    [InlineData(1236)] // ERROR_CONNECTION_ABORTED
    public void CodiciWin32DiReteInterrottaSonoPerditaDiRadice(int codice)
    {
        // 6.7.1 (fix rete): codici di rete persa aggiunti alla classificazione di radice persa.
        var ex = new IOException("rete interrotta", unchecked((int)(0x80070000 | codice)));
        Assert.Equal(SharedWriteLossMonitor.Classe.PerditaRadice, SharedWriteLossMonitor.Classifica(ex));
        Assert.True(SharedWriteLossMonitor.EErrorePerditaCondivisione(ex));
    }

    [Fact]
    public void PerditaSegnaSoloLaRadiceRegistrataERipristinaLaChiude()
    {
        var radice = Path.Combine(_radice, "condivisa-A");
        var altra = Path.Combine(_radice, "condivisa-B");
        SharedWriteLossMonitor.Registra(radice);
        SharedWriteLossMonitor.Registra(altra);

        SharedWriteLossMonitor.Osserva(Path.Combine(radice, "testcases", "testcase_1.json"), new UnauthorizedAccessException("negato"));
        // La radice non risponde: la perdita si decide nella verifica in background, non sul thread di scrittura (P4).
        Assert.True(SharedWriteLossMonitor.AttendiVerificheInCorso(TimeSpan.FromSeconds(10)));
        Assert.NotNull(SharedWriteLossMonitor.MotivoPerdita(radice));
        Assert.Null(SharedWriteLossMonitor.MotivoPerdita(altra));

        var esito = SharedWriteLossMonitor.EsitoPerdita(radice);
        Assert.NotNull(esito);
        Assert.Equal(Models.StatoCondivisione.SolaLettura, esito!.Stato);
        Assert.Contains("bozze locali", esito.Messaggio);

        // Un errore non di rete sulla stessa radice non cambia nulla, e il ripristino riporta la radice operativa.
        SharedWriteLossMonitor.Ripristina(altra);
        SharedWriteLossMonitor.Osserva(Path.Combine(radice, "x.json"), new IOException("in uso", unchecked((int)0x80070020)));
        Assert.NotNull(SharedWriteLossMonitor.MotivoPerdita(radice));
        SharedWriteLossMonitor.Ripristina(radice);
        Assert.Null(SharedWriteLossMonitor.MotivoPerdita(radice));
        Assert.Null(SharedWriteLossMonitor.EsitoPerdita(radice));
    }

    [Fact]
    public void PerditaDiScrittura_PortaInSolaLetturaBloccaLeScritture_ERecuperaConLaProva()
    {
        var root = Path.Combine(_radice, "dati");
        Directory.CreateDirectory(root);
        var configService = new AppConfigService(Path.Combine(_radice, "local", "config.json"));
        configService.Save(new AppConfig { CartellaCondivisa = root, Lingua = "it", ModalitaCartellaCondivisa = "Rete" });
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        Assert.Equal(Models.StatoCondivisione.Operativa, state.StatoCondivisione.Stato);

        // La radice non risponde più alla scrittura (perdita di radice): il gate si chiude subito, senza attendere il giro periodico.
        SharedWriteLossMonitor.Osserva(Path.Combine(root, "testcases", "testcase_1.json"), new DirectoryNotFoundException("cartella non trovata"));
        Assert.Equal(Models.StatoCondivisione.SolaLettura, state.StatoCondivisione.Stato);
        Assert.Contains("bozze locali", state.StatoCondivisione.Messaggio);
        Assert.False(WorkspaceWritePolicy.CanWrite(state.Paths!));
        Assert.Throws<IOException>(() => WorkspaceWritePolicy.EnsureCanWrite(state.Paths!, "Prova"));

        // Isteresi (correzione 2): una prova riuscita non basta; la seconda consecutiva riapre la scrittura.
        state.RileggiDaDisco();
        Assert.NotNull(SharedWriteLossMonitor.MotivoPerdita(root));
        Assert.Equal(Models.StatoCondivisione.SolaLettura, state.StatoCondivisione.Stato);
        state.RileggiDaDisco();
        Assert.Null(SharedWriteLossMonitor.MotivoPerdita(root));
        Assert.Equal(Models.StatoCondivisione.Operativa, state.StatoCondivisione.Stato);
    }

    [Fact]
    public void CartellaNonRaggiungibileAllAvvio_SiRiapreQuandoTornaSenzaRiavviare()
    {
        var root = Path.Combine(_radice, "rete", "dati");
        var configService = new AppConfigService(Path.Combine(_radice, "local", "config.json"));
        configService.Save(new AppConfig { CartellaCondivisa = root, Lingua = "it", ModalitaCartellaCondivisa = "Rete" });
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        Assert.False(state.InizializzazioneWorkspaceCompletata);
        Assert.False(state.CartellaValida);

        // Ancora assente: la riapertura non crea una struttura vuota al posto della cartella mancante.
        Assert.False(state.RiprovaInizializzazione());
        Assert.False(Directory.Exists(root));

        // La cartella (rete o VPN) torna disponibile: la stessa sessione riapre il workspace.
        Directory.CreateDirectory(root);
        Assert.True(state.RiprovaInizializzazione());
        Assert.True(state.InizializzazioneWorkspaceCompletata);
        Assert.True(state.CartellaValida);
        Assert.NotNull(state.TestCases);
        Assert.Equal(Models.StatoCondivisione.Operativa, state.StatoCondivisione.Stato);
    }
}
