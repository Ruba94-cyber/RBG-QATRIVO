namespace TestLoom.App.Services;

/// <summary>
/// Ultimo guard fail-closed davanti alle scritture condivise. La UI normalmente blocca già i comandi quando
/// AppState è in sola lettura, ma repository/servizi non devono fidarsi della sola UI: il workspace può
/// diventare incompatibile/offline tra apertura editor e Save, oppure un percorso interno può invocarli
/// direttamente. Il controllo è deliberatamente NON mutante: niente probe e niente aggiornamento manifest.
/// </summary>
public static class WorkspaceWritePolicy
{
    /// <summary>
    /// Gate di scrittura. Il controllo tocca la cartella condivisa: ha il tetto di tempo della modalità (6.6.1, correzione D).
    /// Scaduto il tetto la scrittura è negata (fail-closed), e le eccezioni del gate arrivano intatte al chiamante.
    /// </summary>
    public static void EnsureCanWrite(DataPaths paths, string operation)
    {
        // Decisione B: per OneDrive/locale il gate resta quello storico (le sue letture sono comunque sonde con tetto di tempo).
        if (!ModalitaCondivisioneService.ERete(paths.Root))
        {
            EnsureCanWriteCore(paths, operation);
            return;
        }
        IoTimeoutGuard.EseguiPropagando(
            () => { EnsureCanWriteCore(paths, operation); return true; },
            TimeoutCondivisione.Per(paths.Root),
            () => throw new IOException($"{operation} bloccata: la cartella condivisa non risponde entro il tempo previsto. Nessuna modifica è stata scritta."));
    }

    private static void EnsureCanWriteCore(DataPaths paths, string operation)
    {
        if (WorkspaceReadOnlyService.IsEnabled(paths.Root))
            throw new IOException($"{operation} bloccata: modalità visualizzatore locale attiva. " +
                "Conserva una bozza locale delle modifiche in corso.");
        // 6.6.1 (d): una scrittura sulla cartella condivisa è già fallita in questa sessione. Il gate resta chiuso anche se
        // la UI non ha ancora aggiornato lo stato; il giro periodico riesegue la prova e lo riapre quando la cartella torna.
        var perdita = SharedWriteLossMonitor.MotivoPerdita(paths.Root);
        if (perdita != null)
            throw new IOException($"{operation} bloccata: scrittura sulla cartella condivisa non disponibile ({perdita}). " +
                "Le modifiche non ancora salvate restano nell'editor; dove l'editor lo supporta, vengono conservate come bozze locali di salvataggio non riuscito.");
        var availability = CondivisioneHealthService.VerificaDisponibilitaSenzaScrivere(paths.Root);
        var compatibility = CompatibilitaVersioneService.VerificaSenzaScrivere(paths, availability);
        if (compatibility.Stato != Models.StatoCondivisione.Operativa)
            throw new IOException($"{operation} bloccata: il workspace non è scrivibile in sicurezza. {compatibility.Messaggio}");

        EnsurePhaseDBootstrapBoundary(paths, operation);

        // Anche un workspace disponibile e compatibile non è scrivibile mentre un altro client sta applicando
        // un commit logico multi-file. La transazione corrente viene ignorata tramite AsyncLocal/ActiveId.
        SharedWorkflowTransactionGuard.EnsureNoBlockingTransaction(paths, operation);
    }

    /// <summary>Come <see cref="EnsureCanWrite"/>: lettura con tetto di tempo, scaduto il tetto non è scrivibile.</summary>
    public static bool CanWrite(DataPaths paths) =>
        !ModalitaCondivisioneService.ERete(paths.Root)
            ? CanWriteCore(paths)   // decisione B: OneDrive/locale invariato
            : IoTimeoutGuard.EseguiPropagando(() => CanWriteCore(paths), TimeoutCondivisione.Per(paths.Root), () => false);

    private static bool CanWriteCore(DataPaths paths)
    {
        try
        {
            if (WorkspaceReadOnlyService.IsEnabled(paths.Root)) return false;
            if (SharedWriteLossMonitor.MotivoPerdita(paths.Root) != null) return false;
            var availability = CondivisioneHealthService.VerificaDisponibilitaSenzaScrivere(paths.Root);
            if (CompatibilitaVersioneService.VerificaSenzaScrivere(paths, availability).Stato != Models.StatoCondivisione.Operativa)
                return false;

            // Un manifest master-v2 senza ready.json è uno stato di migrazione/recovery, non un normale
            // workspace legacy. La UI deve quindi risultare non scrivibile anche se i vecchi JSON esistono.
            if (PhaseDManifestExistsButIsNotReady(paths.Root)) return false;
            return SharedWorkflowTransactionGuard.CanReadConsistently(paths, out _);
        }
        catch { return false; }
    }

    private static void EnsurePhaseDBootstrapBoundary(DataPaths paths, string operation)
    {
        if (!PhaseDManifestExistsButIsNotReady(paths.Root)) return;

        // EnsureReady deve poter recuperare un bootstrap interrotto quando il manifest e' gia presente ma il
        // ready non lo e'. Questa e' l'unica porta esterna ammessa in tale stato: nessun normale repository
        // puo ricadere silenziosamente sui monoliti legacy.
        if (operation.Equals("Bootstrap master Phase D", StringComparison.Ordinal)) return;

        // Dopo AcquireLease il bootstrap usa gli stessi writer Phase D delle mutation normali. Li autorizziamo
        // soltanto se il lease condiviso appartiene davvero a QUESTO device; un Append invocato per errore fuori
        // dal bootstrap, oppure un secondo PC che vede il manifest parziale, resta fail-closed.
        if (IsPhaseDInternalBootstrapOperation(operation) && HasOwnedPhaseDBootstrapLease(paths.Root)) return;

        throw new IOException(
            $"{operation} bloccata: migrazione master Phase D incompleta (manifest presente, ready.json assente). " +
            "È consentito soltanto completare/riprendere il bootstrap in sicurezza.");
    }

    private static bool PhaseDManifestExistsButIsNotReady(string root) =>
        File.Exists(PhaseDMasterJournalService.ManifestPath(root)) && !PhaseDMasterBootstrapService.IsReady(root);

    private static bool IsPhaseDInternalBootstrapOperation(string operation) =>
        operation.Equals("Inizializzazione master Phase D", StringComparison.Ordinal) ||
        operation.Equals("Pubblicazione mutation master Phase D", StringComparison.Ordinal);

    private static bool HasOwnedPhaseDBootstrapLease(string root)
    {
        try
        {
            var path = PhaseDMasterBootstrapService.BootstrapLeasePath(root);
            if (!File.Exists(path)) return false;
            var lease = JsonIo.TryRead<PhaseDBootstrapLease>(path, out _);
            if (lease == null || lease.SchemaVersion != 1 || lease.LeaseId == Guid.Empty ||
                string.IsNullOrWhiteSpace(lease.DeviceId)) return false;
            return lease.DeviceId.Equals(DeviceIdentityService.GetOrCreate(), StringComparison.Ordinal);
        }
        catch { return false; }
    }
}
