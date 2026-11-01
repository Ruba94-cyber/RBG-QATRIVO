namespace TestLoom.Core;

/// <summary>Appends entries to the workspace audit log, dropping the oldest ones beyond the cap.</summary>
public static class WorkspaceAudit
{
    public static void Record(WorkspaceDocument workspace, string? actor, string action, string subject, string detail = "")
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.Audit.Add(new WorkspaceAuditEntry
        {
            Actor = Clip(string.IsNullOrWhiteSpace(actor) ? workspace.DefaultTester : actor, 120),
            Action = Clip(action, 60), Subject = Clip(subject, 250), Detail = Clip(detail, 2000)
        });
        var excess = workspace.Audit.Count - WorkspaceStore.MaxAuditEntries;
        if (excess > 0) workspace.Audit.RemoveRange(0, excess);
    }

    private static string Clip(string? value, int max)
    {
        value ??= "";
        return value.Length <= max ? value : value[..max];
    }
}
