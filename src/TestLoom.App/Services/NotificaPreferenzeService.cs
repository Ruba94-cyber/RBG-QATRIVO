using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Preferenze locali (per PC/operatore) su quali tipi di notifica contano nel badge del centro notifiche e nell'avviso
/// Windows. Logica pura, senza UI. Le notifiche silenziate restano nel repository e nel centro: questa scelta non
/// elimina mai nulla.
/// </summary>
public static class NotificaPreferenzeService
{
    /// <summary>Conflitto fra postazioni o OneDrive sui dati condivisi: sempre conteggiato, mai silenziabile.</summary>
    public const string TipoConflittoSincronizzazione = "ConflittoSincronizzazione";

    /// <summary>Pacchetto divergente fra veicoli e non ancora decisa dal Master (6.6.0). Silenziabile come gli altri
    /// tipi: resta nel centro notifiche anche quando non conta nel badge.</summary>
    public const string TipoWuDaDecidere = "WuDaDecidere";

    /// <summary>Tipi di notifica effettivamente generati dal codice, con l'etichetta mostrata in Impostazioni.</summary>
    public static IReadOnlyList<(string Tipo, string Etichetta)> TipiNoti => new (string Tipo, string Etichetta)[]
    {
        (TipoConflittoSincronizzazione, LocalizationService.Instance.Traduci("NotificaTipoConflittoSync")),
        ("Conflitto", LocalizationService.Instance.Traduci("NotificaTipoConflitto")),
        ("ChangeRequest", LocalizationService.Instance.Traduci("NotificaTipoChangeRequest")),
        ("Assegnazione", LocalizationService.Instance.Traduci("NotificaTipoAssegnazione")),
        ("Classificazione", LocalizationService.Instance.Traduci("NotificaTipoClassificazione")),
        (TipoWuDaDecidere, LocalizationService.Instance.Traduci("NotificaTipoWuDaDecidere")),
        ("Informazione", LocalizationService.Instance.Traduci("NotificaTipoInformazione")),
    };

    /// <summary>False solo per il conflitto di sincronizzazione dati. Il confronto è case-insensitive: nel dubbio un
    /// conflitto non viene mai silenziato.</summary>
    public static bool ESilenziabile(string? tipo) =>
        !string.Equals(tipo, TipoConflittoSincronizzazione, StringComparison.OrdinalIgnoreCase);

    /// <summary>True se il tipo è escluso dal conteggio. Un tipo non silenziabile non lo è mai, anche se la lista nel
    /// config.json lo contenesse a mano.</summary>
    public static bool EStaSilenziato(string? tipo, AppConfig cfg) =>
        tipo is not null && ESilenziabile(tipo) &&
        (cfg.NotificheTipiSilenziati?.Any(t => string.Equals(t, tipo, StringComparison.OrdinalIgnoreCase)) ?? false);

    /// <summary>Criterio unico per badge e avviso Windows: notifica non letta e di un tipo non silenziato.</summary>
    public static bool ConteggiaNelBadge(NotificaApp notifica, AppConfig cfg) =>
        !notifica.Letta && !EStaSilenziato(notifica.Tipo, cfg);

    /// <summary>Notifiche non lette che contribuiscono al badge. La sorgente non viene filtrata.</summary>
    public static int NonLetteRilevanti(IEnumerable<NotificaApp> notifiche, AppConfig cfg) =>
        notifiche.Count(n => ConteggiaNelBadge(n, cfg));

    /// <summary>Imposta se il tipo compare nel contatore. Modifica solo la configurazione in memoria: il salvataggio
    /// resta quello di "Salva preferenze". Per un tipo non silenziabile la richiesta viene ignorata.</summary>
    public static void ImpostaMostraNelContatore(AppConfig cfg, string tipo, bool mostra)
    {
        cfg.NotificheTipiSilenziati ??= new List<string>();
        cfg.NotificheTipiSilenziati.RemoveAll(t => string.Equals(t, tipo, StringComparison.OrdinalIgnoreCase));
        if (!mostra && ESilenziabile(tipo)) cfg.NotificheTipiSilenziati.Add(tipo);
    }
}
