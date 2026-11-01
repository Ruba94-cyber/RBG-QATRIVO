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

    // Additive (optional) data: documents written by earlier builds simply lack these and load with defaults.
    public string Language { get; set; } = "en";
    public string DefaultTester { get; set; } = "";
    public List<WorkspaceTemplate> Templates { get; set; } = new();
    public List<WorkspaceTestPlan> Plans { get; set; } = new();
    public List<WorkspaceAuditEntry> Audit { get; set; } = new();
}

public enum WorkspacePriority
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>Reusable test layout: sections of named fields, optionally required.</summary>
public sealed class WorkspaceTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public List<WorkspaceTemplateSection> Sections { get; set; } = new();

    public override string ToString() => Name;
}

public sealed class WorkspaceTemplateSection
{
    public string Name { get; set; } = "";
    public List<WorkspaceTemplateField> Fields { get; set; } = new();
}

public sealed class WorkspaceTemplateField
{
    public string Name { get; set; } = "";
    public bool Required { get; set; }
}

/// <summary>A named group of tests (a test plan / campaign / protocol, depending on the preset).</summary>
public sealed class WorkspaceTestPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<Guid> TestIds { get; set; } = new();
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public override string ToString() => $"{Name} ({TestIds.Count})";
}

/// <summary>Append-only record of who changed what; capped so it cannot grow without bound.</summary>
public sealed class WorkspaceAuditEntry
{
    public DateTimeOffset Utc { get; set; } = DateTimeOffset.UtcNow;
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>Outcome of one step inside an execution.</summary>
public sealed class WorkspaceStepResult
{
    public Guid StepId { get; set; }
    public ExecutionResult Result { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>A file attached to an execution, stored inside the workspace folder and identified by SHA-256.</summary>
public sealed class WorkspaceEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = "";
    public string StoredName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;

    public override string ToString() => FileName;
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
    public WorkspacePriority Priority { get; set; } = WorkspacePriority.Medium;
    public string Owner { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    /// <summary>Values for the fields of the test's template, keyed by "Section/Field".</summary>
    public Dictionary<string, string> FieldValues { get; set; } = new();

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
    public string Tester { get; set; } = "";
    public string Environment { get; set; } = "";
    public string BuildVersion { get; set; } = "";
    public int DurationSeconds { get; set; }
    public List<WorkspaceStepResult> StepResults { get; set; } = new();
    public List<WorkspaceEvidence> Evidence { get; set; } = new();
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
