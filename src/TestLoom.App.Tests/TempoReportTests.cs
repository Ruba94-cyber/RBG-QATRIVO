using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>TIME (6.6.1): modalita' Tempo totale e Tempo medio della matrice Report. Base "Tutte le esecuzioni" (ogni tentativo
/// eseguito) e base "Ultimo stato" (operazione vincente per combinazione). Deduplica per operazione nei totali, media pesata sui
/// valori noti, completezza noti/eleggibili, filtri TIME stretti con valore vuoto mai corrispondente.</summary>
public sealed class TempoReportTests
{
    private static readonly DateTime Giorno = new(2026, 2, 1);

    private static ConfigurazioneMatriceReport Config(ModalitaCellaMatrice modalita, BaseTempoMatrice baseTempo = BaseTempoMatrice.TutteLeEsecuzioni) => new()
    {
        CampiX = [DimensioniMatrice.Componente],
        CampiY = [DimensioniMatrice.Funzionalita],
        ModalitaCella = modalita,
        BaseTempo = baseTempo,
    };

    private static Condizione Op(string funzionalita, string componente, Esito esito, int? minuti, Guid? origine = null) => new()
    {
        Funzionalita = funzionalita,
        Esito = esito,
        Componente = new EcuSelection([componente]),
        DurataMinuti = minuti,
        OperazioneOrigineUid = origine,
    };

    private static Esecuzione Rev(DateTime data, params Condizione[] condizioni) => new()
    {
        Data = data,
        Operatore = "Op",
        Esito = Esito.Conforme,
        Condizioni = [.. condizioni],
    };

    private static TestCase Test(string id, params Esecuzione[] revisioni)
    {
        var tc = new TestCase
        {
            Id = id,
            Uid = Guid.NewGuid(),
            Componente = new EcuSelection(["BCM"]),
            Veicoli = ["Daily"],
            Funzionalita = ["F1"],
        };
        tc.Esecuzioni.AddRange(revisioni);
        return tc;
    }

    private static RisultatoMatriceReport Genera(ConfigurazioneMatriceReport config, params TestCase[] test) =>
        ReportMatriceService.Genera(test, config, [], [], []);

    private static CellaMatriceReport Cella(RisultatoMatriceReport risultato, string riga, string colonna) =>
        risultato.Righe.Single(r => r.Riga == riga).Celle.Single(c => c.Colonna == colonna);

    [Fact]
    public void TempoMedio_SoloOperazioniConformi_TempoTotaleMantieneTuttiGliEsitiEseguiti()
    {
        var t = Test("T-1", Rev(Giorno,
            Op("F1", "BCM", Esito.Conforme, 12),
            Op("F1", "BCM", Esito.NonConforme, 36),
            Op("F1", "BCM", Esito.ConformeConDeviazioni, 24),
            Op("F1", "BCM", Esito.Conforme, null)));

        var totale = Cella(Genera(Config(ModalitaCellaMatrice.TempoTotale), t), "F1", "BCM").Tempo;
        var medio = Cella(Genera(Config(ModalitaCellaMatrice.TempoMedio), t), "F1", "BCM").Tempo;

        Assert.Equal(new ValoreTempo(72, 3, 4), totale);
        Assert.Equal(new ValoreTempo(12, 1, 2), medio);
        Assert.Equal(12d, medio.Media);
        Assert.Equal("12 min · 1/2", ReportMatriceService.TestoTempo(medio, ModalitaCellaMatrice.TempoMedio));
    }

    [Fact]
    public void UltimoStato_TempoMedioNonRipescaIlConformeQuandoLaVincitriceENonConforme()
    {
        var t = Test("T-1",
            Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 10)),
            Rev(Giorno.AddDays(1), Op("F1", "BCM", Esito.NonConforme, 40)));

        var totale = Cella(Genera(Config(ModalitaCellaMatrice.TempoTotale, BaseTempoMatrice.UltimoStato), t), "F1", "BCM").Tempo;
        var medio = Cella(Genera(Config(ModalitaCellaMatrice.TempoMedio, BaseTempoMatrice.UltimoStato), t), "F1", "BCM").Tempo;

        Assert.Equal(new ValoreTempo(40, 1, 1), totale);
        Assert.Equal(default, medio);
        Assert.Equal("—", ReportMatriceService.TestoTempo(medio, ModalitaCellaMatrice.TempoMedio));
    }

    [Fact]
    public void TutteLeEsecuzioni_VentiPiuDieci_TotaleTrentaMediaQuindici()
    {
        var t = Test("T-1", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 20)), Rev(Giorno.AddDays(1), Op("F1", "BCM", Esito.Conforme, 10)));

        var valore = Cella(Genera(Config(ModalitaCellaMatrice.TempoTotale), t), "F1", "BCM").Tempo;

        Assert.Equal(30, valore.SommaMinuti);
        Assert.Equal(2, valore.Conosciuti);
        Assert.Equal(2, valore.Eleggibili);
        Assert.Equal(15d, valore.Media);
    }

    [Fact]
    public void UltimoStato_VentiPiuDieci_LaPiuRecenteVince_ConUnaSolaOsservazione()
    {
        var t = Test("T-1", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 20)), Rev(Giorno.AddDays(1), Op("F1", "BCM", Esito.Conforme, 10)));

        var valore = Cella(Genera(Config(ModalitaCellaMatrice.TempoTotale, BaseTempoMatrice.UltimoStato), t), "F1", "BCM").Tempo;

        Assert.Equal(10, valore.SommaMinuti);
        Assert.Equal(1, valore.Conosciuti);
        Assert.Equal(1, valore.Eleggibili);
    }

    [Fact]
    public void TentativoNonEffettivoEseguito_CompareInTutteLeEsecuzioni_NonInUltimoStato()
    {
        // Stessa operazione ripetuta nella stessa revisione: il primo tentativo non e' effettivo (vince il ripetuto).
        var primo = Op("F1", "BCM", Esito.NonConforme, 20);
        var ripetuto = Op("F1", "BCM", Esito.Conforme, 10, primo.Uid);
        var t = Test("T-1", Rev(Giorno, primo, ripetuto));

        var tutte = Cella(Genera(Config(ModalitaCellaMatrice.TempoTotale), t), "F1", "BCM").Tempo;
        var ultimo = Cella(Genera(Config(ModalitaCellaMatrice.TempoTotale, BaseTempoMatrice.UltimoStato), t), "F1", "BCM").Tempo;

        Assert.Equal(30, tutte.SommaMinuti);
        Assert.Equal(2, tutte.Eleggibili);
        Assert.Equal(10, ultimo.SommaMinuti);
        Assert.Equal(1, ultimo.Eleggibili);
    }

    [Fact]
    public void NonEseguiteENonNote_NonContanoSuiMinuti_EZeroNotoRestaDistintoDaNonNoto()
    {
        var zero = Test("T-zero", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 0)));
        var nonNoto = Test("T-vuoto", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, null)));
        var pianificata = Test("T-piano", Rev(Giorno, Op("F1", "BCM", Esito.DaEseguire, 15)));

        var valore = Cella(Genera(Config(ModalitaCellaMatrice.TempoMedio), zero, nonNoto, pianificata), "F1", "BCM").Tempo;

        Assert.Equal(0, valore.SommaMinuti);
        Assert.Equal(1, valore.Conosciuti);
        Assert.Equal(2, valore.Eleggibili);
        Assert.Equal(0d, valore.Media);
        Assert.False(valore.Completo);
    }

    [Fact]
    public void SoloNonNoti_MediaEVuota_E_IlTestoMostraLaCompletezza()
    {
        var t = Test("T-1", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, null)));

        var valore = Cella(Genera(Config(ModalitaCellaMatrice.TempoMedio), t), "F1", "BCM").Tempo;

        Assert.False(valore.HaValore);
        Assert.Null(valore.Media);
        Assert.Equal("— · 0/1", ReportMatriceService.TestoTempo(valore, ModalitaCellaMatrice.TempoMedio));
    }

    [Fact]
    public void DurateMancanti_NonDiluisconoLaMedia()
    {
        var noto = Test("T-noto", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 20)));
        var senzaDurata = Test("T-senza-durata", Rev(Giorno.AddDays(1), Op("F1", "BCM", Esito.Conforme, null)));

        var valore = Cella(Genera(Config(ModalitaCellaMatrice.TempoMedio), noto, senzaDurata), "F1", "BCM").Tempo;

        Assert.Equal(20, valore.SommaMinuti);
        Assert.Equal(1, valore.Conosciuti);
        Assert.Equal(2, valore.Eleggibili);
        Assert.Equal(20d, valore.Media);
        Assert.Equal("20 min · 1/2", ReportMatriceService.TestoTempo(valore, ModalitaCellaMatrice.TempoMedio));
    }

    [Fact]
    public void OperazioneSuDueEcu_VentiMinutiPerCella_MaTotaleContatoUnaVolta()
    {
        var condizione = new Condizione
        {
            Funzionalita = "F1",
            Esito = Esito.Conforme,
            Componente = new EcuSelection(["BCM", "ABS"]),
            DurataMinuti = 20,
        };
        var t = Test("T-1", Rev(Giorno, condizione));
        t.Componente = new EcuSelection(["BCM", "ABS"]);

        var risultato = Genera(Config(ModalitaCellaMatrice.TempoTotale), t);

        Assert.Equal(20, Cella(risultato, "F1", "BCM").Tempo.SommaMinuti);
        Assert.Equal(20, Cella(risultato, "F1", "ABS").Tempo.SommaMinuti);
        var riga = risultato.Righe.Single(r => r.Riga == "F1").TotaleRiga.Tempo;
        Assert.Equal(20, riga.SommaMinuti);
        Assert.Equal(1, riga.Eleggibili);
        Assert.Equal(20, risultato.TotaliColonna[risultato.Colonne.IndexOf("BCM")].Tempo.SommaMinuti);
        Assert.Equal(20, risultato.TotaliColonna[risultato.Colonne.IndexOf("ABS")].Tempo.SommaMinuti);
        Assert.Equal(20, risultato.TotaleGenerale.Tempo.SommaMinuti);
        Assert.Equal(1, risultato.TotaleGenerale.Tempo.Eleggibili);
    }

    [Fact]
    public void MediaDiAggregato_EPesataSuiValoriNoti_NonMediaDiMedie()
    {
        // BCM: F1 = 10 e 20 (media 15), F2 = 60. Pesata: (10+20+60)/3 = 30. Media di medie: (15+60)/2 = 37,5.
        var a = Test("T-a", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 10)), Rev(Giorno.AddDays(1), Op("F1", "BCM", Esito.Conforme, 20)));
        var b = Test("T-b", Rev(Giorno, Op("F2", "BCM", Esito.Conforme, 60)));

        var risultato = Genera(Config(ModalitaCellaMatrice.TempoMedio), a, b);

        var colonna = risultato.TotaliColonna[risultato.Colonne.IndexOf("BCM")].Tempo;
        Assert.Equal(90, colonna.SommaMinuti);
        Assert.Equal(3, colonna.Conosciuti);
        Assert.Equal(30d, colonna.Media);
    }

    [Fact]
    public void FiltroOperazione_StrettoEVuotoNonCorrisponde()
    {
        var t = Test("T-1",
            Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 10)),
            Rev(Giorno.AddDays(1), Op("F1", "BCM", Esito.Conforme, 15)),
            Rev(Giorno.AddDays(2), Op("F1", "BCM", Esito.Conforme, 20)),
            Rev(Giorno.AddDays(3), Op("F1", "BCM", Esito.Conforme, null)));

        var maggiore = Config(ModalitaCellaMatrice.TempoTotale);
        maggiore.FiltroTempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 15);
        var minore = Config(ModalitaCellaMatrice.TempoTotale);
        minore.FiltroTempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Minore, 15);

        var sopra = Cella(Genera(maggiore, t), "F1", "BCM").Tempo;
        var sotto = Cella(Genera(minore, t), "F1", "BCM").Tempo;

        Assert.Equal(20, sopra.SommaMinuti);   // 15 esatto e' escluso (stretto), il vuoto non corrisponde
        Assert.Equal(1, sopra.Eleggibili);
        Assert.Equal(10, sotto.SommaMinuti);
        Assert.Equal(1, sotto.Eleggibili);
    }

    [Fact]
    public void FiltroTotaleTest_SelezionaIlTestPerLaSommaDelleSueOperazioni()
    {
        var grande = Test("T-grande", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 10)), Rev(Giorno.AddDays(1), Op("F1", "BCM", Esito.Conforme, 20)));
        var piccolo = Test("T-piccolo", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 5)));
        var config = Config(ModalitaCellaMatrice.TempoTotale);
        config.FiltroTempoTotaleTest = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 10);

        var valore = Cella(Genera(config, grande, piccolo), "F1", "BCM").Tempo;

        Assert.Equal(30, valore.SommaMinuti);
        Assert.Equal(2, valore.Eleggibili);
    }

    [Fact]
    public void TestDaImportUdt_NonEntraNelloSforzo()
    {
        var importato = Test("T-unitlogger", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 30)));
        importato.Origine = OrigineTest.ImportStatoUdt;

        var valore = Cella(Genera(Config(ModalitaCellaMatrice.TempoTotale), importato), "F1", "BCM").Tempo;

        Assert.Equal(0, valore.Eleggibili);
        Assert.False(valore.HaValore);
    }

    [Fact]
    public void CambioModalitaEBase_ConservaAssiEOrdine()
    {
        var a = Test("T-a", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 20)), Rev(Giorno.AddDays(1), Op("F1", "BCM", Esito.Conforme, 10)));
        var b = Test("T-b", Rev(Giorno, Op("F2", "ABS", Esito.NonConforme, 40)));

        var percentuale = Genera(Config(ModalitaCellaMatrice.Percentuale), a, b);
        var tutte = Genera(Config(ModalitaCellaMatrice.TempoTotale), a, b);
        var ultimo = Genera(Config(ModalitaCellaMatrice.TempoMedio, BaseTempoMatrice.UltimoStato), a, b);

        Assert.Equal(percentuale.Colonne, tutte.Colonne);
        Assert.Equal(percentuale.Colonne, ultimo.Colonne);
        Assert.Equal(percentuale.Righe.Select(r => r.Riga), tutte.Righe.Select(r => r.Riga));
        Assert.Equal(percentuale.Righe.Select(r => r.Riga), ultimo.Righe.Select(r => r.Riga));
        Assert.Equal(percentuale.Righe.Select(r => r.Celle.Count), ultimo.Righe.Select(r => r.Celle.Count));
    }

    [Fact]
    public void ModalitaNonTemporale_NonCalcolaIValoriTemporali()
    {
        var t = Test("T-1", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 20)));

        var valore = Cella(Genera(Config(ModalitaCellaMatrice.Stato), t), "F1", "BCM").Tempo;

        Assert.Equal(0, valore.Eleggibili);
    }

    [Fact]
    public void TestoTempo_FormatoStessoPerUiEExport()
    {
        Assert.Equal("30 min · 2/2", ReportMatriceService.TestoTempo(new ValoreTempo(30, 2, 2), ModalitaCellaMatrice.TempoTotale));
        Assert.Equal("15 min · 2/2", ReportMatriceService.TestoTempo(new ValoreTempo(30, 2, 2), ModalitaCellaMatrice.TempoMedio));
        Assert.Equal("— · 0/2", ReportMatriceService.TestoTempo(new ValoreTempo(0, 0, 2), ModalitaCellaMatrice.TempoTotale));
        Assert.Equal("—", ReportMatriceService.TestoTempo(default, ModalitaCellaMatrice.TempoTotale));
        Assert.Equal("N.A.", ReportMatriceService.TestoTempo(new ValoreTempo(30, 2, 2), ModalitaCellaMatrice.TempoTotale, nonApplicabile: true));
    }

    /// <summary>Copia salvata dell'editor: stesso Uid del tentativo, valori aggiornati (la revisione successiva la riporta).</summary>
    private static Condizione Copia(Condizione originale, int? minuti, string? componente = null) => new()
    {
        Uid = originale.Uid,
        Funzionalita = originale.Funzionalita,
        Esito = originale.Esito,
        Componente = new EcuSelection([componente ?? originale.Componente[0]]),
        DurataMinuti = minuti,
    };

    private static ValoreTempo CellaOpzionale(RisultatoMatriceReport risultato, string riga, string colonna) =>
        risultato.Righe.Single(r => r.Riga == riga).Celle.FirstOrDefault(c => c.Colonna == colonna)?.Tempo ?? default;

    [Fact]
    public void CopiaSalvataConLoStessoUid_ContaUnaVoltaConIlValoreDellUltimaRevisione()
    {
        var originale = Op("F1", "BCM", Esito.Conforme, 10);
        var t = Test("T-1", Rev(Giorno, originale), Rev(Giorno.AddDays(1), Copia(originale, 12)));

        var valore = Cella(Genera(Config(ModalitaCellaMatrice.TempoTotale), t), "F1", "BCM").Tempo;

        Assert.Equal(12, valore.SommaMinuti);
        Assert.Equal(1, valore.Conosciuti);
        Assert.Equal(1, valore.Eleggibili);
    }

    [Fact]
    public void SalvataggiRipetuti_NonFannoCrescereIlTotale()
    {
        var originale = Op("F1", "BCM", Esito.Conforme, 10);
        var t = Test("T-1",
            Rev(Giorno, originale),
            Rev(Giorno.AddDays(1), Copia(originale, 12)),
            Rev(Giorno.AddDays(2), Copia(originale, 12)),
            Rev(Giorno.AddDays(3), Copia(originale, 12)));

        var valore = Cella(Genera(Config(ModalitaCellaMatrice.TempoTotale), t), "F1", "BCM").Tempo;

        Assert.Equal(12, valore.SommaMinuti);
        Assert.Equal(1, valore.Eleggibili);
        Assert.Equal(12, ReportMatriceService.TotaleMinutiTest(t));
    }

    [Fact]
    public void CopiaConEcuCambiata_ConteggiaSoloNellEcuDellUltimaRevisione()
    {
        var originale = Op("F1", "BCM", Esito.Conforme, 10);
        var t = Test("T-1", Rev(Giorno, originale), Rev(Giorno.AddDays(1), Copia(originale, 12, componente: "ABS")));

        foreach (var modalita in new[] { (ModalitaCellaMatrice.TempoTotale, BaseTempoMatrice.TutteLeEsecuzioni), (ModalitaCellaMatrice.TempoTotale, BaseTempoMatrice.UltimoStato) })
        {
            var risultato = Genera(Config(modalita.Item1, modalita.Item2), t);
            var abs = Cella(risultato, "F1", "ABS").Tempo;
            var bcm = CellaOpzionale(risultato, "F1", "BCM");

            Assert.Equal(12, abs.SommaMinuti);
            Assert.Equal(1, abs.Eleggibili);
            Assert.Equal(0, bcm.Eleggibili);   // la vecchia componente non conserva il tentativo
        }
    }

    [Fact]
    public void TotaleTest_ContaLeOperazioniDtcEEscludeLoSnapshotUdt()
    {
        var guasto = new Condizione { Funzionalita = "F1", Esito = Esito.Conforme, Componente = new EcuSelection(["BCM"]), DurataMinuti = 30, Guasto = "P0100" };
        var t = Test("T-guasto", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 10), guasto));
        var importato = Test("T-UnitLogger", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 30)));
        importato.Origine = OrigineTest.ImportStatoUdt;

        Assert.Equal(40, ReportMatriceService.TotaleMinutiTest(t));
        Assert.Null(ReportMatriceService.TotaleMinutiTest(importato));
        // Le operazioni guasto non entrano nelle celle della matrice per funzionalita'.
        Assert.Equal(10, Cella(Genera(Config(ModalitaCellaMatrice.TempoTotale), t), "F1", "BCM").Tempo.SommaMinuti);
    }

    [Fact]
    public void FiltroOperazione_PerBase_UltimoStatoSullaCombinazioneCorrente_TuttiISuiTentativi()
    {
        var t = Test("T-1", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 20)), Rev(Giorno.AddDays(1), Op("F1", "BCM", Esito.Conforme, 10)));
        var tutte = Config(ModalitaCellaMatrice.TempoTotale);
        tutte.FiltroTempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 15);
        var ultimo = Config(ModalitaCellaMatrice.TempoTotale, BaseTempoMatrice.UltimoStato);
        ultimo.FiltroTempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 15);

        // Tutte le esecuzioni: ogni tentativo e' filtrato da solo, il 20 passa.
        Assert.Equal(20, Cella(Genera(tutte, t), "F1", "BCM").Tempo.SommaMinuti);
        // Ultimo stato: il vincitore e' il tentativo da 10; la combinazione corrente non corrisponde e non viene sostituita dal 20.
        Assert.Equal(0, CellaOpzionale(Genera(ultimo, t), "F1", "BCM").Eleggibili);
    }

    [Fact]
    public void FiltriModelli_ApplicanoLoStessoCriterioAlTest()
    {
        var t = Test("T-1", Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 10)), Rev(Giorno.AddDays(1), Op("F1", "BCM", Esito.Conforme, 20)));

        Assert.True(new ReportFilter { TempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 15) }.Corrisponde(t));
        Assert.False(new ReportFilter { TempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 25) }.Corrisponde(t));
        Assert.True(new ReportFilter { TempoTotaleTest = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 25) }.Corrisponde(t));
        Assert.False(new ReportFilter { TempoTotaleTest = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 40) }.Corrisponde(t));

        var dashboard = new DashboardFilter { TempoTotaleTest = new FiltroDurataMinuti(OperatoreDurata.Minore, 31) };
        Assert.Single(dashboard.Applica([t], _ => null));
        var dashboardNoto = new DashboardFilter { TempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Minore, 11) };
        Assert.Single(dashboardNoto.Applica([t], _ => null));
    }
}
