namespace TestLoom.App.Services;

/// <summary>
/// Fotografia DIAGNOSTICA e NON MUTANTE della replica locale TestLoom. Legge solo
/// nomi, attributi e metadati dei file; non idrata allegati Files On-Demand,
/// non crea indici/probe, non scrive manifest e non contatta Microsoft Graph.
/// I contatori NON dimostrano che OneDrive abbia completato la sincronizzazione.
/// </summary>
public static class WorkspaceDiagnosticSnapshotService
{
    /// <summary>
    /// Righe iniziali del Centro Integrità (6.7.1): cartella dati in uso e modalità cartella condivisa. Stesse fonti del file di
    /// esportazione diagnostica (DiagnosticExportService): ModalitaCondivisioneService.Effettiva e OverrideRadice.
    /// </summary>
    public static IReadOnlyList<string> IntestazioneCartella(string? sharedRoot)
    {
        var loc = LocalizationService.Instance;
        var cartella = string.IsNullOrWhiteSpace(sharedRoot) ? "-" : sharedRoot;
        var effettiva = ModalitaCondivisioneService.Effettiva(sharedRoot) == ModalitaCondivisione.CartellaRete
            ? loc.Traduci("ModalitaCondivisioneRete")
            : loc.Traduci("ModalitaCondivisioneOneDrive");
        var scelta = ModalitaCondivisioneService.OverrideRadice(sharedRoot) switch
        {
            ModalitaCondivisioneService.ValoreRete => loc.Traduci("ModalitaCondivisioneRete"),
            ModalitaCondivisioneService.ValoreOneDrive => loc.Traduci("ModalitaCondivisioneOneDrive"),
            _ => loc.Traduci("ModalitaCondivisioneAutomatica"),
        };
        return new[]
        {
            string.Format(loc.Traduci("WsDiagCartellaInUsoFmt"), cartella),
            string.Format(loc.Traduci("WsDiagModalitaFmt"), effettiva),
            string.Format(loc.Traduci("WsDiagModalitaSceltaFmt"), scelta),
        };
    }

    public sealed record Snapshot(
        bool Available,
        bool ViewerMode,
        int OperationalFiles,
        int OfflineAttributeFiles,
        int Attachments,
        int OfflineAttributeAttachments,
        int LocalDrafts,
        int LocalSnapshots,
        DateTime? LastSnapshotUtc,
        bool PhaseDManifestPresent,
        bool PhaseDReadyPresent,
        bool? ExternalDestinationPresent,
        IReadOnlyList<string> Warnings)
    {
        public string Format()
        {
            var loc = LocalizationService.Instance;
            string T(string chiave) => loc.Traduci(chiave);
            var lines = new List<string>
            {
                T("WsDiagTitolo"),
                string.Format(T("WsDiagCartellaOperativaFmt"), Available ? T("WsDiagAccessibile") : T("WsDiagNonDisponibileBreve")),
                string.Format(T("WsDiagModalitaVisualizzatoreFmt"), ViewerMode ? T("WsDiagAttiva") : T("WsDiagNonAttiva")),
                string.Format(T("WsDiagOperativiFmt"), OperationalFiles),
                string.Format(T("WsDiagOfflineFmt"), OfflineAttributeFiles),
                string.Format(T("WsDiagAllegatiFmt"), Attachments),
                string.Format(T("WsDiagAllegatiOfflineFmt"), OfflineAttributeAttachments),
                string.Format(T("WsDiagBozzeFmt"), LocalDrafts),
                string.Format(T("WsDiagSnapshotZipFmt"), LocalSnapshots),
                string.Format(T("WsDiagUltimoZipFmt"), LastSnapshotUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? T("WsDiagNonDisponibileBreve")),
                string.Format(T("WsDiagPhaseDManifestFmt"), PhaseDManifestPresent ? T("WsDiagPresente") : T("WsDiagNonPresente")),
                string.Format(T("WsDiagPhaseDReadyFmt"), PhaseDReadyPresent ? T("WsDiagPresente") : T("WsDiagNonPresente")),
                string.Format(T("WsDiagBackupAggiuntivoFmt"), ExternalDestinationPresent switch
                {
                    true => T("WsDiagExtRaggiungibile"),
                    false => T("WsDiagExtAssente"),
                    _ => T("WsDiagExtNonConfigurata")
                }),
                "",
                T("WsDiagAttenzione"),
                T("WsDiagNonModificato")
            };
            if (Warnings.Count > 0)
            {
                lines.Add("");
                lines.Add(T("WsDiagAvvisiTitolo"));
                lines.AddRange(Warnings.Take(25).Select(w => "• " + w));
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    public static Snapshot Collect(string sharedRoot, string? externalBackupDirectory = null,
        string? localBackupBaseDirectory = null, CancellationToken cancellationToken = default)
    {
        var loc = LocalizationService.Instance;
        var available = !string.IsNullOrWhiteSpace(sharedRoot) && Directory.Exists(sharedRoot);
        if (!available)
            return new(false, false, 0, 0, 0, 0, 0, 0, null, false, false,
                null, new[] { loc.Traduci("WsDiagAvvNonRaggiungibile") });

        var root = Path.GetFullPath(sharedRoot);
        var paths = new DataPaths(root);
        var warnings = new List<string>();
        var dataCount = 0;
        var offlineCount = 0;
        var attachments = 0;
        var attachmentsOffline = 0;

        void Count(string file, bool attachment)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attachment) attachments++;
            else dataCount++;
            try
            {
                if ((File.GetAttributes(file) & FileAttributes.Offline) != 0)
                {
                    if (attachment) attachmentsOffline++;
                    else offlineCount++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var warning = loc.Traduci("WsDiagAvvAttributiIllegibili");
                if (!warnings.Contains(warning)) warnings.Add(warning);
            }
        }

        bool IsAttachmentSegment(string file) =>
            Path.GetRelativePath(root, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("allegati", StringComparison.OrdinalIgnoreCase) ||
                                segment.Equals("attachments", StringComparison.OrdinalIgnoreCase));

        void Enumerate(string directory, string pattern, Func<string, bool> isManaged, bool attachment)
        {
            if (!Directory.Exists(directory)) return;
            try
            {
                var folders = new Stack<string>();
                folders.Push(directory);
                while (folders.Count != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = folders.Pop();
                    foreach (var file in Directory.EnumerateFiles(current, pattern, SearchOption.TopDirectoryOnly))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (attachment || (!IsAttachmentSegment(file) && isManaged(file)))
                            Count(file, attachment);
                    }
                    foreach (var child in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
                    {
                        var name = Path.GetFileName(child);
                        if (!attachment && (name.Equals("allegati", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("attachments", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("backup", StringComparison.OrdinalIgnoreCase))) continue;
                        if (new DirectoryInfo(child).LinkTarget != null) continue;
                        folders.Push(child);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add(loc.Traduci("WsDiagAvvEnumerabiliParziali"));
            }
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (TestLoomStorageOwnership.IsManagedJsonPath(root, file) ||
                    name.Equals("accesso-config.json", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("audit-valutazioni-requisito-componente-veicolo.json", StringComparison.OrdinalIgnoreCase))
                    Count(file, false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add(loc.Traduci("WsDiagAvvRadiceNonEnumerabile"));
        }

        foreach (var (folder, pattern) in new (string Folder, string Pattern)[]
        {
            ("testcases", "testcase_*.json"), ("nonconformita", "nc_*.json"),
            ("change-requests", "cr_*.json"), ("segnalazioni", "segnalazione_*.json"),
            ("cestino", "testcase_*.json"), ("notifiche", "notifica_*.json"),
            ("recovery-bin", "recovery_*.json"), ("_ditm", "*.json"), ("_ditm", "*.ndjson")
        })
        {
            Enumerate(Path.Combine(root, folder), pattern,
                f => folder == "_ditm" || TestLoomStorageOwnership.IsManagedJsonPath(root, f), false);
        }
        Enumerate(paths.AllegatiRootDir, "*", _ => true, true);

        int CountFiles(string dir, string pattern) =>
            Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).Count() : 0;

        var draftCount = 0;
        var backupCount = 0;
        DateTime? latest = null;
        try
        {
            draftCount = CountFiles(FailedSaveDraftService.LocalDirectory(root), "*.json");
            var zipDir = WorkspaceLocalPaths.GetBackupDirectory(root, localBackupBaseDirectory);
            if (Directory.Exists(zipDir))
            {
                foreach (var file in Directory.EnumerateFiles(zipDir, "TestLoom_snapshot_*.zip"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    backupCount++;
                    var t = File.GetLastWriteTimeUtc(file);
                    if (!latest.HasValue || t > latest.Value) latest = t;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add(loc.Traduci("WsDiagAvvBozzeBackupParziali"));
        }

        bool? external = null;
        if (!string.IsNullOrWhiteSpace(externalBackupDirectory))
        {
            try { external = Directory.Exists(externalBackupDirectory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                external = false;
            }
        }

        var manifest = File.Exists(PhaseDMasterJournalService.ManifestPath(root));
        var ready = PhaseDMasterBootstrapService.IsReady(root);
        if (manifest && !ready)
            warnings.Add(loc.Traduci("WsDiagAvvPhaseDNonPronto"));

        return new(true, WorkspaceReadOnlyService.IsEnabled(root),
            dataCount, offlineCount, attachments, attachmentsOffline, draftCount,
            backupCount, latest, manifest, ready, external, warnings.Distinct().ToArray());
    }
}
