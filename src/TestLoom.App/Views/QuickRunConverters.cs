using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using TestLoom.App.Models;

namespace TestLoom.App.Views;

/// <summary>Vero se l'esito dell'operazione e' quello indicato nel parametro (nome dell'enum): evidenzia il pulsante scelto nella riga rapida.</summary>
public sealed class EsitoUgualeConverter : IValueConverter
{
    public static readonly EsitoUgualeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Esito esito && parameter is string nome && Enum.TryParse<Esito>(nome, out var atteso) && esito == atteso;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>Restituisce la collezione (stesso riferimento: l'ItemsControl continua a osservarne le modifiche) solo se la modalita'
/// Rapida coincide col parametro ("true"/"false"), altrimenti una lista vuota: la vista non in uso non istanzia nessuna scheda.</summary>
public sealed class CollezioneSeModoRapidoConverter : IMultiValueConverter
{
    public static readonly CollezioneSeModoRapidoConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var atteso = string.Equals(parameter as string, "true", StringComparison.OrdinalIgnoreCase);
        var rapido = values.Count > 1 && values[1] is true;
        return rapido == atteso && values.Count > 0 && values[0] is System.Collections.IEnumerable e ? e : Array.Empty<object>();
    }
}

/// <summary>Riga visibile se il filtro "solo da eseguire" e' spento oppure l'operazione e' ancora "Da eseguire".</summary>
public sealed class RigaVisibileFiltroConverter : IMultiValueConverter
{
    public static readonly RigaVisibileFiltroConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        !(values.Count > 0 && values[0] is true) || (values.Count > 1 && values[1] is Esito.DaEseguire);
}

/// <summary>Restituisce il primo valore solo se il secondo (bool) e' vero, altrimenti null: crea il contenuto pesante di una riga
/// (la scheda completa dell'operazione) soltanto quando l'utente la espande.</summary>
public sealed class ContenutoSeVeroConverter : IMultiValueConverter
{
    public static readonly ContenutoSeVeroConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count >= 2 && values[1] is true ? values[0] : null;
}
