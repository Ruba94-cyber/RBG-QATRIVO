using Avalonia.Controls;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata al punto 4 (feedback utente 2026-08-20): anteprima live del form per Tipi di
/// test e Strumenti. DaTemplate/DaStrumento mappano i modelli reali (CampoTemplate/DefinizioneCampoStrumento)
/// in DTO di anteprima indipendenti dalla sorgente; Renderizza costruisce l'albero di controlli Avalonia.</summary>
public class TestFormPreviewRendererTests
{
    private static TestTemplate CreaTemplate()
    {
        var sezione1 = new SezioneTemplate
        {
            Nome = "Identificativi", Ordine = 0,
            Campi = new List<CampoTemplate>
            {
                new() { Nome = "Note", Ordine = 1, Tipo = TipoCampoTemplate.CampoLibero, TipoLibero = TipoCampoStrumento.Testo, Obbligatorio = false },
                new() { Nome = "Km", Ordine = 0, Tipo = TipoCampoTemplate.CampoLibero, TipoLibero = TipoCampoStrumento.Numero, Obbligatorio = true },
            }
        };
        var sezione2 = new SezioneTemplate
        {
            Nome = "Riferimenti", Ordine = 1,
            Campi = new List<CampoTemplate>
            {
                new() { Nome = "Esito preliminare", Tipo = TipoCampoTemplate.CampoLibero, TipoLibero = TipoCampoStrumento.Lista, OpzioniListaLibero = new List<string> { "OK", "KO" } },
            }
        };
        return new TestTemplate { Nome = "Template Test", Sezioni = new List<SezioneTemplate> { sezione2, sezione1 } }; // ordine volutamente invertito
    }

    [Fact]
    public void DaTemplate_OrdinaLeSezioniPerOrdine()
    {
        var risultato = TestFormPreviewRenderer.DaTemplate(CreaTemplate());
        Assert.Equal(new[] { "Identificativi", "Riferimenti" }, risultato.Select(s => s.Titolo));
    }

    [Fact]
    public void DaTemplate_OrdinaICampiPerOrdineDentroLaSezione()
    {
        var risultato = TestFormPreviewRenderer.DaTemplate(CreaTemplate());
        var identificativi = risultato.First(s => s.Titolo == "Identificativi");
        Assert.Equal(new[] { "Km", "Note" }, identificativi.Campi.Select(c => c.Nome));
    }

    [Fact]
    public void DaTemplate_CampoSistema_MappatoComeTestoGenerico()
    {
        var template = new TestTemplate
        {
            Sezioni = new List<SezioneTemplate>
            {
                new() { Nome = "S", Campi = new List<CampoTemplate> { new() { Nome = "Componente", Tipo = TipoCampoTemplate.CampoSistema, TipoLibero = TipoCampoStrumento.Lista } } }
            }
        };
        var risultato = TestFormPreviewRenderer.DaTemplate(template);
        Assert.Equal(TipoCampoStrumento.Testo, risultato[0].Campi[0].Tipo);
    }

    [Fact]
    public void DaTemplate_ObbligatorioPropagato()
    {
        var risultato = TestFormPreviewRenderer.DaTemplate(CreaTemplate());
        var km = risultato.First(s => s.Titolo == "Identificativi").Campi.First(c => c.Nome == "Km");
        Assert.True(km.Obbligatorio);
    }

    [Fact]
    public void DaTemplate_IncludePianificazioneERiferimenti_NonSoloDettagli()
    {
        // Regressione (feedback utente 2026-08-20): l'anteprima mostrava solo le sezioni di Dettagli
        // (template.Sezioni), ignorando CampiPianificazione/CampiRiferimenti — il foglio è personalizzabile
        // per intero, l'anteprima deve mostrarlo per intero.
        var template = CreaTemplate();
        template.CampiPianificazione = new List<CampoTemplate> { new() { Nome = "Scadenza", Tipo = TipoCampoTemplate.CampoSistema } };
        template.CampiRiferimenti = new List<CampoTemplate> { new() { Nome = "Ticket", Tipo = TipoCampoTemplate.CampoLibero, TipoLibero = TipoCampoStrumento.Testo } };

        var risultato = TestFormPreviewRenderer.DaTemplate(template);

        Assert.Equal(new[] { "Pianificazione", "Riferimenti", "Identificativi", "Riferimenti" }, risultato.Select(s => s.Titolo));
        Assert.Equal("Scadenza", risultato[0].Campi.Single().Nome);
        Assert.Equal("Ticket", risultato[1].Campi.Single().Nome);
    }

    [Fact]
    public void DaTemplate_SenzaPianificazioneORiferimenti_NonAggiungeSezioniVuote()
    {
        var risultato = TestFormPreviewRenderer.DaTemplate(CreaTemplate());
        Assert.DoesNotContain(risultato, s => s.Titolo == "Pianificazione");
    }

    [Fact]
    public void DaStrumento_MappaCampiInUnaSolaSezioneConTitoloNomeStrumento()
    {
        var strumento = new StrumentoDiagnostico
        {
            Nome = "ValidationTool",
            Campi = new List<DefinizioneCampoStrumento>
            {
                new() { Nome = "Tensione", Tipo = TipoCampoStrumento.Numero, UnitaMisura = "V" },
                new() { Nome = "Attivo", Tipo = TipoCampoStrumento.Checkbox },
            }
        };
        var risultato = TestFormPreviewRenderer.DaStrumento(strumento);
        Assert.Equal("ValidationTool", risultato.Titolo);
        Assert.Equal(2, risultato.Campi.Count);
        Assert.Equal("V", risultato.Campi.First(c => c.Nome == "Tensione").UnitaMisura);
    }

    [Fact]
    public void Renderizza_UnaSezioneConCampiVuoti_VieneOmessa()
    {
        var sezioni = new List<PreviewSezione>
        {
            new() { Titolo = "Vuota", Campi = Array.Empty<PreviewCampo>() },
            new() { Titolo = "Piena", Campi = new List<PreviewCampo> { new() { Nome = "X", Tipo = TipoCampoStrumento.Testo } } },
        };
        var risultato = (StackPanel)TestFormPreviewRenderer.Renderizza(sezioni);
        var card = Assert.Single(risultato.Children);
        var contenuto = (StackPanel)((Border)card).Child!;
        var titolo = (TextBlock)contenuto.Children[0];
        Assert.Equal("Piena", titolo.Text);
    }

    [Fact]
    public void Renderizza_NessunCampo_MostraMessaggioVuoto()
    {
        var risultato = (StackPanel)TestFormPreviewRenderer.Renderizza(new List<PreviewSezione>());
        Assert.Single(risultato.Children);
        Assert.IsType<TextBlock>(risultato.Children[0]);
    }

    [Fact]
    public void Renderizza_ControlliDisabilitati_SoloAnteprimaNonCompilabile()
    {
        var sezioni = new List<PreviewSezione>
        {
            new() { Titolo = "S", Campi = new List<PreviewCampo> { new() { Nome = "Campo", Tipo = TipoCampoStrumento.Testo } } }
        };
        var risultato = (StackPanel)TestFormPreviewRenderer.Renderizza(sezioni);
        var card = (Border)risultato.Children[0];
        var contenuto = (StackPanel)card.Child!;
        var griglia = (WrapPanel)contenuto.Children[1];
        var colonna = (StackPanel)griglia.Children[0];
        var controllo = colonna.Children[1];
        Assert.False(controllo.IsEnabled);
    }

    [Theory]
    [InlineData(TipoCampoStrumento.Testo, typeof(TextBox))]
    [InlineData(TipoCampoStrumento.Lista, typeof(ComboBox))]
    [InlineData(TipoCampoStrumento.Checkbox, typeof(ToggleSwitch))]
    [InlineData(TipoCampoStrumento.Data, typeof(CalendarDatePicker))]
    public void Renderizza_SceglieIlControlloGiustoPerOgniTipoCampo(TipoCampoStrumento tipo, Type controlloAtteso)
    {
        var sezioni = new List<PreviewSezione> { new() { Titolo = "S", Campi = new List<PreviewCampo> { new() { Nome = "Campo", Tipo = tipo } } } };
        var risultato = (StackPanel)TestFormPreviewRenderer.Renderizza(sezioni);
        var card = (Border)risultato.Children[0];
        var contenuto = (StackPanel)card.Child!;
        var griglia = (WrapPanel)contenuto.Children[1];
        var colonna = (StackPanel)griglia.Children[0];
        Assert.IsType(controlloAtteso, colonna.Children[1]);
    }

    [Fact]
    public void Renderizza_CampoObbligatorio_EtichettaHaAsterisco()
    {
        var sezioni = new List<PreviewSezione> { new() { Titolo = "S", Campi = new List<PreviewCampo> { new() { Nome = "Campo", Tipo = TipoCampoStrumento.Testo, Obbligatorio = true } } } };
        var risultato = (StackPanel)TestFormPreviewRenderer.Renderizza(sezioni);
        var card = (Border)risultato.Children[0];
        var contenuto = (StackPanel)card.Child!;
        var griglia = (WrapPanel)contenuto.Children[1];
        var colonna = (StackPanel)griglia.Children[0];
        var etichetta = (TextBlock)colonna.Children[0];
        Assert.Equal("Campo *", etichetta.Text);
    }
}
