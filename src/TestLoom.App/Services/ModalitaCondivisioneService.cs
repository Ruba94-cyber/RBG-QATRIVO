using System.Runtime.InteropServices;
using System.Text;

namespace TestLoom.App.Services;

/// <summary>
/// Come viene raggiunta la cartella dati condivisa su questo PC (6.6.1). Lo stesso formato dati (schema 10) vale per
/// entrambe: cambiano solo gli attributi Cloud Files (OneDrive Files On-Demand), i timeout delle prove di I/O e gli
/// avvisi che parlano di OneDrive.
/// </summary>
public enum ModalitaCondivisione
{
    /// <summary>OneDrive/SharePoint sincronizzata (o cartella locale): comportamento storico invariato.</summary>
    CartellaSincronizzata,

    /// <summary>Cartella di rete SMB, raggiunta con percorso UNC (UNC server\share\...) o tramite unità mappata.</summary>
    CartellaRete,
}

/// <summary>
/// Rilevazione automatica della modalità di cartella condivisa e override per PC (AppConfig.ModalitaCartellaCondivisa,
/// locale come il resto di AppConfig, mai nella cartella dati). Config assente, vuoto o sconosciuto = Automatica.
/// </summary>
public static class ModalitaCondivisioneService
{
    public const string ValoreAutomatica = "Automatica";
    public const string ValoreOneDrive = "OneDrive";
    public const string ValoreRete = "Rete";

    private static volatile string? _overrideCorrente;

    /// <summary>Valore canonico del config: "Rete", "OneDrive" o "Automatica". Qualsiasi altro valore (anche null) è Automatica.</summary>
    public static string NormalizzaValore(string? valore)
    {
        var v = valore?.Trim();
        if (string.Equals(v, ValoreRete, StringComparison.OrdinalIgnoreCase)) return ValoreRete;
        if (string.Equals(v, ValoreOneDrive, StringComparison.OrdinalIgnoreCase)) return ValoreOneDrive;
        return ValoreAutomatica;
    }

    /// <summary>Override del PC in vigore. Impostato da AppState all'avvio e ad ogni modifica in Impostazioni.</summary>
    public static void ImpostaOverride(string? valore)
    {
        var normalizzato = NormalizzaValore(valore);
        _overrideCorrente = normalizzato == ValoreAutomatica ? null : normalizzato;
    }

    /// <summary>Valore dell'override in vigore, "Automatica" se nessun override è impostato.</summary>
    public static string OverrideCorrente => _overrideCorrente ?? ValoreAutomatica;

    /// <summary>
    /// Override per cartella configurata (6.6.1, correzione di isolamento): ogni override vale per la radice che descrive e per
    /// i percorsi sotto di essa. Così un override di una sessione (o di un test) non cambia la modalità di altre cartelle
    /// in parallelo. Chiave = radice normalizzata, valore = modalità canonica (Automatica significa nessun override).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> OverridePerRadice =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Imposta (o rimuove, con Automatica/null) l'override di modalità per una cartella configurata.</summary>
    public static void ImpostaOverrideRadice(string? radice, string? valore)
    {
        if (string.IsNullOrWhiteSpace(radice)) return;
        var chiave = NormalizzaRadice(radice);
        var normalizzato = NormalizzaValore(valore);
        if (normalizzato == ValoreAutomatica) OverridePerRadice.TryRemove(chiave, out _);
        else OverridePerRadice[chiave] = normalizzato;
    }

    /// <summary>Override in vigore per questa cartella (o per i percorsi sotto di essa), "Automatica" se nessuno.</summary>
    public static string OverrideRadice(string? radice) => OverridePer(radice) ?? _overrideCorrente ?? ValoreAutomatica;

    /// <summary>Override della radice più specifica che contiene il percorso, null se nessuna radice registrata lo contiene.</summary>
    internal static string? OverridePer(string? percorso)
    {
        if (OverridePerRadice.IsEmpty || string.IsNullOrWhiteSpace(percorso)) return null;
        string completo;
        try { completo = NormalizzaRadice(percorso); }
        catch (Exception) { return null; }
        string? migliore = null;
        var lunghezza = -1;
        foreach (var voce in OverridePerRadice)
        {
            if (voce.Key.Length <= lunghezza) continue;
            if (string.Equals(completo, voce.Key, StringComparison.OrdinalIgnoreCase) ||
                completo.StartsWith(voce.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                migliore = voce.Value;
                lunghezza = voce.Key.Length;
            }
        }
        return migliore;
    }

    private static string NormalizzaRadice(string percorso) =>
        Path.GetFullPath(percorso).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Vero per un percorso UNC di rete (UNC server\share, anche nella forma UNC ?\UNC\...). Percorsi
    /// locali, di dispositivo (UNC .\) o con prefisso lungo locale (UNC ?\C:\) restituiscono falso.</summary>
    public static bool EPercorsoUncRemoto(string? percorso)
    {
        if (string.IsNullOrWhiteSpace(percorso)) return false;
        var n = percorso.Trim().Replace('/', '\\');
        if (n.StartsWith(@"\" + @"\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.StartsWith(@"\" + @"\?\", StringComparison.Ordinal) || n.StartsWith(@"\" + @"\.\", StringComparison.Ordinal)) return false;
        return n.StartsWith(@"\" + @"\", StringComparison.Ordinal) && n.Length > 2 && n[2] != '\\';
    }

    /// <summary>Lettera di unità in forma "Z:" se il percorso inizia con una lettera di unità, altrimenti null.</summary>
    internal static string? LetteraUnita(string? percorso)
    {
        if (string.IsNullOrWhiteSpace(percorso)) return null;
        var n = percorso.Trim();
        return n.Length >= 2 && char.IsAsciiLetter(n[0]) && n[1] == ':' ? n[..2].ToUpperInvariant() : null;
    }

    /// <summary>Rilevazione automatica, ignora qualunque override: UNC o unità mappata di rete = CartellaRete; tutto il resto
    /// (OneDrive/SharePoint sincronizzata o cartella locale) = CartellaSincronizzata.</summary>
    public static ModalitaCondivisione Rileva(string? percorso)
    {
        if (string.IsNullOrWhiteSpace(percorso)) return ModalitaCondivisione.CartellaSincronizzata;
        if (EPercorsoUncRemoto(percorso)) return ModalitaCondivisione.CartellaRete;
        var lettera = LetteraUnita(percorso);
        return lettera != null && UnitaRete.Risolvi(lettera) != null
            ? ModalitaCondivisione.CartellaRete
            : ModalitaCondivisione.CartellaSincronizzata;
    }

    /// <summary>Modalità effettiva per questo PC: l'override in vigore prevale sulla rilevazione automatica.</summary>
    public static ModalitaCondivisione Effettiva(string? percorso) => Effettiva(percorso, OverridePer(percorso) ?? _overrideCorrente);

    /// <summary>Come <see cref="Effettiva(string?)"/> ma con override esplicito (usato dai test e dalle Impostazioni prima del salvataggio).</summary>
    public static ModalitaCondivisione Effettiva(string? percorso, string? valoreOverride) =>
        NormalizzaValore(valoreOverride) switch
        {
            ValoreRete => ModalitaCondivisione.CartellaRete,
            ValoreOneDrive => ModalitaCondivisione.CartellaSincronizzata,
            _ => Rileva(percorso),
        };

    /// <summary>Vero se la cartella (o il file sotto di essa) è gestita come cartella di rete su questo PC.</summary>
    public static bool ERete(string? percorso) => Effettiva(percorso) == ModalitaCondivisione.CartellaRete;

    /// <summary>
    /// Come <see cref="ERete"/>, ma senza attendere la rete: per l'interfaccia e per i giri periodici, che non devono bloccare
    /// il thread UI su un'unità mappata lenta (P4). Un'unità non ancora risolta dà false e avvia la risoluzione in background;
    /// <see cref="UnitaRete.Classificata"/> segnala quando l'esito è pronto.
    /// </summary>
    public static bool ERetePerInterfaccia(string? percorso)
    {
        var valore = NormalizzaValore(OverridePer(percorso) ?? _overrideCorrente);
        if (valore == ValoreRete) return true;
        if (valore == ValoreOneDrive) return false;
        if (EPercorsoUncRemoto(percorso)) return true;
        var lettera = LetteraUnita(percorso);
        return lettera != null && UnitaRete.ReteSenzaAttesa(lettera) == true;
    }

    /// <summary>
    /// Percorso canonico per la chiave del workspace locale (6.6.1, c): un'unità di rete mappata (Z:\dati) diventa il suo
    /// percorso UNC (UNC server\share\dati), così la stessa cartella vista con lettera o con UNC produce un solo workspace
    /// locale. Qualsiasi altro percorso (locale, OneDrive, UNC già esplicito, unità non risolta) resta invariato.
    /// </summary>
    public static string PercorsoCanonico(string percorso)
    {
        var lettera = LetteraUnita(percorso);
        if (lettera == null) return percorso;
        var dati = UnitaRete.Risolvi(lettera);
        if (dati?.PercorsoUnc is not { Length: > 0 } unc) return percorso;
        return unc.TrimEnd('\\') + percorso.Trim()[2..];
    }
}

/// <summary>Dati di un'unità di rete mappata: il percorso UNC remoto, se Windows lo fornisce.</summary>
internal sealed record DatiUnitaRete(string? PercorsoUnc);

/// <summary>
/// Risoluzione delle unità mappate (6.6.1). Il risolutore è sostituibile nei test: nessun test dipende da unità reali.
/// Il valore predefinito usa DriveInfo (tipo di unità Network) e WNetGetConnection (percorso remoto UNC).
/// </summary>
internal static class UnitaRete
{
    /// <summary>Tetto di tempo di una risoluzione di unità mappata (P4): il thread chiamante non attende oltre. Alla scadenza
    /// la lettera è incerta e conta come rete (scelta conservativa: tempi di rete, nessuna modifica agli attributi Cloud Files);
    /// la risoluzione prosegue in background e memorizza il suo esito.</summary>
    internal static TimeSpan TimeoutRisoluzione { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Validità di un esito memorizzato per una lettera: dopo questo tempo la lettera viene riverificata.</summary>
    internal static TimeSpan ValiditaRisultato { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Lettera di unità ("Z:") → dati se è un'unità di rete o incerta, null se locale (fissa, rimovibile, ottica).</summary>
    internal static Func<string, DatiUnitaRete?> Risolutore { get; set; } = RisolviSistema;

    /// <summary>Segnalata, dal thread di risoluzione, quando una lettera ottiene un esito in background: l'interfaccia rilegge la modalità.</summary>
    internal static event Action<string>? Classificata;

    private sealed record Voce(DatiUnitaRete? Dati, DateTime Scadenza);

    /// <summary>Esiti completati per lettera, con scadenza. Anche l'esito «locale» viene memorizzato: senza, l'interfaccia
    /// rilancerebbe la risoluzione a ogni lettura.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<char, Voce> Memoria = new();

    /// <summary>Risoluzioni in volo, una per lettera: le richieste successive attendono la stessa, senza lanciarne altre.</summary>
    private static readonly Dictionary<char, Task<DatiUnitaRete?>> InVolo = new();
    private static readonly object GateVolo = new();

    /// <summary>Esito di una lettera con tetto di tempo (vedi <see cref="TimeoutRisoluzione"/>).</summary>
    internal static DatiUnitaRete? Risolvi(string lettera)
    {
        var chiave = char.ToUpperInvariant(lettera[0]);
        if (LettereFisseLocali.ContainsKey(chiave)) return null;
        if (Memoria.TryGetValue(chiave, out var voce) && voce.Scadenza > DateTime.UtcNow) return voce.Dati;
        var compito = AvviaRisoluzione(chiave, lettera);
        return compito.Wait(TimeoutRisoluzione) ? compito.Result : new DatiUnitaRete(null);
    }

    /// <summary>Esito senza attesa: true = rete (o incerta), false = locale, null = ancora sconosciuta (la risoluzione parte in background).</summary>
    internal static bool? ReteSenzaAttesa(string lettera)
    {
        var chiave = char.ToUpperInvariant(lettera[0]);
        if (LettereFisseLocali.ContainsKey(chiave)) return false;
        if (Memoria.TryGetValue(chiave, out var voce) && voce.Scadenza > DateTime.UtcNow) return voce.Dati != null;
        AvviaRisoluzione(chiave, lettera);
        return null;
    }

    /// <summary>Svuota gli esiti memorizzati (test e cambio di risolutore): le risoluzioni in volo finiscono prima.</summary>
    internal static void Reimposta()
    {
        Task[] inVolo;
        lock (GateVolo) inVolo = InVolo.Values.Cast<Task>().ToArray();
        Task.WaitAll(inVolo, TimeSpan.FromSeconds(30));
        Memoria.Clear();
    }

    private static Task<DatiUnitaRete?> AvviaRisoluzione(char chiave, string lettera)
    {
        lock (GateVolo)
        {
            if (InVolo.TryGetValue(chiave, out var attiva)) return attiva;
            var compito = Task.Run(() => Esegui(chiave, lettera));
            InVolo[chiave] = compito;
            return compito;
        }
    }

    private static DatiUnitaRete? Esegui(char chiave, string lettera)
    {
        DatiUnitaRete? dati;
        try
        {
            dati = Risolutore(lettera);
        }
        catch (Exception)
        {
            // Unità non interrogabile: incerta, quindi di rete (come un tetto di tempo scaduto).
            dati = new DatiUnitaRete(null);
        }
        lock (GateVolo)
        {
            Memoria[chiave] = new Voce(dati, DateTime.UtcNow + ValiditaRisultato);
            InVolo.Remove(chiave);
        }
        try
        {
            Classificata?.Invoke(lettera);
        }
        catch (Exception)
        {
            // La notifica all'interfaccia non deve mai invalidare l'esito già memorizzato.
        }
        return dati;
    }

    /// <summary>Lettere già note come unità fisse locali. Una unità fissa non diventa mai di rete, quindi il risultato è
    /// memorizzato per il processo: i percorsi locali e OneDrive non pagano una chiamata di sistema per ogni scrittura.
    /// Le unità non ancora mappate (VPN non connessa) e quelle di rete NON vengono memorizzate: vanno riverificate.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<char, byte> LettereFisseLocali = new();

    private static DatiUnitaRete? RisolviSistema(string lettera)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var chiave = char.ToUpperInvariant(lettera[0]);
        try
        {
            var tipo = new DriveInfo(lettera + "\\").DriveType;
            if (tipo == DriveType.Fixed)
            {
                LettereFisseLocali[chiave] = 0;
                return null;
            }
            if (tipo == DriveType.Network) return new DatiUnitaRete(PercorsoRemotoWindows(lettera));
            // Non mappata (rete non connessa, VPN giù) o di tipo non riconosciuto: incerta, quindi di rete. Le unità locali
            // rimovibili, ottiche e RAM restano locali.
            return tipo is DriveType.NoRootDirectory or DriveType.Unknown ? new DatiUnitaRete(null) : null;
        }
        catch (Exception)
        {
            return new DatiUnitaRete(null);
        }
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetGetConnectionW", ExactSpelling = true)]
    private static extern int WNetGetConnectionW(string lpLocalName, StringBuilder lpRemoteName, ref int lpnLength);

    private static string? PercorsoRemotoWindows(string lettera)
    {
        try
        {
            var lunghezza = 512;
            var buffer = new StringBuilder(lunghezza);
            var esito = WNetGetConnectionW(lettera, buffer, ref lunghezza);
            if (esito == 234) // ERROR_MORE_DATA: il buffer non basta, riprova con la lunghezza richiesta.
            {
                buffer = new StringBuilder(lunghezza);
                esito = WNetGetConnectionW(lettera, buffer, ref lunghezza);
            }
            return esito == 0 ? buffer.ToString().TrimEnd('\0') : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Timeout delle prove di I/O su cartella condivisa (6.6.1, decisione 2): per OneDrive/SharePoint e cartelle locali restano
/// i valori storici (3 s); per una cartella di rete raggiunta in LAN o VPN sono più ampi, così una latenza normale non produce
/// falsi "Incerta" o "non disponibile". Valori centralizzati qui: nessun altro punto ne ha di propri.
/// </summary>
public static class TimeoutCondivisione
{
    public static readonly TimeSpan CartellaSincronizzata = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan CartellaRete = TimeSpan.FromSeconds(12);

    /// <summary>Timeout per un percorso sulla cartella condivisa, secondo la modalità effettiva di questo PC.</summary>
    public static TimeSpan Per(string? percorso) =>
        ModalitaCondivisioneService.ERete(percorso) ? CartellaRete : CartellaSincronizzata;

    public static TimeSpan PerModalita(ModalitaCondivisione modalita) =>
        modalita == ModalitaCondivisione.CartellaRete ? CartellaRete : CartellaSincronizzata;
}
