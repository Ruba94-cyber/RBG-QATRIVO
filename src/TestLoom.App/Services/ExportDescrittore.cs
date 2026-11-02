namespace TestLoom.App.Services;

/// <summary>Colonna di una tabella esportabile: l'Id e' la chiave stabile salvata nelle configurazioni, l'Etichetta e' il testo mostrato.</summary>
public sealed record ExportColonna(string Id, string Etichetta, bool Numerica = false);

public sealed record ExportFontePivot(string Id, string Etichetta, IReadOnlyList<ExportColonna> Campi);

/// <summary>Sezione di un export (un foglio o un gruppo di fogli): con Colonne l'operatore sceglie anche quali colonne includere.</summary>
public sealed record ExportSezione(string Id, string Etichetta, IReadOnlyList<ExportColonna>? Colonne = null, bool PredefinitaAttiva = true);

/// <summary>Cosa una pagina puo' esportare e con quali filtri: e' tutto cio' che il dialogo generico deve sapere. Il riepilogo dei filtri e'
/// quello a video al momento dell'apertura, e vale sempre per l'export.</summary>
public sealed record ExportDescrittore(string Pagina, string Titolo, IReadOnlyList<ExportSezione> Sezioni, string RiepilogoFiltri,
    IReadOnlyList<ExportFiltro>? Filtri = null, IReadOnlyList<ExportFontePivot>? FontiPivot = null);

public static class ExportSelezione
{
    /// <summary>Selezione iniziale: le sezioni attive per default e tutte le colonne.</summary>
    public static ExportConfigurazione Predefinita(ExportDescrittore d) => new()
    {
        Sezioni = d.Sezioni.Where(s => s.PredefinitaAttiva).Select(s => s.Id).ToList(),
    };

    /// <summary>Riallinea una configurazione salvata al descrittore corrente: una sezione o una colonna che non esiste piu' (pagina
    /// aggiornata, campo rimosso) viene scartata, e una sezione senza piu' colonne valide torna a "tutte".</summary>
    public static ExportConfigurazione Normalizza(ExportConfigurazione c, ExportDescrittore d)
    {
        var risultato = new ExportConfigurazione { Nome = c.Nome, PivotFonte = c.PivotFonte, PivotRighe = c.PivotRighe,
            PivotColonne = c.PivotColonne, PivotPercentuale = c.PivotPercentuale,
            PivotMetrica = c.PivotMetrica, PivotCampoValore = c.PivotCampoValore,
            PivotMostraNumeri = c.PivotMostraNumeri, PivotMostraPercentuali = c.PivotMostraPercentuali };
        foreach (var s in d.Sezioni)
        {
            if (!c.HaSezione(s.Id)) continue;
            risultato.Sezioni.Add(s.Id);
            if (s.Colonne == null || c.ColonneDi(s.Id) is not { } scelte) continue;
            var valide = scelte.Where(id => s.Colonne.Any(k => string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase))).ToList();
            if (valide.Count > 0 && valide.Count < s.Colonne.Count) risultato.Colonne[s.Id] = valide;
        }
        return risultato;
    }

    /// <summary>Colonne da esportare di una sezione, nell'ordine del descrittore (la tabella com'e'), oppure tutte.</summary>
    public static IReadOnlyList<string> ColonneEsportate(ExportConfigurazione c, ExportSezione s)
    {
        if (s.Colonne == null) return Array.Empty<string>();
        var scelte = c.ColonneDi(s.Id);
        return scelte == null
            ? s.Colonne.Select(k => k.Id).ToList()
            : s.Colonne.Where(k => scelte.Contains(k.Id, StringComparer.OrdinalIgnoreCase)).Select(k => k.Id).ToList();
    }
}

/// <summary>Un filtro della pagina modificabile dal dialogo di export. Alla conferma il dialogo scrive i valori scelti sui filtri veri
/// della pagina, cosi' export e tabella a video coincidono sempre.</summary>
public abstract record ExportFiltro(string Id, string Etichetta);

/// <summary>Filtro a selezione multipla (le voci spuntate sono in OR; nessuna voce = tutte).</summary>
public sealed record ExportFiltroMultiplo(string Id, string Etichetta, TestLoom.App.ViewModels.FiltroMultiSelect Filtro) : ExportFiltro(Id, Etichetta);

/// <summary>Filtro a valore singolo (per esempio componente o veicolo): null = tutti.</summary>
/// <param name="EtichettaValore">Etichetta localizzata mostrata al posto del valore grezzo (per esempio il nome di un enum); null = il valore stesso.</param>
public sealed record ExportFiltroSingolo(string Id, string Etichetta, IReadOnlyList<string> Valori, Func<string?> Leggi, Action<string?> Imposta,
    Func<string, string>? EtichettaValore = null)
    : ExportFiltro(Id, Etichetta);

/// <summary>Filtro su una data (da/a): null = nessun limite.</summary>
public sealed record ExportFiltroData(string Id, string Etichetta, Func<DateTime?> Leggi, Action<DateTime?> Imposta) : ExportFiltro(Id, Etichetta);

/// <summary>Identificativi stabili delle sezioni dell'export Report (salvati nelle configurazioni).</summary>
public static class ReportSezioniExport
{
    public const string Vista = "vista", Origine = "origine", Dati = "dati", Riepilogo = "riepilogo", Grafici = "grafici", Nc = "nc",
        Info = "info", Pivot = "pivot";
}

/// <summary>Filtro di testo libero (ricerca): stringa vuota o null = nessun filtro.</summary>
public sealed record ExportFiltroTesto(string Id, string Etichetta, Func<string?> Leggi, Action<string?> Imposta) : ExportFiltro(Id, Etichetta);

/// <summary>Filtro acceso/spento (per esempio "solo problemi").</summary>
public sealed record ExportFiltroBooleano(string Id, string Etichetta, Func<bool> Leggi, Action<bool> Imposta) : ExportFiltro(Id, Etichetta);

/// <summary>Identificativi stabili delle sezioni dell'export Registro guasto.</summary>
public static class DtcSezioniExport
{
    public const string Registro = "registro";
}
