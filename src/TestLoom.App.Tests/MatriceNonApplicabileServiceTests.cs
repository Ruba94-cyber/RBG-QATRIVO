using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Copertura della marcatura manuale "N.A." per cella della matrice Report (decisione utente piano
/// 6.4.0, punto 4): esclusione dalle percentuali, persistenza round-trip su config.json, chiave stabile per
/// VALORE degli assi (non per indice/posizione) e comportamento "dormiente" quando un valore d'asse sparisce.</summary>
public class MatriceNonApplicabileServiceTests
{
    private static RisultatoMatriceReport CreaRisultato() => new()
    {
        Colonne = ["BCM", "EDC"],
        Righe =
        [
            new RigaMatriceReport
            {
                Riga = "Diag. base",
                Celle =
                [
                    new CellaMatriceReport { Riga = "Diag. base", Colonna = "BCM", Totali = 10, Valutati = 10, Conformi = 8 },
                    new CellaMatriceReport { Riga = "Diag. base", Colonna = "EDC", Totali = 4, Valutati = 4, Conformi = 4 }
                ]
            },
            new RigaMatriceReport
            {
                Riga = "Programmazione",
                Celle =
                [
                    new CellaMatriceReport { Riga = "Programmazione", Colonna = "BCM", Totali = 2, Valutati = 2, Conformi = 0 },
                    new CellaMatriceReport { Riga = "Programmazione", Colonna = "EDC", Totali = 0, Valutati = 0, Conformi = 0 }
                ]
            }
        ]
    };

    [Fact]
    public void Applica_CellaMarcata_EEsclusaDallaPercentualeEMostraNA()
    {
        var risultato = CreaRisultato();
        var marcature = new List<CellaNonApplicabile> { new() { ValoreAsseX = "BCM", ValoreAsseY = "Diag. base", Nota = "Non montato su questo veicolo" } };

        MatriceNonApplicabileService.Applica(risultato, marcature);

        var cella = risultato.Righe[0].Celle[0];
        Assert.True(cella.NonApplicabile);
        Assert.Null(cella.Percentuale);
        Assert.Equal("N.A.", cella.Testo);
        Assert.Equal("Non montato su questo veicolo", cella.NotaNonApplicabile);

        // Le altre celle non toccate dalla marcatura restano invariate (percentuale calcolata normalmente).
        var altraCella = risultato.Righe[0].Celle[1];
        Assert.False(altraCella.NonApplicabile);
        Assert.Equal(100d, altraCella.Percentuale);
    }

    [Fact]
    public void Applica_NessunaMarcatura_NonAlteraIlRisultato()
    {
        var risultato = CreaRisultato();
        MatriceNonApplicabileService.Applica(risultato, Array.Empty<CellaNonApplicabile>());

        Assert.All(risultato.Righe.SelectMany(r => r.Celle), c => Assert.False(c.NonApplicabile));
    }

    [Fact]
    public void ImpostaERimuovi_RoundTripSuAppConfig_ConChiaveStabilePerCombinazioneAssi()
    {
        var config = new AppConfig();

        var scritta = MatriceNonApplicabileService.Imposta(config, DimensioniMatrice.Componente, DimensioniMatrice.CategoriaFunzionalita,
            "BCM", "Diag. base", "nota di prova");
        Assert.True(scritta);

        var lette = MatriceNonApplicabileService.Leggi(config, DimensioniMatrice.Componente, DimensioniMatrice.CategoriaFunzionalita);
        Assert.Single(lette);
        Assert.Equal("BCM", lette[0].ValoreAsseX);
        Assert.Equal("Diag. base", lette[0].ValoreAsseY);
        Assert.Equal("nota di prova", lette[0].Nota);
        Assert.True(MatriceNonApplicabileService.EImpostata(config, DimensioniMatrice.Componente, DimensioniMatrice.CategoriaFunzionalita, "bcm", "diag. base"));

        // Stessa scrittura (stessa nota): nessun aggiornamento necessario, coerente con ViewStateService.Scrivi.
        Assert.False(MatriceNonApplicabileService.Imposta(config, DimensioniMatrice.Componente, DimensioniMatrice.CategoriaFunzionalita, "BCM", "Diag. base", "nota di prova"));

        var rimossa = MatriceNonApplicabileService.Rimuovi(config, DimensioniMatrice.Componente, DimensioniMatrice.CategoriaFunzionalita, "BCM", "Diag. base");
        Assert.True(rimossa);
        Assert.Empty(MatriceNonApplicabileService.Leggi(config, DimensioniMatrice.Componente, DimensioniMatrice.CategoriaFunzionalita));

        // Rimuovere di nuovo (gia' assente) non deve lanciare ne' segnalare una scrittura.
        Assert.False(MatriceNonApplicabileService.Rimuovi(config, DimensioniMatrice.Componente, DimensioniMatrice.CategoriaFunzionalita, "BCM", "Diag. base"));
    }

    /// <summary>Fix richiesto in review: la chiave deve includere le DIMENSIONI degli assi, non solo i valori
    /// delle celle — altrimenti una N.A. marcata su Componente×CategoriaFunzionalita potrebbe riapparire su una
    /// matrice con assi completamente diversi (es. Veicolo×Funzionalita) o sugli stessi due assi scambiati, se
    /// per coincidenza i VALORI di riga/colonna coincidono (es. una Funzionalita e una CategoriaFunzionalita
    /// che si chiamano entrambe "Diag. base").</summary>
    [Fact]
    public void Chiave_DistingueCombinazioniDiAssiDiverse_LaNAonRiappareConAssiDiversiONonScambiati()
    {
        var config = new AppConfig();
        MatriceNonApplicabileService.Imposta(config, DimensioniMatrice.Componente, DimensioniMatrice.CategoriaFunzionalita, "BCM", "Diag. base", null);

        // Stessi VALORI di cella ("BCM","Diag. base"), ma assi completamente diversi: nessuna marcatura letta.
        Assert.Empty(MatriceNonApplicabileService.Leggi(config, DimensioniMatrice.Sottoveicolo, DimensioniMatrice.Funzionalita));

        // Stessi DUE assi, ma scambiati (Categoria×Componente invece di Componente×Categoria): chiave diversa per
        // costruzione, quindi nessuna marcatura letta neppure qui.
        Assert.Empty(MatriceNonApplicabileService.Leggi(config, DimensioniMatrice.CategoriaFunzionalita, DimensioniMatrice.Componente));

        // La combinazione originale, invece, la trova ancora.
        Assert.Single(MatriceNonApplicabileService.Leggi(config, DimensioniMatrice.Componente, DimensioniMatrice.CategoriaFunzionalita));

        // Applicato a un risultato con gli stessi VALORI ma costruito per assi Veicolo×Funzionalita: la cella
        // non deve risultare marcata (il "riga"/"colonna" qui sono valori di dimensioni diverse da quelle
        // della marcatura, anche se le stringhe coincidono per coincidenza).
        var risultatoAltriAssi = new RisultatoMatriceReport
        {
            Colonne = ["BCM"],
            Righe = [new RigaMatriceReport { Riga = "Diag. base", Celle = [new CellaMatriceReport { Riga = "Diag. base", Colonna = "BCM", Totali = 1, Valutati = 1, Conformi = 1 }] }]
        };
        MatriceNonApplicabileService.Applica(risultatoAltriAssi,
            MatriceNonApplicabileService.Leggi(config, DimensioniMatrice.Sottoveicolo, DimensioniMatrice.Funzionalita));
        Assert.False(risultatoAltriAssi.Righe[0].Celle[0].NonApplicabile);
    }

    /// <summary>Robustezza richiesta in review: la funzione è nuova (nessuna marcatura in produzione oggi),
    /// quindi non serve una vera migrazione — ma il codice non deve comunque rompersi se
    /// AppConfig.MatriceNonApplicabili contiene chiavi non nel formato atteso (scritte a mano, da una build
    /// futura con un formato diverso, o corrotte). Leggi() usa un semplice TryGetValue sulla chiave costruita
    /// da Chiave(): una chiave "vecchia"/estranea semplicemente non fa match, non lancia.</summary>
    [Fact]
    public void Leggi_ConChiaviEstraneeNelDizionario_NonLanciaERestituisceVuoto()
    {
        var config = new AppConfig
        {
            MatriceNonApplicabili = new Dictionary<string, List<CellaNonApplicabile>>(StringComparer.OrdinalIgnoreCase)
            {
                ["componente|categoria-funzionalita"] = new() { new() { ValoreAsseX = "BCM", ValoreAsseY = "Diag. base" } }, // vecchio formato ipotetico con "|"
                ["formato-sconosciuto"] = new() { new() { ValoreAsseX = "X", ValoreAsseY = "Y" } },
            }
        };

        var eccezione = Record.Exception(() => MatriceNonApplicabileService.Leggi(config, DimensioniMatrice.Componente, DimensioniMatrice.CategoriaFunzionalita));
        Assert.Null(eccezione);
        Assert.Empty(MatriceNonApplicabileService.Leggi(config, DimensioniMatrice.Componente, DimensioniMatrice.CategoriaFunzionalita));
    }

    [Fact]
    public void Persistenza_SopravviveAUnRicaricamentoDiConfigJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_MatriceNA_" + Guid.NewGuid().ToString("N"));
        var percorso = Path.Combine(dir, "config.json");
        try
        {
            var servizioScrittura = new AppConfigService(percorso);
            var config = servizioScrittura.Load();
            MatriceNonApplicabileService.Imposta(config, DimensioniMatrice.Componente, DimensioniMatrice.CategoriaFunzionalita, "BCM", "Diag. base", "nota persistita");
            servizioScrittura.Save(config);

            // Simula la riapertura dell'app: nuova istanza del servizio, nuova lettura da disco.
            var servizioLettura = new AppConfigService(percorso);
            var configRicaricata = servizioLettura.Load();

            var marcature = MatriceNonApplicabileService.Leggi(configRicaricata, DimensioniMatrice.Componente, DimensioniMatrice.CategoriaFunzionalita);
            Assert.Single(marcature);
            Assert.Equal("nota persistita", marcature[0].Nota);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ChiaveCella_EStabilePerValoreNonPerPosizione_ESopravviveARiordini()
    {
        var marcature = new List<CellaNonApplicabile> { new() { ValoreAsseX = "EDC", ValoreAsseY = "Programmazione" } };

        // Prima "vista": ordine A.
        var risultato1 = CreaRisultato();
        MatriceNonApplicabileService.Applica(risultato1, marcature);
        Assert.True(risultato1.Righe.Single(r => r.Riga == "Programmazione").Celle.Single(c => c.Colonna == "EDC").NonApplicabile);

        // Stessa combinazione di valori ma righe/colonne costruite in ordine diverso (simula un riordino o un
        // cambio di pagina della finestra): la marcatura deve continuare a colpire la STESSA cella logica.
        var risultato2 = new RisultatoMatriceReport
        {
            Colonne = ["EDC", "BCM"],
            Righe =
            [
                new RigaMatriceReport { Riga = "Programmazione", Celle = [
                    new CellaMatriceReport { Riga = "Programmazione", Colonna = "EDC", Totali = 1, Valutati = 1, Conformi = 1 },
                    new CellaMatriceReport { Riga = "Programmazione", Colonna = "BCM", Totali = 1, Valutati = 1, Conformi = 1 } ] },
                new RigaMatriceReport { Riga = "Diag. base", Celle = [
                    new CellaMatriceReport { Riga = "Diag. base", Colonna = "EDC", Totali = 1, Valutati = 1, Conformi = 1 },
                    new CellaMatriceReport { Riga = "Diag. base", Colonna = "BCM", Totali = 1, Valutati = 1, Conformi = 1 } ] }
            ]
        };
        MatriceNonApplicabileService.Applica(risultato2, marcature);
        Assert.True(risultato2.Righe.Single(r => r.Riga == "Programmazione").Celle.Single(c => c.Colonna == "EDC").NonApplicabile);
        Assert.False(risultato2.Righe.Single(r => r.Riga == "Diag. base").Celle.Single(c => c.Colonna == "EDC").NonApplicabile);
    }

    [Fact]
    public void ValoreAsseSparito_LaMarcaturaRestaDormienteSenzaErrori_ERiattivaSeIlValoreRicompare()
    {
        var marcature = new List<CellaNonApplicabile> { new() { ValoreAsseX = "TCU", ValoreAsseY = "Diag. base" } };

        // "TCU" non e' fra le colonne del risultato corrente (es. filtrata, o componente rinominata): nessuna
        // eccezione, la marcatura semplicemente non colpisce nulla.
        var risultato = CreaRisultato();
        var eccezione = Record.Exception(() => MatriceNonApplicabileService.Applica(risultato, marcature));
        Assert.Null(eccezione);
        Assert.All(risultato.Righe.SelectMany(r => r.Celle), c => Assert.False(c.NonApplicabile));

        // Il valore ricompare (es. filtro rimosso): la marcatura torna a colpire la cella corrispondente senza
        // che nessuno debba reimpostarla.
        var risultatoConTcu = new RisultatoMatriceReport
        {
            Colonne = ["TCU"],
            Righe = [ new RigaMatriceReport { Riga = "Diag. base", Celle = [ new CellaMatriceReport { Riga = "Diag. base", Colonna = "TCU", Totali = 3, Valutati = 3, Conformi = 1 } ] } ]
        };
        MatriceNonApplicabileService.Applica(risultatoConTcu, marcature);
        Assert.True(risultatoConTcu.Righe[0].Celle[0].NonApplicabile);
    }
}
