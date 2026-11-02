namespace TestLoom.App.Services;

/// <summary>Rappresentazione grafica selezionabile per un grafico della Home (piano 6.5.0 punto 5).</summary>
public enum TipoGrafico
{
    BarreVerticali,
    BarreOrizzontali,
    Torta,
    Ciambella,
    Treemap,
    Linea,
    Area,
}

/// <summary>Catalogo dei tipi di rappresentazione compatibili per ciascun grafico della Home. La
/// compatibilita' dipende dalla natura del dato: categorico (copertura requisiti) -> torta/barre/treemap;
/// serie temporale (andamento anomalie) -> linea/area; esiti per veicolo -> tutti i tipi (Linea/Area come una
/// serie per esito). Mai torte/treemap su una serie temporale.</summary>
public static class HomeChartCatalog
{
    public const string IdCopertura = "copertura";
    public const string IdAndamento = "andamento";
    public const string IdDonut = "donut";

    /// <summary>Soglie oltre le quali le categorie in coda vengono raggruppate in "Altri": il costo di
    /// rendering e la leggibilita' restano limitati anche con 1000+ sottoveicoli.</summary>
    public const int MaxVociBarreTorta = 12;
    public const int MaxVociTreemap = 40;
    public const string EtichettaAltri = "Altri";

    private static readonly IReadOnlyList<TipoGrafico> Copertura =
        new[] { TipoGrafico.BarreVerticali, TipoGrafico.BarreOrizzontali, TipoGrafico.Torta, TipoGrafico.Treemap };
    private static readonly IReadOnlyList<TipoGrafico> Andamento =
        new[] { TipoGrafico.Linea, TipoGrafico.Area };
    private static readonly IReadOnlyList<TipoGrafico> Donut =
        new[] { TipoGrafico.Ciambella, TipoGrafico.Torta, TipoGrafico.BarreVerticali, TipoGrafico.BarreOrizzontali, TipoGrafico.Treemap, TipoGrafico.Linea, TipoGrafico.Area };

    /// <summary>Tipi compatibili (il primo e' il predefinito, coincide con la resa storica del grafico).</summary>
    public static IReadOnlyList<TipoGrafico> Compatibili(string idGrafico) => idGrafico switch
    {
        IdCopertura => Copertura,
        IdAndamento => Andamento,
        IdDonut => Donut,
        _ => Array.Empty<TipoGrafico>(),
    };

    public static TipoGrafico Predefinito(string idGrafico) =>
        Compatibili(idGrafico).FirstOrDefault();

    /// <summary>Valore sicuro per un testo persistito: sconosciuto o non compatibile (preferenza di una
    /// versione precedente, config modificato a mano) -> predefinito, senza eccezioni.</summary>
    public static TipoGrafico Normalizza(string idGrafico, string? testoSalvato)
    {
        if (!string.IsNullOrWhiteSpace(testoSalvato) && Enum.TryParse<TipoGrafico>(testoSalvato, out var tipo)
            && Compatibili(idGrafico).Contains(tipo))
            return tipo;
        return Predefinito(idGrafico);
    }

    /// <summary>Mantiene le prime <paramref name="max"/> voci per valore decrescente (a parita' ordine
    /// alfabetico, per risultato deterministico) e somma il resto in una voce "Altri" aggiunta in coda.
    /// Le voci con valore &lt;= 0 sono scartate. Nessun "Altri" se le voci restano entro la soglia.</summary>
    public static List<(string Nome, long Valore)> RaggruppaAltri(IEnumerable<(string Nome, long Valore)> voci, int max)
    {
        var ordinate = voci.Where(v => v.Valore > 0)
            .OrderByDescending(v => v.Valore).ThenBy(v => v.Nome, StringComparer.OrdinalIgnoreCase).ToList();
        if (ordinate.Count <= max) return ordinate;
        var testa = ordinate.Take(max).ToList();
        testa.Add((EtichettaAltri, ordinate.Skip(max).Sum(v => v.Valore)));
        return testa;
    }
}
