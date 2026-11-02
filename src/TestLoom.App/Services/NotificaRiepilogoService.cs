using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Riepilogo notifiche (6.6.0). Con il riepilogo in vigore, le notifiche non lette dei tipi scelti non contano una per una
/// nel badge e nell'avviso Windows: compare una sola voce di riepilogo che le raccoglie e le mostra su richiesta.
/// Il riepilogo è solo una vista calcolata sul ledger: non elimina e non segna come lette le notifiche. Solo l'azione
/// esplicita "Segna riepilogo come letto" le segna. Configurazione in <see cref="AppConfig"/> (per PC); l'istante
/// dell'ultimo riepilogo, per operatore, in <see cref="ViewStateService"/>. Le funzioni sono pure: orologio e dati
/// arrivano dall'esterno, così la pianificazione si verifica in unit test senza un vero avvio dell'app.
/// </summary>
public static class NotificaRiepilogoService
{
    /// <summary>Modo del riepilogo. Il nome salvato in config.json si ottiene con <see cref="NomeModo"/>.</summary>
    public enum ModoRiepilogo { Disattivato, Giornaliero, AllAvvio }

    /// <summary>Tipi che non entrano mai nel riepilogo, anche se config.json li elencasse: restano sempre immediati.</summary>
    public static bool ESempreImmediato(string? tipo) =>
        string.IsNullOrEmpty(tipo) ||
        string.Equals(tipo, NotificaPreferenzeService.TipoConflittoSincronizzazione, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(tipo, NotificaPreferenzeService.TipoWuDaDecidere, StringComparison.OrdinalIgnoreCase);

    /// <summary>Valore salvato in <c>AppConfig.NotificheRiepilogoModo</c>.</summary>
    public static string NomeModo(ModoRiepilogo modo) => modo switch
    {
        ModoRiepilogo.Giornaliero => "Giornaliero",
        ModoRiepilogo.AllAvvio => "AllAvvio",
        _ => "Disattivato",
    };

    /// <summary>Modo configurato. Valore assente, vuoto o sconosciuto (config.json precedenti o modificati a mano) = Disattivato.</summary>
    public static ModoRiepilogo Modo(AppConfig cfg)
    {
        var testo = cfg.NotificheRiepilogoModo?.Trim();
        if (string.Equals(testo, NomeModo(ModoRiepilogo.Giornaliero), StringComparison.OrdinalIgnoreCase)) return ModoRiepilogo.Giornaliero;
        if (string.Equals(testo, NomeModo(ModoRiepilogo.AllAvvio), StringComparison.OrdinalIgnoreCase)) return ModoRiepilogo.AllAvvio;
        return ModoRiepilogo.Disattivato;
    }

    /// <summary>Imposta il modo (scrive solo la configurazione in memoria: il salvataggio è quello di "Salva preferenze").</summary>
    public static void ImpostaModo(AppConfig cfg, ModoRiepilogo modo) => cfg.NotificheRiepilogoModo = NomeModo(modo);

    /// <summary>True se il tipo è stato escluso dal riepilogo in Impostazioni. Lista assente = nessuna esclusione.</summary>
    public static bool EEsclusoNelRiepilogo(string? tipo, AppConfig cfg) =>
        cfg.NotificheRiepilogoTipiEsclusi?.Any(t => string.Equals(t, tipo, StringComparison.OrdinalIgnoreCase)) ?? false;

    /// <summary>Un tipo entra nel riepilogo se non è sempre immediato, non è silenziato nel contatore e non è escluso in Impostazioni.</summary>
    public static bool EnterRiepilogo(string? tipo, AppConfig cfg) =>
        !ESempreImmediato(tipo) && !NotificaPreferenzeService.EStaSilenziato(tipo, cfg) && !EEsclusoNelRiepilogo(tipo, cfg);

    /// <summary>Include o esclude un tipo dal riepilogo (scrive solo la configurazione in memoria). Un tipo sempre immediato non cambia mai.</summary>
    public static void ImpostaIncluso(AppConfig cfg, string tipo, bool incluso)
    {
        cfg.NotificheRiepilogoTipiEsclusi ??= new List<string>();
        cfg.NotificheRiepilogoTipiEsclusi.RemoveAll(t => string.Equals(t, tipo, StringComparison.OrdinalIgnoreCase));
        if (!incluso && !ESempreImmediato(tipo)) cfg.NotificheRiepilogoTipiEsclusi.Add(tipo);
    }

    /// <summary>Chiave in <see cref="ViewStateService"/> dell'istante dell'ultimo riepilogo dell'operatore (le notifiche sono per operatore).</summary>
    public static string ChiaveUltimo(string operatore) => "Notifiche.Riepilogo.Ultimo." + operatore.Trim().ToUpperInvariant();

    /// <summary>
    /// Il riepilogo è in vigore quando il modo non è Disattivato e per l'operatore esiste già un istante di riepilogo.
    /// Prima del primo riepilogo nulla cambia: ogni notifica conta come sempre, così nessuna sparisce dal badge.
    /// </summary>
    public static bool RiepilogoAttivo(AppConfig cfg, DateTimeOffset? ultimo) =>
        Modo(cfg) != ModoRiepilogo.Disattivato && ultimo is not null;

    /// <summary>
    /// Stabilisce se all'avvio va emesso un nuovo riepilogo: in Giornaliero una volta per giorno locale (al primo avvio
    /// del giorno), in AllAvvio ad ogni avvio, mai in Disattivato.
    /// </summary>
    public static bool DeveEmettere(ModoRiepilogo modo, DateTimeOffset? ultimo, DateTimeOffset adesso) => modo switch
    {
        ModoRiepilogo.AllAvvio => true,
        ModoRiepilogo.Giornaliero => ultimo is null || ultimo.Value.ToLocalTime().Date != adesso.ToLocalTime().Date,
        _ => false,
    };

    /// <summary>
    /// Notifiche raccolte dal riepilogo: non lette, di un tipo che entra nel riepilogo e arrivate entro l'ultimo riepilogo.
    /// Il riepilogo è cumulativo: una notifica non ancora letta resta dentro finché l'utente non lo segna come letto.
    /// </summary>
    public static List<NotificaApp> Elementi(IEnumerable<NotificaApp> notifiche, AppConfig cfg, DateTimeOffset ultimo)
    {
        var limite = ultimo.LocalDateTime;
        return notifiche
            .Where(n => !n.Letta && n.Data <= limite && EnterRiepilogo(n.Tipo, cfg))
            .OrderByDescending(n => n.Data)
            .ToList();
    }

    /// <summary>
    /// Conteggio per badge e avviso Windows. Senza riepilogo in vigore è il conteggio di sempre. Con il riepilogo in vigore
    /// le notifiche riepilogabili non contano una per una, né quelle già nel riepilogo né quelle arrivate dopo: il riepilogo,
    /// se non è vuoto, vale come una sola voce.
    /// </summary>
    public static int ConteggioBadge(IEnumerable<NotificaApp> notifiche, AppConfig cfg, DateTimeOffset? ultimo)
    {
        var lista = notifiche as IList<NotificaApp> ?? notifiche.ToList();
        if (!RiepilogoAttivo(cfg, ultimo)) return NotificaPreferenzeService.NonLetteRilevanti(lista, cfg);
        var singole = lista.Count(n => NotificaPreferenzeService.ConteggiaNelBadge(n, cfg) && !EnterRiepilogo(n.Tipo, cfg));
        return singole + (Elementi(lista, cfg, ultimo!.Value).Count > 0 ? 1 : 0);
    }

    /// <summary>Una riga del riepilogo: quante notifiche di un tipo vi sono raccolte.</summary>
    public sealed record VoceRiepilogo(string Tipo, string Etichetta, int Conteggio);

    /// <summary>Contenuto del riepilogo quando è in vigore e non è vuoto: gli elementi (dal più recente) e il conteggio per tipo.</summary>
    public sealed record Riepilogo(IReadOnlyList<NotificaApp> Elementi, IReadOnlyList<VoceRiepilogo> PerTipo);

    /// <summary>Calcola il riepilogo, oppure null se non è in vigore o non raccoglie nulla.</summary>
    public static Riepilogo? Calcola(IEnumerable<NotificaApp> notifiche, AppConfig cfg, DateTimeOffset? ultimo)
    {
        if (!RiepilogoAttivo(cfg, ultimo)) return null;
        var elementi = Elementi(notifiche, cfg, ultimo!.Value);
        if (elementi.Count == 0) return null;
        var perTipo = elementi
            .GroupBy(n => n.Tipo, StringComparer.OrdinalIgnoreCase)
            .Select(g => new VoceRiepilogo(g.Key, EtichettaTipo(g.Key), g.Count()))
            .OrderByDescending(v => v.Conteggio)
            .ThenBy(v => v.Etichetta, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new Riepilogo(elementi, perTipo);
    }

    /// <summary>Etichetta di un tipo: quella di <see cref="NotificaPreferenzeService.TipiNoti"/>, oppure il tipo stesso se sconosciuto.</summary>
    public static string EtichettaTipo(string tipo)
    {
        foreach (var t in NotificaPreferenzeService.TipiNoti)
            if (string.Equals(t.Tipo, tipo, StringComparison.OrdinalIgnoreCase)) return t.Etichetta;
        return tipo;
    }

    /// <summary>Esito dell'emissione all'avvio: se è stato emesso un riepilogo, il nuovo istante e quante notifiche
    /// riepilogabili sono arrivate dopo il riepilogo precedente (zero = nulla di nuovo, quindi nessun avviso).</summary>
    public sealed record Emissione(bool Emesso, DateTimeOffset? Ultimo, int Nuove);

    /// <summary>Emette il riepilogo se la pianificazione lo richiede. Non modifica le notifiche: restituisce solo il nuovo istante.</summary>
    public static Emissione Emetti(AppConfig cfg, IEnumerable<NotificaApp> notifiche, DateTimeOffset? ultimoPrecedente, DateTimeOffset adesso)
    {
        if (!DeveEmettere(Modo(cfg), ultimoPrecedente, adesso)) return new Emissione(false, ultimoPrecedente, 0);
        var limite = adesso.LocalDateTime;
        var inizio = ultimoPrecedente?.LocalDateTime;
        var nuove = notifiche.Count(n =>
            !n.Letta && n.Data <= limite && (inizio is null || n.Data > inizio.Value) && EnterRiepilogo(n.Tipo, cfg));
        return new Emissione(true, adesso, nuove);
    }
}
