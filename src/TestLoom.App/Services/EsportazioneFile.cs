namespace TestLoom.App.Services;

/// <summary>Scrittura atomica dei file esportati (Excel, PDF, CSV, ZIP). Il contenuto va prima in un file temporaneo
/// nella stessa cartella della destinazione e solo a scrittura completata viene spostato sul percorso scelto. In caso
/// di annullamento o errore il temporaneo viene eliminato e la destinazione non viene toccata: non resta mai un export
/// parziale sul disco.</summary>
public static class EsportazioneFile
{
    /// <summary>Esegue <paramref name="scrivi"/> sul percorso temporaneo e poi sposta il file su <paramref name="percorso"/>.
    /// Se <paramref name="ct"/> viene annullato prima dello spostamento, o <paramref name="scrivi"/> lancia, il temporaneo
    /// viene rimosso e l'eccezione risale al chiamante.</summary>
    public static void ScriviAtomico(string percorso, Action<string> scrivi, CancellationToken ct = default)
        => ScriviAtomico(percorso, scrivi, ct, primaDelCommit: null);

    public static void ScriviAtomico(string percorso, Action<string> scrivi, CancellationToken ct, Action? primaDelCommit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(percorso);
        ArgumentNullException.ThrowIfNull(scrivi);
        ct.ThrowIfCancellationRequested();
        var completo = Path.GetFullPath(percorso);
        var cartella = Path.GetDirectoryName(completo)
            ?? throw new ArgumentException("Il percorso di esportazione non ha una cartella.", nameof(percorso));
        Directory.CreateDirectory(cartella);
        var temporaneo = Path.Combine(cartella, $".{Path.GetFileName(completo)}.{Guid.NewGuid():N}.tmp");
        try
        {
            scrivi(temporaneo);
            ct.ThrowIfCancellationRequested();
            primaDelCommit?.Invoke();
            ct.ThrowIfCancellationRequested();
            File.Move(temporaneo, completo, overwrite: true);
        }
        finally
        {
            RimuoviSeEsiste(temporaneo);
        }
    }

    /// <summary>Come <see cref="ScriviAtomico"/>, per un contenuto gia in memoria.</summary>
    public static void ScriviBytesAtomico(string percorso, byte[] contenuto, CancellationToken ct = default) =>
        ScriviAtomico(percorso, temporaneo => File.WriteAllBytes(temporaneo, contenuto), ct);

    /// <summary>Enumera <paramref name="elementi"/> controllando l'annullamento prima di ciascun elemento: da usare nei
    /// cicli sulle righe degli export, cosi un annullamento si ferma entro una riga.</summary>
    public static IEnumerable<T> ConAnnullamento<T>(this IEnumerable<T> elementi, CancellationToken ct)
    {
        foreach (var elemento in elementi)
        {
            ct.ThrowIfCancellationRequested();
            yield return elemento;
        }
    }

    private static void RimuoviSeEsiste(string percorso)
    {
        try { if (File.Exists(percorso)) File.Delete(percorso); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
