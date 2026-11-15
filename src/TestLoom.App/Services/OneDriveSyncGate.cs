using System.Collections.Concurrent;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Blocco di scrittura IN MEMORIA, per radice, attivo quando all'avvio OneDrive non è risultato allineato: l'app resta
/// utilizzabile in sola lettura per non scrivere su dati non aggiornati. Come <see cref="SharedWriteLossMonitor"/> vive solo in
/// memoria e non è mai la modalità scelta dall'utente (visualizzatore locale, <see cref="WorkspaceReadOnlyService"/>): sparisce
/// al riavvio, che ripete la verifica, o con "Riprova" quando la sincronizzazione riesce. Non scrive mai nella cartella condivisa.
/// È consultato da <see cref="AppState.StatoCondivisione"/> e da <see cref="WorkspaceWritePolicy"/> (fail-closed).
/// </summary>
public static class OneDriveSyncGate
{
    private sealed record Blocco(string Motivo, DateTime Dal);

    private static readonly ConcurrentDictionary<string, Blocco> Blocchi = new(StringComparer.OrdinalIgnoreCase);

    private static string? Chiave(string? radice)
    {
        if (string.IsNullOrWhiteSpace(radice)) return null;
        try { return Path.GetFullPath(radice).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception) { return null; }
    }

    /// <summary>Attiva il blocco per la radice. <paramref name="motivo"/> è già localizzato.</summary>
    public static void Blocca(string? radice, string motivo)
    {
        var chiave = Chiave(radice);
        if (chiave == null) return;
        Blocchi[chiave] = new Blocco(motivo, DateTime.Now);
        DiagnosticTraceService.Registra("OneDrive.SolaLettura", $"radice={chiave}; motivo={motivo}");
    }

    public static void Sblocca(string? radice)
    {
        var chiave = Chiave(radice);
        if (chiave != null && Blocchi.TryRemove(chiave, out _))
            DiagnosticTraceService.Registra("OneDrive.SolaLettura", $"radice={chiave}; sbloccata");
    }

    public static bool Attivo(string? radice)
    {
        var chiave = Chiave(radice);
        return chiave != null && Blocchi.ContainsKey(chiave);
    }

    public static string? Motivo(string? radice)
    {
        var chiave = Chiave(radice);
        return chiave != null && Blocchi.TryGetValue(chiave, out var b) ? b.Motivo : null;
    }

    /// <summary>Esito di sola lettura da mostrare mentre il blocco è attivo; null se non c'è.</summary>
    internal static EsitoStatoCondivisione? Esito(string? radice)
    {
        var chiave = Chiave(radice);
        if (chiave == null || !Blocchi.TryGetValue(chiave, out var b)) return null;
        return new EsitoStatoCondivisione { Stato = StatoCondivisione.SolaLettura, Messaggio = b.Motivo, VerificatoIl = b.Dal };
    }

    /// <summary>Solo test: toglie ogni blocco.</summary>
    internal static void AzzeraTutto() => Blocchi.Clear();
}
