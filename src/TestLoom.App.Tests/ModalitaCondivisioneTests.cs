using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.6.1 (a, b, c, f): rilevazione della modalità, override per PC, percorso canonico, timeout, config e
/// gating delle operazioni Cloud Files. Vedi TestRete per i sostituti del risolutore e dell'override.</summary>
[Collection("CondivisioneRete")]
public class ModalitaCondivisioneTests
{
    public static IEnumerable<object[]> PercorsiDiRete() => new[]
    {
        new object[] { @"\" + @"\srv\share\TestLoom" },
        new object[] { @"\" + @"\?\UNC\srv\share\TestLoom" },
        new object[] { "//srv/share/TestLoom" },
    };

    public static IEnumerable<object[]> PercorsiLocaliOneDrive() => new[]
    {
        new object[] { @"C:\TestLoom\Dati" },
        new object[] { @"C:\Users\operatore\OneDrive - Azienda\TestLoom" },
        new object[] { @"\" + @"\?\C:\TestLoom\Dati" },
        new object[] { @"\" + @"\.\C:" },
    };

    [Theory]
    [MemberData(nameof(PercorsiDiRete))]
    public void Rileva_PercorsoUnc_EUnaCartellaDiRete(string percorso)
    {
        using var _ = new RisolutoreUnitaFinto();
        Assert.Equal(ModalitaCondivisione.CartellaRete, ModalitaCondivisioneService.Rileva(percorso));
    }

    [Theory]
    [MemberData(nameof(PercorsiLocaliOneDrive))]
    public void Rileva_PercorsoLocaleOuOneDrive_RestaCartellaSincronizzata(string percorso)
    {
        using var _ = new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati"));
        Assert.Equal(ModalitaCondivisione.CartellaSincronizzata, ModalitaCondivisioneService.Rileva(percorso));
    }

    [Fact]
    public void Rileva_UnitaMappata_UsaIlRisolutoreELeLettereLocaliNonSono()
    {
        using var _ = new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati"));
        Assert.Equal(ModalitaCondivisione.CartellaRete, ModalitaCondivisioneService.Rileva(@"Z:\TestLoom"));
        Assert.Equal(ModalitaCondivisione.CartellaRete, ModalitaCondivisioneService.Rileva("z:/TestLoom"));
        Assert.Equal(ModalitaCondivisione.CartellaSincronizzata, ModalitaCondivisioneService.Rileva(@"Y:\TestLoom"));
    }

    [Fact]
    public void Rileva_UnitaDiReteSenzaPercorsoUncNoto_ContaComeRete()
    {
        // Windows dice che è un'unità di rete ma non fornisce il percorso remoto: resta cartella di rete, senza canonico.
        using var _ = new RisolutoreUnitaFinto(("Z:", null));
        Assert.Equal(ModalitaCondivisione.CartellaRete, ModalitaCondivisioneService.Rileva(@"Z:\TestLoom"));
        Assert.Equal(@"Z:\TestLoom", ModalitaCondivisioneService.PercorsoCanonico(@"Z:\TestLoom"));
    }

    [Theory]
    [InlineData("Rete", @"C:\TestLoom", ModalitaCondivisione.CartellaRete)]
    [InlineData("OneDrive", @"\" + @"\srv\share\TestLoom", ModalitaCondivisione.CartellaSincronizzata)]
    [InlineData("Automatica", @"\" + @"\srv\share\TestLoom", ModalitaCondivisione.CartellaRete)]
    [InlineData("Automatica", @"C:\TestLoom", ModalitaCondivisione.CartellaSincronizzata)]
    [InlineData("valore-sconosciuto", @"\" + @"\srv\share\TestLoom", ModalitaCondivisione.CartellaRete)]
    [InlineData(null, @"C:\TestLoom", ModalitaCondivisione.CartellaSincronizzata)]
    public void Effettiva_OverrideDelPcHaPrecedenzaSullaRilevazione(string? valore, string percorso, ModalitaCondivisione attesa)
    {
        using var _ = new RisolutoreUnitaFinto();
        Assert.Equal(attesa, ModalitaCondivisioneService.Effettiva(percorso, valore));
    }

    [Theory]
    [InlineData(null, "Automatica")]
    [InlineData("", "Automatica")]
    [InlineData("   ", "Automatica")]
    [InlineData("rete", "Rete")]
    [InlineData("ONEDRIVE", "OneDrive")]
    [InlineData("Qualsiasi", "Automatica")]
    public void NormalizzaValore_AssenteEVuotoEDSconosciutoSonoAutomatica(string? valore, string attesa) =>
        Assert.Equal(attesa, ModalitaCondivisioneService.NormalizzaValore(valore));

    [Fact]
    public void ImpostaOverride_AutomaticaRimuoveLOverrideDelPc()
    {
        using var _ = new OverrideModalitaScope(ModalitaCondivisioneService.ValoreRete, @"C:\testloom-override");
        Assert.Equal(ModalitaCondivisioneService.ValoreRete, ModalitaCondivisioneService.OverrideRadice(@"C:\testloom-override"));
        ModalitaCondivisioneService.ImpostaOverrideRadice(@"C:\testloom-override", "Automatica");
        Assert.Equal(ModalitaCondivisioneService.ValoreAutomatica, ModalitaCondivisioneService.OverrideRadice(@"C:\testloom-override"));
    }

    [Fact]
    public void PercorsoCanonico_UnitaMappataDiventaUncGliAltriRestanoUguali()
    {
        using var _ = new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati"));
        Assert.Equal(@"\" + @"\srv\dati\TestLoom\Dati", ModalitaCondivisioneService.PercorsoCanonico(@"Z:\TestLoom\Dati"));
        Assert.Equal(@"\" + @"\srv\dati", ModalitaCondivisioneService.PercorsoCanonico("Z:"));
        Assert.Equal(@"\" + @"\srv\share\TestLoom", ModalitaCondivisioneService.PercorsoCanonico(@"\" + @"\srv\share\TestLoom"));
        Assert.Equal(@"C:\TestLoom", ModalitaCondivisioneService.PercorsoCanonico(@"C:\TestLoom"));
        Assert.Equal(@"Y:\TestLoom", ModalitaCondivisioneService.PercorsoCanonico(@"Y:\TestLoom"));
    }

    [Fact]
    public void RisolutorePredefinito_UnitaFissaLocale_NonEDiRete_AncheAlSecondoGiro()
    {
        // Test sul risolutore reale (nessun sostituto): C: è un'unità fissa locale, quindi nessuna mappatura di rete e,
        // alla seconda chiamata, il risultato memorizzato per l'unità fissa è lo stesso.
        var precedente = UnitaRete.Risolutore;
        try
        {
            UnitaRete.Risolutore = precedente;
            Assert.Null(UnitaRete.Risolvi("C:"));
            Assert.Null(UnitaRete.Risolvi("C:"));
            Assert.Equal(ModalitaCondivisione.CartellaSincronizzata, ModalitaCondivisioneService.Rileva(@"C:\TestLoom"));
        }
        finally
        {
            UnitaRete.Risolutore = precedente;
        }
    }

    [Fact]
    public void Classificazione_NonBloccaOltreIlTetto_EUnaUnitaIncertaContaComeRete()
    {
        // Risolutore che non risponde (rete lenta): il thread chiamante non attende oltre il tetto, l'unita' incerta conta come
        // rete, e la lettura per l'interfaccia non attende affatto (P4).
        using var __ = new OverrideModalitaScope(null);
        var precedente = UnitaRete.Risolutore;
        var tettoPrecedente = UnitaRete.TimeoutRisoluzione;
        var sblocco = new System.Threading.ManualResetEventSlim(false);
        try
        {
            UnitaRete.Reimposta();
            UnitaRete.TimeoutRisoluzione = TimeSpan.FromMilliseconds(200);
            UnitaRete.Risolutore = _ =>
            {
                sblocco.Wait(TimeSpan.FromSeconds(30));
                return null;
            };
            var attesa = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(ModalitaCondivisione.CartellaRete, ModalitaCondivisioneService.Rileva(@"Z:\TestLoom"));
            Assert.True(attesa.Elapsed < TimeSpan.FromSeconds(5), "la classificazione ha superato il tetto di tempo");

            attesa.Restart();
            Assert.False(ModalitaCondivisioneService.ERetePerInterfaccia(@"Z:\TestLoom"));
            Assert.True(attesa.Elapsed < TimeSpan.FromSeconds(1), "l'interfaccia non deve attendere la rete");
        }
        finally
        {
            sblocco.Set();
            UnitaRete.Risolutore = precedente;
            UnitaRete.TimeoutRisoluzione = tettoPrecedente;
            UnitaRete.Reimposta();
        }
    }

    [Fact]
    public void Timeout_RetePiuAmpioMaInsiemeCentralizzatoELocaleInvariato()
    {
        using var _ = new RisolutoreUnitaFinto();
        using var __ = new OverrideModalitaScope(null);
        Assert.Equal(TimeSpan.FromSeconds(3), TimeoutCondivisione.Per(@"C:\TestLoom"));
        Assert.Equal(TimeSpan.FromSeconds(3), TimeoutCondivisione.Per(null));
        Assert.Equal(TimeSpan.FromSeconds(12), TimeoutCondivisione.Per(@"\" + @"\srv\share\TestLoom"));
        Assert.Equal(TimeSpan.FromSeconds(12), TimeoutCondivisione.PerModalita(ModalitaCondivisione.CartellaRete));
        // Decisione utente 2: 10-15 secondi per LAN/VPN.
        Assert.InRange(TimeoutCondivisione.CartellaRete.TotalSeconds, 10, 15);
    }

    [Fact]
    public void Timeout_SegueLOverrideDelPc()
    {
        using var _ = new RisolutoreUnitaFinto();
        using var __ = new OverrideModalitaScope(ModalitaCondivisioneService.ValoreRete, @"C:\TestLoom");
        Assert.Equal(TimeoutCondivisione.CartellaRete, TimeoutCondivisione.Per(@"C:\TestLoom"));
    }

    [Fact]
    public void AppConfig_SenzaLaProprietaSiCaricaComePrima()
    {
        var path = Path.Combine(TestRete.NuovaDirectoria(), "config.json");
        File.WriteAllText(path, "{\n  \"CartellaCondivisa\": \"C:\\\\TestLoom\",\n  \"Lingua\": \"it\"\n}");
        var config = new AppConfigService(path).Load();
        Assert.Null(config.ModalitaCartellaCondivisa);
        Assert.Equal(ModalitaCondivisioneService.ValoreAutomatica, ModalitaCondivisioneService.NormalizzaValore(config.ModalitaCartellaCondivisa));
        Assert.Equal("C:\\TestLoom", config.CartellaCondivisa);
    }

    [Fact]
    public void AppConfig_ModalitaSalvataERicaricataEAssenteQuandoAutomatica()
    {
        var path = Path.Combine(TestRete.NuovaDirectoria(), "config.json");
        var servizio = new AppConfigService(path);
        servizio.Save(new AppConfig { CartellaCondivisa = @"\" + @"\srv\share\TestLoom", Lingua = "it", ModalitaCartellaCondivisa = "Rete" });
        Assert.Equal("Rete", new AppConfigService(path).Load().ModalitaCartellaCondivisa);

        servizio.Save(new AppConfig { CartellaCondivisa = @"\" + @"\srv\share\TestLoom", Lingua = "it", ModalitaCartellaCondivisa = null });
        Assert.DoesNotContain("ModalitaCartellaCondivisa", File.ReadAllText(path));
    }

    [Fact]
    public void CloudFiles_NonSiApplicanoSulleCartelleDiRete()
    {
        using var _ = new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati"));
        using var __ = new OverrideModalitaScope(null);
        Assert.False(AllegatiSyncService.DeveApplicareAttributiCloudFiles(@"\" + @"\srv\share\TestLoom\allegati"));
        Assert.False(AllegatiSyncService.DeveApplicareAttributiCloudFiles(@"Z:\TestLoom\allegati"));
        Assert.True(AllegatiSyncService.DeveApplicareAttributiCloudFiles(@"C:\Users\x\OneDrive\allegati"));
    }

    [Fact]
    public void CloudFiles_OverrideReteLiSpegneAncheSuUnPercorsoLocale()
    {
        using var _ = new RisolutoreUnitaFinto();
        using var __ = new OverrideModalitaScope(ModalitaCondivisioneService.ValoreRete, @"C:\Users\x\OneDrive");
        Assert.False(AllegatiSyncService.DeveApplicareAttributiCloudFiles(@"C:\Users\x\OneDrive\allegati"));
    }

    [Fact]
    public void EseguiProcesso_TerminaIlProcessoCheSuperaIlTimeout()
    {
        var sw = Stopwatch.StartNew();
        var esito = AllegatiSyncService.EseguiProcesso("ping.exe", "-n 60 127.0.0.1", 300);
        sw.Stop();
        Assert.Equal(AllegatiSyncService.EsitoEsecuzione.Terminato, esito);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"terminazione troppo lenta: {sw.Elapsed}");
    }

    [Fact]
    public void EseguiProcesso_ProcessoCheTerminaInTempoRestituisceCompletato()
    {
        Assert.Equal(AllegatiSyncService.EsitoEsecuzione.Completato,
            AllegatiSyncService.EseguiProcesso("cmd.exe", "/c exit 0", 10000));
    }
}
