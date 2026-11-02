using TestLoom.App.Models;

namespace TestLoom.App.Tests;

public sealed class StateSafetyRegressionTests
{
    [Fact]
    public void EcuSelection_Contains_UsaConfrontoEsattoENonSottostringa()
    {
        var componente = new EcuSelection(new[] { "BCM2", "ECM" });

        Assert.False(componente.Contains("BCM", StringComparison.OrdinalIgnoreCase));
        Assert.True(componente.Contains("bcm2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CampoStrumentoValore_Storico_NonMaterializzaDefaultNelDizionario()
    {
        var valoriStorici = new Dictionary<string, string>();
        var definizione = new DefinizioneCampoStrumento
        {
            Nome = "Versione UnitLogger",
            Tipo = TipoCampoStrumento.Testo,
            ValoreDefault = "12.1"
        };

        var campo = new CampoStrumentoValore(valoriStorici, "UnitLogger", definizione, materializzaDefault: false);

        Assert.Empty(valoriStorici);
        Assert.Null(campo.Valore);
        Assert.Equal("12.1", campo.ValoreDefault);
    }

    [Fact]
    public void CampoStrumentoValore_Corrente_MaterializzaDefaultComePrima()
    {
        var valoriCorrenti = new Dictionary<string, string>();
        var definizione = new DefinizioneCampoStrumento
        {
            Nome = "Versione UnitLogger",
            Tipo = TipoCampoStrumento.Testo,
            ValoreDefault = "12.1"
        };

        var campo = new CampoStrumentoValore(valoriCorrenti, "UnitLogger", definizione);

        Assert.Equal("12.1", valoriCorrenti["UnitLogger::Versione UnitLogger"]);
        Assert.Equal("12.1", campo.Valore);
    }

    [Fact]
    public void CampoStrumentoValore_DefaultListaObsoleto_NonVieneApplicatoEMantieneIlValoreGiaSalvato()
    {
        var definizione = new DefinizioneCampoStrumento
        {
            Nome = "Profilo",
            Tipo = TipoCampoStrumento.Lista,
            OpzioniLista = new() { "Fast", "Safe" },
            ValoreDefault = "Safe",
        };
        var valoriNuovoTest = new Dictionary<string, string>();

        var campoNuovo = new CampoStrumentoValore(valoriNuovoTest, "ValidationTool", definizione, "Obsoleto");

        Assert.Equal("Safe", valoriNuovoTest["ValidationTool::Profilo"]); // fallback Master valido
        Assert.Equal("Safe", campoNuovo.Valore);
        Assert.True(campoNuovo.PrecompilatoDaDefault);
        Assert.True(campoNuovo.HaDefaultPersonale); // il default vecchio resta rimovibile
        Assert.False(campoNuovo.EDefaultPersonale);
        Assert.Equal("Safe", campoNuovo.ValoreDefault); // fallback valido del Master
        Assert.True(campoNuovo.EDefaultMaster);

        var valoriTestSalvato = new Dictionary<string, string> { ["ValidationTool::Profilo"] = "Obsoleto" };
        var campoTestSalvato = new CampoStrumentoValore(valoriTestSalvato, "ValidationTool", definizione, "Obsoleto");
        Assert.Equal("Obsoleto", campoTestSalvato.Valore);
        Assert.Contains("Obsoleto", campoTestSalvato.OpzioniVisualizzate); // i Test esistenti restano leggibili/modificabili
        Assert.False(campoTestSalvato.EDefaultPersonale);
    }

    [Fact]
    public void CampoStrumentoValore_DefaultCheckboxEDateNonValidi_NonVengonoMaterializzati()
    {
        var valori = new Dictionary<string, string>();
        var checkbox = new CampoStrumentoValore(valori, "ValidationTool", new DefinizioneCampoStrumento
        {
            Nome = "Attivo",
            Tipo = TipoCampoStrumento.Checkbox,
            ValoreDefault = "yes",
        }, "si");
        var data = new CampoStrumentoValore(valori, "ValidationTool", new DefinizioneCampoStrumento
        {
            Nome = "Scadenza",
            Tipo = TipoCampoStrumento.Data,
            ValoreDefault = "31/02/2026",
        }, "ieri");

        Assert.Null(checkbox.Valore);
        Assert.Null(checkbox.ValoreCheckbox);
        Assert.Null(data.Valore);
        Assert.False(valori.ContainsKey("ValidationTool::Attivo"));
        Assert.False(valori.ContainsKey("ValidationTool::Scadenza"));
        Assert.True(checkbox.HaDefaultPersonale); // può ancora essere rimosso esplicitamente
        Assert.False(checkbox.EDefaultPersonale);
    }

    [Fact]
    public void CampoStrumentoValore_DefaultFilePersonaleStorico_NonMaterializzaENonOffreAzioniDefault()
    {
        var valoriNuovoTest = new Dictionary<string, string>();
        var file = new CampoStrumentoValore(valoriNuovoTest, "ValidationTool", new DefinizioneCampoStrumento
        {
            Nome = "Allegato",
            Tipo = TipoCampoStrumento.File,
        }, "report-precedente.pdf");

        Assert.Empty(valoriNuovoTest);
        Assert.Null(file.Valore);
        Assert.Null(file.ValoreDefault);
        Assert.False(file.PrecompilatoDaDefault);
        Assert.False(file.HaDefaultPersonale);
        Assert.False(file.MostraRicordaDefault);

        var valoriNormali = new Dictionary<string, string>();
        var profilo = new CampoStrumentoValore(valoriNormali, "ValidationTool", new DefinizioneCampoStrumento
        {
            Nome = "Profilo",
            Tipo = TipoCampoStrumento.Testo,
        }, "Lingua-QA");

        Assert.Equal("Lingua-QA", profilo.Valore);
        Assert.Equal("Lingua-QA", valoriNormali["ValidationTool::Profilo"]);
        Assert.True(profilo.PrecompilatoDaDefault);
        Assert.True(profilo.MostraEtichettaDefaultPersonale);
    }

}
