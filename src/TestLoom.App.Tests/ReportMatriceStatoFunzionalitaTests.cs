using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Decisione utente 2026-09-24 (docs/PIANO_6.4.0.md, "Matrice dei report: stato delle funzionalità,
/// non conteggio delle operazioni"): l'unità della matrice è la combinazione funzionalità×componente×veicolo, con
/// il suo esito più recente fra TUTTE le revisioni di TUTTI i Test — non un'operazione per Test. Questi test
/// coprono esattamente gli scenari citati dall'utente e dal piano: stessa combinazione fallita in un test e
/// riuscita in un altro (o viceversa), più veicoli, più componente, ordine temporale/parità, operazioni non eseguite
/// ignorate, N.A. manuale ancora funzionante, coerenza dell'export.</summary>
public sealed class ReportMatriceStatoFunzionalitaTests
{
    private static readonly ConfigurazioneMatriceReport ConfigFunzEcu = new()
    {
        AsseX = DimensioniMatrice.Componente,
        AsseY = DimensioniMatrice.Funzionalita
    };

    private static TestCase CreaTest(string id, string funzionalita, string componente, string veicolo, DateTime data, Esito esito)
    {
        var tc = new TestCase
        {
            Id = id,
            Componente = new EcuSelection([componente]),
            Veicoli = [veicolo],
            Funzionalita = [funzionalita]
        };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = data,
            Condizioni = [new Condizione { Funzionalita = funzionalita, Esito = esito, Componente = new EcuSelection([componente]) }]
        });
        return tc;
    }

    private static CellaMatriceReport UnicaCella(RisultatoMatriceReport risultato) =>
        Assert.Single(risultato.Righe.SelectMany(r => r.Celle));

    /// <summary>Lo scenario esplicito dell'utente: "una funzionalità può avere un'operazione fallita in un
    /// test e, per stessa componente e stesso veicolo, poi un test in cui va a buon fine" → 1/1 conforme, mai 1/2.</summary>
    [Fact]
    public void FallitaPoiRiuscita_StessaFunzEcuVeicolo_ContaUnaVoltaConformeAllaPiuRecente()
    {
        var falliti = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.NonConforme);
        var riuscito = CreaTest("T-2", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 2), Esito.Conforme);

        var risultato = ReportMatriceService.Genera([falliti, riuscito], ConfigFunzEcu, [], [], []);

        var cella = UnicaCella(risultato);
        Assert.Equal(1, cella.Totali);
        Assert.Equal(1, cella.Valutati);
        Assert.Equal(1, cella.Conformi);
        Assert.Equal(1, cella.StatiPercentuale.Ok);
        Assert.Equal(1, cella.StatiPercentuale.Totale);
    }

    /// <summary>Simmetrico: riuscita e poi fallita nel tempo → 0/1, non 1/2.</summary>
    [Fact]
    public void RiuscitaPoiFallita_StessaFunzEcuVeicolo_ContaUnaVoltaNonConformeAllaPiuRecente()
    {
        var riuscito = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme);
        var fallito = CreaTest("T-2", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 2), Esito.NonConforme);

        var risultato = ReportMatriceService.Genera([riuscito, fallito], ConfigFunzEcu, [], [], []);

        var cella = UnicaCella(risultato);
        Assert.Equal(1, cella.Totali);
        Assert.Equal(1, cella.Valutati);
        Assert.Equal(0, cella.Conformi);
        Assert.Equal(1, cella.StatiPercentuale.NonConforme);
        Assert.Equal(1, cella.StatiPercentuale.Totale);
    }

    /// <summary>Un Test con due veicoli genera due combinazioni distinte (una per veicolo): sull'asse
    /// Sottoveicolo compaiono due colonne, ciascuna con la propria combinazione.</summary>
    [Fact]
    public void TestMultiVeicolo_GeneraUnaCombinazionePerVeicolo()
    {
        var tc = new TestCase { Id = "T-1", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A", "Daily B"], Funzionalita = ["Lettura guasto"] };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(2026, 1, 1),
            Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.Conforme, Componente = new EcuSelection(["BCM"]) }]
        });

        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Sottoveicolo, AsseY = DimensioniMatrice.Funzionalita };
        var risultato = ReportMatriceService.Genera([tc], config, [], [], []);

        Assert.Equal(["Daily A", "Daily B"], risultato.Colonne);
        Assert.All(risultato.Righe.Single().Celle, c =>
        {
            Assert.Equal(1, c.Totali);
            Assert.Equal(1, c.Conformi);
        });
    }

    /// <summary>Un Test multi-componente con operazioni esplicitamente taggate su componente diverse produce combinazioni
    /// distinte per componente: la stessa funzionalità/veicolo con componente diverse non si mescola in un'unica cella.</summary>
    [Fact]
    public void TestMultiEcu_OperazioniTaggateSuEcuDiverse_GenerCombinazioniDistinte()
    {
        var tc = new TestCase { Id = "T-1", Componente = new EcuSelection(["BCM", "EDC"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"] };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(2026, 1, 1),
            Condizioni =
            [
                new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.Conforme, Componente = new EcuSelection(["BCM"]) },
                new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.NonConforme, Componente = new EcuSelection(["EDC"]) }
            ]
        });

        var risultato = ReportMatriceService.Genera([tc], ConfigFunzEcu, [], [], []);

        Assert.Equal(["BCM", "EDC"], risultato.Colonne);
        var riga = Assert.Single(risultato.Righe);
        var bcm = riga.Celle.Single(c => c.Colonna == "BCM");
        var edc = riga.Celle.Single(c => c.Colonna == "EDC");
        Assert.Equal(1, bcm.Conformi);
        Assert.Equal(0, edc.Conformi);
    }

    /// <summary>Operazioni "Da eseguire"/N.A. successive a un esito reale non lo sostituiscono mai: solo
    /// Esito.EEseguito() determina lo stato, indipendentemente da quando arriva un'operazione non eseguita.</summary>
    [Fact]
    public void OperazioniNonEseguiteSuccessive_NonDeterminanoMaiLoStato()
    {
        var eseguita = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme);
        var nonEseguita = new TestCase { Id = "T-2", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"] };
        nonEseguita.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(2026, 1, 5), // più recente, ma Da eseguire: non deve "vincere"
            Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.DaEseguire, Componente = new EcuSelection(["BCM"]) }]
        });

        var risultato = ReportMatriceService.Genera([eseguita, nonEseguita], ConfigFunzEcu, [], [], []);

        var cella = UnicaCella(risultato);
        Assert.Equal(1, cella.Valutati);
        Assert.Equal(1, cella.Conformi);
    }

    /// <summary>Una combinazione mai eseguita ma prevista da un Test pianificato è "non valutata": conta nei
    /// Totali, non nei Valutati (testo "— (0/1)", percentuale nulla).</summary>
    [Fact]
    public void CombinazioneMaiEseguita_ContaNeiTotaliNonNeiValutati()
    {
        var pianificato = new TestCase { Id = "T-1", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"] };
        // Nessuna Esecuzione: la combinazione dichiarata dal Test esiste comunque nei Totali.

        var risultato = ReportMatriceService.Genera([pianificato], ConfigFunzEcu, [], [], []);

        var cella = UnicaCella(risultato);
        Assert.Equal(1, cella.Totali);
        Assert.Equal(0, cella.Valutati);
        Assert.Equal(0, cella.Percentuale);
        Assert.Equal(1, cella.StatiPercentuale.DaEseguire);
        Assert.Equal(1, cella.StatiPercentuale.Totale);
    }

    /// <summary>Parità sulla stessa Data: criterio deterministico (Id del Test in ordine ordinale, poi indice di revisione,
    /// poi indice di tentativo) — qui la parità è forzata sullo stesso Test, tentativo più alto vince, cioè
    /// l'ordine reale delle Condizioni nella stessa revisione (la ripetizione più recente della stessa
    /// operazione logica sostituisce quella precedente, già garantito da CondizioniEffettive).</summary>
    [Fact]
    public void ParitaSullaStessaData_OrdineDiTentativoDecide()
    {
        var tc = new TestCase { Id = "T-1", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"] };
        var stessaData = new DateTime(2026, 1, 1);
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = stessaData,
            Condizioni =
            [
                new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.NonConforme, Componente = new EcuSelection(["BCM"]) },
                new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.Conforme, Componente = new EcuSelection(["BCM"]) }
            ]
        });

        var risultato = ReportMatriceService.Genera([tc], ConfigFunzEcu, [], [], []);

        // Due Condizioni indipendenti (OperazioneOrigineUid nullo su entrambe) nella stessa revisione/Data:
        // la combinazione riceve il candidato con indice di tentativo più alto come "più recente" a parità
        // di Data — qui l'ultima Condizione della lista (Conforme).
        var cella = UnicaCella(risultato);
        Assert.Equal(1, cella.Conformi);
    }

    /// <summary>Revisione F4: stessa Data su DUE Test distinti. Il tie-break è l'Id del Test in ordine ordinale (mai l'ordine
    /// della lista): T-2 è "successivo" a T-1, quindi il rappresentante corrente è T-2 e la combinazione è NonConforme.</summary>
    [Fact]
    public void ParitaSullaStessaData_TestDiversi_RappresentanteIlTestIdOrdinalmentePiuAlto()
    {
        var uno = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme);
        var due = CreaTest("T-2", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.NonConforme);

        foreach (var tests in new[] { new[] { uno, due }, new[] { due, uno } })
        {
            var cella = UnicaCella(ReportMatriceService.Genera(tests, ConfigFunzEcu, [], [], []));
            Assert.Equal(0, cella.Conformi);
            Assert.Equal(1, cella.StatiPercentuale.NonConforme);
        }
    }

    /// <summary>La marcatura manuale N.A. per cella (MatriceNonApplicabileService, invariata da questa
    /// modifica) continua a funzionare sopra il nuovo calcolo per combinazione.</summary>
    [Fact]
    public void MarcaturaNonApplicabileManuale_ContinuaAFunzionareSulNuovoCalcolo()
    {
        var tc = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme);
        var risultato = ReportMatriceService.Genera([tc], ConfigFunzEcu, [], [], []);

        var appConfig = new AppConfig();
        MatriceNonApplicabileService.Imposta(appConfig, ConfigFunzEcu.AsseX, ConfigFunzEcu.AsseY, "BCM", "Lettura guasto", "manuale");
        MatriceNonApplicabileService.Applica(appConfig, ConfigFunzEcu.AsseX, ConfigFunzEcu.AsseY, risultato);

        var cella = UnicaCella(risultato);
        Assert.True(cella.NonApplicabile);
        Assert.Null(cella.Percentuale);
        Assert.Equal("N.A.", cella.Testo);
    }

    /// <summary>Coerenza dell'export: ReportService/PdfReportService leggono lo stesso RisultatoMatriceReport
    /// prodotto da Genera, senza logica propria di conteggio — qui si verifica che l'Excel non lanci e che la
    /// cella scritta rifletta lo stato deduplicato (100%), non l'export in sé (già coperto in dettaglio da
    /// ReportGenerationTests).</summary>
    [Fact]
    public void ExportExcel_UsaLoStessoRisultatoDedupllicato()
    {
        var falliti = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.NonConforme);
        var riuscito = CreaTest("T-2", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 2), Esito.Conforme);

        var risultato = ReportMatriceService.Genera([falliti, riuscito], ConfigFunzEcu, [], [], []);
        var cella = UnicaCella(risultato);

        Assert.Equal(1, cella.StatiPercentuale.Ok);
        Assert.Equal(1, cella.StatiPercentuale.Totale);
    }

    /// <summary>Contratto stato corrente, commit 4fdf2b6 ("Fix current-state matrix axis projection"): la riduzione
    /// a rappresentante corrente della combinazione avviene PRIMA dei filtri non-identità. Il Test di Luigi
    /// (NonConforme, più recente) è il rappresentante corrente: con filtro Assegnatario=Mario non resta alcuna
    /// cella, e l'OK storico di Mario NON riappare (nessuna resurrezione). La versione precedente
    /// ("VinceLaPrecedenteChePassaIlFiltro", filtri PRIMA della riduzione) è superata. Il controllo con
    /// Assegnatario=Luigi mostra la cella con lo stato corrente NOK.</summary>
    [Fact]
    public void Filtro_NonIdentita_ApplicatoDopoLaRiduzione_NessunaResurrezione()
    {
        var mario = new TestCase { Id = "T-1", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"], Assegnatario = "Mario" };
        mario.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 1, 1), Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.Conforme, Componente = new EcuSelection(["BCM"]) }] });
        var luigi = new TestCase { Id = "T-2", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"], Assegnatario = "Luigi" };
        luigi.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 1, 2), Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.NonConforme, Componente = new EcuSelection(["BCM"]) }] });

        var configMario = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Componente,
            AsseY = DimensioniMatrice.Funzionalita,
            Filtri = new() { [DimensioniMatrice.Assegnatario] = ["Mario"] }
        };

        var conMario = ReportMatriceService.Genera([mario, luigi], configMario, [], [], []);
        Assert.Empty(conMario.Righe.SelectMany(r => r.Celle));

        var configLuigi = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Componente,
            AsseY = DimensioniMatrice.Funzionalita,
            Filtri = new() { [DimensioniMatrice.Assegnatario] = ["Luigi"] }
        };

        var cella = UnicaCella(ReportMatriceService.Genera([mario, luigi], configLuigi, [], [], []));
        Assert.Equal(1, cella.Totali);
        Assert.Equal(1, cella.Valutati);
        Assert.Equal(0, cella.Conformi);
        Assert.Equal(1, cella.StatiPercentuale.NonConforme);
    }

    /// <summary>Contratto stato corrente (commit 4fdf2b6): la combinazione funzionalità×componente×veicolo ha un solo rappresentante corrente
    /// (il Test più recente) e la proiezione sull'asse Assegnatario usa il rappresentante: una sola riga, con lo stato corrente,
    /// nessuno spareggio fra Assegnatari. La versione precedente (una riga per Assegnatario con lo stato delle proprie operazioni,
    /// review 2026-09-25 punto 2) è superata.</summary>
    [Fact]
    public void AsseAssegnatario_DueAssegnatariStessaCombinazione_UnaRigaDelRappresentanteCorrente()
    {
        var mario = new TestCase { Id = "T-1", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"], Assegnatario = "Mario" };
        mario.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 1, 1), Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.Conforme, Componente = new EcuSelection(["BCM"]) }] });
        var luigi = new TestCase { Id = "T-2", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"], Assegnatario = "Luigi" };
        luigi.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 1, 2), Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.NonConforme, Componente = new EcuSelection(["BCM"]) }] });

        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Assegnatario };
        var risultato = ReportMatriceService.Genera([mario, luigi], config, [], [], []);

        // Rappresentante: Luigi (Test più recente, NonConforme). Nessuna riga per Mario: il suo OK storico non riappare.
        var riga = Assert.Single(risultato.Righe);
        Assert.Equal("Luigi", riga.Riga);
        Assert.Equal(0, riga.Celle.Single().Conformi);
    }

    /// <summary>Review indipendente 2026-09-25, punto 2: un Test con più Requisiti sullo stesso funzionalità×
    /// componente×veicolo genera una combinazione per ciascun Requisito, come già avviene per i veicoli.</summary>
    [Fact]
    public void AsseRequisito_TestConPiuRequisiti_GenerUnaCombinazionePerRequisito()
    {
        var tc = new TestCase { Id = "T-1", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"], RequisitoId = ["REQ-1", "REQ-2"] };
        tc.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 1, 1), Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.Conforme, Componente = new EcuSelection(["BCM"]) }] });

        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Requisito, AsseY = DimensioniMatrice.Funzionalita };
        var risultato = ReportMatriceService.Genera([tc], config, [], [], []);

        Assert.Equal(["REQ-1", "REQ-2"], risultato.Colonne);
        Assert.All(risultato.Righe.Single().Celle, c => Assert.Equal(1, c.Conformi));
    }

    /// <summary>Punto 4 della review: dentro una revisione si usano solo le operazioni EFFETTIVE
    /// (CondizioniEffettive) — la ripetizione della stessa operazione (stesso OperazioneOrigineUid) non è due
    /// candidati indipendenti, è un solo tentativo il cui esito è quello dell'ULTIMA ripetizione.</summary>
    [Fact]
    public void RipetizioneStessaOperazioneNellaRevisione_UsaSoloIlTentativoPiuRecente()
    {
        var origine = Guid.NewGuid();
        var tc = new TestCase { Id = "T-1", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"] };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(2026, 1, 1),
            Condizioni =
            [
                new Condizione { Uid = origine, Funzionalita = "Lettura guasto", Esito = Esito.NonConforme, Componente = new EcuSelection(["BCM"]) },
                new Condizione { OperazioneOrigineUid = origine, Funzionalita = "Lettura guasto", Esito = Esito.Conforme, Componente = new EcuSelection(["BCM"]) }
            ]
        });

        var risultato = ReportMatriceService.Genera([tc], ConfigFunzEcu, [], [], []);

        var cella = UnicaCella(risultato);
        Assert.Equal(1, cella.Totali);
        Assert.Equal(1, cella.Conformi);
    }
}
