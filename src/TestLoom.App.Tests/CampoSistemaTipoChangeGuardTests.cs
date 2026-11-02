using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class CampoSistemaTipoChangeGuardTests
{
    [Fact]
    public void HaDatiEsistenti_TrueSeAlmenoUnTestCaseHaValoreSulRuoloEcu()
    {
        var definizione = new CampoSistemaDefinizione { Chiave = "Componente", Ruolo = RuoloCampoSistema.Componente };
        var testCases = new List<TestCase> { new() { Componente = "Componente-1" } };

        Assert.True(CampoSistemaTipoChangeGuard.HaDatiEsistenti(definizione, testCases));
    }

    [Fact]
    public void HaDatiEsistenti_FalseSeNessunTestCaseHaValore()
    {
        var definizione = new CampoSistemaDefinizione { Chiave = "Componente", Ruolo = RuoloCampoSistema.Componente };
        var testCases = new List<TestCase> { new() { Componente = "" } };

        Assert.False(CampoSistemaTipoChangeGuard.HaDatiEsistenti(definizione, testCases));
    }

    [Fact]
    public void HaDatiEsistenti_CampoSenzaRuolo_ControllaCampiCustom()
    {
        var definizione = new CampoSistemaDefinizione { Chiave = "MioCampo", Ruolo = RuoloCampoSistema.Nessuno };
        var testCases = new List<TestCase> { new() { CampiCustom = { ["MioCampo"] = "valore" } } };

        Assert.True(CampoSistemaTipoChangeGuard.HaDatiEsistenti(definizione, testCases));
    }

    [Fact]
    public void HaDatiEsistenti_RuoloFunzionalita_ControllaListaNonVuota()
    {
        var definizione = new CampoSistemaDefinizione { Chiave = "Funzionalita", Ruolo = RuoloCampoSistema.Funzionalita };
        var testCases = new List<TestCase> { new() { Funzionalita = new List<string> { "F1" } } };

        Assert.True(CampoSistemaTipoChangeGuard.HaDatiEsistenti(definizione, testCases));
    }

    [Fact]
    public void HaDatiEsistenti_RuoloScadenza_ControllaValoreImpostato()
    {
        var definizione = new CampoSistemaDefinizione { Chiave = "Scadenza", Ruolo = RuoloCampoSistema.Scadenza };
        var testCases = new List<TestCase> { new() { Scadenza = DateTime.Now } };

        Assert.True(CampoSistemaTipoChangeGuard.HaDatiEsistenti(definizione, testCases));
    }
}
