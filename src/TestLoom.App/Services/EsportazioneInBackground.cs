namespace TestLoom.App.Services;

/// <summary>Esito di un'esportazione eseguita in background.</summary>
public enum StatoEsportazione { Completata, Annullata, Fallita }

/// <summary>Risultato di <see cref="EsportazioneInBackground.EseguiAsync"/>; <see cref="Errore"/> e valorizzato solo se
/// l'esportazione e fallita.</summary>
public sealed record EsitoEsportazione(StatoEsportazione Stato, Exception? Errore = null);

/// <summary>Esecuzione in background di un export su file con annullamento cooperativo. Generazione e scrittura girano
/// su un thread del pool (la UI resta reattiva); l'await ritorna solo a lavoro concluso, quindi il chiamante sa con
/// certezza se il file e stato scritto e non resta mai un export parziale (vedi <see cref="EsportazioneFile"/>).
/// Un solo export alla volta per istanza: il chiamante deve verificare <see cref="InCorso"/> prima di avviarne un altro.</summary>
public sealed class EsportazioneInBackground
{
    private CancellationTokenSource? _corrente;

    /// <summary>Vero mentre un export e in corso.</summary>
    public bool InCorso => _corrente != null;

    /// <summary>Chiede l'annullamento dell'export in corso; senza effetto se non ce n'e uno.</summary>
    public void Annulla() => _corrente?.Cancel();

    /// <summary>Esegue <paramref name="scrivi"/> (riceve il percorso temporaneo e il token) su un thread di background e
    /// sposta il risultato su <paramref name="percorso"/> solo a scrittura completa. Il token viene creato in modo
    /// sincrono, prima del primo await: un <see cref="Annulla"/> immediatamente successivo alla chiamata e gia efficace.</summary>
    public async Task<EsitoEsportazione> EseguiAsync(string percorso, Action<string, CancellationToken> scrivi)
        => await EseguiAsync(percorso, scrivi, primaDelCommit: null);

    public async Task<EsitoEsportazione> EseguiAsync(string percorso, Action<string, CancellationToken> scrivi, Action? primaDelCommit)
    {
        ArgumentNullException.ThrowIfNull(scrivi);
        if (_corrente != null)
            return new EsitoEsportazione(StatoEsportazione.Fallita, new InvalidOperationException("Esportazione gia in corso."));
        var controllo = new CancellationTokenSource();
        _corrente = controllo;
        try
        {
            // Nessun token passato a Task.Run: l'await aspetta che il lavoro si fermi davvero (temporaneo ripulito).
            // Una sola lettura logica dei master per l'intera esportazione (MasterLetturaScope e ThreadStatic: va aperto
            // sullo stesso thread che legge i dati).
            await Task.Run(() => EsportazioneFile.ScriviAtomico(percorso, temporaneo =>
            {
                using var letturaMaster = MasterLetturaScope.Apri();
                scrivi(temporaneo, controllo.Token);
            }, controllo.Token, primaDelCommit));
            return new EsitoEsportazione(StatoEsportazione.Completata);
        }
        // Con annullamento richiesto l'esito e sempre "Annullata": una composizione (es. QuestPDF) puo incapsulare
        // l'OperationCanceledException in un'altra eccezione.
        catch (Exception) when (controllo.IsCancellationRequested)
        {
            return new EsitoEsportazione(StatoEsportazione.Annullata);
        }
        // File di destinazione aperto altrove (di solito Excel): al posto del messaggio tecnico dell'OS, un testo che dice cosa fare.
        catch (Exception ex) when (EFileInUso(ex, percorso))
        {
            return new EsitoEsportazione(StatoEsportazione.Fallita,
                new IOException(LocalizationService.Instance.Traduci("EsportazioneFileInUsoMsg"), ex));
        }
        catch (Exception ex)
        {
            return new EsitoEsportazione(StatoEsportazione.Fallita, ex);
        }
        finally
        {
            _corrente = null;
        }
    }

    /// <summary>Destinazione tenuta aperta da un altro processo (di solito Excel). Windows risponde con una violazione di
    /// condivisione o di blocco (IOException 32/33), oppure con accesso negato su un file esistente e scrivibile: un file di
    /// sola lettura o una cartella senza permessi restano errori tecnici, perche' il rimedio e diverso.</summary>
    private static bool EFileInUso(Exception ex, string percorso) =>
        (ex is IOException io && (io.HResult & 0xFFFF) is 32 or 33)
        || (ex is UnauthorizedAccessException && File.Exists(percorso) && !new FileInfo(percorso).IsReadOnly);
}
