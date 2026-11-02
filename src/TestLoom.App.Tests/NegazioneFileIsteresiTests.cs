using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.6.1 (correzione 2): una negazione su un singolo file con radice che risponde non porta la cartella in sola
/// lettura (niente oscillazioni ogni cinque minuti); una perdita della radice sì, e si riapre solo dopo due prove riuscite
/// consecutive (isteresi).</summary>
[Collection("CondivisioneRete")]
public class NegazioneFileIsteresiTests
{
    public sealed class Carico
    {
        public string Testo { get; set; } = "";
    }

    /// <summary>Questi test descrivono una cartella di rete: il monitor di sola lettura vale solo in modalità Rete (decisione B).</summary>


    [Fact]
    public void Classifica_SeparaLaPerditaDellaRadiceDallaNegazioneDiUnSoloFile()
    {
        Assert.Equal(SharedWriteLossMonitor.Classe.NegazioneFile, SharedWriteLossMonitor.Classifica(new UnauthorizedAccessException()));
        Assert.Equal(SharedWriteLossMonitor.Classe.PerditaRadice, SharedWriteLossMonitor.Classifica(new DirectoryNotFoundException()));
        Assert.Equal(SharedWriteLossMonitor.Classe.NegazioneFile, SharedWriteLossMonitor.Classifica(new IOException("negato", unchecked((int)0x80070005))));
        Assert.Equal(SharedWriteLossMonitor.Classe.PerditaRadice, SharedWriteLossMonitor.Classifica(new IOException("rete", unchecked((int)0x80070040))));
        Assert.Equal(SharedWriteLossMonitor.Classe.PerditaRadice, SharedWriteLossMonitor.Classifica(new IOException("rete", unchecked((int)0x80070035))));
        Assert.Equal(SharedWriteLossMonitor.Classe.NonPerdita, SharedWriteLossMonitor.Classifica(new IOException("in uso", unchecked((int)0x80070020))));
        Assert.Equal(SharedWriteLossMonitor.Classe.NonPerdita, SharedWriteLossMonitor.Classifica(new IOException("disco", unchecked((int)0x80070070))));
    }

    [Fact]
    public void NegazioneDiUnSoloFile_ConRadiceCheRisponde_FalliscEIlSalvataggioSenzaSolaLettura()
    {
        var root = TestRete.NuovaDirectoria();
        using var _modalitaTest = new OverrideModalitaScope(ModalitaCondivisioneService.ValoreRete, root);
        SharedWriteLossMonitor.Registra(root);
        SharedWriteLossMonitor.VerificaRadiceOra(root);   // l'ultima verifica di radice operativa precede la negazione (P4)
        var file = Path.Combine(root, "testcases", "testcase_9.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        JsonIo.WriteAtomic(file, new Carico { Testo = "originale" });
        File.SetAttributes(file, FileAttributes.ReadOnly); // un solo file negato: la radice resta scrivibile
        try
        {
            var ex = Assert.ThrowsAny<UnauthorizedAccessException>(() => JsonIo.WriteAtomic(file, new Carico { Testo = "modificato" }));
            Assert.IsType<NegazioneFileCondivisoException>(ex);
            Assert.Contains("La cartella condivisa risponde", ex.Message);
            Assert.Contains("bozza locale", ex.Message);

            // Nessuna sola lettura globale: la radice non passa in perdita per un file solo.
            Assert.Null(SharedWriteLossMonitor.MotivoPerdita(root));
            Assert.Null(SharedWriteLossMonitor.EsitoPerdita(root));

            File.SetAttributes(file, FileAttributes.Normal);
            Assert.Contains("originale", File.ReadAllText(file)); // il file non è stato toccato
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public void NegazioneDiUnFile_ConRadiceNonRispondente_PortaLaRadiceInSolaLettura()
    {
        var root = Path.Combine(TestRete.NuovaDirectoria(), "assente");   // la radice non esiste più
        using var _modalitaTest = new OverrideModalitaScope(ModalitaCondivisioneService.ValoreRete, root);
        SharedWriteLossMonitor.Registra(root);
        var sostitutiva = SharedWriteLossMonitor.Osserva(Path.Combine(root, "x.json"), new UnauthorizedAccessException("negato"));
        Assert.Null(sostitutiva);   // il chiamante rilancia l'errore originale
        // La perdita della radice si decide in background (P4): si attende la verifica prima di leggerne lo stato.
        Assert.True(SharedWriteLossMonitor.AttendiVerificheInCorso(TimeSpan.FromSeconds(10)));
        Assert.NotNull(SharedWriteLossMonitor.MotivoPerdita(root));
    }

    [Fact]
    public void NegazioneFile_ConVerificaLenta_NonAttendeLaSondaENonPortaLaRadiceInSolaLettura()
    {
        // La verifica della radice va in background: la scrittura che fallisce non attende la sonda, e un tetto di tempo
        // scaduto e' un esito incerto, non una perdita della radice (P4).
        var root = TestRete.NuovaDirectoria();
        using var _modalitaTest = new OverrideModalitaScope(ModalitaCondivisioneService.ValoreRete, root);
        var sondaPrecedente = CondivisioneHealthService.Sonda;
        var sblocco = new System.Threading.ManualResetEventSlim(false);
        try
        {
            CondivisioneHealthService.Sonda = _ =>
            {
                sblocco.Wait(TimeSpan.FromSeconds(30));
                return new EsitoStatoCondivisione { Stato = StatoCondivisione.SolaLettura, Messaggio = "lenta", Incerto = true, VerificatoIl = DateTime.Now };
            };
            SharedWriteLossMonitor.Registra(root);
            var attesa = System.Diagnostics.Stopwatch.StartNew();
            var sostitutiva = SharedWriteLossMonitor.Osserva(Path.Combine(root, "x.json"), new UnauthorizedAccessException("negato"));
            Assert.True(attesa.Elapsed < TimeSpan.FromSeconds(5), "Osserva non deve attendere la sonda della radice");
            Assert.Null(sostitutiva);
            sblocco.Set();
            Assert.True(SharedWriteLossMonitor.AttendiVerificheInCorso(TimeSpan.FromSeconds(10)));
            Assert.Null(SharedWriteLossMonitor.MotivoPerdita(root));   // esito incerto: non e' una perdita
        }
        finally
        {
            sblocco.Set();
            CondivisioneHealthService.Sonda = sondaPrecedente;
            SharedWriteLossMonitor.Ripristina(root);
        }
    }

    [Fact]
    public void NegazioneFile_ConRadiceNonScrivibile_PortaLaRadiceInSolaLetturaInBackground()
    {
        var root = TestRete.NuovaDirectoria();
        using var _modalitaTest = new OverrideModalitaScope(ModalitaCondivisioneService.ValoreRete, root);
        var sondaPrecedente = CondivisioneHealthService.Sonda;
        try
        {
            CondivisioneHealthService.Sonda = _ => new EsitoStatoCondivisione
            {
                Stato = StatoCondivisione.SolaLettura, Messaggio = "radice non scrivibile", VerificatoIl = DateTime.Now,
            };
            SharedWriteLossMonitor.Registra(root);
            SharedWriteLossMonitor.Osserva(Path.Combine(root, "x.json"), new UnauthorizedAccessException("negato"));
            Assert.True(SharedWriteLossMonitor.AttendiVerificheInCorso(TimeSpan.FromSeconds(10)));
            Assert.NotNull(SharedWriteLossMonitor.MotivoPerdita(root));
        }
        finally
        {
            CondivisioneHealthService.Sonda = sondaPrecedente;
            SharedWriteLossMonitor.Ripristina(root);
        }
    }

    [Fact]
    public void Isteresi_RiapreSoloDopoDueProveRiuscite_EUnaProvaFallitaAzzeraIlConteggio()
    {
        var root = TestRete.NuovaDirectoria();
        using var _modalitaTest = new OverrideModalitaScope(ModalitaCondivisioneService.ValoreRete, root);
        SharedWriteLossMonitor.Registra(root);
        SharedWriteLossMonitor.Osserva(Path.Combine(root, "x.json"), new DirectoryNotFoundException("cartella non trovata"));
        Assert.NotNull(SharedWriteLossMonitor.MotivoPerdita(root));

        Assert.False(SharedWriteLossMonitor.PuoRipristinare(root, proveRiuscita: true));   // 1/2: ancora in sola lettura
        Assert.False(SharedWriteLossMonitor.PuoRipristinare(root, proveRiuscita: false));  // una prova fallita azzera il conteggio
        Assert.False(SharedWriteLossMonitor.PuoRipristinare(root, proveRiuscita: true));   // di nuovo 1/2
        Assert.True(SharedWriteLossMonitor.PuoRipristinare(root, proveRiuscita: true));    // 2/2: riaperta
        Assert.Null(SharedWriteLossMonitor.MotivoPerdita(root));
        Assert.True(SharedWriteLossMonitor.PuoRipristinare(root, proveRiuscita: false));   // senza perdita nessun blocco
    }

    [Fact]
    public void RilevazioneNellAppState_IsteresiTraDueGiriPeriodici()
    {
        var dir = TestRete.NuovaDirectoria();
        var root = Path.Combine(dir, "dati");
        using var _modalitaTest = new OverrideModalitaScope(ModalitaCondivisioneService.ValoreRete, root);
        Directory.CreateDirectory(root);
        var configService = new AppConfigService(Path.Combine(dir, "config.json"));
        configService.Save(new AppConfig { CartellaCondivisa = root, Lingua = "it", ModalitaCartellaCondivisa = "Rete" });
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        Assert.Equal(Models.StatoCondivisione.Operativa, state.StatoCondivisione.Stato);

        // La radice perde la scrittura: sola lettura subito. Il primo giro riesce, ma non riapre ancora.
        SharedWriteLossMonitor.Osserva(Path.Combine(root, "testcases", "testcase_1.json"), new DirectoryNotFoundException("manca"));
        state.RileggiDaDisco();
        Assert.Equal(Models.StatoCondivisione.SolaLettura, state.StatoCondivisione.Stato);
        Assert.NotNull(SharedWriteLossMonitor.MotivoPerdita(root));

        // Il secondo giro riesce di nuovo consecutivamente: solo ora la modalità torna operativa.
        state.RileggiDaDisco();
        Assert.Null(SharedWriteLossMonitor.MotivoPerdita(root));
        Assert.Equal(Models.StatoCondivisione.Operativa, state.StatoCondivisione.Stato);
    }
}
