using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class AuditMatriceServiceTests
{
    private const string Veicolo = "eDaily My24";
    private static Componente EcuDi(string nome, params string[] veicoli) => new() { Nome = nome, VeicoliAssociati = veicoli.ToList() };
    private static Requisito ReqDi(string id) => new() { Id = id };

    [Fact]
    public void EcuDelSottoveicolo_IncludeSoloEcuAssociateENonArchiviate()
    {
        var componente = new List<Componente>
        {
            EcuDi("BCM", "eDaily My24"),
            EcuDi("ABS", "eDaily My26"),
            new() { Nome = "TCU", VeicoliAssociati = { "eDaily My24" }, Archiviata = true },
        };

        var risultato = AuditMatriceService.EcuDelSottoveicolo(componente, "eDaily My24");

        Assert.Equal(new[] { "BCM" }, risultato);
    }

    [Fact]
    public void Matrice_NessunTestCollegato_CellaDaTestare()
    {
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24") };
        var requisiti = new List<Requisito> { ReqDi("R1") };

        var matrice = AuditMatriceService.Matrice("eDaily My24", componente, requisiti, new List<TestCase>());

        var cella = Assert.Single(matrice);
        Assert.Equal(StatoCellaAudit.DaTestare, cella.Stato);
        Assert.Null(cella.Punteggio);
    }

    [Fact]
    public void Matrice_TestCollegatoSenzaEsecuzioni_CellaTestCreatoNonEseguito()
    {
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24") };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var testCases = new List<TestCase> { new() { Id = "T1", Componente = "BCM", RequisitoId = { "R1" }, Veicolo = Veicolo } };

        var matrice = AuditMatriceService.Matrice("eDaily My24", componente, requisiti, testCases);

        var cella = Assert.Single(matrice);
        Assert.Equal(StatoCellaAudit.TestCreatoNonEseguito, cella.Stato);
    }

    [Fact]
    public void Matrice_EsecuzioneSenzaPunteggioPerQuelRequisito_CellaTestCreatoNonEseguito()
    {
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24") };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var tc = new TestCase { Id = "T1", Componente = "BCM", RequisitoId = { "R1" }, Veicolo = Veicolo };
        tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.Now, Esito = Esito.DaRivedere, Matricola = "V" });

        var matrice = AuditMatriceService.Matrice("eDaily My24", componente, requisiti, new List<TestCase> { tc });

        var cella = Assert.Single(matrice);
        Assert.Equal(StatoCellaAudit.TestCreatoNonEseguito, cella.Stato);
    }

    [Fact]
    public void Matrice_PrendeIlPunteggioDellEsecuzionePiuRecente()
    {
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24") };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var tc = new TestCase { Id = "T1", Componente = "BCM", RequisitoId = { "R1" }, Veicolo = Veicolo };
        tc.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 1, 1), Esito = Esito.NonConforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.NonConforme } } });
        tc.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 6, 1), Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.Conforme } } });

        var matrice = AuditMatriceService.Matrice("eDaily My24", componente, requisiti, new List<TestCase> { tc });

        var cella = Assert.Single(matrice);
        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.Equal(PunteggioConformita.Conforme, cella.Punteggio);
    }

    [Fact]
    public void Matrice_RequisitoArchiviato_NonCompareInGriglia()
    {
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24") };
        var requisiti = new List<Requisito> { new() { Id = "R1", Archiviata = true } };

        var matrice = AuditMatriceService.Matrice("eDaily My24", componente, requisiti, new List<TestCase>());

        Assert.Empty(matrice);
    }

    [Fact]
    public void Matrice_UnaCellaPerOgniCoppiaEcuRequisitoDelSottoveicolo()
    {
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24"), EcuDi("ABS", "eDaily My24") };
        var requisiti = new List<Requisito> { ReqDi("R1"), ReqDi("R2") };

        var matrice = AuditMatriceService.Matrice("eDaily My24", componente, requisiti, new List<TestCase>());

        Assert.Equal(4, matrice.Count);
    }

    private static TestCase TestValutato(string id, string ecuNome, string requisitoId, PunteggioConformita punteggio, DateTime data)
    {
        var tc = new TestCase { Id = id, Componente = ecuNome, RequisitoId = { requisitoId }, Veicolo = Veicolo };
        tc.Esecuzioni.Add(new Esecuzione { Data = data, Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = requisitoId, Valore = punteggio } } });
        return tc;
    }

    [Fact]
    public void Matrice_DueTestDiversiConPunteggiDiscordanti_CellaInConflitto()
    {
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24") };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var testCases = new List<TestCase>
        {
            TestValutato("TC-A", "BCM", "R1", PunteggioConformita.Conforme, new DateTime(2026, 1, 1)),
            TestValutato("TC-B", "BCM", "R1", PunteggioConformita.ModeratamenteConforme, new DateTime(2026, 1, 1)),
        };

        var cella = Assert.Single(AuditMatriceService.Matrice("eDaily My24", componente, requisiti, testCases));

        Assert.Equal(StatoCellaAudit.Conflitto, cella.Stato);
        Assert.Null(cella.Punteggio);
        Assert.Equal(2, cella.Candidati.Count);
        Assert.Contains(cella.Candidati, c => c.TestCaseId == "TC-A" && c.Punteggio == PunteggioConformita.Conforme);
        Assert.Contains(cella.Candidati, c => c.TestCaseId == "TC-B" && c.Punteggio == PunteggioConformita.ModeratamenteConforme);
    }

    [Fact]
    public void Matrice_DueTestDiversiConLoStessoPunteggio_NessunConflitto()
    {
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24") };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var testCases = new List<TestCase>
        {
            TestValutato("TC-A", "BCM", "R1", PunteggioConformita.Conforme, new DateTime(2026, 1, 1)),
            TestValutato("TC-B", "BCM", "R1", PunteggioConformita.Conforme, new DateTime(2026, 6, 1)),
        };

        var cella = Assert.Single(AuditMatriceService.Matrice("eDaily My24", componente, requisiti, testCases));

        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.Equal(PunteggioConformita.Conforme, cella.Punteggio);
    }

    [Fact]
    public void Matrice_ConflittoConRisoluzioneValida_UsaIlPunteggioDelTestIndicato()
    {
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24") };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var testCases = new List<TestCase>
        {
            TestValutato("TC-A", "BCM", "R1", PunteggioConformita.Conforme, new DateTime(2026, 1, 1)),
            TestValutato("TC-B", "BCM", "R1", PunteggioConformita.ModeratamenteConforme, new DateTime(2026, 1, 1)),
        };
        var risoluzioni = new List<RisoluzioneConflittoAudit> { new() { Componente = "BCM", RequisitoId = "R1", TestCaseIdValido = "TC-A" } };

        var cella = Assert.Single(AuditMatriceService.Matrice("eDaily My24", componente, requisiti, testCases, risoluzioni));

        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.Equal(PunteggioConformita.Conforme, cella.Punteggio);
    }

    [Fact]
    public void Matrice_RisoluzioneChePuntaAUnTestNonPiuCandidato_VieneIgnorata()
    {
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24") };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var testCases = new List<TestCase>
        {
            TestValutato("TC-A", "BCM", "R1", PunteggioConformita.Conforme, new DateTime(2026, 1, 1)),
            TestValutato("TC-B", "BCM", "R1", PunteggioConformita.ModeratamenteConforme, new DateTime(2026, 1, 1)),
        };
        var risoluzioni = new List<RisoluzioneConflittoAudit> { new() { Componente = "BCM", RequisitoId = "R1", TestCaseIdValido = "TC-INESISTENTE" } };

        var cella = Assert.Single(AuditMatriceService.Matrice("eDaily My24", componente, requisiti, testCases, risoluzioni));

        Assert.Equal(StatoCellaAudit.Conflitto, cella.Stato);
    }

    // I nuovi Manuali veicolo-specifici competono per timestamp con Test e legacy globali. I fixture dei
    // casi seguenti sono datati dopo i Test che correggono.

    [Fact]
    public void Matrice_InserimentoManuale_SostituisceUnaCellaAncoraDaTestare()
    {
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24") };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var risoluzioni = new List<RisoluzioneConflittoAudit>
        {
            new() { Componente = "BCM", RequisitoId = "R1", PunteggioManuale = PunteggioConformita.NonApplicabile, Motivazione = "Non applicabile a banco", UltimaModifica = new DateTime(2026, 7, 1) }
        };

        var cella = Assert.Single(AuditMatriceService.Matrice("eDaily My24", componente, requisiti, new List<TestCase>(), risoluzioni));

        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.Equal(PunteggioConformita.NonApplicabile, cella.Punteggio);
        Assert.True(cella.InserimentoManuale);
        Assert.Equal("Non applicabile a banco", cella.MotivazioneInserimentoManuale);
    }

    [Fact]
    public void Matrice_InserimentoManuale_SostituisceUnConflittoTraTestDiscordanti()
    {
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24") };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var testCases = new List<TestCase>
        {
            TestValutato("TC-A", "BCM", "R1", PunteggioConformita.Conforme, new DateTime(2026, 1, 1)),
            TestValutato("TC-B", "BCM", "R1", PunteggioConformita.ModeratamenteConforme, new DateTime(2026, 6, 1)),
        };
        var risoluzioni = new List<RisoluzioneConflittoAudit>
        {
            new() { Componente = "BCM", RequisitoId = "R1", PunteggioManuale = PunteggioConformita.AmpiamenteConforme, Motivazione = "Verificato a tavolino", UltimaModifica = new DateTime(2026, 7, 1) }
        };

        var cella = Assert.Single(AuditMatriceService.Matrice("eDaily My24", componente, requisiti, testCases, risoluzioni));

        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.Equal(PunteggioConformita.AmpiamenteConforme, cella.Punteggio);
        Assert.Empty(cella.Candidati); // niente più stato di conflitto: l'inserimento manuale vince e basta
    }

    [Fact]
    public void Matrice_InserimentoManuale_SostituisceUnValoreConcordeDeiTest()
    {
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24") };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var testCases = new List<TestCase> { TestValutato("TC-A", "BCM", "R1", PunteggioConformita.Conforme, new DateTime(2026, 1, 1)) };
        var risoluzioni = new List<RisoluzioneConflittoAudit>
        {
            new() { Componente = "BCM", RequisitoId = "R1", PunteggioManuale = PunteggioConformita.NonConforme, Motivazione = "Il test non copriva il caso reale", UltimaModifica = new DateTime(2026, 2, 1) }
        };

        var cella = Assert.Single(AuditMatriceService.Matrice("eDaily My24", componente, requisiti, testCases, risoluzioni));

        Assert.Equal(PunteggioConformita.NonConforme, cella.Punteggio);
        Assert.True(cella.InserimentoManuale);
    }

    [Fact]
    public void Matrice_ManualePiuRecenteDelTest_VinceSoloSulVeicoloLocale()
    {
        var componente = new List<Componente> { EcuDi("BCM", "My24", "My26") };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var legacyData = new DateTime(2026, 1, 1);
        var risoluzioni = new List<RisoluzioneConflittoAudit>
        {
            new()
            {
                Componente = "BCM", RequisitoId = "R1", PunteggioManuale = PunteggioConformita.AmpiamenteConforme,
                Motivazione = "legacy globale", UltimaModifica = legacyData,
            },
        };
        var locale = new List<ValutazioneRequisitoEcuVeicolo>
        {
            new()
            {
                RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.NonConforme,
                Origine = OrigineValutazioneRequisito.Manuale, Nota = "solo My24", UltimaModifica = legacyData.AddDays(1),
            },
        };

        var my24 = Assert.Single(AuditMatriceService.Matrice("My24", componente, requisiti, new List<TestCase>(), risoluzioni, valutazioni: locale));
        var my26 = Assert.Single(AuditMatriceService.Matrice("My26", componente, requisiti, new List<TestCase>(), risoluzioni, valutazioni: locale));

        Assert.Equal(PunteggioConformita.NonConforme, my24.Punteggio);
        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Manuale, my24.Origine);
        Assert.False(my24.InserimentoManuale);
        Assert.Equal(PunteggioConformita.AmpiamenteConforme, my26.Punteggio);
        Assert.True(my26.InserimentoManuale);
    }

    [Fact]
    public void Matrice_TestPiuRecenteDelLegacyGlobale_Vince()
    {
        var componente = new List<Componente> { EcuDi("BCM", Veicolo) };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var testData = new DateTime(2026, 2, 1);
        var test = TestValutato("T1", "BCM", "R1", PunteggioConformita.Conforme, testData);
        var legacy = new List<RisoluzioneConflittoAudit>
        {
            new()
            {
                Componente = "BCM", RequisitoId = "R1", PunteggioManuale = PunteggioConformita.NonConforme,
                Motivazione = "legacy", UltimaModifica = testData.AddDays(-1),
            },
        };

        var cella = Assert.Single(AuditMatriceService.Matrice(Veicolo, componente, requisiti, new[] { test }, legacy));

        Assert.Equal(PunteggioConformita.Conforme, cella.Punteggio);
        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Diretto, cella.Origine);
    }

    // --- Piano 6.3.1 §1: propagazione esplicita per veicolo (fine della propagazione silenziosa) ---

    [Fact]
    public void Matrice_TestSuAltroVeicoloDellaStessaEcu_NonPropagaPiuInAutomatico_CellaPropagabile()
    {
        // BCM e' su entrambi i sottoveicoli; il Test e' stato eseguito solo su "eDaily My26" — prima di
        // questo piano la cella di "eDaily My24" mostrava comunque il punteggio 5 (propagazione silenziosa).
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24", "eDaily My26") };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var tc = new TestCase { Id = "T1", Componente = "BCM", RequisitoId = { "R1" }, Veicolo = "eDaily My26" };
        tc.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 1, 1), Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.Conforme } } });

        var cella = Assert.Single(AuditMatriceService.Matrice("eDaily My24", componente, requisiti, new List<TestCase> { tc }));

        Assert.Equal(StatoCellaAudit.PropagabileNonConfermato, cella.Stato);
        Assert.Null(cella.Punteggio);
        var candidato = Assert.Single(cella.CandidatiPropagazione);
        Assert.Equal("eDaily My26", candidato.Key);
        Assert.Equal(PunteggioConformita.Conforme, candidato.Value.PunteggioSorgente);
        Assert.Equal("T1", candidato.Value.TestIdSorgente);
    }

    [Fact]
    public void Matrice_TestMultiVeicolo_CopreDirettamenteTutteLeCoppieCheDichiara()
    {
        // Un Test con più veicoli vale per requisito × componente × ciascun veicolo che dichiara (propagazione
        // "interna al test", automatica e invariata da questo piano) — a differenza del caso sopra, qui il
        // Test dichiara ESPLICITAMENTE entrambi i veicoli.
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24", "eDaily My26") };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var tc = new TestCase { Id = "T1", Componente = "BCM", RequisitoId = { "R1" }, Veicoli = { "eDaily My24", "eDaily My26" } };
        tc.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 1, 1), Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.Conforme } } });

        var cellaMy24 = Assert.Single(AuditMatriceService.Matrice("eDaily My24", componente, requisiti, new List<TestCase> { tc }));
        var cellaMy26 = Assert.Single(AuditMatriceService.Matrice("eDaily My26", componente, requisiti, new List<TestCase> { tc }));

        Assert.Equal(StatoCellaAudit.Valutato, cellaMy24.Stato);
        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Diretto, cellaMy24.Origine);
        Assert.Equal(StatoCellaAudit.Valutato, cellaMy26.Stato);
        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Diretto, cellaMy26.Origine);
    }

    [Fact]
    public void Matrice_NessunAltroVeicoloConValore_CellaRestaDaTestare_NonPropagabile()
    {
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24", "eDaily My26") };
        var requisiti = new List<Requisito> { ReqDi("R1") };

        var cella = Assert.Single(AuditMatriceService.Matrice("eDaily My24", componente, requisiti, new List<TestCase>()));

        Assert.Equal(StatoCellaAudit.DaTestare, cella.Stato);
        Assert.Empty(cella.CandidatiPropagazione);
    }

    [Fact]
    public void Matrice_ValutazionePropagataSalvata_MostraIlValoreConOrigineESorgente()
    {
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24", "eDaily My26") };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var valutazioni = new List<ValutazioneRequisitoEcuVeicolo>
        {
            new()
            {
                RequisitoId = "R1", Componente = "BCM", Veicolo = "eDaily My24", Punteggio = PunteggioConformita.Conforme,
                Origine = OrigineValutazioneRequisito.Propagata, SorgenteVeicolo = "eDaily My26", SorgenteTestId = "T1",
                SorgentePunteggio = PunteggioConformita.Conforme, Nota = "Confermata identica su My24",
                Operatore = "AB", UltimaModifica = new DateTime(2026, 2, 1),
            }
        };

        var cella = Assert.Single(AuditMatriceService.Matrice("eDaily My24", componente, requisiti, new List<TestCase>(), valutazioni: valutazioni));

        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.Equal(PunteggioConformita.Conforme, cella.Punteggio);
        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Propagata, cella.Origine);
        Assert.Contains("eDaily My26", cella.Tooltip);
        Assert.Contains("Confermata identica su My24", cella.Tooltip);
    }

    [Fact]
    public void Matrice_TestDirettoArrivaDopoUnaPropagazioneSalvata_IlDirettoVince()
    {
        // Requisito utente: se successivamente arriva un Test diretto sul veicolo, la cella deve seguirlo.
        var componente = new List<Componente> { EcuDi("BCM", "eDaily My24", "eDaily My26") };
        var requisiti = new List<Requisito> { ReqDi("R1") };
        var valutazioni = new List<ValutazioneRequisitoEcuVeicolo>
        {
            new() { RequisitoId = "R1", Componente = "BCM", Veicolo = "eDaily My24", Punteggio = PunteggioConformita.Conforme, Origine = OrigineValutazioneRequisito.Propagata }
        };
        var tc = new TestCase { Id = "T2", Componente = "BCM", RequisitoId = { "R1" }, Veicolo = "eDaily My24" };
        tc.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 3, 1), Esito = Esito.NonConforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.NonConforme } } });

        var cella = Assert.Single(AuditMatriceService.Matrice("eDaily My24", componente, requisiti, new List<TestCase> { tc }, valutazioni: valutazioni));

        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Diretto, cella.Origine);
        Assert.Equal(PunteggioConformita.NonConforme, cella.Punteggio);
    }
}
