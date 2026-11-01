namespace TestLoom.Core;

public static class TestCaseCloner
{
    /// <summary>Duplicates a test (metadata, fields, requirement links, steps with new IDs). History is NOT copied.</summary>
    public static WorkspaceTestCase Clone(WorkspaceTestCase source, string copyPrefix = "Copy of ")
    {
        ArgumentNullException.ThrowIfNull(source);
        var title = copyPrefix + source.Title;
        return new WorkspaceTestCase
        {
            Title = title.Length > 250 ? title[..250] : title,
            TemplateName = source.TemplateName, Description = source.Description,
            Priority = source.Priority, Owner = source.Owner,
            Tags = source.Tags.ToList(),
            FieldValues = new Dictionary<string, string>(source.FieldValues),
            RequirementIds = source.RequirementIds.ToList(),
            Steps = source.Steps.Select(s => new WorkspaceTestStep { Action = s.Action, ExpectedResult = s.ExpectedResult }).ToList()
        };
    }
}
