using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Piano 6.4.0: Propagazione massiva — "Propaga tutti i '!' di questa componente" / "di tutti i componenti" per il
/// veicolo selezionato in Audit. Copertura di AnteprimaPropagazioneMassiva/PropagaMassiva
/// (ValutazioneRequisitoEcuVeicoloService), incluso il ricalcolo fail-closed al salvataggio quando un'altra
/// postazione scrive fra anteprima e conferma. <paramref name="celleAttese"/> di PropagaMassiva è tipizzato
/// come IReadOnlyList&lt;VocePropagazioneMassiva&gt; (review indipendente 2026-09-26, CRITICO — prima era
/// solo (Componente, RequisitoId), e il chiamante poteva includerci anche celle mostrate come conflitto
/// nell'anteprima): qui il tipo stesso impedisce di rappresentare una cella-conflitto come "attesa", e il
/// servizio verifica comunque che il punteggio fresco coincida con quello atteso prima di scrivere — vedi i
/// test dedicati sotto "Guardia nel servizio".</summary>
public sealed class ValutazioneRequisitoEcuVeicoloPropagazioneMassivaTests
{
    private static Componente EcuDi(string nome, params string[] veicoli) => new() { Nome = nome, VeicoliAssociati = veicoli.ToList() };

    private static TestCase TestDiretto(string id, string componente, string requisitoId, string veicolo, PunteggioConformita punteggio, DateTime data)
    {
        var tc = new TestCase { Id = id, Componente = componente, RequisitoId = { requisitoId }, Veicolo = veicolo };
        tc.Esecuzioni.Add(new Esecuzione { Data = data, Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = requisitoId, Valore = punteggio } } });
        return tc;
    }

    private static Requisito ReqAttivo(string id) => new() { Id = id };

    // --- Perimetro: solo veicolo V, solo celle "!" ---

    [Fact]
    public void Anteprima_CellaDirettaDelVeicoloTarget_NonEntraTraLePropagabili()
    {
        // Il veicolo target ("My24") ha un Test diretto: RisolviCella la restituisce Diretto=true,
        // Propagabile=false — la Propagazione massiva deve ignorarla, non provare a sovrascriverla.
        var componente = EcuDi("BCM", "My24", "My26");
        var direttaMy24 = TestDiretto("T-DIRETTA", "BCM", "R1", "My24", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));

        var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            "My24", new[] { componente }, new[] { ReqAttivo("R1") }, new[] { direttaMy24 }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        Assert.Empty(anteprima.DaPropagare);
        Assert.Empty(anteprima.Conflitti);
    }

    [Fact]
    public void Anteprima_SoloCellePropagabiliDelVeicoloEDellEcuScelta_ProponeLaSorgenteCorretta()
    {
        var componente = EcuDi("BCM", "My24", "My26");
        var direttaMy26 = TestDiretto("T-SRC", "BCM", "R1", "My26", PunteggioConformita.AmpiamenteConforme, new DateTime(2026, 2, 1));

        var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            "My24", new[] { componente }, new[] { ReqAttivo("R1") }, new[] { direttaMy26 }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        var voce = Assert.Single(anteprima.DaPropagare);
        Assert.Equal("BCM", voce.Componente);
        Assert.Equal("R1", voce.RequisitoId);
        Assert.Equal("My26", voce.Sorgente.VeicoloSorgente);
        Assert.Empty(anteprima.Conflitti);
    }

    [Fact]
    public void Anteprima_UnaSolaEcuScelta_NonConsideraLeAltreEcuDelVeicolo()
    {
        var ecuA = EcuDi("BCM", "My24", "My26");
        var ecuB = EcuDi("TCU", "My24", "My26");
        var testBcm = TestDiretto("T-BCM", "BCM", "R1", "My26", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var testTcu = TestDiretto("T-TCU", "TCU", "R1", "My26", PunteggioConformita.NonConforme, new DateTime(2026, 1, 1));

        var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            "My24", new[] { ecuA }, new[] { ReqAttivo("R1") }, new[] { testBcm, testTcu }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        var voce = Assert.Single(anteprima.DaPropagare);
        Assert.Equal("BCM", voce.Componente); // TCU non era nell'elenco componente passato: ignorata
    }

    [Fact]
    public void PropagaMassiva_ScriveSoloSulVeicoloRichiesto_NessunEffettoSuAltriVeicoliDellaEcu()
    {
        using var tmp = new TempWorkspace();
        var componente = EcuDi("BCM", "My24", "My26", "My28");
        var sorgente = TestDiretto("T-SRC", "BCM", "R1", "My28", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var testCases = new[] { sorgente };
        var requisiti = new[] { ReqAttivo("R1") };

        var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            "My26", new[] { componente }, requisiti, testCases, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        var risultato = ValutazioneRequisitoEcuVeicoloService.PropagaMassiva(
            tmp.Master.ValutazioniRequisitoEcuVeicolo, "My26", new[] { componente }, requisiti,
            testCases, risoluzioni: null, celleAttese: anteprima.DaPropagare, "confermo su banco", "AB", out var errore);

        Assert.Null(errore);
        var riga = Assert.Single(tmp.Master.ValutazioniRequisitoEcuVeicolo.Load());
        Assert.Equal("My26", riga.Veicolo); // solo il veicolo target, non My24 o My28
        Assert.Single(risultato.Propagate);
    }

    // --- Dirette, Manuali e Propagate non toccate ---

    [Fact]
    public void PropagaMassiva_NonTocaCelleGiaManualiOPropagate()
    {
        using var tmp = new TempWorkspace();
        var componente = EcuDi("BCM", "My24", "My26");
        var sorgente = TestDiretto("T-SRC", "BCM", "R2", "My26", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var testCases = new[] { sorgente };
        var requisiti = new[] { ReqAttivo("R1"), ReqAttivo("R2") };

        // R1 su My24 è già Manuale: RisolviCella la restituisce Origine=Manuale, mai Propagabile — quindi
        // non compare nemmeno nell'anteprima (DaPropagare), e nessun chiamante può passarla come "attesa"
        // (il tipo VocePropagazioneMassiva esiste solo per celle davvero "!").
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            tmp.Master.ValutazioniRequisitoEcuVeicolo, "R1", "BCM", new[] { "My24" }, PunteggioConformita.NonApplicabile, "non applicabile", "AB", null, out _));

        var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            "My24", new[] { componente }, requisiti, testCases, tmp.Master.ValutazioniRequisitoEcuVeicolo.Load());

        Assert.DoesNotContain(anteprima.DaPropagare, v => v.RequisitoId == "R1");
        Assert.Contains(anteprima.DaPropagare, v => v.RequisitoId == "R2");

        var risultato = ValutazioneRequisitoEcuVeicoloService.PropagaMassiva(
            tmp.Master.ValutazioniRequisitoEcuVeicolo, "My24", new[] { componente }, requisiti,
            testCases, null, anteprima.DaPropagare, "nota massiva", "AB", out var errore);

        Assert.Null(errore);
        var salvate = tmp.Master.ValutazioniRequisitoEcuVeicolo.Load();
        Assert.Equal(2, salvate.Count);
        var r1 = salvate.Single(v => v.RequisitoId == "R1");
        Assert.Equal(OrigineValutazioneRequisito.Manuale, r1.Origine);
        Assert.Equal("non applicabile", r1.Nota); // invariata: mai toccata dalla propagazione massiva
        var r2 = salvate.Single(v => v.RequisitoId == "R2");
        Assert.Equal(OrigineValutazioneRequisito.Propagata, r2.Origine);
    }

    // --- Conflitto: nessuna scrittura, cella ancora "!" ---

    [Fact]
    public void Anteprima_CandidatiDiscordanti_VaInConflitto_NonInDaPropagare()
    {
        var componente = EcuDi("BCM", "My24", "My26", "My28");
        var candidatoA = TestDiretto("T-A", "BCM", "R1", "My26", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var candidatoB = TestDiretto("T-B", "BCM", "R1", "My28", PunteggioConformita.NonConforme, new DateTime(2026, 2, 1));

        var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            "My24", new[] { componente }, new[] { ReqAttivo("R1") }, new[] { candidatoA, candidatoB }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        Assert.Empty(anteprima.DaPropagare);
        var conflitto = Assert.Single(anteprima.Conflitti);
        Assert.Equal(2, conflitto.Candidati.Count);
    }

    [Fact]
    public void PropagaMassiva_SoloConflitto_NienteDaPropagare_Rifiutata_NessunaScrittura()
    {
        using var tmp = new TempWorkspace();
        var componente = EcuDi("BCM", "My24", "My26", "My28");
        var candidatoA = TestDiretto("T-A", "BCM", "R1", "My26", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var candidatoB = TestDiretto("T-B", "BCM", "R1", "My28", PunteggioConformita.NonConforme, new DateTime(2026, 2, 1));
        var testCases = new[] { candidatoA, candidatoB };
        var requisiti = new[] { ReqAttivo("R1") };

        // L'unica cella del perimetro è un conflitto: l'anteprima non produce nessuna VocePropagazioneMassiva
        // da confermare, quindi celleAttese resta vuota — PropagaMassiva rifiuta a monte (nota/celle
        // mancanti), esattamente come una nota vuota: nessuna scrittura.
        var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            "My24", new[] { componente }, requisiti, testCases, Array.Empty<ValutazioneRequisitoEcuVeicolo>());
        Assert.Empty(anteprima.DaPropagare);

        var risultato = ValutazioneRequisitoEcuVeicoloService.PropagaMassiva(
            tmp.Master.ValutazioniRequisitoEcuVeicolo, "My24", new[] { componente }, requisiti,
            testCases, null, anteprima.DaPropagare, "nota", "AB", out var errore);

        Assert.NotNull(errore);
        Assert.Empty(risultato.Propagate);
        Assert.Empty(tmp.Master.ValutazioniRequisitoEcuVeicolo.Load());
    }

    // --- Guardia nel servizio (review indipendente 2026-09-26, CRITICO) ---

    [Fact]
    public void PropagaMassiva_CellaInConflittoNellAnteprimaDiventaUnanime_TentativoDiForzarlaVieneScartato()
    {
        // Simula un chiamante che (per bug o per un futuro percorso non ancora scritto) costruisce a mano una
        // VocePropagazioneMassiva per una cella che l'anteprima aveva mostrato come CONFLITTO, usando uno dei
        // due punteggi discordanti originali come "atteso" (qui: quello di My26, Conforme=2). Fra il momento
        // in cui quell'attesa è stata costruita e la chiamata a PropagaMassiva, la cella diventa unanime — ma
        // con un punteggio DIVERSO (AmpiamenteConforme=3, non uno dei due originali): la guardia nel servizio
        // (confronto punteggio fresco vs atteso) la scarta comunque, perché vive nel servizio e non si fida
        // di ciò che il chiamante afferma di aver "visto" nell'anteprima.
        using var tmp = new TempWorkspace();
        var componente = EcuDi("BCM", "My24", "My26", "My28");
        var requisiti = new[] { ReqAttivo("R1") };

        var attesaForzataConPunteggioOriginale = new VocePropagazioneMassiva(
            "BCM", "R1", new CandidatoPropagazioneRequisito("My26", "T-A-VECCHIO", PunteggioConformita.Conforme, new DateTime(2026, 1, 1)));

        // Stato "fresco" al momento della conferma: entrambi i candidati ora concordano, ma su un terzo
        // valore diverso da quello che il chiamante si aspettava.
        var candidatoAFresco = TestDiretto("T-A", "BCM", "R1", "My26", PunteggioConformita.AmpiamenteConforme, new DateTime(2026, 3, 1));
        var candidatoBFresco = TestDiretto("T-B", "BCM", "R1", "My28", PunteggioConformita.AmpiamenteConforme, new DateTime(2026, 3, 1));
        var testCasesFreschi = new[] { candidatoAFresco, candidatoBFresco };

        var risultato = ValutazioneRequisitoEcuVeicoloService.PropagaMassiva(
            tmp.Master.ValutazioniRequisitoEcuVeicolo, "My24", new[] { componente }, requisiti,
            testCasesFreschi, null, new[] { attesaForzataConPunteggioOriginale }, "tentativo forzato", "AB", out var errore);

        Assert.Null(errore);
        Assert.Empty(risultato.Propagate); // MAI scritta: il punteggio fresco (3) non coincide con l'atteso (2)
        Assert.Contains(("BCM", "R1"), risultato.SaltatePerchèCambiate);
        Assert.Empty(tmp.Master.ValutazioniRequisitoEcuVeicolo.Load());
    }

    [Fact]
    public void PropagaMassiva_PunteggioUnanimeCambiaFraAnteprimaEConferma_Saltata_NonScritta()
    {
        // Anteprima: unica sorgente (My26), punteggio NonConforme=1.
        using var tmp = new TempWorkspace();
        var componente = EcuDi("BCM", "My24", "My26");
        var requisiti = new[] { ReqAttivo("R1") };
        var sorgenteAllAnteprima = TestDiretto("T-SRC", "BCM", "R1", "My26", PunteggioConformita.NonConforme, new DateTime(2026, 1, 1));

        var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            "My24", new[] { componente }, requisiti, new[] { sorgenteAllAnteprima }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());
        var voce = Assert.Single(anteprima.DaPropagare);
        Assert.Equal(PunteggioConformita.NonConforme, voce.Sorgente.PunteggioSorgente);

        // Fra anteprima e conferma, lo stesso Test viene ri-eseguito con un punteggio diverso (My26 resta
        // l'unica sorgente, ma non è più lo stesso valore mostrato all'operatore).
        var sorgenteAllaConferma = TestDiretto("T-SRC", "BCM", "R1", "My26", PunteggioConformita.ModeratamenteConforme, new DateTime(2026, 6, 1));

        var risultato = ValutazioneRequisitoEcuVeicoloService.PropagaMassiva(
            tmp.Master.ValutazioniRequisitoEcuVeicolo, "My24", new[] { componente }, requisiti,
            new[] { sorgenteAllaConferma }, null, anteprima.DaPropagare, "confermo", "AB", out var errore);

        Assert.Null(errore);
        Assert.Empty(risultato.Propagate);
        Assert.Contains(("BCM", "R1"), risultato.SaltatePerchèCambiate);
        Assert.Empty(tmp.Master.ValutazioniRequisitoEcuVeicolo.Load());
    }

    [Fact]
    public void PropagaMassiva_SorgenteDiversaMaStessoPunteggio_VieneScrittaConLaSorgenteFresca()
    {
        // Il confronto è sul PUNTEGGIO, non sull'identità della sorgente: se fra anteprima e conferma la
        // sorgente concreta cambia (es. un altro veicolo concorda con lo stesso valore, o lo stesso Test
        // viene ri-eseguito con esito identico ma data diversa) la propagazione resta valida — ma la riga
        // scritta registra la sorgente FRESCA, mai quella (potenzialmente stantia) dell'anteprima.
        using var tmp = new TempWorkspace();
        var componente = EcuDi("BCM", "My24", "My26", "My28");
        var requisiti = new[] { ReqAttivo("R1") };
        var soloMy26AllAnteprima = TestDiretto("T-26", "BCM", "R1", "My26", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));

        var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            "My24", new[] { componente }, requisiti, new[] { soloMy26AllAnteprima }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());
        var voce = Assert.Single(anteprima.DaPropagare);
        Assert.Equal("My26", voce.Sorgente.VeicoloSorgente);

        // Alla conferma è comparso anche My28 con lo STESSO punteggio (Conforme), più recente: ora concordano
        // entrambi, e DecidiPropagazioneMassiva scegli il più recente (My28).
        var my26 = TestDiretto("T-26", "BCM", "R1", "My26", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var my28Fresco = TestDiretto("T-28", "BCM", "R1", "My28", PunteggioConformita.Conforme, new DateTime(2026, 5, 1));

        var risultato = ValutazioneRequisitoEcuVeicoloService.PropagaMassiva(
            tmp.Master.ValutazioniRequisitoEcuVeicolo, "My24", new[] { componente }, requisiti,
            new[] { my26, my28Fresco }, null, anteprima.DaPropagare, "confermo", "AB", out var errore);

        Assert.Null(errore);
        var propagata = Assert.Single(risultato.Propagate);
        Assert.Equal("My28", propagata.Sorgente.VeicoloSorgente); // sorgente FRESCA, non "My26" dell'anteprima
        var riga = Assert.Single(tmp.Master.ValutazioniRequisitoEcuVeicolo.Load());
        Assert.Equal("My28", riga.SorgenteVeicolo);
    }

    // --- Candidati concordi: vince il più recente ---

    [Fact]
    public void DecidiPropagazioneMassiva_CandidatiConcordi_SceglieIlPiuRecente()
    {
        var cella = new RisoluzioneCellaRequisitoEcuVeicolo
        {
            RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Propagabile = true,
            CandidatiPropagazione = new Dictionary<string, CandidatoPropagazioneRequisito>(StringComparer.OrdinalIgnoreCase)
            {
                ["My26"] = new CandidatoPropagazioneRequisito("My26", "T1", PunteggioConformita.Conforme, new DateTime(2026, 1, 1)),
                ["My28"] = new CandidatoPropagazioneRequisito("My28", "T2", PunteggioConformita.Conforme, new DateTime(2026, 6, 1)),
            },
        };

        var voce = ValutazioneRequisitoEcuVeicoloService.DecidiPropagazioneMassiva(cella);

        Assert.NotNull(voce);
        Assert.Equal("My28", voce!.Sorgente.VeicoloSorgente); // 2026-06-01 è più recente di 2026-01-01
    }

    [Fact]
    public void DecidiPropagazioneMassiva_CandidatiConcordiEStessaData_SpareggioPerNomeVeicolo()
    {
        var stessaData = new DateTime(2026, 3, 1);
        var cella = new RisoluzioneCellaRequisitoEcuVeicolo
        {
            RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Propagabile = true,
            CandidatiPropagazione = new Dictionary<string, CandidatoPropagazioneRequisito>(StringComparer.OrdinalIgnoreCase)
            {
                ["My28"] = new CandidatoPropagazioneRequisito("My28", "T2", PunteggioConformita.Conforme, stessaData),
                ["My26"] = new CandidatoPropagazioneRequisito("My26", "T1", PunteggioConformita.Conforme, stessaData),
            },
        };

        var voce = ValutazioneRequisitoEcuVeicoloService.DecidiPropagazioneMassiva(cella);

        Assert.NotNull(voce);
        Assert.Equal("My26", voce!.Sorgente.VeicoloSorgente); // spareggio deterministico: "My26" < "My28"
    }

    // --- Nota obbligatoria ---

    [Fact]
    public void PropagaMassiva_SenzaNota_Rifiutata_NessunaScrittura()
    {
        using var tmp = new TempWorkspace();
        var componente = EcuDi("BCM", "My24", "My26");
        var requisiti = new[] { ReqAttivo("R1") };
        var sorgente = TestDiretto("T-SRC", "BCM", "R1", "My26", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            "My24", new[] { componente }, requisiti, new[] { sorgente }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        var risultato = ValutazioneRequisitoEcuVeicoloService.PropagaMassiva(
            tmp.Master.ValutazioniRequisitoEcuVeicolo, "My24", new[] { componente }, requisiti,
            new[] { sorgente }, null, anteprima.DaPropagare, "   ", "AB", out var errore);

        Assert.NotNull(errore);
        Assert.Empty(risultato.Propagate);
        Assert.Empty(tmp.Master.ValutazioniRequisitoEcuVeicolo.Load());
    }

    [Fact]
    public void PropagaMassiva_NessunaCellaAttesa_Rifiutata()
    {
        using var tmp = new TempWorkspace();
        var componente = EcuDi("BCM", "My24", "My26");

        var risultato = ValutazioneRequisitoEcuVeicoloService.PropagaMassiva(
            tmp.Master.ValutazioniRequisitoEcuVeicolo, "My24", new[] { componente }, new[] { ReqAttivo("R1") },
            Array.Empty<TestCase>(), null, Array.Empty<VocePropagazioneMassiva>(), "nota", "AB", out var errore);

        Assert.NotNull(errore);
        Assert.Empty(risultato.Propagate);
    }

    // --- Variante "tutti i componenti" ---

    [Fact]
    public void Anteprima_TutteLeEcuDelVeicolo_ConsideraOgniEcuPassata()
    {
        var ecuBcm = EcuDi("BCM", "My24", "My26");
        var ecuTcu = EcuDi("TCU", "My24", "My26");
        var testBcm = TestDiretto("T-BCM", "BCM", "R1", "My26", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var testTcu = TestDiretto("T-TCU", "TCU", "R1", "My26", PunteggioConformita.NonConforme, new DateTime(2026, 1, 1));

        var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            "My24", new[] { ecuBcm, ecuTcu }, new[] { ReqAttivo("R1") }, new[] { testBcm, testTcu }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        Assert.Equal(2, anteprima.DaPropagare.Count);
        Assert.Contains(anteprima.DaPropagare, v => v.Componente == "BCM");
        Assert.Contains(anteprima.DaPropagare, v => v.Componente == "TCU");
    }

    // --- L'anteprima coincide con ciò che viene salvato ---

    [Fact]
    public void PropagaMassiva_SenzaScritturaConcorrente_SalvaEsattamenteQuelloCheAnteprimaMostrava()
    {
        using var tmp = new TempWorkspace();
        var ecuBcm = EcuDi("BCM", "My24", "My26", "My28");
        var ecuTcu = EcuDi("TCU", "My24", "My26");
        var srcBcm = TestDiretto("T-BCM", "BCM", "R1", "My26", PunteggioConformita.AmpiamenteConforme, new DateTime(2026, 1, 1));
        var srcTcu = TestDiretto("T-TCU", "TCU", "R2", "My26", PunteggioConformita.ModeratamenteConforme, new DateTime(2026, 2, 1));
        var testCases = new[] { srcBcm, srcTcu };
        var requisiti = new[] { ReqAttivo("R1"), ReqAttivo("R2") };

        var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            "My24", new[] { ecuBcm, ecuTcu }, requisiti, testCases, Array.Empty<ValutazioneRequisitoEcuVeicolo>());
        Assert.Equal(2, anteprima.DaPropagare.Count);

        var risultato = ValutazioneRequisitoEcuVeicoloService.PropagaMassiva(
            tmp.Master.ValutazioniRequisitoEcuVeicolo, "My24", new[] { ecuBcm, ecuTcu }, requisiti, testCases, null, anteprima.DaPropagare, "confermo tutto", "AB", out var errore);

        Assert.Null(errore);
        Assert.Equal(anteprima.DaPropagare.Count, risultato.Propagate.Count);
        var salvate = tmp.Master.ValutazioniRequisitoEcuVeicolo.Load();
        Assert.Equal(2, salvate.Count);
        foreach (var voce in anteprima.DaPropagare)
        {
            var riga = salvate.Single(v => v.Componente == voce.Componente && v.RequisitoId == voce.RequisitoId);
            Assert.Equal(voce.Sorgente.PunteggioSorgente, riga.Punteggio);
            Assert.Equal(voce.Sorgente.VeicoloSorgente, riga.SorgenteVeicolo);
            Assert.Equal(OrigineValutazioneRequisito.Propagata, riga.Origine);
        }
        Assert.Empty(risultato.SaltatePerchèCambiate);
        Assert.Empty(risultato.ConflittiRimasti);
    }

    // --- Concorrenza: un'altra postazione scrive fra anteprima e conferma ---

    [Fact]
    public void PropagaMassiva_AltraPostazioneScriveManualeFraAnteprimaEConferma_QuellaCellaSaltata_LeAltrePropagate()
    {
        using var tmp = new TempWorkspace();
        var componente = EcuDi("BCM", "My24", "My26", "My28");
        var srcR1 = TestDiretto("T-R1", "BCM", "R1", "My26", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var srcR2 = TestDiretto("T-R2", "BCM", "R2", "My26", PunteggioConformita.NonConforme, new DateTime(2026, 1, 1));
        var testCases = new[] { srcR1, srcR2 };
        var requisiti = new[] { ReqAttivo("R1"), ReqAttivo("R2") };

        // Anteprima calcolata dalla "postazione 1": entrambe R1 e R2 sono "!" propagabili.
        var repoPostazione1 = tmp.Master.ValutazioniRequisitoEcuVeicolo;
        var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            "My24", new[] { componente }, requisiti, testCases, repoPostazione1.Load());
        Assert.Equal(2, anteprima.DaPropagare.Count);

        // Nel frattempo un'altra postazione, sulla stessa cartella condivisa, risolve R1 a mano.
        var repoPostazione2 = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repoPostazione2, "R1", "BCM", new[] { "My24" }, PunteggioConformita.NonApplicabile, "deciso a mano dall'altra postazione", "PC2", null, out _));

        // La postazione 1 confermava la Propagazione massiva SULL'ANTEPRIMA originale (R1+R2): R1 non è più
        // "!" (ora Manuale di PC2), quindi va saltata; R2 è ancora "!" e viene propagata normalmente.
        var risultato = ValutazioneRequisitoEcuVeicoloService.PropagaMassiva(
            repoPostazione1, "My24", new[] { componente }, requisiti, testCases, null, anteprima.DaPropagare, "confermo dalla postazione 1", "PC1", out var errore);

        Assert.Null(errore);
        Assert.Single(risultato.Propagate);
        Assert.Equal("R2", risultato.Propagate[0].RequisitoId);
        Assert.Contains(("BCM", "R1"), risultato.SaltatePerchèCambiate);

        var statoFinale = repoPostazione1.Load();
        Assert.Equal(2, statoFinale.Count);
        var r1 = statoFinale.Single(v => v.RequisitoId == "R1");
        Assert.Equal(OrigineValutazioneRequisito.Manuale, r1.Origine); // la scrittura di PC2 non è stata sovrascritta
        Assert.Equal("deciso a mano dall'altra postazione", r1.Nota);
        var r2 = statoFinale.Single(v => v.RequisitoId == "R2");
        Assert.Equal(OrigineValutazioneRequisito.Propagata, r2.Origine);
        Assert.Equal("confermo dalla postazione 1", r2.Nota);
    }

    private sealed class TempWorkspace : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "TestLoom_PropagaMassiva_" + Guid.NewGuid().ToString("N"));
        public readonly DataPaths Paths;
        public readonly MasterDataStore Master;

        public TempWorkspace()
        {
            Paths = new DataPaths(Root);
            Paths.EnsureStructure();
            PhaseDMasterBootstrapService.EnsureReady(Paths);
            Master = new MasterDataStore(Paths);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
