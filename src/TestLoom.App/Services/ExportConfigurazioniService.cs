using System.Text.Json;

namespace TestLoom.App.Services;

/// <summary>Una configurazione di export scelta dall'operatore per una pagina: quali sezioni includere (vista, dati, riepiloghi, grafici,
/// pivot, info...) e, per ciascuna, quali colonne e in che ordine. Nessuna lista di colonne per una sezione vuol dire "tutte".
/// I filtri non fanno parte della configurazione: l'export parte sempre da quelli a video.</summary>
public sealed class ExportConfigurazione
{
    public string Nome { get; set; } = "";
    public List<string> Sezioni { get; set; } = new();
    public Dictionary<string, List<string>> Colonne { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? PivotFonte { get; set; }
    public string? PivotRighe { get; set; }
    public string? PivotColonne { get; set; }
    public string? PivotPercentuale { get; set; }
    public string? PivotMetrica { get; set; }
    public string? PivotCampoValore { get; set; }
    /// <summary>Pivot standard del Report: mostra i numeri e/o le percentuali (almeno uno dei due; il default e' entrambi).</summary>
    public bool PivotMostraNumeri { get; set; } = true;
    public bool PivotMostraPercentuali { get; set; } = true;

    public bool HaSezione(string id) => Sezioni.Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Colonne scelte di una sezione, nell'ordine della configurazione, oppure null per "tutte".</summary>
    public IReadOnlyList<string>? ColonneDi(string sezione) =>
        Colonne.TryGetValue(sezione, out var scelte) && scelte.Count > 0 ? scelte : null;

    public ExportConfigurazione Clona() => new()
    {
        Nome = Nome, Sezioni = Sezioni.ToList(),
        PivotFonte = PivotFonte, PivotRighe = PivotRighe, PivotColonne = PivotColonne, PivotPercentuale = PivotPercentuale,
        PivotMetrica = PivotMetrica, PivotCampoValore = PivotCampoValore,
        PivotMostraNumeri = PivotMostraNumeri, PivotMostraPercentuali = PivotMostraPercentuali,
        Colonne = new Dictionary<string, List<string>>(Colonne.ToDictionary(c => c.Key, c => c.Value.ToList()), StringComparer.OrdinalIgnoreCase),
    };
}

public sealed class ExportConfigurazioniPagina
{
    public List<ExportConfigurazione> Configurazioni { get; set; } = new();
    /// <summary>Nome dell'ultima configurazione usata: e' quella proposta all'apertura del dialogo.</summary>
    public string? Ultima { get; set; }

    public ExportConfigurazione? UltimaUsata =>
        Configurazioni.FirstOrDefault(c => string.Equals(c.Nome, Ultima, StringComparison.OrdinalIgnoreCase));
}

public enum EsitoSalvataggioExport { Salvata, NomeNonValido, LimiteRaggiunto }

/// <summary>Salvataggio delle configurazioni di export per operatore e per pagina, nello stato di vista (come filtri e colonne): nessun
/// file condiviso in piu'. Il contenuto danneggiato o non leggibile equivale a "nessuna configurazione", mai a un errore.</summary>
public static class ExportConfigurazioniService
{
    public const int Massimo = 6;
    public const int LunghezzaMassimaNome = 40;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private static string Chiave(string pagina) => "Export.Configurazioni." + pagina;

    public static ExportConfigurazioniPagina Carica(AppState state, string pagina)
    {
        var testo = ViewStateService.Leggi(state, Chiave(pagina));
        if (string.IsNullOrWhiteSpace(testo)) return new ExportConfigurazioniPagina();
        try
        {
            var letta = JsonSerializer.Deserialize<ExportConfigurazioniPagina>(testo, Json) ?? new ExportConfigurazioniPagina();
            letta.Configurazioni = letta.Configurazioni
                .Where(c => !string.IsNullOrWhiteSpace(c.Nome)).Take(Massimo).ToList();
            foreach (var c in letta.Configurazioni)
                c.Colonne = new Dictionary<string, List<string>>(c.Colonne ?? new(), StringComparer.OrdinalIgnoreCase);
            return letta;
        }
        catch (JsonException) { return new ExportConfigurazioniPagina(); }
    }

    private static void Scrivi(AppState state, string pagina, ExportConfigurazioniPagina contenuto) =>
        ViewStateService.Scrivi(state, Chiave(pagina), contenuto.Configurazioni.Count == 0 && contenuto.Ultima == null
            ? null : JsonSerializer.Serialize(contenuto, Json));

    /// <summary>Salva o sostituisce (stesso nome, senza distinguere maiuscole) una configurazione. Una nuova oltre le sei viene rifiutata.</summary>
    public static EsitoSalvataggioExport Salva(AppState state, string pagina, ExportConfigurazione configurazione)
    {
        var nome = (configurazione.Nome ?? "").Trim();
        if (nome.Length == 0 || nome.Length > LunghezzaMassimaNome) return EsitoSalvataggioExport.NomeNonValido;
        var contenuto = Carica(state, pagina);
        var copia = configurazione.Clona();
        copia.Nome = nome;
        var indice = contenuto.Configurazioni.FindIndex(c => string.Equals(c.Nome, nome, StringComparison.OrdinalIgnoreCase));
        if (indice >= 0) contenuto.Configurazioni[indice] = copia;
        else if (contenuto.Configurazioni.Count >= Massimo) return EsitoSalvataggioExport.LimiteRaggiunto;
        else contenuto.Configurazioni.Add(copia);
        contenuto.Ultima = nome;
        Scrivi(state, pagina, contenuto);
        return EsitoSalvataggioExport.Salvata;
    }

    public static bool Elimina(AppState state, string pagina, string nome)
    {
        var contenuto = Carica(state, pagina);
        var rimosse = contenuto.Configurazioni.RemoveAll(c => string.Equals(c.Nome, nome, StringComparison.OrdinalIgnoreCase));
        if (rimosse == 0) return false;
        if (string.Equals(contenuto.Ultima, nome, StringComparison.OrdinalIgnoreCase)) contenuto.Ultima = null;
        Scrivi(state, pagina, contenuto);
        return true;
    }

    private static string ChiaveUltimaSelezione(string pagina) => "Export.Ultima." + pagina;

    /// <summary>Ultima selezione fatta nel dialogo, salvata o no con un nome: all'apertura successiva si riparte da li'.</summary>
    public static ExportConfigurazione? CaricaUltimaSelezione(AppState state, string pagina)
    {
        var testo = ViewStateService.Leggi(state, ChiaveUltimaSelezione(pagina));
        if (string.IsNullOrWhiteSpace(testo)) return null;
        try
        {
            var c = JsonSerializer.Deserialize<ExportConfigurazione>(testo, Json);
            if (c == null) return null;
            c.Colonne = new Dictionary<string, List<string>>(c.Colonne ?? new(), StringComparer.OrdinalIgnoreCase);
            return c;
        }
        catch (JsonException) { return null; }
    }

    public static void SalvaUltimaSelezione(AppState state, string pagina, ExportConfigurazione selezione) =>
        ViewStateService.Scrivi(state, ChiaveUltimaSelezione(pagina), JsonSerializer.Serialize(selezione, Json));

    public static void ImpostaUltima(AppState state, string pagina, string? nome)
    {
        var contenuto = Carica(state, pagina);
        contenuto.Ultima = contenuto.Configurazioni.Any(c => string.Equals(c.Nome, nome, StringComparison.OrdinalIgnoreCase)) ? nome : null;
        Scrivi(state, pagina, contenuto);
    }
}
