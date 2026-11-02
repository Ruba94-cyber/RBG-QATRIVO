using System.Diagnostics;

namespace TestLoom.App.Services;

internal sealed class LockLeaseObservationTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Osservazione> _osservazioni = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<long> _timestamp;
    private readonly int _capacitaMassima;

    internal LockLeaseObservationTracker(Func<long>? timestamp = null, int capacitaMassima = 4096)
    {
        if (capacitaMassima <= 0) throw new ArgumentOutOfRangeException(nameof(capacitaMassima));
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _capacitaMassima = capacitaMassima;
    }

    internal int Count
    {
        get { lock (_gate) return _osservazioni.Count; }
    }

    internal bool EScaduto(string percorso, string impronta, TimeSpan soglia)
    {
        var chiave = NormalizzaPercorso(percorso);
        var ora = _timestamp();
        lock (_gate)
        {
            if (_osservazioni.TryGetValue(chiave, out var precedente) &&
                string.Equals(precedente.Impronta, impronta, StringComparison.Ordinal))
            {
                _osservazioni[chiave] = precedente with { UltimaOsservazione = ora };
                return Stopwatch.GetElapsedTime(precedente.PrimaOsservazione, ora) > soglia;
            }

            if (_osservazioni.Count >= _capacitaMassima && !_osservazioni.ContainsKey(chiave))
            {
                // Evicting an observation restarts only that path's age on its next read. This may delay
                // stale-lock recovery under churn, but can never make an unobserved lock expire early.
                var menoRecente = _osservazioni.MinBy(pair => pair.Value.UltimaOsservazione).Key;
                _osservazioni.Remove(menoRecente);
            }
            _osservazioni[chiave] = new(impronta, ora, ora);
            return false;
        }
    }

    internal void Rimuovi(string percorso)
    {
        lock (_gate) _osservazioni.Remove(NormalizzaPercorso(percorso));
    }

    internal void RimuoviRadice(string radice)
    {
        var root = NormalizzaPercorso(radice).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = root + Path.DirectorySeparatorChar;
        lock (_gate)
        {
            foreach (var path in _osservazioni.Keys
                         .Where(path => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                                        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .ToList())
                _osservazioni.Remove(path);
        }
    }

    internal void Svuota()
    {
        lock (_gate) _osservazioni.Clear();
    }

    private static string NormalizzaPercorso(string percorso)
    {
        try { return Path.GetFullPath(percorso); }
        catch { return percorso; }
    }

    private sealed record Osservazione(string Impronta, long PrimaOsservazione, long UltimaOsservazione);
}
