using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Negazione di scrittura su un singolo file della cartella condivisa, mentre la radice risponde (6.6.1, correzione 2). Non
/// porta il workspace in sola lettura: il salvataggio di quel file fallisce con un messaggio chiaro e la modifica resta nella
/// bozza locale (stesso percorso dei salvataggi falliti). È un UnauthorizedAccessException, quindi i chiamanti esistenti lo
/// trattano come prima.
/// </summary>
public sealed class NegazioneFileCondivisoException : UnauthorizedAccessException
{
    public NegazioneFileCondivisoException(string percorso, Exception originale)
        : base($"Windows ha negato la scrittura del file '{Path.GetFileName(percorso)}'. La cartella condivisa risponde: " +
               "il salvataggio non è stato applicato. Dove l'editor lo supporta, le modifiche restano nella bozza locale. Controllare i permessi del file.",
               originale)
    {
        Percorso = percorso;
    }

    public string Percorso { get; }
}

/// <summary>
/// Perdita di scrittura della RADICE della cartella condivisa rilevata durante la sessione (6.6.1, d). Solo gli errori che
/// indicano radice o rete persa (cartella non trovata, nome di rete scomparso, prova di scrittura della radice fallita) portano
/// il workspace in sola lettura: il lavoro resta nelle bozze locali. La riapertura richiede due prove riuscite consecutive
/// (isteresi), così una rete instabile non fa oscillare la modalità ogni cinque minuti. Lo stato vive solo in memoria, non è
/// mai la modalità scelta dall'utente (quella è il visualizzatore locale, WorkspaceReadOnlyService). Questa classe non scrive
/// mai nella cartella condivisa.
/// </summary>
public static class SharedWriteLossMonitor
{
    /// <summary>Prove di scrittura riuscite consecutive necessarie per riaprire una radice in perdita.</summary>
    internal const int SogliaRipristino = 2;

    /// <summary>Classe di un errore di scrittura su una cartella condivisa.</summary>
    internal enum Classe
    {
        /// <summary>Errore locale o non legato alla condivisione (es. disco pieno, file in uso).</summary>
        NonPerdita,

        /// <summary>Radice, cartella o rete persa: sola lettura della radice.</summary>
        PerditaRadice,

        /// <summary>Un solo file negato dal sistema: la radice va verificata prima di decidere.</summary>
        NegazioneFile,
    }

    /// <summary>Codici Win32 (HResult &amp; 0xFFFF) che indicano radice, rete o percorso persi. Esclusi di proposito: 5
    /// (accesso negato, trattato come negazione di file), 32 (violazione di condivisione di un file aperto altrove), 112
    /// (disco pieno), 80/183 (file esistente).</summary>
    private static readonly int[] CodiciWin32Radice =
    {
        3,    // ERROR_PATH_NOT_FOUND
        21,   // ERROR_NOT_READY
        53,   // ERROR_BAD_NETPATH
        55,   // ERROR_DEV_NOT_EXIST (risorsa di rete non più disponibile, tipico di una VPN caduta)
        59,   // ERROR_UNEXP_NET_ERR (errore imprevisto della rete)
        64,   // ERROR_NETNAME_DELETED
        67,   // ERROR_BAD_NET_NAME
        88,   // ERROR_NET_WRITE_FAULT
        121,  // ERROR_SEM_TIMEOUT
        1201, // ERROR_CONNECTION_UNAVAIL
        1203, // ERROR_NO_NET_OR_BAD_PATH
        1222, // ERROR_NO_NETWORK
        1225, // ERROR_CONNECTION_REFUSED
        1231, // ERROR_NETWORK_UNREACHABLE
        1232, // ERROR_HOST_UNREACHABLE
        1236, // ERROR_CONNECTION_ABORTED (connessione di rete interrotta, per esempio da una VPN)
        1326, // ERROR_LOGON_FAILURE
        2250, // ERROR_NOT_CONNECTED
    };

    /// <summary>Stato di una radice in perdita. Il conteggio conta le prove riuscite consecutive dall'inizio della perdita.</summary>
    private sealed class Perdita
    {
        public Perdita(DateTime rilevataIl, string motivo)
        {
            RilevataIl = rilevataIl;
            Motivo = motivo;
        }

        public DateTime RilevataIl { get; }
        public string Motivo { get; }
        public int ProveRiuscite { get; set; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Perdita?> Radici = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registra una cartella condivisa aperta. Riaprirla azzera una perdita precedente: la prova di scrittura
    /// dell'inizializzazione decide di nuovo lo stato.</summary>
    /// <remarks>6.6.1 (decisione B): il monitor e il gate di scrittura valgono SOLO per le cartelle di rete. Una cartella
    /// OneDrive/SharePoint non viene mai registrata: resta il comportamento storico, senza sola lettura automatica.</remarks>
    public static void Registra(string? radice)
    {
        var chiave = Normalizza(radice);
        if (chiave == null || !EReteDecisa(chiave)) return;
        lock (Gate) Radici[chiave] = null;
    }

    /// <summary>Radici riconosciute come rete in questa sessione: il riconoscimento non si ricalcola a ogni apertura (P4).</summary>
    private static readonly HashSet<string> RadiciDiRete = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Decisione di monitoraggio per la radice configurata (P4). Una radice riconosciuta come rete resta tale per tutta la
    /// sessione: lo stato istantaneo di un'unità mappata non ancora connessa non la toglie più dal monitoraggio. Una decisione
    /// negativa viene rivalutata alla prossima apertura.</summary>
    private static bool EReteDecisa(string chiave)
    {
        lock (Gate)
        {
            if (RadiciDiRete.Contains(chiave)) return true;
        }
        var rete = ModalitaCondivisioneService.ERete(chiave);
        if (rete)
        {
            lock (Gate) RadiciDiRete.Add(chiave);
        }
        return rete;
    }

    /// <summary>Motivo della perdita attiva per questa radice, null se la scrittura risulta disponibile.</summary>
    public static string? MotivoPerdita(string? radice)
    {
        var chiave = Normalizza(radice);
        if (chiave == null) return null;
        lock (Gate) return Radici.TryGetValue(chiave, out var perdita) ? perdita?.Motivo : null;
    }

    /// <summary>Esito di sola lettura da mostrare mentre una perdita è attiva; null se non ce n'è nessuna.</summary>
    internal static EsitoStatoCondivisione? EsitoPerdita(string? radice)
    {
        var chiave = Normalizza(radice);
        if (chiave == null) return null;
        lock (Gate)
        {
            if (!Radici.TryGetValue(chiave, out var perdita) || perdita == null) return null;
            return new EsitoStatoCondivisione
            {
                Stato = Models.StatoCondivisione.SolaLettura,
                Messaggio = $"Scrittura sulla cartella condivisa non disponibile ({perdita.Motivo}). " +
                    "Dove l'editor lo supporta, le modifiche non salvate restano nelle bozze locali; TestLoom riprova automaticamente " +
                    "e torna operativo quando la cartella è di nuovo scrivibile.",
                VerificatoIl = perdita.RilevataIl,
            };
        }
    }

    /// <summary>
    /// Osserva un errore di scrittura su un percorso. Un errore di radice persa mette la radice in perdita. Una negazione su
    /// un singolo file verifica la radice con una prova di scrittura: se risponde, la radice resta operativa e il file fallisce
    /// con un messaggio chiaro (il valore restituito, che il chiamante lancia al posto dell'originale). Non lancia mai eccezioni.
    /// </summary>
    /// <returns>Eccezione sostitutiva per una negazione di singolo file, altrimenti null (il chiamante rilancia l'originale).</returns>
    public static Exception? Osserva(string? percorso, Exception ex)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(percorso)) return null;
            var classe = Classifica(ex);
            if (classe == Classe.NonPerdita) return null;

            var completo = Path.GetFullPath(percorso);
            var radice = RadiceRegistrataPer(completo);
            if (radice == null) return null;

            if (classe == Classe.NegazioneFile)
            {
                // Nessuna prova sul thread di scrittura (P4): il messaggio di file negato vale solo se l'ultima verifica in
                // background ha trovato la radice operativa. La radice viene comunque riverificata in background, che decide
                // l'eventuale perdita; un esito incerto per tetto di tempo non lo è.
                var radiceRisponde = RadiceRispondeDiRecente(radice);
                AvviaVerificaRadice(radice);
                if (!radiceRisponde) return null;
                DiagnosticTraceService.Registra("Condivisione.NegazioneFile",
                    $"radice={radice}; file={Path.GetFileName(completo)}; motivo={Descrivi(ex)}");
                return new NegazioneFileCondivisoException(completo, ex);
            }

            lock (Gate)
            {
                if (Radici.TryGetValue(radice, out var attuale) && attuale == null)
                {
                    var motivo = Descrivi(ex);
                    Radici[radice] = new Perdita(DateTime.Now, motivo);
                    DiagnosticTraceService.Registra("Condivisione.PerditaScrittura", $"radice={radice}; motivo={motivo}");
                }
            }
            return null;
        }
        catch (Exception)
        {
            // Osservare non deve mai far fallire il salvataggio che ha già sollevato l'errore originale.
            return null;
        }
    }

    /// <summary>Ultima verifica in background con radice operativa: serve al messaggio di file negato. Scade dopo <see cref="ValiditaVerificaOk"/>.</summary>
    private static readonly Dictionary<string, DateTime> UltimaVerificaOk = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Verifiche in background in corso, una per radice: le segnalazioni successive non ne lanciano altre.</summary>
    private static readonly Dictionary<string, Task> VerificheInCorso = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan ValiditaVerificaOk = TimeSpan.FromSeconds(60);

    private static bool RadiceRispondeDiRecente(string radice)
    {
        lock (Gate) return UltimaVerificaOk.TryGetValue(radice, out var quando) && DateTime.UtcNow - quando < ValiditaVerificaOk;
    }

    private static void AvviaVerificaRadice(string radice)
    {
        lock (Gate)
        {
            if (VerificheInCorso.ContainsKey(radice)) return;
            VerificheInCorso[radice] = Task.Run(() => VerificaRadiceOra(radice));
        }
    }

    /// <summary>
    /// Verifica una radice e ne aggiorna lo stato: è il lavoro del compito in background, esposto ai test per una verifica
    /// deterministica. Solo un esito definitivo di radice non operativa porta in sola lettura; un tetto di tempo scaduto è incerto.
    /// </summary>
    internal static void VerificaRadiceOra(string radice)
    {
        var chiave = Normalizza(radice);
        if (chiave == null) return;
        try
        {
            var prova = CondivisioneHealthService.Sonda(chiave);
            var operativa = prova.Stato == Models.StatoCondivisione.Operativa;
            lock (Gate)
            {
                if (operativa) UltimaVerificaOk[chiave] = DateTime.UtcNow;
                else UltimaVerificaOk.Remove(chiave);
            }
            if (operativa) return;
            if (prova.Incerto)
            {
                DiagnosticTraceService.Registra("Condivisione.VerificaIncerta", $"radice={chiave}; motivo={prova.Messaggio}");
                return;
            }
            lock (Gate)
            {
                if (Radici.TryGetValue(chiave, out var attuale) && attuale == null)
                {
                    Radici[chiave] = new Perdita(DateTime.Now, prova.Messaggio);
                    DiagnosticTraceService.Registra("Condivisione.PerditaScrittura", $"radice={chiave}; motivo={prova.Messaggio}");
                }
            }
        }
        catch (Exception)
        {
            // In background un'eccezione della verifica non deve far cadere l'app: lo stato resta invariato.
        }
        finally
        {
            lock (Gate) VerificheInCorso.Remove(radice);
        }
    }

    /// <summary>Attende le verifiche in background in corso (test): true se finiscono tutte entro il tetto.</summary>
    internal static bool AttendiVerificheInCorso(TimeSpan tetto)
    {
        Task[] attese;
        lock (Gate) attese = VerificheInCorso.Values.ToArray();
        return Task.WaitAll(attese, tetto);
    }

    /// <summary>
    /// Esito di una prova di scrittura sulla radice in perdita (6.6.1, correzione 2, isteresi). Vero se la radice può tornare
    /// operativa: nessuna perdita attiva, oppure <see cref="SogliaRipristino"/> prove riuscite consecutive. Una prova fallita
    /// azzera il conteggio e resta in sola lettura.
    /// </summary>
    public static bool PuoRipristinare(string? radice, bool proveRiuscita)
    {
        var chiave = Normalizza(radice);
        if (chiave == null) return true;
        lock (Gate)
        {
            if (!Radici.TryGetValue(chiave, out var perdita) || perdita == null) return true;
            if (!proveRiuscita)
            {
                perdita.ProveRiuscite = 0;
                return false;
            }
            perdita.ProveRiuscite++;
            if (perdita.ProveRiuscite < SogliaRipristino)
            {
                DiagnosticTraceService.Registra("Condivisione.RipristinoInIsteresi",
                    $"radice={chiave}; prove={perdita.ProveRiuscite}/{SogliaRipristino}");
                return false;
            }
            Radici[chiave] = null;
            DiagnosticTraceService.Registra("Condivisione.RipristinoScrittura", $"radice={chiave}; sospesa-dal={perdita.RilevataIl:O}");
            return true;
        }
    }

    /// <summary>Azzeramento esplicito di una perdita (riapertura, test). Il ritorno alla scrittura normale passa da
    /// <see cref="PuoRipristinare"/>, che applica l'isteresi.</summary>
    public static void Ripristina(string? radice)
    {
        var chiave = Normalizza(radice);
        if (chiave == null) return;
        lock (Gate) Radici[chiave] = null;
    }

    /// <summary>Classifica un errore di scrittura su una cartella condivisa.</summary>
    internal static Classe Classifica(Exception ex) => ex switch
    {
        DirectoryNotFoundException => Classe.PerditaRadice,
        UnauthorizedAccessException => Classe.NegazioneFile,
        IOException io => ClassificaWin32(io.HResult & 0xFFFF),
        _ => Classe.NonPerdita,
    };

    /// <summary>Vero se l'errore indica radice persa o negazione di file (e non un errore locale).</summary>
    internal static bool EErrorePerditaCondivisione(Exception ex) => Classifica(ex) != Classe.NonPerdita;

    private static Classe ClassificaWin32(int codice)
    {
        if (codice == 5) return Classe.NegazioneFile; // ERROR_ACCESS_DENIED
        return Array.IndexOf(CodiciWin32Radice, codice) >= 0 ? Classe.PerditaRadice : Classe.NonPerdita;
    }

    /// <summary>Radice registrata che contiene il percorso, o null. Chiamare sotto il lock o fuori: prende il lock.</summary>
    private static string? RadiceRegistrataPer(string completo)
    {
        lock (Gate)
        {
            foreach (var radice in Radici.Keys)
            {
                if (string.Equals(completo, radice, StringComparison.OrdinalIgnoreCase) ||
                    completo.StartsWith(radice + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return radice;
            }
            return null;
        }
    }

    private static string Descrivi(Exception ex)
    {
        var codice = ex is IOException io ? $" (codice {io.HResult & 0xFFFF})" : "";
        var messaggio = ex.Message.Length > 160 ? ex.Message[..160] : ex.Message;
        return $"{ex.GetType().Name}{codice}: {messaggio}";
    }

    private static string? Normalizza(string? radice)
    {
        if (string.IsNullOrWhiteSpace(radice)) return null;
        try
        {
            return Path.GetFullPath(radice).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
