using System.Collections.Concurrent;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Stato Ticketing (sola lettura, da cache condivisa) di un requisito, pronto per badge, filtro e dettaglio.</summary>
public sealed record RequisitoPolarionStato(
    string RequisitoId,
    RequisitoPolarionLink? Link,
    PolarionStatoClasse Classe,
    string? Stato,
    string? Risoluzione,
    string? Titolo,
    DateTimeOffset? LettoAlUtc,
    int Commenti,
    bool Obsoleto,
    bool ProgettoNonConfigurato)
{
    public bool Collegato => Link is not null;

    /// <summary>Una riga "non collegato" riusabile: nessun requisito senza collegamento costa allocazioni per riga.</summary>
    public static RequisitoPolarionStato SenzaCollegamento(string requisitoId) =>
        new(requisitoId, null, PolarionStatoClasse.NonCollegato, null, null, null, null, 0, false, false);

    /// <summary>Testo breve per il badge: "Stato" oppure "Stato / Risoluzione"; mai vuoto per un requisito collegato.</summary>
    public string TestoBreve(LocalizationService loc)
    {
        if (!Collegato) return "";
        if (Classe == PolarionStatoClasse.NonDisponibile)
            return ProgettoNonConfigurato
                ? loc.Scegli("progetto non configurato", "project not configured", "projet non configuré")
                : loc.Scegli("non disponibile", "not available", "non disponible");
        var testo = Stato ?? "";
        if (!string.IsNullOrWhiteSpace(Risoluzione) && !string.Equals(Risoluzione, Stato, StringComparison.OrdinalIgnoreCase))
            testo += " / " + Risoluzione;
        return Obsoleto ? "⚠ " + testo : testo;
    }
}

/// <summary>Costruzione pura (senza I/O proprio) dello stato di un requisito a partire dal suo collegamento e dalla cache.</summary>
public static class RequisitoPolarionStatoBuilder
{
    public static readonly TimeSpan SogliaObsoleto = TimeSpan.FromHours(24);

    /// <param name="progettoConfigurato">true se il progetto e tra quelli configurati dal Master.</param>
    /// <param name="caricaSnapshot">lettura della cache (progetto, id); null = non sincronizzato. Le eccezioni di
    /// I/O/dati sono assorbite: il requisito risulta "non disponibile", mai un crash della lista.</param>
    public static RequisitoPolarionStato Costruisci(Requisito requisito, Func<string, bool> progettoConfigurato,
        Func<string, string, PolarionTicketSnapshot?> caricaSnapshot, DateTimeOffset adessoUtc)
    {
        ArgumentNullException.ThrowIfNull(requisito);
        var link = RequisitoPolarionRegole.Di(requisito);
        if (link is not { } l) return RequisitoPolarionStato.SenzaCollegamento(requisito.Id);
        if (!progettoConfigurato(l.Progetto))
            return new(requisito.Id, l, PolarionStatoClasse.NonDisponibile, null, null, null, null, 0, false, true);
        PolarionTicketSnapshot? snapshot;
        try { snapshot = caricaSnapshot(l.Progetto, l.WorkItemId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        {
            snapshot = null;
        }
        if (snapshot is null)
            return new(requisito.Id, l, PolarionStatoClasse.NonDisponibile, null, null, null, null, 0, false, false);
        var obsoleto = snapshot.RetrievedAtUtc != default && adessoUtc - snapshot.RetrievedAtUtc > SogliaObsoleto;
        return new(requisito.Id, l, PolarionStatoClassificatore.Classifica(snapshot.Status), snapshot.Status, snapshot.Resolution,
            snapshot.Title, snapshot.RetrievedAtUtc == default ? null : snapshot.RetrievedAtUtc, snapshot.Comments?.Count ?? 0, obsoleto, false);
    }
}

/// <summary>
/// Cache in memoria (per cartella dati) dello stato Ticketing di tutti i requisiti. Si ricostruisce in background dalla
/// cache condivisa degli snapshot (mai dalla rete e mai sul thread UI): le liste leggono SOLO da qui, quindi nessuna lettura
/// di file per riga. Le richieste ravvicinate sono coalescenti e limitate nel tempo.
/// </summary>
public static class RequisitoPolarionCache
{
    private sealed record Entry(IReadOnlyDictionary<string, RequisitoPolarionStato> Mappa, DateTimeOffset CaricatoUtc);

    private static readonly ConcurrentDictionary<string, Entry> Voci = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> InCorso = new(StringComparer.OrdinalIgnoreCase);
    public static readonly TimeSpan IntervalloMinimo = TimeSpan.FromSeconds(15);
    private static int _notificaInCoda;

    /// <summary>Sollevato (sul thread UI quando disponibile) dopo ogni ricostruzione riuscita della cache.</summary>
    public static event Action? Aggiornato;

    /// <summary>Esecuzione dell'evento: sostituibile nei test, dove non esiste un dispatcher UI.</summary>
    internal static Action<Action> Postatore { get; set; } = azione =>
    {
        try { Avalonia.Threading.Dispatcher.UIThread.Post(azione); }
        catch (Exception) { azione(); }
    };

    private static string Chiave(DataPaths paths) => paths.Root;

    public static bool Caricata(DataPaths? paths) => paths is not null && Voci.ContainsKey(Chiave(paths));

    /// <summary>Stato del requisito se la cache e pronta, altrimenti null (il chiamante mostra solo l'ID senza badge).</summary>
    public static RequisitoPolarionStato? TryGet(DataPaths? paths, string requisitoId)
    {
        if (paths is null || string.IsNullOrEmpty(requisitoId)) return null;
        return Voci.TryGetValue(Chiave(paths), out var voce) && voce.Mappa.TryGetValue(requisitoId, out var stato) ? stato : null;
    }

    /// <summary>ID dei requisiti la cui classe di stato e tra quelle richieste; null se la cache non e ancora pronta.
    /// "NonCollegato" comprende tutti i requisiti senza collegamento Ticketing.</summary>
    public static IReadOnlyList<string>? IdPerClassi(DataPaths? paths, IReadOnlyCollection<PolarionStatoClasse> classi)
    {
        if (paths is null || !Voci.TryGetValue(Chiave(paths), out var voce)) return null;
        return voce.Mappa.Values.Where(s => classi.Contains(s.Classe)).Select(s => s.RequisitoId).ToList();
    }

    /// <summary>Scarta la cache della cartella (es. dopo il salvataggio di un collegamento o un aggiornamento manuale).</summary>
    public static void Invalida(DataPaths? paths)
    {
        if (paths is not null) Voci.TryRemove(Chiave(paths), out _);
    }

    /// <summary>Ricostruzione asincrona e coalescente. Non blocca il chiamante e non fa mai rete.</summary>
    public static void Richiedi(AppState? state, bool forza = false)
    {
        if (state is null || !state.CartellaValida || state.Paths is not { } paths) return;
        var chiave = Chiave(paths);
        if (!forza && Voci.TryGetValue(chiave, out var attuale) && DateTimeOffset.UtcNow - attuale.CaricatoUtc < IntervalloMinimo) return;
        if (!InCorso.TryAdd(chiave, 0)) return;
        _ = Task.Run(() =>
        {
            try { Ricostruisci(paths, () => state.Master.Requisiti.Load()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                DiagnosticTraceService.Registra("Requisiti.Ticketing", "cache-failed kind=" + ex.GetType().Name);
            }
            finally { InCorso.TryRemove(chiave, out _); }
        });
    }

    /// <summary>Ricostruzione sincrona (eseguita su un thread di lavoro; esposta ai test).</summary>
    internal static void Ricostruisci(DataPaths paths, Func<IReadOnlyList<Requisito>> requisiti)
    {
        var elenco = requisiti();
        PolarionApiSettings settings;
        try { settings = PolarionApiSettingsService.Load(paths); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { settings = new PolarionApiSettings(); }
        var store = new PolarionSnapshotStore(paths, sharedWriteAllowed: false);
        var adesso = DateTimeOffset.UtcNow;
        var mappa = new Dictionary<string, RequisitoPolarionStato>(StringComparer.OrdinalIgnoreCase);
        // Lo stesso work item collegato a piu requisiti si legge una volta sola.
        var lette = new Dictionary<(string, string), PolarionTicketSnapshot?>();
        PolarionTicketSnapshot? Carica(string progetto, string id)
        {
            var k = (progetto.ToUpperInvariant(), id.ToUpperInvariant());
            if (!lette.TryGetValue(k, out var s)) lette[k] = s = store.Load(progetto, id);
            return s;
        }
        foreach (var r in elenco)
        {
            if (string.IsNullOrEmpty(r.Id)) continue;
            mappa[r.Id] = RequisitoPolarionStatoBuilder.Costruisci(r, settings.ProgettoConfigurato, Carica, adesso);
        }
        Voci[Chiave(paths)] = new Entry(mappa, adesso);
        NotificaAggiornato();
    }

    private static void NotificaAggiornato()
    {
        if (Interlocked.Exchange(ref _notificaInCoda, 1) != 0) return;
        Postatore(() =>
        {
            Interlocked.Exchange(ref _notificaInCoda, 0);
            Aggiornato?.Invoke();
        });
    }
}
