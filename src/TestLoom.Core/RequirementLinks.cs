namespace TestLoom.Core;

/// <summary>
/// Domain-neutral requirement traceability edits. The relationship uses
/// stable GUIDs so renames do not drop test history or coverage.
/// </summary>
public static class RequirementLinks
{
    /// <returns>True when the relationship actually changes.</returns>
    public static bool SetLinked(
        WorkspaceDocument workspace, Guid testCaseId, Guid requirementId, bool linked)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.TestCases is null || workspace.Requirements is null)
            throw new InvalidDataException("The workspace has missing collections.");

        var test = workspace.TestCases.SingleOrDefault(x => x.Id == testCaseId)
            ?? throw new ArgumentException("Test case does not exist.", nameof(testCaseId));
        if (test.Archived)
            throw new InvalidOperationException("Archived tests cannot change requirement links.");
        if (!workspace.Requirements.Any(r => r.Id == requirementId))
            throw new ArgumentException("Requirement does not exist.", nameof(requirementId));
        if (test.RequirementIds is null)
            throw new InvalidDataException("Test case requirement links are missing.");

        var exists = test.RequirementIds.Contains(requirementId);
        if (linked == exists)
            return false;

        if (linked)
            test.RequirementIds.Add(requirementId);
        else
            test.RequirementIds.Remove(requirementId);
        return true;
    }
}
