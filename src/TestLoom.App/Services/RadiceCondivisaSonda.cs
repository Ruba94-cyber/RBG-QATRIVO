namespace TestLoom.App.Services;

/// <summary>
/// Sonda della radice condivisa con tetto di tempo (6.6.1, correzione 1). Ogni verifica di esistenza o di attributi della
/// radice passa da qui: una cartella di rete in stallo (VPN) non trattiene il chiamante oltre <see cref="TimeoutCondivisione"/>,
/// e lo scadere del tetto vale come non disponibile: nulla viene creato né seminato. Per la UI esiste una lettura NON
/// bloccante (<see cref="EsitoRecenteUi"/>) alimentata da sonde eseguite in background, mai sul thread UI.
/// </summary>
public static class RadiceCondivisaSonda
{
    public enum Esito
    {
        /// <summary>La cartella esiste e ha attributi leggibili.</summary>
        Presente,
        /// <summary>La cartella esiste ma è disponibile solo offline (Files On-Demand).</summary>
        Offline,
        /// <summary>La cartella non esiste.</summary>
        Assente,
        /// <summary>La cartella esiste ma gli attributi non sono leggibili.</summary>
        Inaccessibile,
        /// <summary>La sonda non ha risposto entro il tetto di tempo.</summary>
        Lenta,
    }

    /// <summary>Sonda reale o sostituto nei test: un sostituto lento dimostra che il tetto vale davvero.</summary>
    internal static Func<string, Esito> SondaSistema { get; set; } = SondaReale;

    /// <summary>Età oltre la quale l'ultimo esito noto è considerato vecchio e rinfrescato in background.</summary>
    internal static TimeSpan EtaMassimaUi { get; set; } = TimeSpan.FromSeconds(30);

    private static readonly object Gate = new();

    /// <summary>Ultimo esito noto per ciascun percorso verificato (radice o sottocartella).</summary>
    private static readonly Dictionary<string, (Esito Esito, DateTime QuandoUtc)> UltimiEsiti = new(StringComparer.OrdinalIgnoreCase);

    private static int _rinfrescoInCorso;

    /// <summary>
    /// Verifica con tetto di tempo, per i thread di fondo (inizializzazione, giro periodico, prove di scrittura). Su scadenza
    /// restituisce <see cref="Esito.Lenta"/>. Registra l'esito come ultimo noto per la lettura UI.
    /// </summary>
    public static Esito Verifica(string? radice, TimeSpan? tetto = null)
    {
        if (string.IsNullOrWhiteSpace(radice)) return Esito.Assente;
        var percorso = radice;
        // Il tetto di tempo vale per i percorsi di RETE (UNC o unità mappata), dove la risposta può non arrivare. Una cartella
        // OneDrive/locale resta una verifica diretta, come prima della 6.6.1 (decisione B): nessun thread di appoggio, quindi
        // nessuna scadenza spuria quando il pool dei thread è occupato.
        var esito = ModalitaCondivisioneService.ERete(percorso)
            ? IoTimeoutGuard.Esegui(() => SondaSistema(percorso), tetto ?? TimeoutCondivisione.Per(percorso), Esito.Lenta)
            : SondaSistema(percorso);
        Registra(percorso, esito);
        return esito;
    }

    /// <summary>
    /// Lettura NON bloccante per la UI: restituisce l'ultimo esito noto per questa radice. Se è vecchio o non c'è, avvia in
    /// background una sola sonda alla volta e restituisce subito il valore noto. Senza alcun esito noto restituisce Presente:
    /// chi lo chiama lo fa solo dopo un'inizializzazione riuscita, e la verifica di fondo è già partita.
    /// </summary>
    public static Esito EsitoRecenteUi(string? radice)
    {
        if (string.IsNullOrWhiteSpace(radice)) return Esito.Assente;
        Esito? noto = null;
        var vecchio = true;
        lock (Gate)
        {
            if (UltimiEsiti.TryGetValue(radice, out var ultimo))
            {
                noto = ultimo.Esito;
                vecchio = DateTime.UtcNow - ultimo.QuandoUtc > EtaMassimaUi;
            }
        }
        if (vecchio) RinfrescaInBackground(radice);
        return noto ?? Esito.Presente;
    }

    /// <summary>Vero se la radice risulta presente per la UI (Presente o Offline). Non bloccante, vedi <see cref="EsitoRecenteUi"/>.</summary>
    public static bool PresenteUi(string? radice) => EsitoRecenteUi(radice) is Esito.Presente or Esito.Offline;

    /// <summary>Avvia una verifica con tetto in background, al massimo una alla volta. Non attende e non lancia eccezioni.</summary>
    public static void RinfrescaInBackground(string radice)
    {
        if (Interlocked.CompareExchange(ref _rinfrescoInCorso, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                Verifica(radice);
            }
            catch (Exception)
            {
                // Una verifica di fondo non deve mai far cadere l'applicazione: il prossimo giro riprova.
            }
            finally
            {
                Interlocked.Exchange(ref _rinfrescoInCorso, 0);
            }
        });
    }

    private static void Registra(string radice, Esito esito)
    {
        lock (Gate) UltimiEsiti[radice] = (esito, DateTime.UtcNow);
    }

    /// <summary>Solo test: dimentica gli esiti noti, così ogni test parte da una cache vuota.</summary>
    internal static void AzzeraEsitiNoti()
    {
        lock (Gate) UltimiEsiti.Clear();
    }

    private static Esito SondaReale(string radice)
    {
        // Directory.Exists non lancia: restituisce false anche per percorsi inaccessibili.
        if (!Directory.Exists(radice)) return Esito.Assente;
        try
        {
            return (File.GetAttributes(radice) & FileAttributes.Offline) != 0 ? Esito.Offline : Esito.Presente;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Esito.Inaccessibile;
        }
    }
}
