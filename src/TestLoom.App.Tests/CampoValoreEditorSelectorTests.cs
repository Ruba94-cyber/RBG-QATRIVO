using Avalonia.Controls;
using Avalonia.Controls.Templates;
using TestLoom.App.Models;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Perf 6.4.0 (richiesta utente 2026-09-25): copertura di <see cref="CampoValoreEditorSelector"/>,
/// che sostituisce nel DataTemplate di CampoStrumentoValore (TestCaseEditorView.axaml) il vecchio schema
/// "costruisci tutti i controlli di ogni Tipo e nascondi quelli sbagliati con IsVisible" con "costruisci SOLO
/// il controllo del Tipo del campo". Qui il comportamento del selector si verifica con IDataTemplate finti che
/// contano quante volte Build viene invocato, senza bisogno di una Application Avalonia reale: quello che
/// conta è che il selector interpelli UN SOLO sotto-template (quello giusto) e nessun altro.</summary>
public class CampoValoreEditorSelectorTests
{
    /// <summary>Template finto: registra ogni chiamata a Build e restituisce sempre lo stesso controllo, per
    /// poter verificare sia CHE sia stato costruito, sia che gli ALTRI non lo siano stati.</summary>
    private sealed class TemplateFinto : IDataTemplate
    {
        public int ChiamateBuild { get; private set; }
        public bool Match(object? data) => true;
        public Control? Build(object? param)
        {
            ChiamateBuild++;
            return new TextBlock();
        }
    }

    private static CampoStrumentoValore NuovoCampo(TipoCampoStrumento tipo) =>
        new(new Dictionary<string, string>(), "ValidationTool", new DefinizioneCampoStrumento { Nome = "Trace", Tipo = tipo });

    private sealed class Templates
    {
        public TemplateFinto Testo { get; } = new();
        public TemplateFinto Numero { get; } = new();
        public TemplateFinto Lista { get; } = new();
        public TemplateFinto Checkbox { get; } = new();
        public TemplateFinto Data { get; } = new();
        public TemplateFinto File { get; } = new();
        public TemplateFinto ListaTesto { get; } = new();

        public CampoValoreEditorSelector NuovoSelector() => new()
        {
            TestoTemplate = Testo,
            NumeroTemplate = Numero,
            ListaTemplate = Lista,
            CheckboxTemplate = Checkbox,
            DataCampoTemplate = Data,
            FileTemplate = File,
            ListaTestoTemplate = ListaTesto,
        };

        public IEnumerable<TemplateFinto> Tutti()
        {
            yield return Testo; yield return Numero; yield return Lista;
            yield return Checkbox; yield return Data; yield return File; yield return ListaTesto;
        }
    }

    [Theory]
    [InlineData(TipoCampoStrumento.Testo)]
    [InlineData(TipoCampoStrumento.Numero)]
    [InlineData(TipoCampoStrumento.Lista)]
    [InlineData(TipoCampoStrumento.Checkbox)]
    [InlineData(TipoCampoStrumento.Data)]
    [InlineData(TipoCampoStrumento.File)]
    [InlineData(TipoCampoStrumento.ListaTesto)]
    public void Build_CostruisceSoloIlTemplateDelTipoDelCampo_NessunAltro(TipoCampoStrumento tipo)
    {
        var templates = new Templates();
        var selector = templates.NuovoSelector();
        var campo = NuovoCampo(tipo);

        var risultato = selector.Build(campo);

        Assert.NotNull(risultato);
        var atteso = tipo switch
        {
            TipoCampoStrumento.Testo => templates.Testo,
            TipoCampoStrumento.Numero => templates.Numero,
            TipoCampoStrumento.Lista => templates.Lista,
            TipoCampoStrumento.Checkbox => templates.Checkbox,
            TipoCampoStrumento.Data => templates.Data,
            TipoCampoStrumento.File => templates.File,
            TipoCampoStrumento.ListaTesto => templates.ListaTesto,
            _ => throw new InvalidOperationException(),
        };

        foreach (var t in templates.Tutti())
            Assert.Equal(t == atteso ? 1 : 0, t.ChiamateBuild);
    }

    [Fact]
    public void Build_TipoSenzaTemplateImpostato_NonMaterializzaNulla()
    {
        // Selector "semplice" come quello usato per la maggior parte dei punti dell'editor: ListaTesto non ha
        // un sotto-template configurato (proprietà lasciata null), esattamente come prima nessun IsVisible
        // risultava vero per quel Tipo in quei punti.
        var selector = new CampoValoreEditorSelector
        {
            TestoTemplate = new TemplateFinto(),
        };
        var campo = NuovoCampo(TipoCampoStrumento.ListaTesto);

        var risultato = selector.Build(campo);

        Assert.Null(risultato);
    }

    [Fact]
    public void Build_ParamNonCampoStrumentoValore_RestituisceNull()
    {
        var selector = new Templates().NuovoSelector();
        Assert.Null(selector.Build("non un campo"));
        Assert.Null(selector.Build(null));
    }

    [Fact]
    public void Match_SoloCampoStrumentoValore()
    {
        var selector = new CampoValoreEditorSelector();
        Assert.True(selector.Match(NuovoCampo(TipoCampoStrumento.Testo)));
        Assert.False(selector.Match("altro"));
        Assert.False(selector.Match(null));
    }
}
