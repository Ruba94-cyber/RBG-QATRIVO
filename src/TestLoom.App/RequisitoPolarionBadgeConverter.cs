using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.Views;

namespace TestLoom.App;

/// <summary>Badge Ticketing di un requisito per liste ed editor.</summary>
public sealed class RequisitoBadge
{
    public string Id { get; init; } = "";
    public string Testo { get; init; } = "";
    public string Tooltip { get; init; } = "";
    public IBrush Pennello { get; init; } = Brushes.Gray;
}

/// <summary>
/// Converter (DataPaths, versione cache) -> badge. Legge SOLO la cache in memoria <see cref="RequisitoPolarionCache"/>: nessun
/// I/O per riga e nessuna rete. Un requisito senza collegamento (o con cache non ancora pronta) non produce badge.
/// Valori: [0] TestCase o string (ID requisito), [1] DataPaths, [2] versione (serve solo a rinfrescare il binding).
/// Parametro per un singolo ID: "testo", "pennello", "tooltip", "visibile"; senza parametro con TestCase: elenco di <see cref="RequisitoBadge"/>.
/// </summary>
public sealed class RequisitoPolarionBadgeConverter : IMultiValueConverter
{
    public static readonly RequisitoPolarionBadgeConverter Instance = new();
    private const int MaxBadgePerRiga = 4;

    public static RequisitoBadge? Crea(DataPaths? paths, string id)
    {
        var s = RequisitoPolarionCache.TryGet(paths, id);
        if (s is not { Collegato: true }) return null;
        var loc = LocalizationService.Instance;
        var testo = s.TestoBreve(loc);
        var tip = id + " · " + s.Link + "\n" + RequisitoPolarionUi.EtichettaClasse(s.Classe) +
                  (s.Classe == PolarionStatoClasse.NonDisponibile ? "" : "\n" + loc.Scegli("Commenti", "Comments", "Commentaires") + ": " + s.Commenti) +
                  "\n" + loc.Scegli("Clic per il dettaglio", "Click for details", "Clic pour le détail");
        return new RequisitoBadge { Id = id, Testo = id + " · " + testo, Tooltip = tip, Pennello = RequisitoPolarionUi.Pennello(s.Classe, s.Obsoleto) };
    }

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var paths = values.Count > 1 ? values[1] as DataPaths : null;
        var modo = parameter?.ToString();
        if (values.Count > 0 && values[0] is string idSingolo)
        {
            var b = Crea(paths, idSingolo);
            return modo switch
            {
                "visibile" => b is not null,
                "pennello" => b?.Pennello ?? Brushes.Transparent,
                "tooltip" => b?.Tooltip ?? "",
                _ => b?.Testo ?? ""
            };
        }
        if (values.Count > 0 && values[0] is Requisito req)
        {
            // Cella della tabella Requisiti: il collegamento puo essere appena stato modificato e non ancora salvato.
            var link = RequisitoPolarionRegole.Di(req);
            var inModifica = values.Count > 3 && values[3] is true;
            var loc = LocalizationService.Instance;
            var b = link is null ? null : Crea(paths, req.Id);
            var coerente = b is not null && RequisitoPolarionCache.TryGet(paths, req.Id)?.Link is { } l && ConfrontoSenzaMaiuscole.Instance.Equals(l, link.Value);
            return modo switch
            {
                "pennello" => coerente ? b!.Pennello : Brushes.Gray,
                "tooltip" => coerente ? b!.Tooltip : link is null
                    ? (inModifica ? loc.Scegli("Clic per collegare il requisito a un work item Ticketing (progetto e ID)", "Click to link the requirement to a Ticketing work item (project and ID)", "Cliquez pour lier l'exigence à un work item Ticketing (projet et ID)")
                                  : loc.Scegli("Nessun collegamento Ticketing", "No Ticketing link", "Aucun lien Ticketing"))
                    : link.Value + " · " + loc.Scegli("non ancora sincronizzato o non salvato", "not yet synced or not saved", "pas encore synchronisé ou enregistré"),
                _ => link is null ? (inModifica ? loc.Scegli("Collega…", "Link…", "Lier…") : loc.Scegli("Non collegato", "Not linked", "Non lié")) : coerente ? b!.Testo.Substring(req.Id.Length + 3) : link.Value.ToString()
            };
        }
        if (values.Count > 0 && values[0] is TestCase tc)
        {
            var elenco = new List<RequisitoBadge>();
            foreach (var id in tc.RequisitoId)
            {
                if (Crea(paths, id) is { } b) elenco.Add(b);
                if (elenco.Count == MaxBadgePerRiga) break;
            }
            return elenco;
        }
        return modo == "visibile" ? false : modo == "pennello" ? Brushes.Transparent : (object?)(modo == null ? new List<RequisitoBadge>() : "");
    }
}
