using System.Text.Json;

namespace TestLoom.App.Services;

/// <summary>
/// Barriera cross-PC per i commit logici multi-file. WorkflowTransactionService pubblica un marker sotto
/// _ditm/transactions prima della prima mutazione e lo rimuove soltanto dopo commit/rollback. Finché quel
/// marker è visibile, un altro client non deve né scrivere né ricaricare i JSON di dominio: altrimenti può
/// osservare il breve stato intermedio Test aggiornato / NC-CR-Segnalazioni ancora vecchie.
///
/// Lease (review P2.4): il proprietario rinnova il marker con un battito (HeartbeatUtc). Un marker di un altro PC
/// il cui battito è scaduto viene sbloccato SOLO dopo aver verificato il ledger del proprietario: ricevuta presente
/// = commit avvenuto e marker residuo rimosso; ricevuta assente o ledger non verificabile = il marker resta
/// bloccante. Le before-image sono locali al PC proprietario: senza la sua recovery non è sicuro proseguire su uno
/// stato forse incompleto. I marker delle versioni precedenti (senza lease) restano sempre bloccanti.
/// </summary>
public static class SharedWorkflowTransactionGuard
{
    /// <summary>
    /// Backoff brevi (stessi valori di JsonIo) usati prima di dichiarare un marker illeggibile. Su cartella
    /// condivisa il client di sincronizzazione puo' tenere un handle sul .pending.json per frazioni di
    /// secondo: senza retry quella contesa transitoria congelava l'intero workspace fino al giro di refresh
    /// successivo, e potenzialmente all'infinito se la contesa si ripresentava ogni volta.
    /// </summary>
    private static readonly int[] MarkerReadRetryDelaysMs = [75, 225];

    public static bool CanReadConsistently(DataPaths paths, out string message)
    {
        // Lettura: un marker scaduto ma confermato dal ledger non blocca, però il marker non viene rimosso qui.
        var blocking = FindBlocking(paths, WorkflowTransactionService.ActiveId, rimuoviScaduti: false);
        message = blocking.Message;
        return !blocking.Blocked;
    }

    public static void EnsureNoBlockingTransaction(DataPaths paths, string operation, Guid? ignoreId = null)
    {
        var blocking = FindBlocking(paths, ignoreId ?? WorkflowTransactionService.ActiveId, rimuoviScaduti: true);
        if (!blocking.Blocked) return;
        throw new IOException($"{operation} bloccata: {blocking.Message}");
    }

    /// <summary>
    /// Recovery is allowed to ignore its own marker only after the owner's lease has expired. A second
    /// process can encounter the same transaction directory while the owner is still applying writes;
    /// process-local active-transaction state cannot identify that case.
    /// </summary>
    public static void EnsureRecoveryMarkerNotLive(DataPaths paths, Guid transactionId, string? transactionDeviceId)
    {
        var markerPath = Path.Combine(paths.Root, "_ditm", "transactions", $"{transactionId:N}.pending.json");
        var lettura = ReadMarker(markerPath);
        if (lettura.Vanished) return;
        if (!lettura.Ok)
            throw new IOException($"Recovery transazione workflow bloccata: {lettura.Message}");

        var marker = lettura.Marker;
        if (marker == null || marker.Id != transactionId)
            throw new IOException("Recovery transazione workflow bloccata: il marker condiviso non corrisponde alla transazione da recuperare.");

        var identityLease = marker.HeartbeatUtc.HasValue || marker.LeaseSeconds.HasValue;
        if (transactionDeviceId != null && identityLease && string.IsNullOrWhiteSpace(marker.DeviceId))
            throw new IOException("Recovery transazione workflow bloccata: identità del marker condiviso incompleta.");
        if (transactionDeviceId != null && !string.IsNullOrWhiteSpace(marker.DeviceId) &&
            !marker.DeviceId.Equals(transactionDeviceId, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Recovery transazione workflow bloccata: il marker appartiene a un dispositivo diverso dal manifest.");

        if (MarkerScaduto(marker)) return;

        var device = string.IsNullOrWhiteSpace(marker.Device) ? "device sconosciuto" : marker.Device;
        var description = string.IsNullOrWhiteSpace(marker.Description) ? "salvataggio workflow" : marker.Description;
        throw new IOException(
            $"Recovery transazione workflow bloccata: il marker della transazione è ancora attivo (device {device}, " +
            $"avviato {marker.StartedUtc.ToLocalTime():g}, {description}). Nessun dato viene ripristinato finché il proprietario non completa il commit.");
    }

    private static BarrierResult FindBlocking(DataPaths paths, Guid? ignoreId, bool rimuoviScaduti)
    {
        try
        {
            var dir = Path.Combine(paths.Root, "_ditm", "transactions");
            if (!Directory.Exists(dir)) return BarrierResult.Clear;

            var ignoreName = ignoreId.HasValue ? $"{ignoreId.Value:N}.pending.json" : null;
            foreach (var file in Directory.EnumerateFiles(dir, "*.pending.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (ignoreName != null && Path.GetFileName(file).Equals(ignoreName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var lettura = ReadMarker(file);
                if (lettura.Vanished)
                {
                    // Il marker è sparito tra l'enumerazione e la lettura: il commit/recovery proprietario
                    // lo ha rimosso. Nessun marker = nessuna barriera, non è una condizione di errore.
                    continue;
                }
                if (!lettura.Ok) return new BarrierResult(true, lettura.Message);
                var marker = lettura.Marker;

                if (marker == null || marker.Id == Guid.Empty)
                {
                    return new BarrierResult(true,
                        $"è presente un marker di transazione TestLoom non valido ({Path.GetFileName(file)}). " +
                        "Per sicurezza il workspace resta congelato finché la recovery non viene completata.");
                }

                if (ignoreId.HasValue && marker.Id == ignoreId.Value) continue;

                if (MarkerScaduto(marker))
                {
                    var esito = WorkflowCommitLedgerService.VerificaRicevuta(paths, marker.DeviceId, marker.Id, out var dettaglio);
                    if (esito == EsitoRicevutaCommit.Committed)
                    {
                        // Il proprietario ha pubblicato la ricevuta ed è caduto prima di rimuovere il marker: il commit è avvenuto e il
                        // marker è solo residuo. La rimozione avviene nei percorsi di scrittura, non in quelli di sola lettura.
                        if (!rimuoviScaduti) continue;
                        try
                        {
                            File.Delete(file);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            CrashLogService.Registra($"Rimozione marker scaduto {Path.GetFileName(file)}", ex);
                            return new BarrierResult(true,
                                MessaggioMarkerScaduto(marker, LocalizationService.Instance.Traduci("TxMarkerNonRimovibile")));
                        }
                    }

                    if (esito == EsitoRicevutaCommit.AssenteCertificata &&
                        !WorkflowTransactionService.HasPublishedTransaction(paths.Root, marker.Id) &&
                        !string.IsNullOrWhiteSpace(marker.DeviceId) &&
                        marker.DeviceId.Equals(DeviceIdentityService.GetOrCreate(), StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            // Only this device can prove that a marker without a published tx-* directory is
                            // its abandoned, pre-mutation prepare. Remote before-images are not available here.
                            if (!WorkflowTransactionService.CleanupUnpublishedPreparation(
                                    paths, marker.Id, marker.DeviceId, delete: rimuoviScaduti))
                                throw new InvalidDataException("La preparazione locale non è presente; l'assenza di una directory tx-* non prova che il proprietario remoto non abbia scritto dati.");
                            if (rimuoviScaduti) File.Delete(file);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            return new BarrierResult(true,
                                $"il marker scaduto {Path.GetFileName(file)} non può essere verificato come preparazione senza scritture: {ex.Message}");
                        }
                    }

                    var motivo = esito == EsitoRicevutaCommit.AssenteCertificata
                        ? LocalizationService.Instance.Traduci("TxMotivoRicevutaAssente")
                        : dettaglio ?? LocalizationService.Instance.Traduci("TxLedgerIncoerente");
                    return new BarrierResult(true, MessaggioMarkerScaduto(marker, motivo));
                }

                var device = string.IsNullOrWhiteSpace(marker.Device) ? "device sconosciuto" : marker.Device;
                var description = string.IsNullOrWhiteSpace(marker.Description) ? "salvataggio workflow" : marker.Description;
                return new BarrierResult(true,
                    $"un altro commit TestLoom è in corso o richiede recovery (device {device}, avviato {marker.StartedUtc.ToLocalTime():g}, {description}). " +
                    "Nessun dato viene ricaricato o scritto finché il marker non viene rimosso dal commit/recovery proprietario.");
            }

            return BarrierResult.Clear;
        }
        catch (Exception ex)
        {
            // Anche non riuscire a verificare la directory dei marker è una condizione di safety, non un
            // permesso implicito a procedere.
            return new BarrierResult(true,
                $"non è stato possibile verificare le transazioni TestLoom condivise: {ex.Message}");
        }
    }

    /// <summary>
    /// Un marker con lease è scaduto quando il suo ultimo battito è più vecchio della durata dichiarata. Un marker senza lease
    /// (versioni precedenti) non scade mai e resta bloccante, come prima.
    /// </summary>
    private static bool MarkerScaduto(WorkflowTransactionMarker marker) =>
        marker.HeartbeatUtc is DateTime battito && marker.LeaseSeconds is int durata &&
        DateTime.UtcNow - battito > TimeSpan.FromSeconds(durata);

    private static string MessaggioMarkerScaduto(WorkflowTransactionMarker marker, string motivo)
    {
        var device = string.IsNullOrWhiteSpace(marker.Device) ? "sconosciuto" : marker.Device;
        var ultimoSegno = marker.HeartbeatUtc is DateTime battito ? battito.ToLocalTime().ToString("g") : "sconosciuto";
        return string.Format(LocalizationService.Instance.Traduci("TxMarkerScadutoFmt"), device, ultimoSegno, motivo);
    }

    /// <summary>
    /// Legge il marker con lettura condivisa e retry brevi, distinguendo tre esiti che prima collassavano
    /// tutti su "marker illeggibile -> workspace congelato":
    ///  - marker scomparso durante la lettura (commit/recovery completato): NON blocca;
    ///  - contesa di I/O che persiste oltre la finestra di retry: blocca, ma il messaggio dice che si tratta
    ///    di contesa sul file e non di un danno, così l'utente non va a caccia di una corruzione inesistente;
    ///  - contenuto non deserializzabile anche dopo i retry: blocca come prima, marker realmente corrotto.
    /// La semantica fail-closed non cambia: in caso di dubbio si blocca sempre, non si procede mai.
    /// </summary>
    private static MarkerReadResult ReadMarker(string file)
    {
        Exception? ultimaEccezione = null;
        var contesaIo = false;

        for (var tentativo = 0; tentativo <= MarkerReadRetryDelaysMs.Length; tentativo++)
        {
            try
            {
                // ReadAllBytesShared (FileShare.ReadWrite | Delete): un handle del client di sincronizzazione
                // non deve piu' produrre da solo una sharing violation.
                var bytes = JsonIo.ReadAllBytesShared(file);
                return MarkerReadResult.Letto(
                    JsonSerializer.Deserialize<WorkflowTransactionMarker>(bytes, JsonIo.Options));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return MarkerReadResult.Scomparso;
            }
            catch (Exception ex)
            {
                ultimaEccezione = ex;
                // Una consegna parziale puo' dare anche JsonException su byte troncati: entrambi i casi
                // meritano un altro tentativo, è la classificazione finale a distinguerli.
                contesaIo = ex is IOException or UnauthorizedAccessException;
            }

            if (tentativo >= MarkerReadRetryDelaysMs.Length) break;
            Thread.Sleep(MarkerReadRetryDelaysMs[tentativo]);
        }

        if (!File.Exists(file)) return MarkerReadResult.Scomparso;

        var nome = Path.GetFileName(file);
        var dettaglio = ultimaEccezione?.Message ?? "causa sconosciuta";
        return contesaIo
            ? MarkerReadResult.Bloccante(
                $"il marker di transazione TestLoom {nome} resta non leggibile per contesa di I/O anche dopo i tentativi ravvicinati " +
                $"(file aperto da un altro processo o dal client di sincronizzazione). Il marker non risulta danneggiato: " +
                $"il workspace torna operativo da solo appena il file si libera. Dettaglio: {dettaglio}")
            : MarkerReadResult.Bloccante(
                $"è presente un marker di transazione TestLoom illeggibile ({nome}). " +
                $"Per sicurezza il workspace resta congelato finché il PC che ha iniziato il commit non completa la recovery. Dettaglio: {dettaglio}");
    }

    private readonly record struct MarkerReadResult(
        WorkflowTransactionMarker? Marker,
        bool Ok,
        bool Vanished,
        string Message)
    {
        public static MarkerReadResult Letto(WorkflowTransactionMarker? marker) => new(marker, true, false, "");
        public static MarkerReadResult Scomparso => new(null, false, true, "");
        public static MarkerReadResult Bloccante(string message) => new(null, false, false, message);
    }

    private readonly record struct BarrierResult(bool Blocked, string Message)
    {
        public static BarrierResult Clear => new(false, "");
    }
}
