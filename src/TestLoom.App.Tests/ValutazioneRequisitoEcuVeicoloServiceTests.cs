using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Piano 6.3.1 §1: copertura della risoluzione cella (Requisito×componente×Veicolo), della persistenza
/// Phase D del nuovo catalogo, dell'aggiornamento via Unisci/Rinomina (MasterRiferimentiService) e della
/// retro-compatibilità con un client/workspace che non conosce ancora il catalogo.</summary>
public sealed class ValutazioneRequisitoEcuVeicoloServiceTests
{
    private static Componente EcuDi(string nome, params string[] veicoli) => new() { Nome = nome, VeicoliAssociati = veicoli.ToList() };

    private static TestCase TestDiretto(string id, string componente, string requisitoId, string veicolo, PunteggioConformita punteggio, DateTime data)
    {
        var tc = new TestCase { Id = id, Componente = componente, RequisitoId = { requisitoId }, Veicolo = veicolo };
        tc.Esecuzioni.Add(new Esecuzione { Data = data, Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = requisitoId, Valore = punteggio } } });
        return tc;
    }

    /// <summary>Test SENZA veicolo (Veicolo="" e Veicoli vuoto — dati storici, es. cartella legacy
    /// `_SENZA_GAMMA`, creati prima che il campo Veicolo esistesse): VeicoliEffettivi resta vuoto.</summary>
    private static TestCase TestSenzaVeicolo(string id, string componente, string requisitoId, PunteggioConformita punteggio, DateTime data)
    {
        var tc = new TestCase { Id = id, Componente = componente, RequisitoId = { requisitoId } };
        tc.Esecuzioni.Add(new Esecuzione { Data = data, Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = requisitoId, Valore = punteggio } } });
        return tc;
    }

    // --- RisolviCella: fonte con timestamp più recente tra diretto, manuale e propagata; poi "!" / vuoto ---

    [Fact]
    public void RisolviCella_TestDirettoSulVeicolo_Vince()
    {
        var componente = EcuDi("BCM", "My24", "My26");
        var test = TestDiretto("T1", "BCM", "R1", "My24", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));

        var r = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", new[] { test }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        Assert.True(r.Diretto);
        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Diretto, r.Origine);
        Assert.Equal(PunteggioConformita.Conforme, r.Punteggio);
        Assert.False(r.Propagabile);
    }

    [Fact]
    public void RisolviCella_TestMultiVeicolo_RisolveDirettoSuOgniVeicoloDichiarato()
    {
        var componente = EcuDi("BCM", "My24", "My26");
        var tc = new TestCase { Id = "T1", Componente = "BCM", RequisitoId = { "R1" }, Veicoli = { "My24", "My26" } };
        tc.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 1, 1), Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.AmpiamenteConforme } } });

        var rMy24 = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", new[] { tc }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());
        var rMy26 = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My26", new[] { tc }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        Assert.True(rMy24.Diretto);
        Assert.True(rMy26.Diretto);
        Assert.Equal(PunteggioConformita.AmpiamenteConforme, rMy24.Punteggio);
        Assert.Equal(PunteggioConformita.AmpiamenteConforme, rMy26.Punteggio);
    }

    [Fact]
    public void RisolviCella_NessunTestSulVeicoloMaAltroVeicoloHaValore_Propagabile()
    {
        var componente = EcuDi("BCM", "My24", "My26");
        var test = TestDiretto("T1", "BCM", "R1", "My26", PunteggioConformita.ModeratamenteConforme, new DateTime(2026, 1, 1));

        var r = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", new[] { test }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        Assert.False(r.Diretto);
        Assert.Null(r.Punteggio);
        Assert.True(r.Propagabile);
        var candidato = Assert.Single(r.CandidatiPropagazione);
        Assert.Equal("My26", candidato.Key);
        Assert.Equal(PunteggioConformita.ModeratamenteConforme, candidato.Value.PunteggioSorgente);
        Assert.Equal("T1", candidato.Value.TestIdSorgente);
    }

    // --- Test SENZA veicolo (dati storici, es. _SENZA_GAMMA): valore componente-generico, mai Diretto, sempre "!" ---

    [Fact]
    public void RisolviCella_TestSenzaVeicolo_NonEMaiDiretto_MaEPropagabile()
    {
        var componente = EcuDi("BCM", "My24", "My26");
        var test = TestSenzaVeicolo("T-LEGACY", "BCM", "R1", PunteggioConformita.Conforme, new DateTime(2020, 1, 1));

        var r = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", new[] { test }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        Assert.False(r.Diretto);
        Assert.False(r.ConflittoDiretto);
        Assert.False(r.TestCreatoNonEseguito);
        Assert.Null(r.Punteggio);
        Assert.True(r.Propagabile);
        var candidato = Assert.Single(r.CandidatiPropagazione);
        Assert.Equal(ValutazioneRequisitoEcuVeicoloService.SorgenteVeicoloEcuGenerica, candidato.Key);
        Assert.Equal(PunteggioConformita.Conforme, candidato.Value.PunteggioSorgente);
        Assert.Equal("T-LEGACY", candidato.Value.TestIdSorgente);
    }

    [Fact]
    public void RisolviCella_TestSenzaVeicolo_EPropagabilePerQualsiasiVeicoloDellEcu()
    {
        var componente = EcuDi("BCM", "My24", "My26", "My28");
        var test = TestSenzaVeicolo("T-LEGACY", "BCM", "R1", PunteggioConformita.Conforme, new DateTime(2020, 1, 1));

        foreach (var veicolo in componente.VeicoliAssociati)
        {
            var r = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, veicolo, new[] { test }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());
            Assert.True(r.Propagabile);
            Assert.False(r.Diretto);
        }
    }

    [Fact]
    public void RisolviCella_TestSenzaVeicoloEUnAltroVeicoloConValoreProprio_ProponeEntrambeLeSorgenti()
    {
        var componente = EcuDi("BCM", "My24", "My26");
        var legacy = TestSenzaVeicolo("T-LEGACY", "BCM", "R1", PunteggioConformita.Conforme, new DateTime(2020, 1, 1));
        var diretto = TestDiretto("T-MY26", "BCM", "R1", "My26", PunteggioConformita.NonConforme, new DateTime(2026, 1, 1));

        var r = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", new[] { legacy, diretto }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        Assert.True(r.Propagabile);
        Assert.Equal(2, r.CandidatiPropagazione.Count);
        Assert.Equal(PunteggioConformita.NonConforme, r.CandidatiPropagazione["My26"].PunteggioSorgente);
        Assert.Equal(PunteggioConformita.Conforme, r.CandidatiPropagazione[ValutazioneRequisitoEcuVeicoloService.SorgenteVeicoloEcuGenerica].PunteggioSorgente);
    }

    [Fact]
    public void RisolviCella_DueTestSenzaVeicoloDiscordanti_NessunaSorgenteEcuGenericaProposta()
    {
        var componente = EcuDi("BCM", "My24", "My26");
        var legacy1 = TestSenzaVeicolo("T-LEGACY-1", "BCM", "R1", PunteggioConformita.Conforme, new DateTime(2020, 1, 1));
        var legacy2 = TestSenzaVeicolo("T-LEGACY-2", "BCM", "R1", PunteggioConformita.NonConforme, new DateTime(2020, 6, 1));

        var r = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", new[] { legacy1, legacy2 }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        Assert.False(r.Propagabile);
        Assert.Empty(r.CandidatiPropagazione);
    }

    [Fact]
    public void RisolviCella_TestConVeicoliMultipliCheIncludeQuestoVeicolo_ERestaDiretto_NonEcuGenerico()
    {
        // Un Test con più veicoli espliciti (Veicoli non vuoto) NON è il caso "Componente-generico": copre
        // esplicitamente quei veicoli, propagazione interna al Test invariata (Piano 6.3.1 §1).
        var componente = EcuDi("BCM", "My24", "My26");
        var tc = new TestCase { Id = "T-MULTI", Componente = "BCM", RequisitoId = { "R1" }, Veicoli = { "My24", "My26" } };
        tc.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 1, 1), Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.Conforme } } });

        var r = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", new[] { tc }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        Assert.True(r.Diretto);
        Assert.False(r.Propagabile);
    }

    [Fact]
    public void RisolviCella_ValutazionePropagataSalvata_UsaOrigineESorgente()
    {
        var componente = EcuDi("BCM", "My24", "My26");
        var val = new ValutazioneRequisitoEcuVeicolo
        {
            RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme,
            Origine = OrigineValutazioneRequisito.Propagata, SorgenteVeicolo = "My26", SorgenteTestId = "T1",
            SorgentePunteggio = PunteggioConformita.Conforme, Nota = "confermato", Operatore = "AB",
            UltimaModifica = new DateTime(2026, 2, 1),
        };

        var r = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", Array.Empty<TestCase>(), new[] { val });

        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Propagata, r.Origine);
        Assert.Equal(PunteggioConformita.Conforme, r.Punteggio);
        Assert.NotNull(r.Tooltip);
        Assert.Contains("My26", r.Tooltip);
    }

    [Fact]
    public void RisolviCella_ValutazioneManualeSalvata_UsaOrigineManuale()
    {
        var componente = EcuDi("BCM", "My24");
        var val = new ValutazioneRequisitoEcuVeicolo
        {
            RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.NonApplicabile,
            Origine = OrigineValutazioneRequisito.Manuale, Nota = "non applicabile su questo veicolo", Operatore = "AB",
            UltimaModifica = new DateTime(2026, 2, 1),
        };

        var r = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", Array.Empty<TestCase>(), new[] { val });

        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Manuale, r.Origine);
        Assert.Equal(PunteggioConformita.NonApplicabile, r.Punteggio);
        Assert.Contains("manuale", r.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RisolviCella_NessunaFonte_NonPropagabile()
    {
        var componente = EcuDi("BCM", "My24", "My26");

        var r = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", Array.Empty<TestCase>(), Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        Assert.False(r.Diretto);
        Assert.False(r.Propagabile);
        Assert.Null(r.Punteggio);
        Assert.Empty(r.CandidatiPropagazione);
    }

    [Fact]
    public void RisolviCella_ConflittoTraTestDirettiSulloStessoVeicolo_SegnalaConflitto()
    {
        var componente = EcuDi("BCM", "My24");
        var t1 = TestDiretto("T1", "BCM", "R1", "My24", PunteggioConformita.Conforme, new DateTime(2026, 2, 1));
        var t2 = TestDiretto("T2", "BCM", "R1", "My24", PunteggioConformita.NonConforme, new DateTime(2026, 2, 1));

        var r = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", new[] { t1, t2 }, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        Assert.True(r.ConflittoDiretto);
        Assert.Null(r.Punteggio);
        Assert.Equal(2, r.CandidatiConflittoDiretto.Count);
    }

    [Fact]
    public void RisolviCella_ConflittoDirettoConRisoluzioneValida_UsaIlTestScelto()
    {
        var componente = EcuDi("BCM", "My24");
        var t1 = TestDiretto("T1", "BCM", "R1", "My24", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));
        var t2 = TestDiretto("T2", "BCM", "R1", "My24", PunteggioConformita.NonConforme, new DateTime(2026, 2, 1));
        var risoluzione = new RisoluzioneConflittoAudit { Componente = "BCM", RequisitoId = "R1", TestCaseIdValido = "T1" };

        var r = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", new[] { t1, t2 }, Array.Empty<ValutazioneRequisitoEcuVeicolo>(), risoluzione);

        Assert.True(r.Diretto);
        Assert.Equal(PunteggioConformita.Conforme, r.Punteggio);
    }

    [Fact]
    public void RisolviCella_ManualePiuRecenteDelTest_Vince()
    {
        var componente = EcuDi("BCM", "My24");
        var dataTest = new DateTime(2026, 2, 1);
        var manuale = new ValutazioneRequisitoEcuVeicolo
        {
            RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.NonConforme,
            Origine = OrigineValutazioneRequisito.Manuale, Nota = "corretto a mano", UltimaModifica = dataTest.AddDays(1),
        };

        var risolta = ValutazioneRequisitoEcuVeicoloService.RisolviCella(
            "R1", componente, "My24",
            new[] { TestDiretto("T1", "BCM", "R1", "My24", PunteggioConformita.Conforme, dataTest) },
            new[] { manuale });

        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Manuale, risolta.Origine);
        Assert.Equal(PunteggioConformita.NonConforme, risolta.Punteggio);
    }

    [Fact]
    public void RisolviCella_TestPiuRecenteDelManuale_Vince()
    {
        var componente = EcuDi("BCM", "My24");
        var dataTest = new DateTime(2026, 2, 2);
        var manuale = new ValutazioneRequisitoEcuVeicolo
        {
            RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.NonConforme,
            Origine = OrigineValutazioneRequisito.Manuale, Nota = "valore precedente", UltimaModifica = dataTest.AddDays(-1),
        };

        var risolta = ValutazioneRequisitoEcuVeicoloService.RisolviCella(
            "R1", componente, "My24",
            new[] { TestDiretto("T1", "BCM", "R1", "My24", PunteggioConformita.Conforme, dataTest) },
            new[] { manuale });

        Assert.Equal(OrigineCellaRequisitoEcuVeicolo.Diretto, risolta.Origine);
        Assert.Equal(PunteggioConformita.Conforme, risolta.Punteggio);
    }

    [Fact]
    public void RisolviCella_FontiConTimestampPari_RestaInConflitto()
    {
        var componente = EcuDi("BCM", "My24");
        var data = new DateTime(2026, 2, 1);
        var manuale = new ValutazioneRequisitoEcuVeicolo
        {
            RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.NonConforme,
            Origine = OrigineValutazioneRequisito.Manuale, Nota = "manuale", UltimaModifica = data,
        };

        var risolta = ValutazioneRequisitoEcuVeicoloService.RisolviCella(
            "R1", componente, "My24",
            new[] { TestDiretto("T1", "BCM", "R1", "My24", PunteggioConformita.Conforme, data) },
            new[] { manuale });

        Assert.True(risolta.ConflittoDiretto);
        Assert.Contains("timestamp pari", risolta.DettaglioConflitto, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CostruisciStoricoCella_UnisceOriginiESaltaRevSenzaSnapshotDettagli()
    {
        var dataTestConSnapshot = new DateTime(2026, 1, 2, 10, 0, 0);
        var dataTestSenzaSnapshot = new DateTime(2026, 1, 1, 10, 0, 0);
        var vecchioManuale = new SnapshotValutazioneAudit
        {
            EventId = Guid.NewGuid(), Origine = OrigineStoricoValutazioneAudit.Manuale,
            Punteggio = PunteggioConformita.NonConforme, Data = new DateTime(2025, 12, 1), Nota = "manuale precedente",
        };
        var vecchiaLegacy = new SnapshotValutazioneAudit
        {
            EventId = Guid.NewGuid(), Origine = OrigineStoricoValutazioneAudit.Legacy,
            Punteggio = PunteggioConformita.ParzialmenteConforme, Data = new DateTime(2025, 11, 1), Nota = "legacy precedente",
        };
        var valutazione = new ValutazioneRequisitoEcuVeicolo
        {
            RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme,
            Origine = OrigineValutazioneRequisito.Propagata, UltimaModifica = new DateTime(2026, 2, 1),
            EventoCorrenteId = Guid.NewGuid(), Nota = "propagata corrente", Storico = { vecchioManuale },
        };
        var risoluzioneLegacy = new RisoluzioneConflittoAudit
        {
            Componente = "BCM", RequisitoId = "R1", TestCaseIdValido = "TC-PRESERVATO",
            PunteggioManuale = PunteggioConformita.AmpiamenteConforme, UltimaModifica = new DateTime(2026, 3, 1),
            EventoPunteggioManualeCorrenteId = Guid.NewGuid(), Motivazione = "legacy corrente",
            StoricoPunteggiManuali = { vecchiaLegacy },
        };

        var testConSnapshot = new TestCase { Id = "TC-REV", Componente = "ALTRO", Veicolo = "Altro" };
        testConSnapshot.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 4, Data = dataTestConSnapshot, Operatore = "Operatore REV", Note = "nota REV",
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.ModeratamenteConforme } },
            Dettagli = new DettagliTestSnapshot { Componente = new EcuSelection(new[] { "BCM" }), Veicoli = { "My24" } },
        });
        var testSenzaSnapshot = new TestCase { Id = "TC-LEGACY", Componente = "BCM", Veicolo = "My24" };
        testSenzaSnapshot.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = dataTestSenzaSnapshot,
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.Conforme } },
        });

        var storia = ValutazioneRequisitoEcuVeicoloService.CostruisciStoricoCella(
            "R1", "BCM", "My24", new[] { testConSnapshot, testSenzaSnapshot },
            new[] { valutazione }, new[] { risoluzioneLegacy });

        Assert.Contains(storia, e => e.Origine == OrigineStoricoValutazioneAudit.Test &&
            e.TestCaseId == "TC-REV" && e.IterazioneTest == 4 && e.Data == dataTestConSnapshot && e.Nota == "nota REV");
        Assert.DoesNotContain(storia, e => e.TestCaseId == "TC-LEGACY");
        Assert.Contains(storia, e => e.EventId == vecchioManuale.EventId && e.Origine == OrigineStoricoValutazioneAudit.Manuale);
        Assert.Contains(storia, e => e.EventId == valutazione.EventoCorrenteId && e.Origine == OrigineStoricoValutazioneAudit.Propagata);
        Assert.Contains(storia, e => e.EventId == vecchiaLegacy.EventId && e.Origine == OrigineStoricoValutazioneAudit.Legacy);
        Assert.Contains(storia, e => e.EventId == risoluzioneLegacy.EventoPunteggioManualeCorrenteId &&
            e.Origine == OrigineStoricoValutazioneAudit.Legacy);
        Assert.Equal(5, storia.Count);
        Assert.Equal("TC-PRESERVATO", risoluzioneLegacy.TestCaseIdValido);
    }

    // --- ImpostaManuale: singola coppia e sottoinsieme di veicoli, con optimistic-concurrency ---

    [Fact]
    public void ImpostaManuale_SottoinsiemeDiVeicoli_CreaUnaRigaPerVeicoloConStessaNotaEOperatore()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);

        var ok = ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", "BCM", new[] { "My24", "My26" }, PunteggioConformita.AmpiamenteConforme,
            "verificato a banco su entrambi", "AB", baselineSw: "1.2.3", out var errore);

        Assert.True(ok, errore);
        var salvate = repo.Load();
        Assert.Equal(2, salvate.Count);
        Assert.All(salvate, v =>
        {
            Assert.Equal(OrigineValutazioneRequisito.Manuale, v.Origine);
            Assert.Equal(PunteggioConformita.AmpiamenteConforme, v.Punteggio);
            Assert.Equal("1.2.3", v.BaselineSw);
            Assert.Equal("verificato a banco su entrambi", v.Nota);
        });
        Assert.Contains(salvate, v => v.Veicolo == "My24");
        Assert.Contains(salvate, v => v.Veicolo == "My26");
    }

    [Fact]
    public void ImpostaManuale_SenzaNota_Rifiutato()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);

        var ok = ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", "BCM", new[] { "My24" }, PunteggioConformita.Conforme, "", "AB", null, out var errore);

        Assert.False(ok);
        Assert.NotNull(errore);
        Assert.Empty(repo.Load());
    }

    [Fact]
    public void ImpostaManuale_RipetutaSullaStessaCella_SostituisceNonAccumula()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);

        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(repo, "R1", "BCM", new[] { "My24" }, PunteggioConformita.Conforme, "prima", "AB", null, out _));
        var prima = Assert.Single(repo.Load());
        var uid = prima.Uid;
        var eventoIniziale = prima.EventoCorrenteId;
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(repo, "R1", "BCM", new[] { "My24" }, PunteggioConformita.NonConforme, "corretto", "CD", null, out _));

        var salvata = Assert.Single(repo.Load());
        Assert.Equal(PunteggioConformita.NonConforme, salvata.Punteggio);
        Assert.Equal("corretto", salvata.Nota);
        Assert.Equal(uid, salvata.Uid);
        Assert.NotEqual(eventoIniziale, salvata.EventoCorrenteId);
        var eventoArchiviato = Assert.Single(salvata.Storico);
        Assert.Equal(PunteggioConformita.Conforme, eventoArchiviato.Punteggio);
        Assert.Equal("prima", eventoArchiviato.Nota);
        Assert.Equal(OrigineStoricoValutazioneAudit.Manuale, eventoArchiviato.Origine);
    }

    [Fact]
    public void ImpostaManualeMassivaAudit_CompilaECorreggeInUnSoloBatchConConferma()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var celle = new[]
        {
            // Simula un valore diretto Test o legacy globale: nessuna riga locale da cancellare, ma serve conferma.
            new CellaApplicazioneManualeAudit("BCM", "R1", "My24", Sovrascrive: true, HaValutazioneVeicoloSalvata: false),
            new CellaApplicazioneManualeAudit("ABS", "R1", "My24", Sovrascrive: false, HaValutazioneVeicoloSalvata: false),
        };
        var attese = ValutazioneRequisitoEcuVeicoloService.CatturaApplicazioneManualeAudit(repo, celle);

        var senzaConferma = ValutazioneRequisitoEcuVeicoloService.ImpostaManualeMassivaAudit(
            repo, attese, PunteggioConformita.NonConforme, "sovrascrittura autorizzata", "AB", "4.2", false,
            out var celleApplicateSenzaConferma, out _);

        Assert.False(senzaConferma);
        Assert.Equal(0, celleApplicateSenzaConferma);
        Assert.Empty(repo.Load()); // il rifiuto non applica nemmeno la cella vuota del batch

        var salvato = ValutazioneRequisitoEcuVeicoloService.ImpostaManualeMassivaAudit(
            repo, attese, PunteggioConformita.NonConforme, "sovrascrittura autorizzata", "AB", "4.2", true,
            out var celleApplicate, out var errore);

        Assert.True(salvato, errore);
        Assert.Equal(2, celleApplicate);
        var righe = repo.Load();
        Assert.Equal(2, righe.Count);
        Assert.All(righe, r =>
        {
            Assert.Equal("My24", r.Veicolo);
            Assert.Equal(OrigineValutazioneRequisito.Manuale, r.Origine);
            Assert.Equal(PunteggioConformita.NonConforme, r.Punteggio);
            Assert.Equal("sovrascrittura autorizzata", r.Nota);
            Assert.Equal("4.2", r.BaselineSw);
        });
        Assert.Contains(righe, r => r.Componente == "BCM");
        Assert.Contains(righe, r => r.Componente == "ABS");
    }

    [Fact]
    public void ImpostaManualeMassivaAudit_FotografiaObsoleta_NonScriveParzialmente()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var cella = new CellaApplicazioneManualeAudit("BCM", "R1", "My24", Sovrascrive: true, HaValutazioneVeicoloSalvata: true);
        var attese = ValutazioneRequisitoEcuVeicoloService.CatturaApplicazioneManualeAudit(repo, new[] { cella });

        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", "BCM", new[] { "My24" }, PunteggioConformita.Conforme, "scrittura concorrente", "CD", null, out _));

        var obsoleto = ValutazioneRequisitoEcuVeicoloService.ImpostaManualeMassivaAudit(
            repo, attese, PunteggioConformita.NonConforme, "batch superato", "AB", null, true,
            out var celleApplicate, out _);

        Assert.False(obsoleto);
        Assert.Equal(0, celleApplicate);
        var riga = Assert.Single(repo.Load());
        Assert.Equal(PunteggioConformita.Conforme, riga.Punteggio);
        Assert.Equal("scrittura concorrente", riga.Nota);
    }

    [Fact]
    public void ImpostaManualeMassivaAudit_FonteTestAggiuntaDopoAnteprima_FallisceSenzaScrivere()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var cella = new CellaApplicazioneManualeAudit("BCM", "R1", "My24", Sovrascrive: false, HaValutazioneVeicoloSalvata: false);
        var attese = ValutazioneRequisitoEcuVeicoloService.CatturaApplicazioneManualeAudit(
            repo, new[] { cella }, Array.Empty<TestCase>(), Array.Empty<RisoluzioneConflittoAudit>());
        var testNuovo = new TestCase { Id = "TC-NUOVO", Componente = "BCM", Veicolo = "My24", RequisitoId = { "R1" } };
        testNuovo.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = DateTime.Now, Esito = Esito.NonConforme,
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.NonConforme } },
        });

        var applicato = ValutazioneRequisitoEcuVeicoloService.ImpostaManualeMassivaAudit(
            repo, attese, PunteggioConformita.Conforme, "bulk obsoleto", "AB", null, true,
            new[] { testNuovo }, Array.Empty<RisoluzioneConflittoAudit>(), out var celleApplicate, out var errore);

        Assert.False(applicato);
        Assert.Contains("fonti", errore, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, celleApplicate);
        Assert.Empty(repo.Load());
    }

    [Fact]
    public void ImpostaManualeMassivaAudit_RefreshAutorevoleTrovaTestNuovoPrimaDelCommit_FallisceSenzaScrivere()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var testAllaPreview = Array.Empty<TestCase>();
        var risoluzioniAllaPreview = Array.Empty<RisoluzioneConflittoAudit>();
        var cella = new CellaApplicazioneManualeAudit("BCM", "R1", "My24", Sovrascrive: false, HaValutazioneVeicoloSalvata: false);
        var attese = ValutazioneRequisitoEcuVeicoloService.CatturaApplicazioneManualeAudit(
            repo, new[] { cella }, testAllaPreview, risoluzioniAllaPreview);
        var testArrivatoSuAltraPostazione = TestDiretto(
            "TC-NUOVO-REMOTO", "BCM", "R1", "My24", PunteggioConformita.NonConforme, new DateTime(2026, 10, 15, 12, 0, 0));
        var refreshEseguito = false;

        var applicato = ValutazioneRequisitoEcuVeicoloService.ImpostaManualeMassivaAudit(
            repo, attese, PunteggioConformita.Conforme, "conferma su fotografia superata", "AB", null, true,
            testAllaPreview, risoluzioniAllaPreview, out var celleApplicate, out var errore,
            () =>
            {
                refreshEseguito = true;
                return (new[] { testArrivatoSuAltraPostazione }, risoluzioniAllaPreview);
            },
            new DateTime(2026, 10, 15, 12, 0, 1));

        Assert.True(refreshEseguito);
        Assert.False(applicato);
        Assert.Contains("fonti Test", errore, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, celleApplicate);
        Assert.Empty(repo.Load());
    }

    [Fact]
    public void ImpostaManualeMassivaAudit_RefreshAutorevoleFallito_FallisceChiusaSenzaScrivere()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var testsAllaPreview = Array.Empty<TestCase>();
        var risoluzioniAllaPreview = Array.Empty<RisoluzioneConflittoAudit>();
        var cella = new CellaApplicazioneManualeAudit("BCM", "R1", "My24", Sovrascrive: false, HaValutazioneVeicoloSalvata: false);
        var attese = ValutazioneRequisitoEcuVeicoloService.CatturaApplicazioneManualeAudit(
            repo, new[] { cella }, testsAllaPreview, risoluzioniAllaPreview);

        var applicato = ValutazioneRequisitoEcuVeicoloService.ImpostaManualeMassivaAudit(
            repo, attese, PunteggioConformita.Conforme, "refresh non disponibile", "AB", null, true,
            testsAllaPreview, risoluzioniAllaPreview, out var celleApplicate, out var errore,
            () => throw new IOException("refresh Test non riuscito"),
            new DateTime(2026, 10, 15, 12, 0, 1));

        Assert.False(applicato);
        Assert.Contains("refresh Test non riuscito", errore, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, celleApplicate);
        Assert.Empty(repo.Load());
    }

    [Fact]
    public void ImpostaManualeMassivaAudit_LegacyAggiuntaDopoAnteprima_FallisceSenzaScrivere()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var cella = new CellaApplicazioneManualeAudit("ABS", "R1", "My24", Sovrascrive: false, HaValutazioneVeicoloSalvata: false);
        var vuote = Array.Empty<TestCase>();
        var attese = ValutazioneRequisitoEcuVeicoloService.CatturaApplicazioneManualeAudit(
            repo, new[] { cella }, vuote, Array.Empty<RisoluzioneConflittoAudit>());
        var legacy = new RisoluzioneConflittoAudit
        {
            Componente = "ABS", RequisitoId = "R1", PunteggioManuale = PunteggioConformita.NonConforme,
            Motivazione = "nuovo valore legacy", UltimaModifica = DateTime.Now, ModificatoDa = "CD",
        };

        var applicato = ValutazioneRequisitoEcuVeicoloService.ImpostaManualeMassivaAudit(
            repo, attese, PunteggioConformita.Conforme, "bulk obsoleto", "AB", null, true,
            vuote, new[] { legacy }, out var celleApplicate, out var errore);

        Assert.False(applicato);
        Assert.Contains("fonti", errore, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, celleApplicate);
        Assert.Empty(repo.Load());
    }

    [Fact]
    public void Propaga_SalvaOrigineESorgente()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var sorgente = new CandidatoPropagazioneRequisito("My26", "T1", PunteggioConformita.Conforme, new DateTime(2026, 1, 1));

        var ok = ValutazioneRequisitoEcuVeicoloService.Propaga(
            repo, "R1", "BCM", new[] { ("My24", sorgente) }, "confermo identico", "AB", out var errore);

        Assert.True(ok, errore);
        var salvata = Assert.Single(repo.Load());
        Assert.Equal(OrigineValutazioneRequisito.Propagata, salvata.Origine);
        Assert.Equal("My26", salvata.SorgenteVeicolo);
        Assert.Equal("T1", salvata.SorgenteTestId);
        Assert.Equal(PunteggioConformita.Conforme, salvata.Punteggio);
        Assert.Equal("confermo identico", salvata.Nota);
    }

    [Fact]
    public void PropagaVerificando_TestAggiornatoPrimaDelCommit_FallisceSenzaScrivere()
    {
        using var tmp = new TempWorkspace(bootstrapPhaseD: true);
        var repo = tmp.Master.ValutazioniRequisitoEcuVeicolo;
        var componente = EcuDi("BCM", "My24", "My26");
        var dataConferma = new DateTime(2026, 7, 1, 12, 0, 0);
        var sorgente = TestDiretto("T-SRC", "BCM", "R1", "My26", PunteggioConformita.Conforme, dataConferma.AddDays(-1));
        IReadOnlyList<TestCase> testPrimaDelCommit = new[] { sorgente };
        var nessunaValutazione = Array.Empty<ValutazioneRequisitoEcuVeicolo>();
        var nessunaRisoluzione = Array.Empty<RisoluzioneConflittoAudit>();
        var cella = ValutazioneRequisitoEcuVeicoloService.RisolviCella("R1", componente, "My24", testPrimaDelCommit, nessunaValutazione);
        var candidato = Assert.Single(cella.CandidatiPropagazione).Value;
        var attesa = new VocePropagazioneAuditAttesa("My24", candidato,
            ValutazioneRequisitoEcuVeicoloService.ImprontaPropagazioneCella(
                "R1", componente, "My24", testPrimaDelCommit, nessunaValutazione, nessunaRisoluzione));

        // Simula il commit di una REV da un'altra postazione dopo la prima GetAll() e prima del Save Phase D.
        var testDopoIlCommit = testPrimaDelCommit.Append(
            TestDiretto("T-NUOVO", "BCM", "R1", "My26", PunteggioConformita.NonConforme, dataConferma.AddTicks(1))).ToList();
        var ok = ValutazioneRequisitoEcuVeicoloService.PropagaVerificando(
            repo, "R1", componente, new[] { attesa }, testPrimaDelCommit, nessunaRisoluzione,
            "conferma propagazione", "AB", out var errore, dataConferma, () => testDopoIlCommit);

        Assert.False(ok);
        Assert.Contains("cambiato", errore, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(repo.Load());
    }

    [Fact]
    public void PropagaVerificando_PersistiDataConfermaRicevutaSenzaRicalcolarla()
    {
        using var tmp = new TempWorkspace(bootstrapPhaseD: true);
        var repo = tmp.Master.ValutazioniRequisitoEcuVeicolo;
        var componente = EcuDi("BCM", "My24", "My26");
        var dataConferma = new DateTime(2026, 7, 1, 12, 0, 0);
        IReadOnlyList<TestCase> testCases = new[]
        {
            TestDiretto("T-SRC", "BCM", "R1", "My26", PunteggioConformita.Conforme, dataConferma.AddDays(-1)),
        };
        var risoluzioni = Array.Empty<RisoluzioneConflittoAudit>();
        var cella = ValutazioneRequisitoEcuVeicoloService.RisolviCella(
            "R1", componente, "My24", testCases, Array.Empty<ValutazioneRequisitoEcuVeicolo>());
        var candidato = Assert.Single(cella.CandidatiPropagazione).Value;
        var attesa = new VocePropagazioneAuditAttesa("My24", candidato,
            ValutazioneRequisitoEcuVeicoloService.ImprontaPropagazioneCella(
                "R1", componente, "My24", testCases, Array.Empty<ValutazioneRequisitoEcuVeicolo>(), risoluzioni));

        var ok = ValutazioneRequisitoEcuVeicoloService.PropagaVerificando(
            repo, "R1", componente, new[] { attesa }, testCases, risoluzioni,
            "conferma propagazione", "AB", out var errore, dataConferma, () => testCases);

        Assert.True(ok, errore);
        Assert.Equal(dataConferma, Assert.Single(repo.Load()).UltimaModifica);
    }

    [Fact]
    public void PropagaMassiva_TestAggiornatoPrimaDelCommit_FallisceSenzaScrivere()
    {
        using var tmp = new TempWorkspace(bootstrapPhaseD: true);
        var repo = tmp.Master.ValutazioniRequisitoEcuVeicolo;
        var componente = EcuDi("BCM", "My24", "My26");
        var requisito = new Requisito { Id = "R1" };
        var dataConferma = new DateTime(2026, 7, 1, 12, 0, 0);
        IReadOnlyList<TestCase> testPrimaDelCommit = new[]
        {
            TestDiretto("T-SRC", "BCM", "R1", "My26", PunteggioConformita.Conforme, dataConferma.AddDays(-1)),
        };
        var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            "My24", new[] { componente }, new[] { requisito }, testPrimaDelCommit, Array.Empty<ValutazioneRequisitoEcuVeicolo>());
        var attesa = Assert.Single(anteprima.DaPropagare);
        var testDopoIlCommit = testPrimaDelCommit.Append(
            TestDiretto("T-NUOVO", "BCM", "R1", "My26", PunteggioConformita.NonConforme, dataConferma.AddTicks(1))).ToList();

        var risultato = ValutazioneRequisitoEcuVeicoloService.PropagaMassiva(
            repo, "My24", new[] { componente }, new[] { requisito }, testPrimaDelCommit, Array.Empty<RisoluzioneConflittoAudit>(),
            new[] { attesa }, "conferma propagazione", "AB", out var errore, dataConferma, () => testDopoIlCommit);

        Assert.NotNull(errore);
        Assert.Empty(risultato.Propagate);
        Assert.Empty(repo.Load());
    }

    [Fact]
    public void PropagaMassiva_PersistiDataConfermaRicevutaSenzaRicalcolarla()
    {
        using var tmp = new TempWorkspace(bootstrapPhaseD: true);
        var repo = tmp.Master.ValutazioniRequisitoEcuVeicolo;
        var componente = EcuDi("BCM", "My24", "My26");
        var requisito = new Requisito { Id = "R1" };
        var dataConferma = new DateTime(2026, 7, 1, 12, 0, 0);
        IReadOnlyList<TestCase> testCases = new[]
        {
            TestDiretto("T-SRC", "BCM", "R1", "My26", PunteggioConformita.Conforme, dataConferma.AddDays(-1)),
        };
        var anteprima = ValutazioneRequisitoEcuVeicoloService.AnteprimaPropagazioneMassiva(
            "My24", new[] { componente }, new[] { requisito }, testCases, Array.Empty<ValutazioneRequisitoEcuVeicolo>());

        var risultato = ValutazioneRequisitoEcuVeicoloService.PropagaMassiva(
            repo, "My24", new[] { componente }, new[] { requisito }, testCases, Array.Empty<RisoluzioneConflittoAudit>(),
            anteprima.DaPropagare, "conferma propagazione", "AB", out var errore,
            dataConferma, () => testCases);

        Assert.Null(errore);
        Assert.Single(risultato.Propagate);
        Assert.Equal(dataConferma, Assert.Single(repo.Load()).UltimaModifica);
    }

    // --- RimuoviValutazione: la cella salvata (Manuale/Propagata) torna al calcolo automatico ---

    [Fact]
    public void RimuoviValutazione_RigaEsistente_VieneArchiviataEStoriaRestaSullaStessaRiga()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(repo, "R1", "BCM", new[] { "My24" }, PunteggioConformita.Conforme, "nota", "AB", null, out _));

        var ok = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazione(repo, "R1", "BCM", "My24", out var errore);

        Assert.True(ok, errore);
        var archiviata = Assert.Single(repo.Load());
        var uid = archiviata.Uid;
        Assert.True(archiviata.IsArchiviata);
        Assert.Equal(PunteggioConformita.Conforme, Assert.Single(archiviata.Storico).Punteggio);
        Assert.Empty(ValutazioneRequisitoEcuVeicoloService.UltimaPerVeicolo(new[] { archiviata }));

        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", "BCM", new[] { "My24" }, PunteggioConformita.NonConforme, "nuovo valore", "CD", null, out _));
        var riattivata = Assert.Single(repo.Load());
        Assert.Equal(uid, riattivata.Uid);
        Assert.False(riattivata.IsArchiviata);
        Assert.Single(riattivata.Storico); // lo snapshot rimosso resta disponibile dopo la riattivazione
    }

    [Fact]
    public void RimuoviValutazione_RigaAssente_NonFaNulla()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);

        var ok = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazione(repo, "R1", "BCM", "My24", out var errore);

        Assert.False(ok);
        Assert.Null(errore);
    }

    [Fact]
    public void RimuoviValutazioniRigaAudit_RimuoveSoloIlRequisitoSulVeicoloSceltoEConservaStoricoETest()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var componenti = new[] { EcuDi("BCM", "My24", "My26"), EcuDi("ABS", "My24", "My26"), EcuDi("TCU", "My26") };
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", "BCM", new[] { "My24" }, PunteggioConformita.NonConforme, "nota manuale", "AB", "SW-1", out var errore), errore);
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", "BCM", new[] { "My26" }, PunteggioConformita.Conforme, "altro veicolo", "CD", null, out errore), errore);
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R2", "BCM", new[] { "My24" }, PunteggioConformita.AmpiamenteConforme, "altra riga", "CD", null, out errore), errore);
        var sorgentePropagazione = new CandidatoPropagazioneRequisito(
            "My26", "TC-ORIGINE", PunteggioConformita.ModeratamenteConforme, new DateTime(2026, 10, 15, 10, 0, 0));
        Assert.True(ValutazioneRequisitoEcuVeicoloService.Propaga(
            repo, "R1", "ABS", new[] { ("My24", sorgentePropagazione) }, "nota propagata", "AB", out errore), errore);

        var testDiretto = TestDiretto("TC-AUDIT-DA-PRESERVARE", "BCM", "R1", "My24",
            PunteggioConformita.Conforme, new DateTime(2026, 10, 15, 11, 0, 0));
        var tests = new[] { testDiretto };
        var risoluzioni = new[]
        {
            new RisoluzioneConflittoAudit
            {
                Componente = "BCM", RequisitoId = "R1", PunteggioManuale = PunteggioConformita.ScarsamenteConforme,
                Motivazione = "legacy globale intoccabile", UltimaModifica = new DateTime(2026, 10, 15, 9, 0, 0), ModificatoDa = "EF",
            },
        };
        var fingerprintTest = TestCaseFingerprint.Compute(testDiretto);
        var fingerprintLegacy = EntityStateFingerprint.Compute(risoluzioni[0]);
        var anteprima = ValutazioneRequisitoEcuVeicoloService.CatturaRimozioneValutazioniRigaAudit(
            "R1", "My24", componenti, tests, risoluzioni, repo.Load());
        Assert.Equal(2, anteprima.NumeroValutazioni);
        Assert.Contains(anteprima.Valutazioni, v => v.Componente == "BCM" && v.Origine == OrigineValutazioneRequisito.Manuale);
        Assert.Contains(anteprima.Valutazioni, v => v.Componente == "ABS" && v.Origine == OrigineValutazioneRequisito.Propagata);

        var rimosso = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazioniRigaAudit(
            repo, anteprima, componenti, tests, risoluzioni, "AB", out var numeroRimosse, out errore,
            () => new FontiRimozioneValutazioniRigaAudit(componenti, tests, risoluzioni),
            new DateTime(2026, 10, 16, 12, 0, 0));

        Assert.True(rimosso, errore);
        Assert.Equal(2, numeroRimosse);
        var righe = repo.Load();
        var bcmSelezionato = Assert.Single(righe, v => v.Componente == "BCM" && v.RequisitoId == "R1" && v.Veicolo == "My24");
        var absSelezionato = Assert.Single(righe, v => v.Componente == "ABS" && v.RequisitoId == "R1" && v.Veicolo == "My24");
        Assert.True(bcmSelezionato.IsArchiviata);
        Assert.True(absSelezionato.IsArchiviata);
        Assert.Null(bcmSelezionato.EventoCorrenteId);
        Assert.Null(absSelezionato.EventoCorrenteId);
        Assert.Contains(bcmSelezionato.Storico, s => s.Origine == OrigineStoricoValutazioneAudit.Manuale &&
            s.Punteggio == PunteggioConformita.NonConforme && s.Nota == "nota manuale" && s.BaselineSw == "SW-1");
        Assert.Contains(absSelezionato.Storico, s => s.Origine == OrigineStoricoValutazioneAudit.Propagata &&
            s.Punteggio == PunteggioConformita.ModeratamenteConforme && s.Nota == "nota propagata" && s.TestCaseId == "TC-ORIGINE");
        Assert.Contains(righe, v => v.Componente == "BCM" && v.RequisitoId == "R1" && v.Veicolo == "My26" && !v.IsArchiviata);
        Assert.Contains(righe, v => v.Componente == "BCM" && v.RequisitoId == "R2" && v.Veicolo == "My24" && !v.IsArchiviata);
        Assert.Equal(fingerprintTest, TestCaseFingerprint.Compute(testDiretto));
        Assert.Equal(fingerprintLegacy, EntityStateFingerprint.Compute(risoluzioni[0]));
    }

    [Fact]
    public void RimuoviValutazioniRigaAudit_FontiCambiateDopoAnteprima_FallisceSenzaScrivere()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var componenti = new[] { EcuDi("BCM", "My24") };
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", "BCM", new[] { "My24" }, PunteggioConformita.Conforme, "non rimuovere senza refresh", "AB", null, out var errore), errore);
        var testsAnteprima = Array.Empty<TestCase>();
        var risoluzioni = Array.Empty<RisoluzioneConflittoAudit>();
        var anteprima = ValutazioneRequisitoEcuVeicoloService.CatturaRimozioneValutazioniRigaAudit(
            "R1", "My24", componenti, testsAnteprima, risoluzioni, repo.Load());
        var testNuovo = TestDiretto("TC-NUOVO", "BCM", "R1", "My24", PunteggioConformita.NonConforme, DateTime.Now);

        var salvata = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazioniRigaAudit(
            repo, anteprima, componenti, new[] { testNuovo }, risoluzioni, "AB", out var rimosse, out errore);

        Assert.False(salvata);
        Assert.Contains("cambiata", errore, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, rimosse);
        var riga = Assert.Single(repo.Load());
        Assert.False(riga.IsArchiviata);
        Assert.Empty(riga.Storico);
    }

    [Fact]
    public void RimuoviValutazioniRigaAudit_RefreshTrovaFonteNuova_FallisceSenzaScrivere()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var componenti = new[] { EcuDi("BCM", "My24") };
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", "BCM", new[] { "My24" }, PunteggioConformita.Conforme, "non rimuovere senza refresh", "AB", null, out var errore), errore);
        var testsAnteprima = Array.Empty<TestCase>();
        var risoluzioni = Array.Empty<RisoluzioneConflittoAudit>();
        var anteprima = ValutazioneRequisitoEcuVeicoloService.CatturaRimozioneValutazioniRigaAudit(
            "R1", "My24", componenti, testsAnteprima, risoluzioni, repo.Load());
        var testNuovo = TestDiretto("TC-NUOVO-REMOTO", "BCM", "R1", "My24", PunteggioConformita.NonConforme, DateTime.Now);

        var salvata = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazioniRigaAudit(
            repo, anteprima, componenti, testsAnteprima, risoluzioni, "AB", out var rimosse, out errore,
            () => new FontiRimozioneValutazioniRigaAudit(componenti, new[] { testNuovo }, risoluzioni));

        Assert.False(salvata);
        Assert.Contains("cambiata", errore, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, rimosse);
        var riga = Assert.Single(repo.Load());
        Assert.False(riga.IsArchiviata);
        Assert.Empty(riga.Storico);
    }

    [Fact]
    public void RimuoviValutazioniRigaAudit_ConcorrenzaDuranteCommit_FallisceSenzaParzialeScrittura()
    {
        using var tmp = new TempWorkspace();
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var componenti = new[] { EcuDi("BCM", "My24"), EcuDi("ABS", "My24") };
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", "BCM", new[] { "My24" }, PunteggioConformita.Conforme, "riga BCM", "AB", null, out var errore), errore);
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", "ABS", new[] { "My24" }, PunteggioConformita.NonConforme, "riga ABS", "AB", null, out errore), errore);
        var tests = Array.Empty<TestCase>();
        var risoluzioni = Array.Empty<RisoluzioneConflittoAudit>();
        var anteprima = ValutazioneRequisitoEcuVeicoloService.CatturaRimozioneValutazioniRigaAudit(
            "R1", "My24", componenti, tests, risoluzioni, repo.Load());

        var salvata = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazioniRigaAudit(
            repo, anteprima, componenti, tests, risoluzioni, "AB", out var rimosse, out errore,
            () =>
            {
                var concorrente = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
                var correnti = concorrente.Load();
                var atteso = EntityStateFingerprint.Clone(correnti);
                correnti.Single(v => v.Componente == "ABS").Nota = "modifica concorrente";
                concorrente.Save(correnti, atteso);
                return new FontiRimozioneValutazioniRigaAudit(componenti, tests, risoluzioni);
            });

        Assert.False(salvata);
        Assert.NotNull(errore);
        Assert.Equal(0, rimosse);
        var dopoConcorrenza = repo.Load();
        Assert.All(dopoConcorrenza, v => Assert.False(v.IsArchiviata));
        Assert.All(dopoConcorrenza, v => Assert.Empty(v.Storico));
        Assert.Equal("modifica concorrente", Assert.Single(dopoConcorrenza, v => v.Componente == "ABS").Nota);
    }

    [Fact]
    public void RimuoviValutazioniRigaAudit_NuovaUidStessoPerimetroDaSecondaPostazione_FallisceSenzaParzialeScrittura()
    {
        using var tmp = new TempWorkspace(bootstrapPhaseD: true);
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var uidOriginale = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var uidConcorrente = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var iniziali = repo.Load();
        var baselineIniziale = EntityStateFingerprint.Clone(iniziali);
        iniziali.Add(new ValutazioneRequisitoEcuVeicolo
        {
            Uid = uidOriginale, EventoCorrenteId = Guid.Parse("20000000-0000-0000-0000-000000000001"),
            RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme,
            Origine = OrigineValutazioneRequisito.Manuale, Nota = "valore da rimuovere", Operatore = "AB",
            UltimaModifica = new DateTime(2026, 10, 15, 9, 0, 0),
            Storico =
            {
                new SnapshotValutazioneAudit
                {
                    EventId = Guid.Parse("30000000-0000-0000-0000-000000000001"),
                    Origine = OrigineStoricoValutazioneAudit.Propagata,
                    Punteggio = PunteggioConformita.ModeratamenteConforme,
                    Data = new DateTime(2026, 10, 14, 9, 0, 0), Operatore = "CD", Nota = "storico precedente",
                },
            },
        });
        repo.Save(iniziali, baselineIniziale);

        var componenti = new[] { EcuDi("BCM", "My24") };
        var tests = Array.Empty<TestCase>();
        var risoluzioni = Array.Empty<RisoluzioneConflittoAudit>();
        var fotografia = repo.Load();
        var improntaPrimaDelCommit = EntityStateFingerprint.Compute(Assert.Single(fotografia));
        var anteprima = ValutazioneRequisitoEcuVeicoloService.CatturaRimozioneValutazioniRigaAudit(
            "R1", "My24", componenti, tests, risoluzioni, fotografia);

        var salvata = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazioniRigaAudit(
            repo, anteprima, componenti, tests, risoluzioni, "AB", out var rimosse, out var errore,
            () =>
            {
                // Interleaving deterministico: una seconda postazione legge il catalogo corrente e pubblica
                // una nuova UID nella stessa cella dopo l'anteprima ma prima del commit del batch.
                var secondaPostazione = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
                var catalogoSecondaPostazione = secondaPostazione.Load();
                var attesoSecondaPostazione = EntityStateFingerprint.Clone(catalogoSecondaPostazione);
                catalogoSecondaPostazione.Add(new ValutazioneRequisitoEcuVeicolo
                {
                    Uid = uidConcorrente, EventoCorrenteId = Guid.Parse("20000000-0000-0000-0000-000000000002"),
                    RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.NonConforme,
                    Origine = OrigineValutazioneRequisito.Manuale, Nota = "aggiunta da PC2", Operatore = "CD",
                    UltimaModifica = new DateTime(2026, 10, 16, 9, 0, 0),
                });
                secondaPostazione.Save(catalogoSecondaPostazione, attesoSecondaPostazione);
                return new FontiRimozioneValutazioniRigaAudit(componenti, tests, risoluzioni);
            });

        Assert.False(salvata);
        Assert.NotNull(errore);
        Assert.Equal(0, rimosse);
        var dopo = repo.Load();
        Assert.Equal(2, dopo.Count);
        var originaleDopo = Assert.Single(dopo, v => v.Uid == uidOriginale);
        Assert.False(originaleDopo.IsArchiviata);
        Assert.Equal(improntaPrimaDelCommit, EntityStateFingerprint.Compute(originaleDopo));
        var concorrenteDopo = Assert.Single(dopo, v => v.Uid == uidConcorrente);
        Assert.False(concorrenteDopo.IsArchiviata);
        Assert.Equal(OrigineValutazioneRequisito.Manuale, concorrenteDopo.Origine);
        Assert.Equal("aggiunta da PC2", concorrenteDopo.Nota);
        Assert.Empty(concorrenteDopo.Storico);
    }

    [Fact]
    public void RimuoviValutazioniColonnaAudit_NuovaUidStessaCellaDaSecondaPostazione_FallisceSenzaParzialeScrittura()
    {
        using var tmp = new TempWorkspace(bootstrapPhaseD: true);
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var uidOriginale = Guid.Parse("11000000-0000-0000-0000-000000000001");
        var uidConcorrente = Guid.Parse("11000000-0000-0000-0000-000000000002");
        var iniziali = repo.Load();
        var baselineIniziale = EntityStateFingerprint.Clone(iniziali);
        iniziali.Add(new ValutazioneRequisitoEcuVeicolo
        {
            Uid = uidOriginale, EventoCorrenteId = Guid.Parse("21000000-0000-0000-0000-000000000001"),
            RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme,
            Origine = OrigineValutazioneRequisito.Propagata, Nota = "valore da rimuovere", Operatore = "AB",
            UltimaModifica = new DateTime(2026, 10, 15, 9, 0, 0),
            Storico =
            {
                new SnapshotValutazioneAudit
                {
                    EventId = Guid.Parse("31000000-0000-0000-0000-000000000001"),
                    Origine = OrigineStoricoValutazioneAudit.Manuale,
                    Punteggio = PunteggioConformita.NonConforme,
                    Data = new DateTime(2026, 10, 14, 9, 0, 0), Operatore = "CD", Nota = "storico precedente",
                },
            },
        });
        repo.Save(iniziali, baselineIniziale);

        var componenti = new[] { EcuDi("BCM", "My24") };
        var requisiti = new[] { new Requisito { Id = "R1", Testo = "R1" } };
        var tests = Array.Empty<TestCase>();
        var risoluzioni = Array.Empty<RisoluzioneConflittoAudit>();
        var fotografia = repo.Load();
        var improntaPrimaDelCommit = EntityStateFingerprint.Compute(Assert.Single(fotografia));
        var anteprima = ValutazioneRequisitoEcuVeicoloService.CatturaRimozioneValutazioniColonnaAudit(
            "BCM", "My24", componenti, requisiti, tests, risoluzioni, fotografia);

        var salvata = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazioniColonnaAudit(
            repo, anteprima, componenti, requisiti, tests, risoluzioni, "AB", out var rimosse, out var errore,
            () =>
            {
                var secondaPostazione = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
                var catalogoSecondaPostazione = secondaPostazione.Load();
                var attesoSecondaPostazione = EntityStateFingerprint.Clone(catalogoSecondaPostazione);
                catalogoSecondaPostazione.Add(new ValutazioneRequisitoEcuVeicolo
                {
                    Uid = uidConcorrente, EventoCorrenteId = Guid.Parse("21000000-0000-0000-0000-000000000002"),
                    RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.ScarsamenteConforme,
                    Origine = OrigineValutazioneRequisito.Manuale, Nota = "aggiunta da PC2", Operatore = "CD",
                    UltimaModifica = new DateTime(2026, 10, 16, 9, 0, 0),
                });
                secondaPostazione.Save(catalogoSecondaPostazione, attesoSecondaPostazione);
                return new FontiRimozioneValutazioniColonnaAudit(componenti, requisiti, tests, risoluzioni);
            });

        Assert.False(salvata);
        Assert.NotNull(errore);
        Assert.Equal(0, rimosse);
        var dopo = repo.Load();
        Assert.Equal(2, dopo.Count);
        var originaleDopo = Assert.Single(dopo, v => v.Uid == uidOriginale);
        Assert.False(originaleDopo.IsArchiviata);
        Assert.Equal(improntaPrimaDelCommit, EntityStateFingerprint.Compute(originaleDopo));
        var concorrenteDopo = Assert.Single(dopo, v => v.Uid == uidConcorrente);
        Assert.False(concorrenteDopo.IsArchiviata);
        Assert.Equal(OrigineValutazioneRequisito.Manuale, concorrenteDopo.Origine);
        Assert.Equal("aggiunta da PC2", concorrenteDopo.Nota);
        Assert.Empty(concorrenteDopo.Storico);
    }

    [Fact]
    public void RimuoviValutazioniRigaAudit_NuovaUidFuoriPerimetroDaSecondaPostazione_FallisceSenzaScritturaParziale()
    {
        using var tmp = new TempWorkspace(bootstrapPhaseD: true);
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var uidOriginale = Guid.Parse("12000000-0000-0000-0000-000000000001");
        var uidConcorrente = Guid.Parse("12000000-0000-0000-0000-000000000002");
        var iniziali = repo.Load();
        var baselineIniziale = EntityStateFingerprint.Clone(iniziali);
        iniziali.Add(new ValutazioneRequisitoEcuVeicolo
        {
            Uid = uidOriginale, EventoCorrenteId = Guid.Parse("22000000-0000-0000-0000-000000000001"),
            RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme,
            Origine = OrigineValutazioneRequisito.Manuale, Nota = "valore da rimuovere", Operatore = "AB",
            UltimaModifica = new DateTime(2026, 10, 15, 9, 0, 0),
        });
        repo.Save(iniziali, baselineIniziale);

        var componenti = new[] { EcuDi("BCM", "My24", "My26") };
        var tests = Array.Empty<TestCase>();
        var risoluzioni = Array.Empty<RisoluzioneConflittoAudit>();
        var fotografia = repo.Load();
        var improntaPrimaDelCommit = EntityStateFingerprint.Compute(Assert.Single(fotografia));
        var anteprima = ValutazioneRequisitoEcuVeicoloService.CatturaRimozioneValutazioniRigaAudit(
            "R1", "My24", componenti, tests, risoluzioni, fotografia);

        var salvata = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazioniRigaAudit(
            repo, anteprima, componenti, tests, risoluzioni, "AB", out var rimosse, out var errore,
            () =>
            {
                var secondaPostazione = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
                var catalogoSecondaPostazione = secondaPostazione.Load();
                var attesoSecondaPostazione = EntityStateFingerprint.Clone(catalogoSecondaPostazione);
                catalogoSecondaPostazione.Add(new ValutazioneRequisitoEcuVeicolo
                {
                    Uid = uidConcorrente, EventoCorrenteId = Guid.Parse("22000000-0000-0000-0000-000000000002"),
                    RequisitoId = "R2", Componente = "BCM", Veicolo = "My26", Punteggio = PunteggioConformita.NonConforme,
                    Origine = OrigineValutazioneRequisito.Manuale, Nota = "fuori perimetro da PC2", Operatore = "CD",
                    UltimaModifica = new DateTime(2026, 10, 16, 9, 0, 0),
                });
                secondaPostazione.Save(catalogoSecondaPostazione, attesoSecondaPostazione);
                return new FontiRimozioneValutazioniRigaAudit(componenti, tests, risoluzioni);
            });

        Assert.False(salvata);
        Assert.NotNull(errore);
        Assert.Equal(0, rimosse);
        var dopo = repo.Load();
        Assert.Equal(2, dopo.Count);
        var originaleDopo = Assert.Single(dopo, v => v.Uid == uidOriginale);
        Assert.False(originaleDopo.IsArchiviata);
        Assert.Equal(improntaPrimaDelCommit, EntityStateFingerprint.Compute(originaleDopo));
        var concorrenteDopo = Assert.Single(dopo, v => v.Uid == uidConcorrente);
        Assert.False(concorrenteDopo.IsArchiviata);
        Assert.Equal("fuori perimetro da PC2", concorrenteDopo.Nota);
        Assert.Empty(concorrenteDopo.Storico);
    }

    // --- Persistenza round-trip Phase D ---

    [Fact]
    public void PersistenzaPhaseD_RoundTrip_Load_Save_Load()
    {
        using var tmp = new TempWorkspace(bootstrapPhaseD: true);
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);

        var iniziale = repo.Load();
        Assert.Empty(iniziale);
        var atteso = EntityStateFingerprint.Clone(iniziale);
        iniziale.Add(new ValutazioneRequisitoEcuVeicolo
        {
            RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme,
            Origine = OrigineValutazioneRequisito.Manuale, Nota = "nota", Operatore = "AB", UltimaModifica = DateTime.Now,
            EventoCorrenteId = Guid.NewGuid(), IsArchiviata = true,
            Storico = { new SnapshotValutazioneAudit
            {
                EventId = Guid.NewGuid(), Origine = OrigineStoricoValutazioneAudit.Propagata,
                Punteggio = PunteggioConformita.ModeratamenteConforme, Data = new DateTime(2026, 1, 1), Nota = "snapshot",
            } },
        });
        repo.Save(iniziale, atteso);

        var repo2 = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var riletta = repo2.Load();
        var riga = Assert.Single(riletta);
        Assert.Equal("R1", riga.RequisitoId);
        Assert.Equal("BCM", riga.Componente);
        Assert.Equal("My24", riga.Veicolo);
        Assert.Equal(PunteggioConformita.Conforme, riga.Punteggio);
        Assert.NotEqual(Guid.Empty, riga.Uid);
        Assert.True(riga.IsArchiviata);
        Assert.NotNull(riga.EventoCorrenteId);
        Assert.Equal("snapshot", Assert.Single(riga.Storico).Nota);
    }

    // --- Retro-compatibilità: catalogo assente / workspace non Phase D ---

    [Fact]
    public void CatalogoAssente_Load_RestituisceListaVuota_NonRompeIlClient()
    {
        using var tmp = new TempWorkspace(bootstrapPhaseD: false);
        Assert.False(File.Exists(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile));

        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);
        var lista = repo.Load();

        Assert.NotNull(lista);
        Assert.Empty(lista);
    }

    [Fact]
    public void CatalogoAssenteInPhaseD_Load_RestituisceListaVuota_CatalogoVieneCreatoAlPrimoSave()
    {
        using var tmp = new TempWorkspace(bootstrapPhaseD: true);
        var repo = new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(tmp.Paths.ValutazioniRequisitoEcuVeicoloFile);

        var lista = repo.Load();
        Assert.Empty(lista);

        var atteso = EntityStateFingerprint.Clone(lista);
        lista.Add(new ValutazioneRequisitoEcuVeicolo { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme });
        repo.Save(lista, atteso);

        Assert.Single(repo.Load());
    }

    // --- Registro riferimenti: rinomina componente/veicolo/requisito aggiorna le valutazioni salvate ---

    [Fact]
    public void ApplicaSuCataloghiIncrociati_RinominaEcu_AggiornaLeValutazioniSalvate()
    {
        using var tmp = new TempWorkspace(bootstrapPhaseD: true);
        var repo = tmp.Master.ValutazioniRequisitoEcuVeicolo;
        var iniziale = repo.Load();
        var atteso = EntityStateFingerprint.Clone(iniziale);
        iniziale.Add(new ValutazioneRequisitoEcuVeicolo { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme });
        repo.Save(iniziale, atteso);

        var avvisi = new List<string>();
        var toccati = MasterRiferimentiService.ApplicaSuCataloghiIncrociati(tmp.Master, "Componente", "BCM", new[] { "BCM-2" }, avvisi);

        Assert.True(toccati > 0);
        var riga = Assert.Single(repo.Load());
        Assert.Equal("BCM-2", riga.Componente);
    }

    [Fact]
    public void ApplicaSuCataloghiIncrociati_RinominaVeicolo_AggiornaCellaESorgentePropagazione()
    {
        using var tmp = new TempWorkspace(bootstrapPhaseD: true);
        var repo = tmp.Master.ValutazioniRequisitoEcuVeicolo;
        var iniziale = repo.Load();
        var atteso = EntityStateFingerprint.Clone(iniziale);
        iniziale.Add(new ValutazioneRequisitoEcuVeicolo
        {
            RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme,
            Origine = OrigineValutazioneRequisito.Propagata, SorgenteVeicolo = "My24",
        });
        repo.Save(iniziale, atteso);

        var avvisi = new List<string>();
        MasterRiferimentiService.ApplicaSuCataloghiIncrociati(tmp.Master, "Veicolo", "My24", new[] { "My24b" }, avvisi);

        var riga = Assert.Single(repo.Load());
        Assert.Equal("My24b", riga.Veicolo);
        Assert.Equal("My24b", riga.SorgenteVeicolo);
    }

    [Fact]
    public void ApplicaSuCataloghiIncrociati_RinominaRequisito_AggiornaLaCella()
    {
        using var tmp = new TempWorkspace(bootstrapPhaseD: true);
        var repo = tmp.Master.ValutazioniRequisitoEcuVeicolo;
        var iniziale = repo.Load();
        var atteso = EntityStateFingerprint.Clone(iniziale);
        iniziale.Add(new ValutazioneRequisitoEcuVeicolo { RequisitoId = "R1", Componente = "BCM", Veicolo = "My24", Punteggio = PunteggioConformita.Conforme });
        repo.Save(iniziale, atteso);

        var avvisi = new List<string>();
        MasterRiferimentiService.ApplicaSuCataloghiIncrociati(tmp.Master, "Requisito", "R1", new[] { "R1-bis" }, avvisi);

        var riga = Assert.Single(repo.Load());
        Assert.Equal("R1-bis", riga.RequisitoId);
    }

    [Fact]
    public void RequisitoVeicoloEcu_RiferimentiEControlloVeicoliEsclusi()
    {
        var req = new Requisito { VeicoliEcuD4D = new() { new() { Veicolo = "V1", Componente = "E1" } } };
        RequisitoApplicabilita.ImpostaSottoveicoli(req, new[] { "V1", "V2" });
        Assert.True(RequisitoApplicabilita.RiferisceEcu(req, "e1"));
        Assert.True(RequisitoApplicabilita.RiferisceVeicolo(req, "v1"));
        Assert.True(RequisitoApplicabilita.RiferisceVeicolo(req, "v2"));
        Assert.False(RequisitoApplicabilita.RiferisceEcu(req, "E2"));
        Assert.Equal(new[] { "V1" }, RequisitoApplicabilita.VeicoliEcuEsclusi(req, new[] { "V2" }));
        Assert.Empty(RequisitoApplicabilita.VeicoliEcuEsclusi(req, new[] { "v1", "V2" }));
    }

    [Fact]
    public void RequisitoVeicoloEcu_PersistenzaRinominaEUnione()
    {
        using var tmp = new TempWorkspace(bootstrapPhaseD: true);
        var repo = tmp.Master.Requisiti;
        var items = repo.Load();
        var snapshot = EntityStateFingerprint.Clone(items);
        var req = new Requisito { Id = "Extra-1", VeicoliEcuD4D = new()
        {
            new() { Veicolo = "V1", Componente = "E1" }, new() { Veicolo = "V1", Componente = "E2" }
        } };
        RequisitoApplicabilita.ImpostaTipo(req, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(req, new[] { "V1" });
        items.Add(req);
        repo.Save(items, snapshot);
        Assert.Equal(2, Assert.Single(repo.Load()).VeicoliEcuD4D!.Count);

        MasterRiferimentiService.ApplicaSuCataloghiIncrociati(tmp.Master, "Componente", "E1", new[] { "E2" }, new());
        var coppia = Assert.Single(Assert.Single(repo.Load()).VeicoliEcuD4D!);
        Assert.Equal("E2", coppia.Componente);
        MasterRiferimentiService.ApplicaSuCataloghiIncrociati(tmp.Master, "Veicolo", "V1", new[] { "V2", "V3" }, new());
        var aggiornato = Assert.Single(repo.Load());
        Assert.Equal(new[] { "V2", "V3" }, aggiornato.VeicoliEcuD4D!.Select(x => x.Veicolo));
        Assert.Equal(new[] { "V2", "V3" }, RequisitoApplicabilita.SottoveicoliDi(aggiornato));
    }

    private sealed class TempWorkspace : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "TestLoom_ValReqEcuVeicolo_" + Guid.NewGuid().ToString("N"));
        public readonly DataPaths Paths;
        public readonly MasterDataStore Master;

        public TempWorkspace(bool bootstrapPhaseD = false)
        {
            Paths = new DataPaths(Root);
            Paths.EnsureStructure();
            if (bootstrapPhaseD) PhaseDMasterBootstrapService.EnsureReady(Paths);
            Master = new MasterDataStore(Paths);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
