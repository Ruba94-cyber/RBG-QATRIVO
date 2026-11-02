using Avalonia;
using TestLoom.App.Models;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Riserva della review sul commit be89a65 (nessun test unitario sul MultiValueConverter): copre
/// <see cref="CampiSeEspansoConverter"/>, che decide se l'ItemsControl annidato dentro un GruppoCampiStrumento
/// costruisce i suoi container (Expander espanso) oppure resta vuoto (Expander chiuso, caso comune
/// all'apertura del Test) — vedi il commento su TestCaseEditorView.axaml.cs per il razionale perf.</summary>
public class CampiSeEspansoConverterTests
{
    private static readonly List<CampoStrumentoValore> Campi = new()
    {
        new CampoStrumentoValore(new Dictionary<string, string>(), "ValidationTool", new DefinizioneCampoStrumento { Nome = "Trace", Tipo = TipoCampoStrumento.Testo }),
    };

    [Fact]
    public void Convert_EspansoFalso_RestituisceNull()
    {
        var risultato = CampiSeEspansoConverter.Instance.Convert(new List<object?> { Campi, false }, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Null(risultato);
    }

    [Fact]
    public void Convert_EspansoVero_RestituisceICampi()
    {
        var risultato = CampiSeEspansoConverter.Instance.Convert(new List<object?> { Campi, true }, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Same(Campi, risultato);
    }

    [Fact]
    public void Convert_SecondoValoreUnsetValue_RestituisceNull()
    {
        var risultato = CampiSeEspansoConverter.Instance.Convert(new List<object?> { Campi, AvaloniaProperty.UnsetValue }, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Null(risultato);
    }

    [Fact]
    public void Convert_MenoDiDueValori_RestituisceNull()
    {
        var risultato = CampiSeEspansoConverter.Instance.Convert(new List<object?> { Campi }, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Null(risultato);
    }
}
