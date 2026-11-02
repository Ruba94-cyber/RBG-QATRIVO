namespace TestLoom.App.Services;

/// <summary>
/// Collega le write JSON già tracciate all'esito finale della WorkflowTransaction senza introdurre dipendenze
/// nel motore transazionale. I callback esistenti vengono eseguiti solo dopo commit realmente completato oppure
/// dopo rollback completato; una recovery incompleta non viene quindi falsamente marcata come rolled-back.
/// La diagnostica è sempre best-effort: nessun errore di trace può cambiare l'esito di una write applicativa.
/// </summary>
public static class PilotTransactionOutcomeTraceService
{
    public static void RegisterForSuccessfulWrite(string path, ReadOnlySpan<byte> candidateBytes)
    {
        if (!PilotConcurrencyTraceService.IsEnabled) return;
        try
        {
            var transactionId = WorkflowTransactionService.ActiveId;
            if (transactionId == null || transactionId == Guid.Empty) return;

            var workspaceRoot = TryLocateWorkspaceRoot(path);
            if (workspaceRoot == null) return;

            // Calcolato soltanto quando il pilot trace è attivo: nessun costo aggiuntivo sui benchmark normali.
            var candidateSha256 = WorkflowTransactionVisibilityService.Hash(candidateBytes);
            var id = transactionId.Value;
            var visibilityManaged = TestLoomStorageOwnership.IsVisibilityManagedDomainJsonPath(workspaceRoot, path);
            var detailCommit = visibilityManaged
                ? "visibility-managed; callback executes only after receipt publication and transaction commit"
                : "transaction side-effect; no visibility receipt required for this path";

            _ = WorkflowTransactionService.DeferAfterCommit(() =>
                PilotConcurrencyTraceService.Record(
                    workspaceRoot,
                    operation: "workflow-transaction",
                    stage: "finalize",
                    outcome: "committed",
                    path: path,
                    transactionId: id,
                    candidateAfterSha256: candidateSha256,
                    observedAfterSha256: PilotConcurrencyTraceService.TryHashFile(path),
                    detail: detailCommit));

            _ = WorkflowTransactionService.DeferAfterRollback(() =>
                PilotConcurrencyTraceService.Record(
                    workspaceRoot,
                    operation: "workflow-transaction",
                    stage: "finalize",
                    outcome: "rolled-back",
                    path: path,
                    transactionId: id,
                    candidateAfterSha256: candidateSha256,
                    observedAfterSha256: PilotConcurrencyTraceService.TryHashFile(path),
                    detail: "rollback callback executed after owned mutations were restored/removed"));
        }
        catch (Exception ex)
        {
            // Non usare mai il trace come motivo per far fallire una write/transaction reale.
            try { CrashLogService.Registra("Pilot transaction outcome trace", ex); }
            catch { }
        }
    }

    private static string? TryLocateWorkspaceRoot(string path)
    {
        try
        {
            var configured = Environment.GetEnvironmentVariable("TestLoom_PILOT_TRACE_WORKSPACE_ROOT");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var root = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var full = Path.GetFullPath(path);
                var prefix = root + Path.DirectorySeparatorChar;
                if (full.Equals(root, PathComparison) || full.StartsWith(prefix, PathComparison)) return root;
            }

            var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
            for (var depth = 0; directory != null && depth < 32; depth++, directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "version-manifest.json")) ||
                    Directory.Exists(Path.Combine(directory.FullName, "_ditm")))
                    return directory.FullName;
            }
        }
        catch { }
        return null;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
