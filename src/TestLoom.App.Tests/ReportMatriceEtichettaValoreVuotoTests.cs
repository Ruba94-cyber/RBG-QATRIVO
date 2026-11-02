using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Piano 6.4.0 punto 5(3), richiesta coordinatore: la riga/colonna della matrice Report per un asse
/// senza valore (es. Test senza componente sull'asse componente) mostrava solo "—", incomprensibile (stesso problema già
/// risolto sull'avviso Home). Verifica: l'etichetta è esplicita e coerente con l'avviso Home per l'asse componente,
/// generica ma esplicita per gli altri assi; i conteggi restano identici a prima (solo l'etichetta della
/// chiave cambia); l'overlay N.A. (piano 6.4.0 punto 4, di competenza di un altro sviluppatore ma qui solo
/// verificato, non modificato) continua a funzionare sulla nuova etichetta.</summary>
public sealed class ReportMatriceEtichettaValoreVuotoTests
{
    [Fact]
    public void AsseEcu_TestSenzaEcu_UsaLEtichettaCoerenteConLAvvisoHome()
    {
        var tests = new[]
        {
            new TestCase { Id = "A", Componente = new EcuSelection(["BCM"]) },
            new TestCase { Id = "B", Componente = new EcuSelection() }
        };

        var result = ReportMatriceService.Genera(tests,
            new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Assegnatario }, [], [], []);

        Assert.Contains("Test senza componente (da correggere)", result.Colonne);
        Assert.DoesNotContain("—", result.Colonne);
    }

    [Fact]
    public void AsseDiverso_ValoreVuoto_UsaUnEtichettaGenericaMaEsplicita()
    {
        var tests = new[] { new TestCase { Id = "A", Componente = new EcuSelection(["BCM"]) } }; // nessun Assegnatario

        var result = ReportMatriceService.Genera(tests,
            new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Assegnatario }, [], [], []);

        var riga = Assert.Single(result.Righe);
        Assert.Equal("(nessun valore)", riga.Riga);
        Assert.NotEqual("—", riga.Riga);
    }

    /// <summary>Decisione utente 6.4.0: l'unità è la combinazione funzionalità×componente×veicolo, contata una sola
    /// volta. B e C sono la STESSA combinazione (nessuna funzionalità/componente/veicolo dichiarati, nessun
    /// Assegnatario) e collassano in una: Totali=1, non più 2 come nel vecchio conteggio per-Test.</summary>
    [Fact]
    public void CombinazioniIdentiche_DaTestDiversiCollassanoInUnaSola()
    {
        var tests = new[]
        {
            new TestCase { Id = "A", Componente = new EcuSelection(["BCM"]) },
            new TestCase { Id = "B", Componente = new EcuSelection() },
            new TestCase { Id = "C", Componente = new EcuSelection() }
        };

        var result = ReportMatriceService.Genera(tests,
            new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Assegnatario }, [], [], []);

        var riga = Assert.Single(result.Righe);
        var cellaSenzaEcu = riga.Celle.Single(c => c.Colonna == "Test senza componente (da correggere)");
        Assert.Equal(1, cellaSenzaEcu.Totali);
        var cellaBcm = riga.Celle.Single(c => c.Colonna == "BCM");
        Assert.Equal(1, cellaBcm.Totali);
    }

    [Fact]
    public void OverlayNonApplicabile_FunzionaSullaRigaConLaNuovaEtichetta()
    {
        var tests = new[]
        {
            new TestCase { Id = "A", Componente = new EcuSelection(["BCM"]), Assegnatario = "Mario",
                Esecuzioni = new List<Esecuzione> { new() { Esito = Esito.Conforme } } },
            new TestCase { Id = "B", Componente = new EcuSelection(), Assegnatario = "Mario",
                Esecuzioni = new List<Esecuzione> { new() { Esito = Esito.Conforme } } }
        };
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Assegnatario };
        var result = ReportMatriceService.Genera(tests, config, [], [], []);

        var appConfig = new AppConfig();
        var impostata = MatriceNonApplicabileService.Imposta(appConfig, config.AsseX, config.AsseY,
            "Test senza componente (da correggere)", "Mario", nota: "da correggere manualmente");
        Assert.True(impostata);

        MatriceNonApplicabileService.Applica(appConfig, config.AsseX, config.AsseY, result);

        var riga = result.Righe.Single(r => r.Riga == "Mario");
        var cellaSenzaEcu = riga.Celle.Single(c => c.Colonna == "Test senza componente (da correggere)");
        Assert.True(cellaSenzaEcu.NonApplicabile);
        Assert.Null(cellaSenzaEcu.Percentuale);
        Assert.Equal("N.A.", cellaSenzaEcu.Testo);
        // La cella BCM non è marcata: resta calcolata normalmente, a riprova che l'overlay non altera nulla
        // fuori dalla cella marcata.
        var cellaBcm = riga.Celle.Single(c => c.Colonna == "BCM");
        Assert.False(cellaBcm.NonApplicabile);
    }
}
