using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Matrice multi-campo (6.6.1): stato per cella, universo "Non testato" dal catalogo, zero campi su un asse,
/// gruppi comprimibili, ordine per livello, marcatura N.A. con chiavi multi-campo e compatibilità con la forma a un
/// campo per asse. Lo Stato deriva SEMPRE dall'ULTIMO esito di ogni combinazione componente×Veicolo×Funzionalità (Da eseguire
/// e N.A. non determinano lo stato); una cella che aggrega più combinazioni ne combina gli ultimi esiti.</summary>
public sealed class ReportMatriceMultiCampoTests
{
    private const char Sep = ReportMatriceService.SepLivello;

    private static TestCase CreaTest(string id, string funzionalita, string componente, string veicolo, DateTime data, Esito esito)
    {
        var tc = new TestCase { Id = id, Componente = new EcuSelection([componente]), Veicoli = [veicolo], Funzionalita = [funzionalita] };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = data,
            Condizioni = [new Condizione { Funzionalita = funzionalita, Esito = esito, Componente = new EcuSelection([componente]) }]
        });
        return tc;
    }

    private static ConfigurazioneMatriceReport ConfigStato(IReadOnlyList<string> campiX, IReadOnlyList<string> campiY) => new()
    {
        CampiX = campiX.ToList(),
        CampiY = campiY.ToList(),
        ModalitaCella = ModalitaCellaMatrice.Stato,
    };

    private static Veicolo Veicolo(string nome) => new() { Nome = nome, GammaNome = "LCV" };

    private static Funzionalita Funzione(string nome, params string[] categorie) => new() { Nome = nome, Categorie = categorie.ToList() };

    private static Componente EcuCatalogo(string nome, params string[] veicoli) => new() { Nome = nome, VeicoliAssociati = veicoli.ToList() };

    private static CellaMatriceReport Cella(RisultatoMatriceReport r, string riga, string colonna) =>
        r.Righe.Single(x => string.Equals(x.Riga, riga, StringComparison.OrdinalIgnoreCase))
            .Celle.Single(c => string.Equals(c.Colonna, colonna, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Stato_ultimoEsitoVinceSuUnNonConformePrecedente()
    {
        var tests = new[]
        {
            CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.NonConforme),
            CreaTest("T-2", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 2, 1), Esito.Conforme),
        };
        var r = ReportMatriceService.Genera(tests, ConfigStato([DimensioniMatrice.Componente], [DimensioniMatrice.Funzionalita]), [], [], []);
        Assert.Equal(StatoCellaMatrice.Conforme, Cella(r, "Lettura guasto", "BCM").CodiceStato);
    }

    [Fact]
    public void FiltroEsitoStatoCorrenteNonRiportaEsitiStoriciEMantieneIlManualeVincente()
    {
        var tests = new[]
        {
            CreaTest("T-OLD", "Storica", "BCM", "V1", new DateTime(2026, 1, 1), Esito.Conforme),
            CreaTest("T-NEW", "Storica", "BCM", "V1", new DateTime(2026, 2, 1), Esito.NonConforme),
        };
        var manuali = new[]
        {
            new StatoFunzionalitaEcuVeicolo
            {
                Funzionalita = "Manuale", Componente = "BCM", Veicolo = "V1", Esito = Esito.Conforme,
                Operatore = "Master", Nota = "corrente", DataDecisione = new DateTime(2026, 2, 1),
            }
        };
        var config = ConfigStato([DimensioniMatrice.Esito], [DimensioniMatrice.Funzionalita]);
        config.Filtri[DimensioniMatrice.Esito] = [Esito.Conforme.Label()];

        var conforme = ReportMatriceService.Genera(tests, config, [], [], [], manuali: manuali);
        Assert.Equal(["Manuale"], conforme.Righe.Select(r => r.Riga));

        config.Filtri[DimensioniMatrice.Esito] = [Esito.NonConforme.Label()];
        var nonConforme = ReportMatriceService.Genera(tests, config, [], [], [], manuali: manuali);
        Assert.Equal(["Storica"], nonConforme.Righe.Select(r => r.Riga));
    }

    [Fact]
    public void StatoCorrenteRiduceGliAssiExtraPrimaDiApplicareFiltroECalcolareTotali()
    {
        var precedente = CreaTest("T-OLD", "Storica", "BCM", "V1", new DateTime(2026, 1, 1), Esito.Conforme);
        precedente.Assegnatario = "A";
        precedente.CampiCustom["responsabile"] = "vecchio";
        var corrente = CreaTest("T-NEW", "Storica", "BCM", "V1", new DateTime(2026, 2, 1), Esito.NonConforme);
        corrente.Assegnatario = "B";
        corrente.CampiCustom["responsabile"] = "corrente";
        var config = ConfigStato([DimensioniMatrice.Componente],
            [DimensioniMatrice.Funzionalita, DimensioniMatrice.Assegnatario, DimensioniMatrice.CampoCustomPrefix + "responsabile"]);

        var senzaFiltro = ReportMatriceService.Genera([precedente, corrente], config, [], [], []);
        var rigaCorrente = string.Join(Sep, "Storica", "B", "corrente");
        Assert.Equal([rigaCorrente], senzaFiltro.Righe
            .Where(r => r.Riga.Split(Sep).Length == 3)
            .Select(r => r.Riga));
        var cellaCorrente = Cella(senzaFiltro, rigaCorrente, "BCM");
        Assert.Equal(StatoCellaMatrice.NonConforme, cellaCorrente.CodiceStato);
        Assert.Equal(1, senzaFiltro.TotaleGenerale.Stati.NonConforme);
        Assert.Equal(0, senzaFiltro.TotaleGenerale.Stati.Ok);

        config.Filtri[DimensioniMatrice.Esito] = [Esito.NonConforme.Label()];
        var filtratoNok = ReportMatriceService.Genera([precedente, corrente], config, [], [], []);
        Assert.Equal([rigaCorrente], filtratoNok.Righe.Where(r => r.Riga.Split(Sep).Length == 3).Select(r => r.Riga));
        Assert.Equal(1, filtratoNok.TotaleGenerale.Stati.NonConforme);

        config.Filtri[DimensioniMatrice.Esito] = [Esito.Conforme.Label()];
        var filtratoOk = ReportMatriceService.Genera([precedente, corrente], config, [], [], []);
        Assert.Empty(filtratoOk.Righe);
        Assert.Equal(0, filtratoOk.TotaleGenerale.Stati.Totale);
        Assert.Equal("—", filtratoOk.TotaleGenerale.TestoStato);
    }

    [Fact]
    public void ManualeCorrenteMantieneGliAssiDescrittiviDellUltimoTest()
    {
        var test = CreaTest("T-TEST", "Manuale", "BCM", "V1", new DateTime(2026, 1, 1), Esito.NonConforme);
        test.Assegnatario = "Collaudatore";
        test.CampiCustom["responsabile"] = "Reparto A";
        var manuale = new StatoFunzionalitaEcuVeicolo
        {
            Funzionalita = "Manuale", Componente = "BCM", Veicolo = "V1", Esito = Esito.Conforme,
            Operatore = "Master", Nota = "decisione corrente", DataDecisione = new DateTime(2026, 2, 1),
        };
        var config = ConfigStato([DimensioniMatrice.Componente],
            [DimensioniMatrice.Funzionalita, DimensioniMatrice.Assegnatario, DimensioniMatrice.CampoCustomPrefix + "responsabile"]);

        var risultato = ReportMatriceService.Genera([test], config, [], [], [], manuali: [manuale]);
        var chiaveRiga = string.Join(Sep, "Manuale", "Collaudatore", "Reparto A");
        var cella = Cella(risultato, chiaveRiga, "BCM");

        Assert.Equal(StatoCellaMatrice.Conforme, cella.CodiceStato);
        Assert.Equal(1, cella.Manuali);
        Assert.Contains("Master", cella.TooltipManuale);
        Assert.Contains("decisione corrente", cella.TooltipManuale);
    }

    [Fact]
    public void OrigineFiltraSulCoreCorrenteMaConservaLaCronologiaDeiCoreInclusi()
    {
        var precedente = CreaTest("T-OLD", "Storica", "BCM", "V1", new DateTime(2026, 1, 1), Esito.Conforme);
        precedente.Assegnatario = "A";
        precedente.CampiCustom["responsabile"] = "vecchio";
        var corrente = CreaTest("T-NEW", "Storica", "BCM", "V1", new DateTime(2026, 2, 1), Esito.NonConforme);
        corrente.Assegnatario = "B";
        corrente.CampiCustom["responsabile"] = "corrente";
        var config = ConfigStato([DimensioniMatrice.Componente],
            [DimensioniMatrice.Funzionalita, DimensioniMatrice.Assegnatario, DimensioniMatrice.CampoCustomPrefix + "responsabile"]);

        config.Filtri[DimensioniMatrice.Esito] = [Esito.NonConforme.Label()];
        var (righeNokLazy, _, totaleNok) = ReportMatriceService.Origine([precedente, corrente], config, [], [], [], 100);
        var righeNok = righeNokLazy.ToList();
        Assert.Equal(2, totaleNok);
        Assert.Equal(2, righeNok.Count);
        Assert.False(righeNok.Single(r => r.TestId == "T-OLD").DeterminaStato);
        Assert.True(righeNok.Single(r => r.TestId == "T-NEW").DeterminaStato);
        // L'origine proietta ogni dato sulla cella che contribuisce a determinare (coordinate del rappresentante corrente), come i Totali.
        Assert.Contains("B", righeNok.Single(r => r.TestId == "T-OLD").Riga);
        Assert.Contains("B", righeNok.Single(r => r.TestId == "T-NEW").Riga);

        config.Filtri[DimensioniMatrice.Esito] = [Esito.Conforme.Label()];
        var (righeOk, _, totaleOk) = ReportMatriceService.Origine([precedente, corrente], config, [], [], [], 100);
        Assert.Empty(righeOk);
        Assert.Equal(0, totaleOk);

        config.Filtri.Remove(DimensioniMatrice.Esito);
        config.Filtri[DimensioniMatrice.Assegnatario] = ["A"];
        var (righeStoriche, _, totaleStoriche) = ReportMatriceService.Origine([precedente, corrente], config, [], [], [], 100);
        Assert.Empty(righeStoriche);
        Assert.Equal(0, totaleStoriche);
    }

    [Fact]
    public void OpzioniEsitoStatoUsanoIlValoreVisualizzatoPerPrevisteManualiECatalogo()
    {
        var testPrevisto = new TestCase
        {
            Id = "T-PREVISTO", Componente = new EcuSelection(["BCM"]), Veicoli = ["V1"], Funzionalita = ["Prevista"]
        };
        testPrevisto.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(2026, 1, 1),
            Condizioni = [new Condizione { Funzionalita = "Prevista", Componente = new EcuSelection(["BCM"]), Esito = Esito.DaEseguire }]
        });
        var testConforme = CreaTest("T-CONFORME", "Catalogo", "BCM", "V1", new DateTime(2026, 1, 2), Esito.Conforme);
        var testNonConforme = CreaTest("T-OLD", "Storica", "BCM", "V1", new DateTime(2026, 1, 1), Esito.Conforme);
        var testNonConformeRecente = CreaTest("T-NEW", "Storica", "BCM", "V1", new DateTime(2026, 2, 1), Esito.NonConforme);
        var manuali = new[]
        {
            new StatoFunzionalitaEcuVeicolo
            {
                Funzionalita = "Manuale", Componente = "BCM", Veicolo = "V1", Esito = Esito.Conforme,
                Operatore = "Master", Nota = "corrente", DataDecisione = new DateTime(2026, 2, 1),
            }
        };
        var veicoli = new[] { Veicolo("V1"), Veicolo("V2") };
        var funzioni = new[]
        {
            Funzione("Prevista"), Funzione("Catalogo"), Funzione("Storica"), Funzione("Manuale")
        };
        var ecuCatalogo = new[] { EcuCatalogo("BCM", "V1", "V2") };
        var config = new ConfigurazioneMatriceReport
        {
            CampiX = [DimensioniMatrice.Componente],
            CampiY = [DimensioniMatrice.Funzionalita],
            ModalitaCella = ModalitaCellaMatrice.Stato,
        };

        var valori = ReportMatriceService.ValoriDisponibiliPerDimensioni(
            [DimensioniMatrice.Esito],
            [testPrevisto, testConforme, testNonConforme, testNonConformeRecente],
            veicoli, funzioni, [], config, ecuCatalogo, manuali)[DimensioniMatrice.Esito];

        Assert.Equal(
            new[] { Esito.Conforme.Label(), Esito.NonConforme.Label(), "Non testato" }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            valori);
        Assert.DoesNotContain(Esito.DaEseguire.Label(), valori);
        Assert.DoesNotContain(new StatiMatrice(0, 0, 0, 0, 0, 1).Testo, valori);
    }

    [Fact]
    public void Stato_daEseguirePiuRecenteNonSovrascriveUnOk()
    {
        var tests = new[]
        {
            CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme),
            CreaTest("T-2", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 2, 1), Esito.DaEseguire),
        };
        var r = ReportMatriceService.Genera(tests, ConfigStato([DimensioniMatrice.Componente], [DimensioniMatrice.Funzionalita]), [], [], []);
        var cella = Cella(r, "Lettura guasto", "BCM");
        Assert.Equal(StatoCellaMatrice.Conforme, cella.CodiceStato);
        Assert.Equal(1, cella.Stati.Ok);
        Assert.Equal(0, cella.Stati.DaEseguire);
    }

    [Fact]
    public void Stato_okSuVeicoloANokSuVeicoloBEMistoSenzaFiltroEConformeFiltrandoA()
    {
        var tests = new[]
        {
            CreaTest("T-A", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme),
            CreaTest("T-B", "Lettura guasto", "BCM", "Daily B", new DateTime(2026, 1, 2), Esito.NonConforme),
        };
        var config = ConfigStato([DimensioniMatrice.Componente], [DimensioniMatrice.Funzionalita]);
        var misto = Cella(ReportMatriceService.Genera(tests, config, [], [], []), "Lettura guasto", "BCM");
        Assert.Equal(StatoCellaMatrice.Misto, misto.CodiceStato);
        Assert.Equal(1, misto.Stati.Ok);
        Assert.Equal(1, misto.Stati.NonConforme);
        Assert.Contains("1 OK", misto.TestoStato);
        Assert.Contains("1 NOK", misto.TestoStato);

        var filtrata = ConfigStato([DimensioniMatrice.Componente], [DimensioniMatrice.Funzionalita]);
        filtrata.Filtri[DimensioniMatrice.Sottoveicolo] = ["Daily A"];
        var conformeA = Cella(ReportMatriceService.Genera(tests, filtrata, [], [], []), "Lettura guasto", "BCM");
        Assert.Equal(StatoCellaMatrice.Conforme, conformeA.CodiceStato);
    }

    [Fact]
    public void Stato_miscelaOkDaEseguireNonTestatoDalCatalogo()
    {
        var tests = new[]
        {
            CreaTest("T-A", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme),
            CreaTest("T-B", "Lettura guasto", "BCM", "Daily B", new DateTime(2026, 1, 2), Esito.DaEseguire),
        };
        var catalogo = new[] { EcuCatalogo("BCM", "Daily A", "Daily B", "Daily C") };
        var veicoli = new[] { Veicolo("Daily A"), Veicolo("Daily B"), Veicolo("Daily C") };
        var funzioni = new[] { Funzione("Lettura guasto", "Diagnostica") };
        var r = ReportMatriceService.Genera(tests, ConfigStato([DimensioniMatrice.Componente], [DimensioniMatrice.Funzionalita]),
            veicoli, funzioni, [], null, catalogo);
        var cella = Cella(r, "Lettura guasto", "BCM");
        Assert.Equal(1, cella.Stati.Ok);
        Assert.Equal(1, cella.Stati.DaEseguire);
        Assert.Equal(1, cella.Stati.DaDecidere); // Daily C: stessa componente con esito su Daily A, quindi "!" (propagabile)
        Assert.Equal(StatoCellaMatrice.Misto, cella.CodiceStato);
        // Le percentuali non cambiano: nessuna combinazione non testata entra nei Valutati.
        Assert.Equal(1, cella.Valutati);
    }

    [Fact]
    public void Universo_nonTestatoSoloSeCatalogoCoerenteEConFiltroVeicolo()
    {
        var catalogo = new[] { EcuCatalogo("BCM", "Daily A"), EcuCatalogo("EDC", "Daily B") };
        var veicoli = new[] { Veicolo("Daily A"), Veicolo("Daily B") };
        var funzioni = new[] { Funzione("Lettura guasto", "Diagnostica") };
        var r = ReportMatriceService.Genera([], ConfigStato([DimensioniMatrice.Componente], [DimensioniMatrice.Funzionalita]),
            veicoli, funzioni, [], null, catalogo);
        // Componenti associati solo ai rispettivi veicoli: BCM e EDC compaiono, ciascuna con la sua combinazione.
        Assert.Equal(StatoCellaMatrice.NonTestato, Cella(r, "Lettura guasto", "BCM").CodiceStato);
        Assert.Equal(StatoCellaMatrice.NonTestato, Cella(r, "Lettura guasto", "EDC").CodiceStato);

        var config = ConfigStato([DimensioniMatrice.Componente], [DimensioniMatrice.Funzionalita]);
        config.Filtri[DimensioniMatrice.Sottoveicolo] = ["Daily A"];
        var filtrata = ReportMatriceService.Genera([], config, veicoli, funzioni, [], null, catalogo);
        Assert.Equal(StatoCellaMatrice.NonTestato, Cella(filtrata, "Lettura guasto", "BCM").CodiceStato);
        Assert.DoesNotContain(filtrata.Righe.SelectMany(x => x.Celle), c => c.Colonna == "EDC" && c.Stati.Totale > 0);
    }

    [Fact]
    public void Universo_nonAttivoConDimensioneNonDiCatalogo()
    {
        var catalogo = new[] { EcuCatalogo("BCM", "Daily A") };
        var funzioni = new[] { Funzione("Lettura guasto", "Diagnostica") };
        var config = ConfigStato([DimensioniMatrice.Componente], [DimensioniMatrice.Assegnatario]);
        var r = ReportMatriceService.Genera([], config, [Veicolo("Daily A")], funzioni, [], null, catalogo);
        Assert.Empty(r.Righe);
    }

    [Fact]
    public void ZeroCampi_colonnaUnicaStatoAggregaIVeicoli()
    {
        var tests = new[]
        {
            CreaTest("T-A", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme),
            CreaTest("T-B", "Lettura guasto", "BCM", "Daily B", new DateTime(2026, 1, 2), Esito.NonConforme),
            CreaTest("T-C", "Programmazione", "EDC", "Daily A", new DateTime(2026, 1, 3), Esito.Conforme),
        };
        // Righe = Funzionalità › componente, nessuna colonna: una sola colonna "Stato".
        var config = ConfigStato([], [DimensioniMatrice.Funzionalita, DimensioniMatrice.Componente]);
        var r = ReportMatriceService.Genera(tests, config, [], [], []);
        Assert.Equal(new[] { "" }, r.Colonne);
        Assert.Empty(r.FasceColonne);
        Assert.Equal(StatoCellaMatrice.Misto, Cella(r, "Lettura guasto" + Sep + "BCM", "").CodiceStato);
        Assert.Equal(StatoCellaMatrice.Conforme, Cella(r, "Programmazione" + Sep + "EDC", "").CodiceStato);
        // La colonna unica coincide col totale di riga (stessa combinazione distinta, nessuna doppia contabilizzazione).
        var riga = r.Righe.Single(x => x.Riga == "Lettura guasto" + Sep + "BCM");
        Assert.Equal(riga.TotaleRiga.Testo, Cella(r, riga.Riga, "").Testo);
    }

    [Fact]
    public void PercentualiInvarianti_fraModalitaPercentualeEStato()
    {
        var tests = new[]
        {
            CreaTest("T-A", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme),
            CreaTest("T-B", "Lettura guasto", "BCM", "Daily B", new DateTime(2026, 1, 2), Esito.NonConforme),
            CreaTest("T-C", "Programmazione", "BCM", "Daily A", new DateTime(2026, 1, 3), Esito.DaEseguire),
        };
        var percentuale = new ConfigurazioneMatriceReport { CampiX = [DimensioniMatrice.Componente], CampiY = [DimensioniMatrice.Funzionalita] };
        var stato = ConfigStato([DimensioniMatrice.Componente], [DimensioniMatrice.Funzionalita]);
        var p = ReportMatriceService.Genera(tests, percentuale, [], [], []);
        var s = ReportMatriceService.Genera(tests, stato, [], [], []);
        Assert.Equal(p.TotaleGenerale.Testo, s.TotaleGenerale.Testo);
        Assert.Equal(p.TotaliColonna.Select(t => t.Testo), s.TotaliColonna.Select(t => t.Testo));
        Assert.Equal(p.Righe.Select(x => x.TotaleRiga.Testo), s.Righe.Select(x => x.TotaleRiga.Testo));
        Assert.Equal(p.Righe.SelectMany(x => x.Celle).Select(c => c.Testo), s.Righe.SelectMany(x => x.Celle).Select(c => c.Testo));
    }

    [Fact]
    public void Gruppo_compressoMostraLaRigaAggregataDelGruppo()
    {
        var tests = new[]
        {
            CreaTest("T-A", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme),
            CreaTest("T-B", "Lettura guasto", "EDC", "Daily A", new DateTime(2026, 1, 2), Esito.NonConforme),
            CreaTest("T-C", "Lettura guasto", "BCM", "Daily B", new DateTime(2026, 1, 3), Esito.Conforme),
        };
        var config = ConfigStato([DimensioniMatrice.Funzionalita], [DimensioniMatrice.Sottoveicolo, DimensioniMatrice.Componente]);
        var espansa = ReportMatriceService.GeneraFinestra(tests, config, [], [], [], 0, 50, 0, 12);
        Assert.Equal(3, espansa.TotaleRighe);
        // Gruppo espanso: le righe sono le foglie (Daily A › BCM, Daily A › EDC, Daily B › BCM).

        config.GruppiCompressi.Add(ReportMatriceService.ChiaveCompressione(true, "Daily A"));
        var finestra = ReportMatriceService.GeneraFinestra(tests, config, [], [], [], 0, 50, 0, 12);
        Assert.Equal(2, finestra.TotaleRighe);
        var aggregata = finestra.Risultato.Righe.Single(x => x.Compressa);
        Assert.Equal("Daily A", aggregata.Riga);
        Assert.True(aggregata.Aggregata);
        // Gruppo "Daily A": BCM Conforme + EDC NonConforme => Misto, senza marcatura N.A. sul gruppo.
        var cella = aggregata.Celle.Single();
        Assert.Equal(StatoCellaMatrice.Misto, cella.CodiceStato);
        Assert.Equal(2, cella.Stati.Totale);
        Assert.False(cella.Marcabile);
        // Fascia del livello esterno: un solo segmento "Daily A" di span 1 (gruppo compresso) e uno per "Daily B".
        var fascia = finestra.Risultato.FasceRighe[0];
        Assert.Equal(new[] { "Daily A", "Daily B" }, fascia.Select(f => f.Etichetta));
        Assert.True(fascia[0].Compressa);
    }

    [Fact]
    public void Ordine_personaleApplicatoAOgniLivello()
    {
        var tests = new[]
        {
            CreaTest("T-A", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme),
            CreaTest("T-B", "Lettura guasto", "EDC", "Daily A", new DateTime(2026, 1, 2), Esito.Conforme),
        };
        var config = ConfigStato([DimensioniMatrice.Funzionalita], [DimensioniMatrice.Sottoveicolo, DimensioniMatrice.Componente]);
        config.Ordini[DimensioniMatrice.Componente] = ["EDC", "BCM"];
        var r = ReportMatriceService.Genera(tests, config, [], [], []);
        Assert.Equal(new[] { "Daily A" + Sep + "EDC", "Daily A" + Sep + "BCM" }, r.Righe.Select(x => x.Riga));
    }

    [Fact]
    public void NA_chiaveMultiCampoEValoriComposti()
    {
        var config = new AppConfig();
        var campiX = new[] { DimensioniMatrice.Componente, DimensioniMatrice.Sottoveicolo };
        var campiY = new[] { DimensioniMatrice.Funzionalita };
        // Un campo per asse: stessa chiave di prima (le marcature esistenti restano valide).
        Assert.Equal(MatriceNonApplicabileService.Chiave(DimensioniMatrice.Componente, DimensioniMatrice.Funzionalita),
            MatriceNonApplicabileService.ChiaveAssi([DimensioniMatrice.Componente], [DimensioniMatrice.Funzionalita]));
        Assert.NotEqual(MatriceNonApplicabileService.ChiaveAssi([DimensioniMatrice.Componente], [DimensioniMatrice.Funzionalita]),
            MatriceNonApplicabileService.ChiaveAssi(campiX, campiY));

        var tests = new[] { CreaTest("T-A", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme) };
        var configurazione = new ConfigurazioneMatriceReport { CampiX = campiX.ToList(), CampiY = campiY.ToList() };
        var r = ReportMatriceService.Genera(tests, configurazione, [], [], []);
        var cella = r.Righe.Single().Celle.Single();
        Assert.True(MatriceNonApplicabileService.Imposta(config, campiX, campiY, cella.Colonna, cella.Riga, "prova"));
        MatriceNonApplicabileService.Applica(config, campiX, campiY, r);
        Assert.True(r.Righe.Single().Celle.Single().NonApplicabile);
        Assert.Equal("N.A.", r.Righe.Single().Celle.Single().TestoVisualizzato);
        Assert.Empty(MatriceNonApplicabileService.Leggi(config, [DimensioniMatrice.Componente], [DimensioniMatrice.Funzionalita]));
    }

    [Fact]
    public void Compatibilita_configurazioneAUnCampoPerAsse()
    {
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.CategoriaFunzionalita };
        Assert.Equal(new[] { DimensioniMatrice.Componente }, config.CampiX);
        Assert.Equal(new[] { DimensioniMatrice.CategoriaFunzionalita }, config.CampiY);
        Assert.Equal(DimensioniMatrice.Componente, config.AsseX);
        Assert.Equal(ModalitaCellaMatrice.Percentuale, config.ModalitaCella);
        var vuoto = new ConfigurazioneMatriceReport { AsseX = "" };
        Assert.Empty(vuoto.CampiX);
    }
}
