using System.Text.Json.Serialization;

namespace TestLoom.Core;

/// <summary>
/// An independent, domain-neutral workspace schema for the first desktop edition.
/// The storage contract is owned exclusively by this product.
/// </summary>
public sealed class WorkspaceDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Name { get; set; } = "";
    public string PresetId { get; set; } = "blank";
    public List<WorkspaceTestCase> TestCases { get; set; } = new();
    public List<WorkspaceRequirement> Requirements { get; set; } = new();
    public List<WorkspaceDefect> Defects { get; set; } = new();
}

public sealed class WorkspaceTestCase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Archived { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<WorkspaceExecution> Executions { get; set; } = new();
    public List<WorkspaceTestStep> Steps { get; set; } = new();
    public List<Guid> RequirementIds { get; set; } = new();

    [JsonIgnore]
    public string LatestResult => Executions.LastOrDefault()?.Result.ToString() ?? "Not run";

    public override string ToString() => $"{(Archived ? "[Archived] " : "")}{Title}  ·  {LatestResult}";
}

/// <summary>
/// An ordered, editable action/expected-result pair. Keeping the list inside
/// each test preserves document v1 compatibility and local save semantics.
/// </summary>
public sealed class WorkspaceTestStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Action { get; set; } = "";
    public string ExpectedResult { get; set; } = "";

    public override string ToString() => Action + " → " + ExpectedResult;
}

public enum ExecutionResult
{
    Passed,
    Failed,
    Blocked
}

public sealed class WorkspaceExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset ExecutedUtc { get; set; } = DateTimeOffset.UtcNow;
    public ExecutionResult Result { get; set; }
    public string Notes { get; set; } = "";
}

public enum WorkspaceDefectSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public enum WorkspaceDefectStatus
{
    Open,
    InProgress,
    Resolved,
    Closed
}

/// <summary>
/// Domain-neutral issue/nonconformity linked to a test through its stable ID.
/// </summary>
public sealed class WorkspaceDefect
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public WorkspaceDefectSeverity Severity { get; set; } = WorkspaceDefectSeverity.Medium;
    public WorkspaceDefectStatus Status { get; set; } = WorkspaceDefectStatus.Open;
    public Guid? TestCaseId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public override string ToString() => Key + " · " + Title + " (" + Status + ")";
}

public sealed class WorkspaceRequirement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    public override string ToString() => $"{Key} · {Title}";
}
