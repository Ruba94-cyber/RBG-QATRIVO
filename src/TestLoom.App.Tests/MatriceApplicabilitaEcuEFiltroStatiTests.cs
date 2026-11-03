using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Matrici Requisito x componente: (A) un requisito Extra relativo a certe componente e' "N.A." nelle altre e i componenti non coinvolte escono
/// dalla matrice (Report e Audit), senza toccare i dati persistiti; (C) filtro per stato che nasconde righe e colonne senza
/// celle negli stati scelti (default: nessun cambio).
/// </summary>
public sealed class MatriceApplicabilitaEcuEFiltroStatiTests
{
    private static TestCase CreaTest(string id, string requisito, string componente, Esito esito)
    {
        var tc = new TestCase
        {
            Id = id,
            Componente = new EcuSelection([componente]),
            Veicoli = ["Daily A"],
            Funzionalita = ["Lettura guasto"],
            RequisitoId = [requisito],
        };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(2026, 1, 1),
            Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = esito, Componente = new EcuSelection([componente]) }]
        });
        return tc;
    }

    private static ConfigurazioneMatriceReport Config(bool ristretto, params StatoCellaMatrice[] stati)
    {
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Requisito };
        if (ristretto) config.EcuRistrettePerRequisito["R1"] = new HashSet<string>(["BCM"], StringComparer.OrdinalIgnoreCase);
        foreach (var s in stati) config.StatiFiltroRigheColonne.Add(s);
        return config;
    }

    private static Requisito Extra(string id, params (string Veicolo, string Componente)[] coppie)
    {
        var r = new Requisito { Id = id };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        if (coppie.Length > 0) r.VeicoliEcuD4D = coppie.Select(c => new RequisitoVeicoloEcu { Veicolo = c.Veicolo, Componente = c.Componente }).ToList();
        return r;
    }

    // ---------------------------------------------------------------- dominio

    [Fact]
    public void SiApplicaAllaEcu_StandardESenzaCoppie_ValeSempre_D4dConCoppie_SoloLeEcuDichiarate()
    {
        Assert.True(RequisitoApplicabilita.SiApplicaAllaEcu(new Requisito { Id = "S" }, "BCM"));
        Assert.True(RequisitoApplicabilita.SiApplicaAllaEcu(Extra("D"), "BCM"));
        var ristretto = Extra("D", ("Daily A", "BCM"), ("Daily B", "EDC"));
        Assert.True(RequisitoApplicabilita.SiApplicaAllaEcu(ristretto, "BCM"));
        Assert.True(RequisitoApplicabilita.SiApplicaAllaEcu(ristretto, "EDC")); // senza veicolo: unione delle coppie
        Assert.False(RequisitoApplicabilita.SiApplicaAllaEcu(ristretto, "ABS"));
        Assert.False(RequisitoApplicabilita.SiApplicaAllaEcu(ristretto, "EDC", "Daily A")); // per il veicolo contano le sue coppie
        Assert.True(RequisitoApplicabilita.SiApplicaAllaEcu(ristretto, "EDC", "Daily B"));
        Assert.True(RequisitoApplicabilita.SiApplicaAllaEcu(ristretto, "BCM", "Daily C")); // veicolo senza coppie: unione
    }

    [Fact]
    public void EcuRistrettePerRequisito_SoloD4dConCoppieNonArchiviati()
    {
        var archiviato = Extra("A", ("V", "BCM"));
        archiviato.Archiviata = true;
        var mappa = RequisitoApplicabilita.EcuRistrettePerRequisito(
            [Extra("R1", ("V", "BCM"), ("V", "ABS")), Extra("R2"), new Requisito { Id = "R3" }, archiviato]);
        var voce = Assert.Single(mappa);
        Assert.Equal("R1", voce.Key);
        Assert.Equal(new[] { "ABS", "BCM" }, voce.Value.OrderBy(x => x));
    }

    // ---------------------------------------------------------------- Report A

    [Fact]
    public void Report_RequisitoRistretto_NelleAltreEcuLaCellaENonApplicabile_ENonEntraNeiTotali()
    {
        var tests = new[]
        {
            CreaTest("T1", "R1", "BCM", Esito.Conforme),
            CreaTest("T2", "R1", "EDC", Esito.NonConforme), // dato presente ma R1 non riguarda EDC
            CreaTest("T3", "R2", "EDC", Esito.Conforme),
        };

        var r = ReportMatriceService.Genera(tests, Config(ristretto: true), [], [], []);

        Assert.Equal(new[] { "BCM", "EDC" }, r.Colonne);
        var riga1 = r.Righe.Single(x => x.Riga == "R1");
        var riga2 = r.Righe.Single(x => x.Riga == "R2");
        Assert.False(riga1.Celle[r.Colonne.IndexOf("BCM")].NonApplicabile);
        var celleEdc = riga1.Celle[r.Colonne.IndexOf("EDC")];
        Assert.True(celleEdc.NonApplicabile);
        Assert.Equal("N.A.", celleEdc.TestoVisualizzato);
        Assert.False(riga2.Celle[r.Colonne.IndexOf("EDC")].NonApplicabile);
        // Il NonConforme di R1/EDC non pesa sul totale della colonna EDC: resta solo R2 (conforme).
        var totaleEdc = r.TotaliColonna[r.Colonne.IndexOf("EDC")];
        Assert.Equal(1, totaleEdc.Valutati);
        Assert.Equal(1, totaleEdc.Conformi);
    }

    [Fact]
    public void Report_EcuNonCoinvoltaDaNessunRequisito_NascostaDallaMatrice()
    {
        var tests = new[] { CreaTest("T1", "R1", "BCM", Esito.Conforme), CreaTest("T2", "R1", "EDC", Esito.Conforme) };

        var senza = ReportMatriceService.Genera(tests, Config(ristretto: false), [], [], []);
        var con = ReportMatriceService.Genera(tests, Config(ristretto: true), [], [], []);

        Assert.Equal(new[] { "BCM", "EDC" }, senza.Colonne);
        Assert.Equal(new[] { "BCM" }, con.Colonne); // EDC: l'unico requisito e' relativo solo a BCM
        Assert.False(senza.Righe.Single().Celle.Any(c => c.NonApplicabile));
    }

    [Fact]
    public void Report_EcuSuRigheERequisitoSuColonne_StessoComportamento()
    {
        var tests = new[] { CreaTest("T1", "R1", "BCM", Esito.Conforme), CreaTest("T2", "R1", "EDC", Esito.Conforme) };
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Requisito, AsseY = DimensioniMatrice.Componente };
        config.EcuRistrettePerRequisito["R1"] = new HashSet<string>(["BCM"], StringComparer.OrdinalIgnoreCase);

        var r = ReportMatriceService.Genera(tests, config, [], [], []);

        Assert.Equal(new[] { "BCM" }, r.Righe.Select(x => x.Riga));
    }

    [Fact]
    public void Report_AssiSenzaRequisitoEcu_MappaIgnorata()
    {
        var tests = new[] { CreaTest("T1", "R1", "BCM", Esito.Conforme), CreaTest("T2", "R1", "EDC", Esito.Conforme) };
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Funzionalita };
        config.EcuRistrettePerRequisito["R1"] = new HashSet<string>(["BCM"], StringComparer.OrdinalIgnoreCase);

        var r = ReportMatriceService.Genera(tests, config, [], [], []);

        Assert.Equal(new[] { "BCM", "EDC" }, r.Colonne);
    }

    // ---------------------------------------------------------------- Report C

    private static TestCase[] DatiStati() =>
    [
        CreaTest("T1", "R1", "BCM", Esito.Conforme),
        CreaTest("T2", "R2", "EDC", Esito.NonConforme),
    ];

    [Fact]
    public void FiltroStati_Default_TuttoVisibile()
    {
        var r = ReportMatriceService.Genera(DatiStati(), Config(false), [], [], []);

        Assert.Equal(new[] { "BCM", "EDC" }, r.Colonne);
        Assert.Equal(new[] { "R1", "R2" }, r.Righe.Select(x => x.Riga));
    }

    [Fact]
    public void FiltroStati_NonConforme_NascondeRigheEColonneSenzaQuelloStato_TotaliInvariati()
    {
        var completo = ReportMatriceService.Genera(DatiStati(), Config(false), [], [], []);
        var r = ReportMatriceService.Genera(DatiStati(), Config(false, StatoCellaMatrice.NonConforme), [], [], []);

        Assert.Equal(new[] { "EDC" }, r.Colonne);
        Assert.Equal(new[] { "R2" }, r.Righe.Select(x => x.Riga));
        Assert.Equal(completo.TotaleGenerale.Testo, r.TotaleGenerale.Testo);
    }

    [Fact]
    public void FiltroStati_Multiselezione_MostraLUnioneDegliStati()
    {
        var r = ReportMatriceService.Genera(DatiStati(), Config(false, StatoCellaMatrice.NonConforme, StatoCellaMatrice.Conforme), [], [], []);

        Assert.Equal(new[] { "BCM", "EDC" }, r.Colonne);
        Assert.Equal(new[] { "R1", "R2" }, r.Righe.Select(x => x.Riga));
    }

    [Fact]
    public void FiltroStati_StatoAssente_MatriceVuota()
    {
        var r = ReportMatriceService.Genera(DatiStati(), Config(false, StatoCellaMatrice.DaDecidere), [], [], []);

        Assert.Empty(r.Colonne);
        Assert.Empty(r.Righe);
    }

    [Fact]
    public void FiltroStati_NonConsideraLeCelleNonApplicabili()
    {
        // R1/EDC e' NonConforme ma N.A. (R1 riguarda solo BCM): non deve tenere visibile la colonna EDC.
        var tests = new[]
        {
            CreaTest("T1", "R1", "BCM", Esito.Conforme),
            CreaTest("T2", "R1", "EDC", Esito.NonConforme),
            CreaTest("T3", "R2", "EDC", Esito.Conforme),
        };

        var r = ReportMatriceService.Genera(tests, Config(true, StatoCellaMatrice.NonConforme), [], [], []);

        Assert.Empty(r.Colonne);
    }

    [Fact]
    public void FiltroStati_VistaEdExportUsanoLaStessaSorgente_FinestraEquivalenteAlCompleto()
    {
        var prep = ReportMatriceService.Prepara(DatiStati(), Config(false, StatoCellaMatrice.NonConforme), [], [], []);

        Assert.Equal(new[] { "EDC" }, prep.TutteLeColonne);
        Assert.Equal(new[] { "R2" }, prep.TutteLeRighe);
        var finestra = prep.Finestra(0, 10, 0, 10).Risultato;
        Assert.Equal(prep.Completa().Colonne, finestra.Colonne);
    }

    // ---------------------------------------------------------------- ViewModel Report

    [Fact]
    public void ViewModel_FiltroStatiMatrice_SeiStatiDefaultNessunaSelezioneESelezionePersistita()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoomFiltroStati_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(dir);
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        try
        {
            var vm = new ReportViewModel(state);
            Assert.Equal(6, vm.FiltroStatiMatrice.Opzioni.Count);
            Assert.False(vm.FiltroStatiMatrice.HaSelezione);

            vm.FiltroStatiMatrice.Opzioni.Single(o => o.Valore == nameof(StatoCellaMatrice.NonConforme)).Selezionato = true;

            Assert.True(vm.HaFiltriAttivi);
            Assert.Contains(vm.FiltroStatiMatrice.Opzioni.Single(o => o.Selezionato).Etichetta, vm.RiepilogoFiltriAttivi);
            var vm2 = new ReportViewModel(state);
            Assert.Equal(new[] { nameof(StatoCellaMatrice.NonConforme) }, vm2.FiltroStatiMatrice.Selezionati);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ---------------------------------------------------------------- Audit

    private static Componente EcuDi(string nome) => new() { Nome = nome, VeicoliAssociati = ["Daily A"] };

    [Fact]
    public void Audit_RequisitoD4dRistretto_CellaNonApplicabileNelleAltreEcu_EEcuNonCoinvoltaNascosta()
    {
        var componente = new List<Componente> { EcuDi("BCM"), EcuDi("EDC") };
        var ristretto = Extra("R1", ("Daily A", "BCM"));

        var celle = AuditMatriceService.Matrice("Daily A", componente, [ristretto], []);

        Assert.False(celle.Single(c => c.Componente == "BCM").NonApplicabileEcu);
        Assert.True(celle.Single(c => c.Componente == "EDC").NonApplicabileEcu);
        Assert.Equal(new[] { "BCM" }, AuditMatriceService.EcuConAlmenoUnaCellaApplicabile(["BCM", "EDC"], celle));
    }

    [Fact]
    public void Audit_EcuConAlmenoUnRequisitoApplicabile_RestaVisibile_ESenzaCelleInvariata()
    {
        var componente = new List<Componente> { EcuDi("BCM"), EcuDi("EDC") };
        var celle = AuditMatriceService.Matrice("Daily A", componente, [Extra("R1", ("Daily A", "BCM")), new Requisito { Id = "R2" }], []);

        Assert.Equal(new[] { "BCM", "EDC" }, AuditMatriceService.EcuConAlmenoUnaCellaApplicabile(["BCM", "EDC"], celle));
        Assert.Equal(new[] { "BCM", "ABS" }, AuditMatriceService.EcuConAlmenoUnaCellaApplicabile(["BCM", "ABS"], celle.Where(c => c.Componente == "BCM")));
    }

    [Fact]
    public void Audit_Kpi_LeCelleNonApplicabiliNonContanoNelTotale()
    {
        var componente = new List<Componente> { EcuDi("BCM"), EcuDi("EDC") };
        var requisiti = new List<Requisito> { Extra("R1", ("Daily A", "BCM")), new() { Id = "R2" } };

        var kpi = AuditMatriceService.CalcolaKpiComplessivi([new Veicolo { Nome = "Daily A" }], componente, requisiti, []);

        // R2 x (BCM, EDC) + R1 x BCM = 3 (R1 x EDC e' N.A.)
        Assert.Equal(3, kpi.TotaleDaTestare);
    }
}
