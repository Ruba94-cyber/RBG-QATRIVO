using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.5.0: nell'Audit i requisiti Extra sono una lista/sezione SEPARATA dagli Standard, con propri totali
/// (i totali Standard non includono i Extra); i Extra con campi facoltativi vuoti non creano celle fantasma.</summary>
public sealed class AuditD4dSeparazioneTests
{
    private static Requisito Std(string id) => new() { Id = id, Testo = "t " + id };
    private static Requisito Extra(string id, params string[] sottoveicoli)
    {
        var r = new Requisito { Id = id, Testo = "t " + id };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        if (sottoveicoli.Length > 0) RequisitoApplicabilita.ImpostaSottoveicoli(r, sottoveicoli);
        return r;
    }

    private static TestCase Test(string id, string componente, string veicolo, string req, PunteggioConformita p)
    {
        var tc = new TestCase { Id = id, Componente = new EcuSelection(new[] { componente }), Veicolo = veicolo, Veicoli = { veicolo }, RequisitoId = { req } };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = DateTime.Now, Esito = Esito.Conforme,
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = req, Valore = p } }
        });
        return tc;
    }

    private static readonly Veicolo V = new() { Nome = "VEH-A" };
    private static readonly Componente E = new() { Nome = "Componente-1", VeicoliAssociati = { "VEH-A" } };

    [Fact]
    public void SeparaPerTipo_DividePulito()
    {
        var (s, d) = AuditMatriceService.SeparaPerTipo(new[] { Std("S1"), Extra("D1"), Std("S2") });
        Assert.Equal(new[] { "S1", "S2" }, s.Select(x => x.Id));
        Assert.Equal(new[] { "D1" }, d.Select(x => x.Id));
    }

    [Fact]
    public void Kpi_TotaliStandardNonIncludonoD4d_E_D4dHaiPropriTotali()
    {
        var reqs = new[] { Std("S1"), Extra("D1", "VEH-A") };
        var tests = new List<TestCase>
        {
            Test("T1", "Componente-1", "VEH-A", "S1", PunteggioConformita.Conforme),
            Test("T2", "Componente-1", "VEH-A", "D1", PunteggioConformita.Conforme),
        };
        var (std, extra) = AuditMatriceService.SeparaPerTipo(reqs);
        var kStd = AuditMatriceService.CalcolaKpiComplessivi(new[] { V }, new[] { E }, std, tests);
        var kD4d = AuditMatriceService.CalcolaKpiComplessivi(new[] { V }, new[] { E }, extra, tests);
        var kTutti = AuditMatriceService.CalcolaKpiComplessivi(new[] { V }, new[] { E }, reqs, tests);

        Assert.Equal(1, kStd.TotaleValutate);
        Assert.Equal(0, kStd.TotaleDaTestare);
        Assert.Equal(1, kD4d.TotaleValutate);
        Assert.Equal(2, kTutti.TotaleValutate);
        Assert.Equal(kStd.TotaleValutate + kD4d.TotaleValutate, kTutti.TotaleValutate);
    }

    [Fact]
    public void Extra_SenzaSottoveicoli_NellaMatriceDiOgniVeicolo_SenzaCelleFantasma()
    {
        var celle = AuditMatriceService.Matrice("VEH-A", new[] { E }, new[] { Extra("D-LIBERO") },
            new List<TestCase>());
        var c = Assert.Single(celle);
        Assert.Equal(("Componente-1", "D-LIBERO"), (c.Componente, c.RequisitoId));
        Assert.Equal(StatoCellaAudit.DaTestare, c.Stato);
    }

    [Fact]
    public void Extra_ConSottoveicoloDiverso_NonCompareNellaMatrice()
    {
        var celle = AuditMatriceService.Matrice("VEH-A", new[] { E }, new[] { Extra("D-ALTRO", "VEH-B") }, new List<TestCase>());
        Assert.Empty(celle);
    }

    [Fact]
    public void PunteggioPerRequisito_D4dContribuisceComeStandard()
    {
        var d = Extra("D1", "VEH-A");
        var tests = new List<TestCase> { Test("T2", "Componente-1", "VEH-A", "D1", PunteggioConformita.Conforme) };
        var cella = AuditMatriceService.Matrice("VEH-A", new[] { E }, new[] { d }, tests).Single();
        Assert.Equal(StatoCellaAudit.Valutato, cella.Stato);
        Assert.Equal(PunteggioConformita.Conforme, cella.Punteggio);
    }
}
