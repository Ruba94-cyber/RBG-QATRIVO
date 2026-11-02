namespace TestLoom.App.Services;

/// <summary>Fasi osservabili del backup giornaliero eseguito in background da <see cref="AppState"/>
/// (6.4.1: prima girava in modo sincrono dentro InizializzaCartella e poteva bloccare l'avvio per decine di
/// minuti su una cartella grande). Consultato da Impostazioni → Stato e manutenzione, mai un popup.</summary>
public enum DailyBackupState
{
    NonAvviato,
    InCorso,
    Completato,
    Rinviato,
    Errore
}

/// <summary>Istantanea immutabile dello stato: ogni transizione crea un nuovo record invece di mutare uno stato
/// condiviso, così il thread di backup (priorità bassa, in background) e il thread UI che legge la proprietà
/// non si contendono mai lo stesso oggetto mutabile.</summary>
public sealed record DailyBackupStatus(DailyBackupState State, DateTime AggiornatoUtc, string? Dettaglio)
{
    public static readonly DailyBackupStatus NonAvviato = new(DailyBackupState.NonAvviato, DateTime.MinValue, null);

    public static DailyBackupStatus InCorso() => new(DailyBackupState.InCorso, DateTime.UtcNow, null);
    public static DailyBackupStatus Completato(DateTime orarioUtc) => new(DailyBackupState.Completato, orarioUtc, null);
    /// <summary>La copia locale è integra, ma la destinazione aggiuntiva non è raggiungibile.</summary>
    public static DailyBackupStatus CompletatoConAvviso(DateTime orarioUtc, string avviso) =>
        new(DailyBackupState.Completato, orarioUtc, avviso);
    public static DailyBackupStatus Rinviato(string motivo) => new(DailyBackupState.Rinviato, DateTime.UtcNow, motivo);
    public static DailyBackupStatus Errore(string messaggio) => new(DailyBackupState.Errore, DateTime.UtcNow, messaggio);
}
