using System.Text.Json;

namespace TestLoom.App.Services;

/// <summary>
/// Ricorda l'ultima selezione automatica di una pagina (feedback utente 2026-09-18: "chiudendo e riaprendo
/// l'app anche in audit mi torna al selettore veicolo iniziale e non a quello precedentemente selezionato,
/// tutte le viste dovrebbero rimanere esattamente come quando l'app è stata chiusa"). Distinto da
/// AppConfig.VisteSalvate (un salvataggio ESPLICITO con nome scelto dall'utente): qui l'utente non salva
/// nulla di proposito, quindi qualunque lettura/scrittura deve restare a costo trascurabile e MAI bloccare
/// l'interfaccia — usato solo per selezioni discrete e poco frequenti (una ComboBox, un checkbox), non per
/// digitazione di testo carattere per carattere, dove salvare ad ogni tasto sarebbe puro spreco di I/O.
///
/// Locale al PC come il resto di AppConfig: due operatori sulla stessa cartella condivisa hanno ciascuno la
/// propria "ultima vista", non condivisa con l'altro.
/// </summary>
public static class ViewStateService
{
    /// <summary>Serializza TUTTE le letture/scritture della mappa UltimaSelezionePagine e il relativo Save: il
    /// Dictionary non e' thread-safe e WU Status persiste il testo di ricerca da un thread di lavoro (debounce).</summary>
    // Condiviso con le altre mutazioni di AppConfig: WuStatus può salvare un filtro da Task.Run mentre
    // la UI persiste un layout tabella. Un solo lock evita snapshot concorrenti e perdita di preferenze.
    private static readonly object Gate = AppConfigService.ConfigSync;

    public static string? Leggi(AppState state, string chiave)
    {
        lock (Gate) return state.Config.UltimaSelezionePagine.TryGetValue(chiave, out var valore) ? valore : null;
    }

    /// <summary>Nessuna scrittura se il valore non è cambiato: evita un WriteAtomic ridondante quando
    /// Aggiorna() ri-assegna la stessa selezione già persistita (es. la pagina resta viva tra un cambio
    /// scheda e l'altro e OnPropertyChanged scatta comunque).</summary>
    public static void Scrivi(AppState state, string chiave, string? valore)
    {
        lock (Gate)
        {
            var mappa = state.Config.UltimaSelezionePagine;
            if (mappa.TryGetValue(chiave, out var attuale) && attuale == valore) return;
            if (valore == null) mappa.Remove(chiave);
            else mappa[chiave] = valore;
            state.ConfigService.Save(state.Config);
        }
    }

    /// <summary>Variante per selezioni multiple (FiltroMultiSelect): stessa chiave/valore libero di
    /// Leggi/Scrivi, solo serializzata come lista JSON invece di una singola stringa — evita di inventare un
    /// secondo Dictionary in AppConfig solo per i filtri a scelta multipla.</summary>
    public static IReadOnlyList<string> LeggiLista(AppState state, string chiave)
    {
        var testo = Leggi(state, chiave);
        if (string.IsNullOrEmpty(testo)) return Array.Empty<string>();
        try { return (IReadOnlyList<string>?)JsonSerializer.Deserialize<List<string>>(testo) ?? Array.Empty<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    public static void ScriviLista(AppState state, string chiave, IReadOnlyList<string> valori)
    {
        // Confronto sul contenuto (non sul riferimento) prima di serializzare: Ripopola richiama questo ad
        // ogni rivisita pagina anche quando la selezione persistita coincide già, ed evitare di riscrivere
        // config.json in quel caso comune è lo stesso principio già applicato a Scrivi().
        var nuovo = valori.Count == 0 ? null : JsonSerializer.Serialize(valori);
        lock (Gate)
        {
            var mappa = state.Config.UltimaSelezionePagine;
            if (mappa.TryGetValue(chiave, out var attuale) && attuale == nuovo) return;
            if (nuovo == null) mappa.Remove(chiave);
            else mappa[chiave] = nuovo;
            state.ConfigService.Save(state.Config);
        }
    }

    /// <summary>Variante per un intero set di filtri scalari (liste Test/CR/Anomalie, che già catturano la
    /// propria selezione in un Dictionary&lt;string,string?&gt; per le Viste Salvate — vedi
    /// TestCaseListViewModel.CatturaFiltriCorrenti/ApplicaVista): una sola chiave invece di una per campo, e
    /// riusa la stessa logica di parsing già scritta per applicare una vista salvata.</summary>
    public static IReadOnlyDictionary<string, string?> LeggiDizionario(AppState state, string chiave)
    {
        var testo = Leggi(state, chiave);
        if (string.IsNullOrEmpty(testo)) return new Dictionary<string, string?>();
        try { return (IReadOnlyDictionary<string, string?>?)JsonSerializer.Deserialize<Dictionary<string, string?>>(testo) ?? new Dictionary<string, string?>(); }
        catch (JsonException) { return new Dictionary<string, string?>(); }
    }

    public static void ScriviDizionario(AppState state, string chiave, IReadOnlyDictionary<string, string?> valori)
    {
        var senzaNulli = valori.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);
        var nuovo = senzaNulli.Count == 0 ? null : JsonSerializer.Serialize(senzaNulli);
        lock (Gate)
        {
            var mappa = state.Config.UltimaSelezionePagine;
            if (mappa.TryGetValue(chiave, out var attuale) && attuale == nuovo) return;
            if (nuovo == null) mappa.Remove(chiave);
            else mappa[chiave] = nuovo;
            state.ConfigService.Save(state.Config);
        }
    }

    /// <summary>Variante per DateTimeOffset (intervalli data di Home/Report): formato round-trip "O", così un
    /// fuso orario diverso da quello con cui è stato salvato non altera l'istante persistito.</summary>
    /// <summary>Stato dell'app per le preferenze di vista lette dal code-behind, che non ha un ViewModel che le porti
    /// (6.6.1: pannello filtri della pagina Anomalie). Impostato da MainViewModel insieme a TableLayoutPreferences.Configure.</summary>
    private static AppState? _statoLocale;

    public static void Configura(AppState state) => _statoLocale = state;

    /// <summary>Preferenza di vista dell'operatore attivo (chiave + "|" + operatore), locale al PC; null se non configurato.</summary>
    public static string? LeggiPerOperatore(string chiave) =>
        _statoLocale is { } stato ? Leggi(stato, $"{chiave}|{stato.Config.OperatoreAttivo}") : null;

    public static void ScriviPerOperatore(string chiave, string? valore)
    {
        if (_statoLocale is { } stato) Scrivi(stato, $"{chiave}|{stato.Config.OperatoreAttivo}", valore);
    }

    public static DateTimeOffset? LeggiData(AppState state, string chiave) =>
        DateTimeOffset.TryParse(Leggi(state, chiave), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var valore) ? valore : null;

    public static void ScriviData(AppState state, string chiave, DateTimeOffset? valore) =>
        Scrivi(state, chiave, valore?.ToString("O"));
}
