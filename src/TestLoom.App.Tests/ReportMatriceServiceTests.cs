using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class ReportMatriceServiceTests
{
    [Fact]
    public void StatiNascostiPresentazione_NascondeSoloLeVociVisibiliEConservaConteggiEDenominatore()
    {
        var stati = new StatiMatrice(2, 1, 1, 0, 2, 0);
        var nascosti = new HashSet<StatoCellaMatrice> { StatoCellaMatrice.NonTestato, StatoCellaMatrice.ConformeConDeviazioni };
        var cella = new CellaMatriceReport
        {
            Stati = stati,
            StatiPercentuale = stati,
            ModalitaStato = false,
            StatiNascostiPresentazione = nascosti,
        };

        Assert.Equal(6, cella.Stati.Totale);
        Assert.Equal(6, cella.StatiPercentuale.Totale);
        Assert.Contains("OK", cella.TestoVisualizzato);
        Assert.DoesNotContain("DEV", cella.TestoVisualizzato);
        Assert.DoesNotContain("NT", cella.TestoVisualizzato);
        Assert.Contains("33", cella.TestoVisualizzato);

        var stato = new CellaMatriceReport
        {
            Stati = stati,
            ModalitaStato = true,
            StatiNascostiPresentazione = nascosti,
        };
        Assert.Contains("OK", stato.TestoVisualizzato);
        Assert.DoesNotContain("DEV", stato.TestoVisualizzato);
    }

    [Fact]
    public void StatiNascostiPresentazione_ApplicaMascheraInStatoStatoPercentualeETestPercentualeSenzaRicalcolare()
    {
        var nascosti = new HashSet<StatoCellaMatrice> { StatoCellaMatrice.NonConforme, StatoCellaMatrice.DaEseguire };

        foreach (var modalita in new[] { ModalitaCellaMatrice.Stato, ModalitaCellaMatrice.Percentuale, ModalitaCellaMatrice.TestPercentuale })
        {
            var configurazione = new ConfigurazioneMatriceReport
            {
                AsseX = DimensioniMatrice.Componente,
                AsseY = DimensioniMatrice.Funzionalita,
                ModalitaCella = modalita,
            };
            var senzaMaschera = ReportMatriceService.Genera([CreaTest()], configurazione, Veicoli, Funzioni, []);
            configurazione.StatiNascostiPresentazione = nascosti;
            var conMaschera = ReportMatriceService.Genera([CreaTest()], configurazione, Veicoli, Funzioni, []);
            var celleOriginali = senzaMaschera.Righe.Single(r => r.Riga == "Programma componente").Celle;
            var celleVisibili = conMaschera.Righe.Single(r => r.Riga == "Programma componente").Celle;
            Assert.Equal(celleOriginali.Count, celleVisibili.Count);
            for (var i = 0; i < celleVisibili.Count; i++)
            {
                var original = celleOriginali[i];
                var visibile = celleVisibili[i];
                Assert.Equal(original.Stati, visibile.Stati);
                Assert.Equal(original.StatiPercentuale, visibile.StatiPercentuale);
                Assert.Equal(original.Totali, visibile.Totali);
                Assert.Equal(original.Valutati, visibile.Valutati);
                Assert.Equal(original.Conformi, visibile.Conformi);
                Assert.DoesNotContain(LocalizationService.Instance.Traduci("MatriceStatoAbbrNok"), visibile.TestoVisualizzato);
                if (modalita == ModalitaCellaMatrice.Stato)
                    Assert.Equal("", visibile.TestoVisualizzato);
                Assert.DoesNotContain(LocalizationService.Instance.Traduci("MatriceStatoAbbrDa"), visibile.TestoVisualizzato);
                if (modalita == ModalitaCellaMatrice.TestPercentuale && original.Totali > 0)
                    Assert.Contains($"({original.Totali} test)", visibile.TestoVisualizzato);
            }
        }
    }

    [Fact]
    public void StatiNascostiPresentazione_PermetteDiLasciareSoloOkConLaPercentualeSulTotaleOriginale()
    {
        var stati = new StatiMatrice(1, 1, 1, 1, 1, 1);
        var nascosti = new HashSet<StatoCellaMatrice>
        {
            StatoCellaMatrice.NonConforme,
            StatoCellaMatrice.ConformeConDeviazioni,
            StatoCellaMatrice.DaEseguire,
            StatoCellaMatrice.NonTestato,
            StatoCellaMatrice.DaDecidere,
        };
        var cella = new CellaMatriceReport
        {
            StatiPercentuale = stati,
            StatiNascostiPresentazione = nascosti,
        };

        Assert.Equal(6, cella.StatiPercentuale.Totale);
        Assert.Contains("OK", cella.TestoVisualizzato);
        Assert.Contains("16", cella.TestoVisualizzato);
        Assert.Single(cella.TestoVisualizzato.Split(" · ", StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain("NOK", cella.TestoVisualizzato);
        Assert.DoesNotContain("DEV", cella.TestoVisualizzato);
        Assert.DoesNotContain("NT", cella.TestoVisualizzato);

        var tuttiNascosti = Enum.GetValues<StatoCellaMatrice>().Where(stato => stato is not (StatoCellaMatrice.Vuota or StatoCellaMatrice.Misto)).ToHashSet();
        Assert.Equal(6, stati.Totale);
        Assert.Equal("", stati.RiepilogoVisibile(tuttiNascosti));
        Assert.Equal("", stati.PercentualiVisibili(tuttiNascosti));
    }

    [Fact]
    public void MatricePreparata_AggiornaSoloLaMascheraVisualeSenzaRicalcolareGliAggregati()
    {
        var config = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Componente,
            AsseY = DimensioniMatrice.Funzionalita,
        };
        var preparata = ReportMatriceService.Prepara([CreaTest()], config, Veicoli, Funzioni, []);
        var prima = preparata.Finestra(0, 50, 0, 50).Risultato.Righe
            .Single(r => r.Riga == "Programma componente").Celle.Single(c => c.Colonna == "BCM");
        var statiOriginali = prima.Stati;
        var percentualiOriginali = prima.StatiPercentuale;
        Assert.Contains("NOK", prima.TestoVisualizzato);

        preparata.ImpostaStatiNascostiPresentazione(new HashSet<StatoCellaMatrice>
        {
            StatoCellaMatrice.NonConforme,
        });
        var dopo = preparata.Finestra(0, 50, 0, 50).Risultato.Righe
            .Single(r => r.Riga == "Programma componente").Celle.Single(c => c.Colonna == "BCM");

        Assert.DoesNotContain("NOK", dopo.TestoVisualizzato);
        Assert.Equal(statiOriginali, dopo.Stati);
        Assert.Equal(percentualiOriginali, dopo.StatiPercentuale);
        Assert.Equal(2, dopo.Stati.Totale);
        Assert.Equal(2, dopo.StatiPercentuale.Totale);
    }

    private static readonly List<Veicolo> Veicoli =
    [
        new() { Nome = "Daily A", GammaNome = "LCV" },
        new() { Nome = "Daily B", GammaNome = "LCV" }
    ];

    private static readonly List<Funzionalita> Funzioni =
    [
        new() { Nome = "Lettura guasto", Categorie = ["Diag. base"] },
        new() { Nome = "Programma componente", Categorie = ["Programmazione"] }
    ];

    [Fact]
    public void FiltroGamma_SiApplicaAncheQuandoGammaNonEUnAsse()
    {
        var altroGamma = new TestCase
        {
            Id = "T-ALTRO-GAMMA",
            Componente = new EcuSelection(["BCM", "EDC"]),
            Veicoli = ["Daily C"],
            Funzionalita = ["Lettura guasto", "Programma componente"]
        };
        altroGamma.Esecuzioni.Add(new Esecuzione
        {
            Data = DateTime.Now,
            Condizioni =
            [
                new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.Conforme, Componente = new EcuSelection(["BCM", "EDC"]) },
                new Condizione { Funzionalita = "Programma componente", Esito = Esito.NonConforme, Componente = new EcuSelection(["BCM", "EDC"]) }
            ]
        });
        var veicoli = Veicoli.Append(new Veicolo { Nome = "Daily C", GammaNome = "LCV Diesel" }).ToList();
        var configurazione = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Componente,
            AsseY = DimensioniMatrice.Funzionalita,
            Filtri = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [DimensioniMatrice.Gamma] = ["LCV"]
            }
        };

        var risultato = ReportMatriceService.Genera([CreaTest(), altroGamma], configurazione, veicoli, Funzioni, []);

        // CreaTest ricade in due veicoli LCV (2 componente x 2 funzionalità x 2 veicoli); il Test aggiuntivo
        // aggiunge quattro combinazioni LCV Diesel, che vanno escluse anche se Gamma non è un asse.
        Assert.Equal(8, risultato.TotaleGenerale.StatiPercentuale.Totale);
    }

    /// <summary>Decisione utente 6.4.0: l'unità della matrice è la combinazione funzionalità×componente×veicolo, non
    /// più il Test. Senza funzionalità/veicolo dichiarati, A e B (stessa componente "BCM"/"bcm" case-insensitive)
    /// ricadono nella STESSA combinazione e contano una volta sola nei Totali — a differenza del vecchio
    /// modello per-operazione, dove ogni Test pesava per conto proprio (Totali 2). Nessuna delle due Esecuzioni
    /// esiste: la combinazione è "non valutata" (Valutati 0).</summary>
    [Fact]
    public void MatriceRaggruppata_UnisceMaiuscoleEMinuscoleEDedupllicaLaStessaCombinazione()
    {
        var tests = new[]
        {
            new TestCase { Id = "A", Componente = "BCM", Assegnatario = "Mario" },
            new TestCase { Id = "B", Componente = "bcm", Assegnatario = "mario" },
            new TestCase { Id = "C", Componente = "EDC", Assegnatario = "Luigi" },
            new TestCase { Id = "D", Componente = "HIDDEN", Assegnatario = "Deleted", Eliminato = true }
        };
        var result = ReportMatriceService.Genera(tests,
            new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Assegnatario }, [], [], []);
        Assert.Equal(new[] { "BCM", "EDC" }, result.Colonne);
        Assert.Equal(2, result.Righe.Count);
        // Riga unica di A e B (grafia "Mario"/"mario" unita). Nessuna esecuzione e nessuna data: il rappresentante è scelto
        // dall'Id del Test in ordine ordinale ("B" > "A"), quindi la riga mostra la grafia di B, "mario", in modo deterministico.
        var mario = result.Righe.Single(r => r.Riga == "mario");
        Assert.Equal(1, mario.Celle[0].Totali);
        Assert.Equal(0, mario.Celle[0].Valutati);
        Assert.Equal(0, mario.Celle[1].Totali);
        var luigi = result.Righe.Single(r => r.Riga == "Luigi");
        Assert.Equal(0, luigi.Celle[0].Totali);
        Assert.Equal(1, luigi.Celle[1].Totali);
    }

    /// <summary>Test con Condizioni esplicitamente taggate su ENTRAMBE i componenti del Test (a differenza
    /// dell'ambiguità che farebbe ricadere l'operazione nel bucket "senza componente", OperationalSemantics.EcuUnits)
    /// — usato dai test sotto che vogliono verificare l'esplosione su componente/veicolo, non l'ambiguità.</summary>
    private static TestCase CreaTest()
    {
        var tc = new TestCase
        {
            Id = "T-1",
            Componente = new EcuSelection(["BCM", "EDC"]),
            Veicoli = ["Daily A", "Daily B"],
            Funzionalita = ["Lettura guasto", "Programma componente"]
        };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = DateTime.Now,
            Condizioni =
            [
                new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.Conforme, Componente = new EcuSelection(["BCM", "EDC"]) },
                new Condizione { Funzionalita = "Programma componente", Esito = Esito.NonConforme, Componente = new EcuSelection(["BCM", "EDC"]) }
            ]
        });
        return tc;
    }

    /// <summary>ValoriDisponibiliPerDimensioni (feedback utente 2026-09-17, volumi 100k: ripetere l'espansione
    /// Test→Unita' una volta per dimensione era il costo dominante della pagina Report) deve produrre
    /// esattamente lo stesso risultato di N chiamate separate a ValoriDisponibili, solo più rapidamente.</summary>
    [Fact]
    public void ValoriDisponibiliPerDimensioni_CoincideConNChiamateSingole()
    {
        var testCases = new[] { CreaTest() };
        var dimensioni = new[] { DimensioniMatrice.Componente, DimensioniMatrice.Sottoveicolo, DimensioniMatrice.Funzionalita, DimensioniMatrice.CategoriaFunzionalita, DimensioniMatrice.Esito };

        var batch = ReportMatriceService.ValoriDisponibiliPerDimensioni(dimensioni, testCases, Veicoli, Funzioni, []);

        foreach (var dimensione in dimensioni)
        {
            var singolo = ReportMatriceService.ValoriDisponibili(dimensione, testCases, Veicoli, Funzioni, []);
            Assert.Equal(singolo, batch[dimensione]);
        }
    }

    [Fact]
    public void ValoriDisponibiliPerDimensioni_TestMaiEseguitoMantieneFunzionalitaCategorieEdEsito()
    {
        var testCases = new[]
        {
            new TestCase
            {
                Id = "T-non-eseguito",
                Componente = new EcuSelection(["BCM", "EDC"]),
                Veicoli = ["Daily A", "Daily B"],
                Funzionalita = ["Lettura guasto", "Programma componente", "sconosciuta"]
            },
            new TestCase { Id = "T-senza-funzionalita", Componente = new EcuSelection(["BCM"]) }
        };
        var dimensioni = new[]
        {
            DimensioniMatrice.Funzionalita,
            DimensioniMatrice.CategoriaFunzionalita,
            DimensioniMatrice.Esito,
            DimensioniMatrice.Componente,
            DimensioniMatrice.Sottoveicolo
        };

        var batch = ReportMatriceService.ValoriDisponibiliPerDimensioni(dimensioni, testCases, Veicoli, Funzioni, []);

        foreach (var dimensione in dimensioni)
        {
            var singolo = ReportMatriceService.ValoriDisponibili(dimensione, testCases, Veicoli, Funzioni, []);
            Assert.Equal(singolo, batch[dimensione]);
        }
        Assert.Equal(["Lettura guasto", "Programma componente", "sconosciuta"], batch[DimensioniMatrice.Funzionalita]);
        Assert.Equal(["Diag. base", "Programmazione"], batch[DimensioniMatrice.CategoriaFunzionalita]);
        Assert.Equal(["Non testato"], batch[DimensioniMatrice.Esito]);
    }

    /// <summary>Con Componente esplicito su entrambe i componenti (CreaTest), ogni combinazione funzionalità×componente×veicolo
    /// esiste per entrambi i veicoli: la cella (categoria×componente) aggrega le 2 combinazioni-veicolo, non più "1
    /// operazione = 1 unità" del vecchio modello.</summary>
    [Fact]
    public void MatriceDefault_EsplodeEcuSottoveicoliEUsaLoStatoPerCombinazione()
    {
        var risultato = ReportMatriceService.Genera([CreaTest()], new ConfigurazioneMatriceReport(), Veicoli, Funzioni, []);

        Assert.Equal(["BCM", "EDC"], risultato.Colonne);
        // Stato %: Lettura guasto conforme su entrambi i veicoli (2 su 2 OK); Programma componente non conforme su entrambi (0% conforme su 2).
        var baseRow = risultato.Righe.Single(r => r.Riga == "Diag. base");
        Assert.All(baseRow.Celle, c => Assert.Equal(new StatiMatrice(2, 0, 0, 0, 0, 0), c.StatiPercentuale));
        var programmazione = risultato.Righe.Single(r => r.Riga == "Programmazione");
        Assert.All(programmazione.Celle, c => Assert.Equal(new StatiMatrice(0, 2, 0, 0, 0, 0), c.StatiPercentuale));
        Assert.All(programmazione.Celle, c => Assert.Equal(0d, c.Percentuale));
    }

    [Fact]
    public void FiltriMatrice_LimitanoCategorieEdEcuSenzaDipendereDagliAssi()
    {
        var config = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Componente,
            AsseY = DimensioniMatrice.Funzionalita,
            Filtri = new()
            {
                [DimensioniMatrice.Componente] = ["BCM"],
                [DimensioniMatrice.CategoriaFunzionalita] = ["Diag. base"]
            }
        };

        var risultato = ReportMatriceService.Genera([CreaTest()], config, Veicoli, Funzioni, []);

        Assert.Equal(["BCM"], risultato.Colonne);
        Assert.Equal("Lettura guasto", Assert.Single(risultato.Righe).Riga);
    }

    /// <summary>Sottoveicolo non è componente: una cella (funzionalità×veicolo) aggrega le combinazioni delle 2 componente
    /// del Test, quindi Valutati=2 (non 1 come nel vecchio modello dove il componente non moltiplicava l'unità).</summary>
    [Fact]
    public void AsseSottoveicolo_AggregaLeCombinazioniDiTutteLeEcuPerVeicolo()
    {
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Sottoveicolo, AsseY = DimensioniMatrice.Funzionalita };
        var risultato = ReportMatriceService.Genera([CreaTest()], config, Veicoli, Funzioni, []);

        Assert.Equal(["Daily A", "Daily B"], risultato.Colonne);
        Assert.All(risultato.Righe.SelectMany(r => r.Celle), c => Assert.Equal(2, c.Valutati));
    }

    [Fact]
    public void Deviazione_ValeConformeSoloSeAccettataConOverrideEsplicito()
    {
        var tc = CreaTest();
        tc.Funzionalita = ["Lettura guasto"];
        tc.Esecuzioni[0].Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.ConformeConDeviazioni }];

        var nonAccettata = ReportMatriceService.Genera([tc], new(), Veicoli, Funzioni, []);
        Assert.All(nonAccettata.Righe.Single().Celle, c => Assert.Equal(0, c.Conformi));

        tc.Esecuzioni[0].EsitoSovrascritto = true;
        tc.Esecuzioni[0].Esito = Esito.Conforme;
        var accettata = ReportMatriceService.Genera([tc], new(), Veicoli, Funzioni, []);
        // 2 combinazioni (una per veicolo: Daily A e Daily B), entrambe conformi con deviazione accettata.
        Assert.All(accettata.Righe.Single().Celle, c => Assert.Equal(2, c.Conformi));
    }

    [Fact]
    public void Componente_VieneSerializzataComeListaSenzaCampoSecondarie()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(CreaTest(), JsonIo.Options);
        using var documento = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(["BCM", "EDC"], documento.RootElement.GetProperty("Componente").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("EcuSecondarie", json);
    }

    [Fact]
    public void FinestraSuEntrambiGliAssi_MantieneValoriETotaliDellaMatriceCompleta()
    {
        var casi = Enumerable.Range(0, 70).Select(i => new TestCase
        {
            Id = $"T-{i:D3}", Componente = $"Componente-{i:D3}", Assegnatario = $"Operatore-{i:D3}"
        }).ToList();
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Assegnatario };
        var completa = ReportMatriceService.Genera(casi, config, [], [], []);
        var finestra = ReportMatriceService.GeneraFinestra(casi, config, [], [], [], 50, 20, 12, 12);

        Assert.Equal(70, finestra.TotaleRighe);
        Assert.Equal(70, finestra.TotaleColonne);
        Assert.Equal(completa.Colonne.Skip(12).Take(12), finestra.Risultato.Colonne);
        for (var i = 0; i < finestra.Risultato.Righe.Count; i++)
        {
            Assert.Equal(completa.Righe[i + 50].Riga, finestra.Risultato.Righe[i].Riga);
            Assert.Equal(completa.Righe[i + 50].Celle.Skip(12).Take(12).Select(c => c.Testo),
                finestra.Risultato.Righe[i].Celle.Select(c => c.Testo));
        }
    }

    [Fact]
    public void FinestraMillePerMille_MaterializzaSoloLeCelleVisibili()
    {
        var casi = Enumerable.Range(0, 1000).Select(i => new TestCase
        {
            Id = $"T-{i:D4}", Componente = $"Componente-{i:D4}", Assegnatario = $"Operatore-{i:D4}"
        }).ToList();
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Assegnatario };
        var finestra = ReportMatriceService.GeneraFinestra(casi, config, [], [], [], 950, 50, 988, 12);

        Assert.Equal(1000, finestra.TotaleRighe);
        Assert.Equal(1000, finestra.TotaleColonne);
        Assert.Equal(50, finestra.Risultato.Righe.Count);
        Assert.Equal(12, finestra.Risultato.Colonne.Count);
        Assert.Equal(600, finestra.Risultato.Righe.Sum(r => r.Celle.Count));
    }
}

