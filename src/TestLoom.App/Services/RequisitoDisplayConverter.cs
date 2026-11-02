using System.Globalization;
using Avalonia.Data.Converters;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Binding read-only per la griglia Requisiti. Requisito resta il tipo base per compatibilita; Tipo e target
/// Extra possono quindi provenire anche da JsonExtensionData e vengono risolti dal servizio di dominio unico.
/// </summary>
public sealed class RequisitoDisplayConverter : IValueConverter
{
    public static RequisitoDisplayConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Requisito requisito) return "";
        return parameter?.ToString() switch
        {
            "tipo" => RequisitoApplicabilita.TipoDi(requisito).ToString(),
            "sottoveicoli" => RequisitoApplicabilita.ED4D(requisito)
                ? string.Join(", ", RequisitoApplicabilita.SottoveicoliDi(requisito))
                : "Tutti (cross)",
            "veicoliecu" => requisito.VeicoliEcuD4D is { Count: > 0 } associazioni
                ? string.Join(", ", associazioni.Select(x => $"{x.Veicolo} / {x.Componente}"))
                : LocalizationService.Instance.Scegli("Seleziona componente", "Select Componenti", "Sélectionner les componente"),
            "ambito" => RequisitoApplicabilita.DescrizioneAmbito(requisito),
            _ => ""
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
