using ClosedXML.Excel;

namespace TestLoom.App.Services;

/// <summary>Crea una pivot Excel e una sorgente autonoma, indipendente dalle colonne scelte per i fogli di visualizzazione.</summary>
public static class ExportPivotService
{
    public static IReadOnlyList<ExportColonna> Campi(params (string Id, string It, string En, string Fr)[] elementi) =>
        elementi.Select(e => new ExportColonna(e.Id, LocalizationService.Instance.Scegli(e.It, e.En, e.Fr))).ToList();

    /// <summary>Giorni interi tra inizio e fine (o oggi se ancora aperta): null se manca l'inizio o la fine precede l'inizio.</summary>
    public static int? GiorniDiVita(DateTime? inizio, DateTime? fine, DateTime oggi)
    {
        if (inizio is not { } i || i == default) return null;
        var f = fine is { } x && x != default ? x : oggi;
        return f < i ? null : (int)Math.Floor((f.Date - i.Date).TotalDays);
    }

    public static string EtichettaGiorniDiVita => LocalizationService.Instance.Scegli("Giorni di vita", "Days open", "Jours d'ouverture");

    /// <summary>Marca come numerici (somma/media consentite) i campi indicati; gli altri restano invariati.</summary>
    public static IReadOnlyList<ExportColonna> ConNumerici(IReadOnlyList<ExportColonna> campi, params string[] idNumerici) =>
        campi.Select(c => idNumerici.Contains(c.Id, StringComparer.OrdinalIgnoreCase) ? c with { Numerica = true } : c).ToList();

    /// <summary>Scorciatoia per i chiamanti che hanno la configurazione scelta nel dialogo: righe, colonne, percentuale, misura e campo valore.</summary>
    public static void Scrivi(XLWorkbook wb, IReadOnlyList<ExportColonna> campi, IEnumerable<IReadOnlyList<object?>> righe,
        ExportConfigurazione scelta, CancellationToken ct = default, string? granularita = null)
    {
        ArgumentNullException.ThrowIfNull(scelta);
        Scrivi(wb, campi, righe, scelta.PivotRighe, scelta.PivotColonne, scelta.PivotPercentuale, ct, granularita,
            scelta.PivotMetrica, scelta.PivotCampoValore);
    }

    /// <summary>Converte un valore sorgente in numero per somma/media: vuoto o null = nessun valore (escluso dalla media), testo o altro tipo non
    /// numerico = errore esplicito (mai un parse silenzioso a 0).</summary>
    internal static double? ANumero(object? valore, string campo, int riga)
    {
        switch (valore)
        {
            case null: return null;
            case string testo when string.IsNullOrWhiteSpace(testo): return null;
            case double d: return double.IsNaN(d) || double.IsInfinity(d) ? throw ErroreNumerico(valore, campo, riga) : d;
            case float f: return double.IsNaN(f) || double.IsInfinity(f) ? throw ErroreNumerico(valore, campo, riga) : f;
            case decimal m: return (double)m;
            case int or long or short or byte or sbyte or uint or ulong or ushort: return Convert.ToDouble(valore, System.Globalization.CultureInfo.InvariantCulture);
            default: throw ErroreNumerico(valore, campo, riga);
        }
    }

    private static InvalidOperationException ErroreNumerico(object? valore, string campo, int riga) =>
        new(LocalizationService.Instance.Scegli(
            $"Il campo \"{campo}\" contiene il valore non numerico \"{valore}\" (riga {riga}): somma e media richiedono solo numeri.",
            $"Field \"{campo}\" holds the non-numeric value \"{valore}\" (row {riga}): sum and average require numbers only.",
            $"Le champ « {campo} » contient la valeur non numérique « {valore} » (ligne {riga}) : somme et moyenne exigent des nombres."));

    public static void Scrivi(XLWorkbook wb, IReadOnlyList<ExportColonna> campi, IEnumerable<IReadOnlyList<object?>> righe,
        string? campoRighe, string? campoColonne, string? percentuale, CancellationToken ct = default, string? granularita = null,
        string? metrica = null, string? campoValore = null)
    {
        ArgumentNullException.ThrowIfNull(wb);
        if (campi.Count < 2) throw new ArgumentException(LocalizationService.Instance.Scegli("La sorgente pivot richiede almeno due campi.", "The pivot source requires at least two fields.", "La source du tableau croisé requiert au moins deux champs."), nameof(campi));
        var riga = Indice(campi, campoRighe, 0);
        var colonna = Indice(campi, campoColonne, riga == 0 ? 1 : 0);
        if (riga == colonna) throw new ArgumentException(LocalizationService.Instance.Scegli("I campi di riga e colonna devono essere diversi.", "Row and column fields must be different.", "Les champs de ligne et de colonne doivent être différents."));
        var loc = LocalizationService.Instance;
        var misura = metrica?.Trim().ToLowerInvariant() switch
        {
            null or "" or "conteggio" => "conteggio",
            "somma" => "somma",
            "media" => "media",
            _ => throw new ArgumentException(loc.Scegli($"Misura pivot sconosciuta: {metrica}.", $"Unknown pivot measure: {metrica}.", $"Mesure de tableau croisé inconnue : {metrica}."), nameof(metrica)),
        };
        var indiceValore = -1;
        if (misura != "conteggio")
        {
            indiceValore = -1;
            for (var i = 0; i < campi.Count; i++)
                if (string.Equals(campi[i].Id, campoValore, StringComparison.OrdinalIgnoreCase)) { indiceValore = i; break; }
            if (indiceValore < 0) throw new ArgumentException(loc.Scegli("Somma e media richiedono un campo valore presente nella sorgente.", "Sum and average require a value field present in the source.", "La somme et la moyenne exigent un champ valeur présent dans la source."), nameof(campoValore));
            if (!campi[indiceValore].Numerica) throw new ArgumentException(loc.Scegli($"Il campo \"{campi[indiceValore].Etichetta}\" non è numerico: somma e media non sono applicabili.", $"Field \"{campi[indiceValore].Etichetta}\" is not numeric: sum and average do not apply.", $"Le champ « {campi[indiceValore].Etichetta} » n'est pas numérique : somme et moyenne non applicables."), nameof(campoValore));
        }
        var conteggio = loc.Scegli("Numero righe", "Row count", "Nombre de lignes");
        // Le intestazioni della cache devono essere univoche e non vuote.
        var nomi = new List<string>();
        foreach (var campo in campi)
        {
            var baseNome = string.IsNullOrWhiteSpace(campo.Etichetta) ? campo.Id : campo.Etichetta;
            var nome = baseNome;
            for (var n = 2; nomi.Contains(nome, StringComparer.OrdinalIgnoreCase); n++) nome = $"{baseNome} ({n})";
            nomi.Add(nome);
        }
        while (nomi.Contains(conteggio, StringComparer.OrdinalIgnoreCase)) conteggio += "_";
        var writer = new ExcelExportTableWriter(wb, NomeLibero(wb, loc.Scegli("Origine pivot", "Pivot source", "Source pivot")), [.. nomi, conteggio]);
        var numero = 0;
        foreach (var valori in righe)
        {
            ct.ThrowIfCancellationRequested();
            if (valori.Count != campi.Count) throw new ArgumentException(loc.Scegli("La riga pivot non corrisponde ai campi della sorgente.", "The pivot row does not match the source fields.", "La ligne du tableau croisé ne correspond pas aux champs de la source."), nameof(righe));
            if (++numero >= ExcelExportTableWriter.LimiteRigheFoglio) throw new InvalidOperationException(loc.Scegli("La sorgente pivot supera il limite di righe di Excel.", "The pivot source exceeds Excel's row limit.", "La source du tableau croisé dépasse la limite de lignes d'Excel."));
            object?[] cella = [.. valori, 1];
            if (indiceValore >= 0) cella[indiceValore] = ANumero(valori[indiceValore], campi[indiceValore].Etichetta, numero + 1);
            writer.AggiungiRiga(numero.ToString(), cella);
        }
        writer.Completa();
        var foglio = wb.Worksheets.Add(NomeLibero(wb, "Pivot"));
        foglio.Cell(1, 1).Value = loc.Scegli("Dati filtrati all'esportazione. In Excel è possibile cambiare i campi e aggiornare la pivot.",
            "Data filtered at export. Change fields and refresh the pivot in Excel.", "Données filtrées à l'export. Modifiez les champs et actualisez le tableau dans Excel.");
        if (!string.IsNullOrWhiteSpace(granularita)) foglio.Cell(2, 1).Value = granularita;
        if (numero == 0)
        {
            foglio.Cell(3, 1).Value = loc.Scegli("Nessun dato per i filtri selezionati.", "No data for the selected filters.", "Aucune donnée pour les filtres sélectionnés.");
            return;
        }
        var origine = wb.Worksheet(writer.NomeFoglioPrincipale).Range(1, 1, numero + 1, campi.Count + 1);
        var pivot = foglio.PivotTables.Add("PivotExport" + wb.Worksheets.Count, foglio.Cell(4, 1), origine);
        pivot.PivotCache.RefreshDataOnOpen = true;
        pivot.RowLabels.Add(nomi[riga]);
        pivot.ColumnLabels.Add(nomi[colonna]);
        var campoEtichetta = indiceValore >= 0 ? nomi[indiceValore] : "";
        var sorgenteValore = indiceValore >= 0 ? campoEtichetta : conteggio;
        var etichettaMisura = misura switch
        {
            "somma" => loc.Scegli($"Somma di {campoEtichetta}", $"Sum of {campoEtichetta}", $"Somme de {campoEtichetta}"),
            "media" => loc.Scegli($"Media di {campoEtichetta}", $"Average of {campoEtichetta}", $"Moyenne de {campoEtichetta}"),
            _ => loc.Scegli("Numero", "Count", "Nombre"),
        };
        var pivotValore = pivot.Values.Add(sorgenteValore, etichettaMisura)
            .SetSummaryFormula(misura == "media" ? XLPivotSummary.Average : XLPivotSummary.Sum);
        var testoNota = loc.Scegli(
            misura == "conteggio" ? "Misura: numero di righe." : $"Misura: {(misura == "somma" ? "somma" : "media")} di \"{campoEtichetta}\"; i valori vuoti sono esclusi.",
            misura == "conteggio" ? "Measure: row count." : $"Measure: {(misura == "somma" ? "sum" : "average")} of \"{campoEtichetta}\"; empty values are excluded.",
            misura == "conteggio" ? "Mesure : nombre de lignes." : $"Mesure : {(misura == "somma" ? "somme" : "moyenne")} de « {campoEtichetta} » ; les valeurs vides sont exclues.");
        if (percentuale is "riga" or "colonna" or "totale")
        {
            if (misura == "media")
                testoNota += " " + loc.Scegli("La percentuale non si applica alla media ed è omessa.", "Percentages do not apply to an average and are omitted.", "Le pourcentage ne s'applique pas à une moyenne et est omis.");
            else
            {
                var denominatore = percentuale switch
                {
                    "riga" => loc.Scegli("sul totale della riga", "of the row total", "du total de la ligne"),
                    "colonna" => loc.Scegli("sul totale della colonna", "of the column total", "du total de la colonne"),
                    _ => loc.Scegli("sul totale generale", "of the grand total", "du total général"),
                };
                var valore = pivot.Values.Add(sorgenteValore, "% " + denominatore).SetSummaryFormula(XLPivotSummary.Sum);
                switch (percentuale)
                {
                    case "riga": valore.ShowAsPercentageOfRow(); break;
                    case "colonna": valore.ShowAsPercentageOfColumn(); break;
                    case "totale": valore.ShowAsPercentageOfTotal(); break;
                }
                valore.NumberFormat.SetFormat("0.0%");
                testoNota += " " + loc.Scegli($"Percentuale = valore {denominatore}.", $"Percentage = value {denominatore}.", $"Pourcentage = valeur {denominatore}.");
            }
        }
        foglio.Cell(3, 1).Value = testoNota;
    }

    private static int Indice(IReadOnlyList<ExportColonna> campi, string? id, int predefinito)
    {
        for (var i = 0; i < campi.Count; i++)
            if (string.Equals(campi[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return predefinito;
    }

    private static string NomeLibero(XLWorkbook wb, string nome)
    {
        var baseNome = nome.Length > 31 ? nome[..31] : nome;
        var risultato = baseNome;
        for (var n = 2; wb.Worksheets.Any(w => string.Equals(w.Name, risultato, StringComparison.OrdinalIgnoreCase)); n++)
        {
            var suffisso = $" ({n})";
            risultato = baseNome[..Math.Min(baseNome.Length, 31 - suffisso.Length)] + suffisso;
        }
        return risultato;
    }
}
