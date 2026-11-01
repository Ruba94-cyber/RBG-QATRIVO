using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestLoom.Core;

/// <summary>
/// Local, single-user workspace persistence for the first independent desktop
/// edition. A private product's profile is never discovered, read or migrated.
/// Concurrent/shared storage is NOT supported by this initial implementation.
/// </summary>
public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;
    private readonly object _sync = new();
    private string? _lastObservedFingerprint;
    public string FilePath => Path.Combine(_directory, "workspace.json");

    public WorkspaceStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    /// <summary>Read a complete snapshot, remembering its fingerprint for conflict detection.</summary>
    public WorkspaceDocument? Load()
    {
        lock (_sync)
        {
            RejectLinkedWorkspacePaths();
            if (!File.Exists(FilePath))
            {
                _lastObservedFingerprint = null;
                return null;
            }

            // A failed reload invalidates the old fingerprint. Otherwise the
            // same UI session cannot explicitly recover a newly damaged file:
            // the previously loaded hash is stale by construction.
            _lastObservedFingerprint = null;
            var bytes = File.ReadAllBytes(FilePath);
            VerifyDeclaredSchema(bytes);
            using var stream = new MemoryStream(bytes, writable: false);
            var document = JsonSerializer.Deserialize<WorkspaceDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("Workspace document is empty.");
            Validate(document);
            _lastObservedFingerprint = Fingerprint(bytes);
            return document;
        }
    }

    /// <summary>
    /// Save only if the on-disk file has not changed since the last successful
    /// read or save through this store. The initial save is create-only.
    /// A conflict never overwrites a workspace created by another process.
    /// </summary>
    public void Save(WorkspaceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);
        lock (_sync)
        {
            RejectLinkedWorkspacePaths();
            Directory.CreateDirectory(_directory);
            using var writerLock = AcquireExclusiveWriterLock();
            RejectLinkedWorkspacePaths();
            string? currentFingerprint = File.Exists(FilePath)
                ? Fingerprint(File.ReadAllBytes(FilePath))
                : null;
            if (!string.Equals(_lastObservedFingerprint, currentFingerprint,
                    StringComparison.Ordinal))
                throw new WorkspaceConflictException(
                    "The workspace was changed on disk. Reload it before saving.");

            var json = JsonSerializer.Serialize(document, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            var temporary = Path.Combine(_directory, $".workspace-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (currentFingerprint is not null)
                {
                    // Never redirect our primary/backup through a linked file.
                    RejectLinkedWorkspacePaths();
                    // Atomic replacement with one previous-version backup.
                    File.Replace(temporary, FilePath, FilePath + ".bak");
                }
                else
                {
                    // File.Move without overwrite is create-only, including races.
                    File.Move(temporary, FilePath);
                }
                _lastObservedFingerprint = Fingerprint(bytes);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Explicit, local recovery for a damaged primary JSON file only. It never
    /// scans for other products' data, and never overwrites a valid workspace.
    /// The damaged bytes are preserved beside the original file.
    /// </summary>
    public WorkspaceDocument RecoverCorruptedFromBackup()
    {
        lock (_sync)
        {
            RejectLinkedWorkspacePaths();
            using var writerLock = AcquireExclusiveWriterLock();
            RejectLinkedWorkspacePaths();
            if (!File.Exists(FilePath))
                throw new FileNotFoundException("No workspace exists to recover.", FilePath);

            var currentBytes = File.ReadAllBytes(FilePath);
            if (_lastObservedFingerprint is not null &&
                !string.Equals(_lastObservedFingerprint, Fingerprint(currentBytes),
                    StringComparison.Ordinal))
                throw new WorkspaceConflictException(
                    "The workspace changed after it was loaded; recovery was cancelled.");

            try
            {
                VerifyDeclaredSchema(currentBytes);

                using var currentStream = new MemoryStream(currentBytes, writable: false);
                var current = JsonSerializer.Deserialize<WorkspaceDocument>(currentStream, JsonOptions)
                    ?? throw new InvalidDataException("The workspace is empty.");
                // An unknown schema may belong to a NEWER application version.
                // It must not be mistaken for corruption and rolled back.
                if (current.SchemaVersion != WorkspaceDocument.CurrentSchemaVersion)
                    throw new NotSupportedException("The workspace belongs to an unsupported schema version. Recovery is blocked to protect newer data.");
                Validate(current);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // A verified invalid primary is the ONLY case where recovery is allowed.
                var backupPath = FilePath + ".bak";
                if (!File.Exists(backupPath))
                    throw new FileNotFoundException("No previous version is available.", backupPath);

                var backupBytes = File.ReadAllBytes(backupPath);
                // The backup must also declare the supported schema. Missing
                // versions must not silently default to this application's v1.
                VerifyDeclaredSchema(backupBytes);
                using var backupStream = new MemoryStream(backupBytes, writable: false);
                var restored = JsonSerializer.Deserialize<WorkspaceDocument>(backupStream, JsonOptions)
                    ?? throw new InvalidDataException("The previous version is empty.");
                Validate(restored);

                var temporary = Path.Combine(_directory, $".recover-{Guid.NewGuid():N}.tmp");
                var damagedCopy = FilePath + ".corrupt-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllBytes(temporary, backupBytes);
                    // Detect changes from non-cooperating writers as late as
                    // possible. Cooperative TestLoom writers share our lock.
                    if (!currentBytes.AsSpan().SequenceEqual(File.ReadAllBytes(FilePath)))
                        throw new WorkspaceConflictException(
                            "The damaged workspace changed during recovery. No file was replaced.");
                    // Keep the original damaged bytes in a separate file, not .bak.
                    RejectLinkedWorkspacePaths();
                    File.Replace(temporary, FilePath, damagedCopy);
                    _lastObservedFingerprint = Fingerprint(backupBytes);
                    return restored;
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }

            throw new InvalidOperationException(
                "The current workspace is valid; restore is only available for corrupted data.");
        }
    }

    // This edition must not accidentally follow a linked product profile or
    // workspace/backup file into another application's storage. These checks
    // defend against existing links; they are not a general hostile-process
    // sandbox or a substitute for filesystem access controls.
    private void RejectLinkedWorkspacePaths()
    {
        var directory = new DirectoryInfo(_directory);
        if (directory.LinkTarget is not null ||
            (directory.Exists &&
             (directory.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Linked workspace directories are not supported.");

        foreach (var path in new[]
        {
            FilePath, FilePath + ".bak", Path.Combine(_directory, ".workspace.lock")
        })
        {
            var file = new FileInfo(path);
            if (file.LinkTarget is not null ||
                (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException("Linked workspace or backup files are not supported.");
        }
    }

    // Coordinates separate TestLoom processes on one local filesystem.
    // The lock file remains intentionally; its stream is released automatically.
    // External programs that ignore this lock are detected by fingerprints
    // where possible, but cross-machine/network storage is NOT supported.
    private FileStream AcquireExclusiveWriterLock()
    {
        Directory.CreateDirectory(_directory);
        return new FileStream(Path.Combine(_directory, ".workspace.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>
    /// Inspect the schema before deserializing into older CLR types: a newer
    /// document can use incompatible field shapes. Missing, duplicate, invalid,
    /// or unknown schema identifiers are NOT evidence of corruption and must
    /// never authorize a backup rollback.
    /// </summary>
    private static void VerifyDeclaredSchema(byte[] bytes)
    {
        using var header = JsonDocument.Parse(bytes);
        if (header.RootElement.ValueKind != JsonValueKind.Object)
            throw new NotSupportedException("Workspace root is not a versioned JSON object. Automatic or manual rollback is blocked.");

        var found = false;
        foreach (var field in header.RootElement.EnumerateObject())
        {
            // System.Text.Json is case-sensitive with these serializer
            // options. Accepting a case-insensitive schema declaration here
            // would let typed deserialization silently use its default v1.
            if (!field.Name.Equals("SchemaVersion", StringComparison.Ordinal))
            {
                if (field.Name.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException(
                        "Workspace schema property uses unsupported casing. Do not assume version 1.");
                continue;
            }

            if (found || field.Value.ValueKind != JsonValueKind.Number ||
                !field.Value.TryGetInt32(out var version) ||
                version != WorkspaceDocument.CurrentSchemaVersion)
                throw new NotSupportedException(
                    "Unsupported or ambiguous workspace schema version. Install a compatible TestLoom version.");
            found = true;
        }

        if (!found)
            throw new NotSupportedException(
                "Workspace schema version is missing. Automatic or manual rollback is blocked.");
    }

    private static string Fingerprint(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    public static WorkspaceDocument Create(string name, string presetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (TestLoomWorkspacePresets.Find(presetId) is null)
            throw new ArgumentException("Unknown workspace preset.", nameof(presetId));

        return new WorkspaceDocument
        {
            Name = name.Trim(),
            PresetId = presetId
        };
    }

    public static void Validate(WorkspaceDocument document)
    {
        if (document.SchemaVersion != WorkspaceDocument.CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported workspace schema version.");
        if (string.IsNullOrWhiteSpace(document.Name) || document.Name.Length > 150)
            throw new InvalidDataException("Workspace name is required (maximum 150 characters).");
        if (TestLoomWorkspacePresets.Find(document.PresetId) is null)
            throw new InvalidDataException("Unknown workspace preset.");
        if (document.TestCases is null)
            throw new InvalidDataException("Test cases collection cannot be null.");

        if (document.Requirements is null)
            throw new InvalidDataException("Requirements collection cannot be null.");
        var requirementIds = new HashSet<Guid>();
        var requirementKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in document.Requirements)
        {
            if (requirement is null || requirement.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(requirement.Key) || requirement.Key.Length > 80 ||
                string.IsNullOrWhiteSpace(requirement.Title) || requirement.Title.Length > 250 ||
                !requirementIds.Add(requirement.Id) ||
                !requirementKeys.Add(requirement.Key.Trim()))
                throw new InvalidDataException("Invalid or duplicate requirement.");
        }

        var identifiers = new HashSet<Guid>();
        foreach (var test in document.TestCases)
        {
            if (test is null || test.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(test.Title) || test.Title.Length > 250 ||
                test.Executions is null || test.Steps is null || test.RequirementIds is null ||
                !identifiers.Add(test.Id))
                throw new InvalidDataException("Invalid or duplicate test case.");
            var links = new HashSet<Guid>();
            foreach (var requirementId in test.RequirementIds)
            {
                if (!requirementIds.Contains(requirementId) || !links.Add(requirementId))
                    throw new InvalidDataException("A test refers to a missing or duplicate requirement.");
            }
            if (test.Steps.Count > 1000)
                throw new InvalidDataException("A test cannot contain more than 1000 steps.");
            var stepIds = new HashSet<Guid>();
            foreach (var step in test.Steps)
            {
                if (step is null || step.Id == Guid.Empty ||
                    string.IsNullOrWhiteSpace(step.Action) || step.Action.Length > 1000 ||
                    string.IsNullOrWhiteSpace(step.ExpectedResult) || step.ExpectedResult.Length > 1000 ||
                    !stepIds.Add(step.Id))
                    throw new InvalidDataException("Invalid or duplicate test step.");
            }
            var executionIds = new HashSet<Guid>();
            foreach (var execution in test.Executions)
            {
                if (execution is null || execution.Id == Guid.Empty ||
                    !Enum.IsDefined(execution.Result) ||
                    !executionIds.Add(execution.Id))
                    throw new InvalidDataException("Invalid or duplicate execution.");
            }
        }
        if (document.Defects is null)
            throw new InvalidDataException("Defect collection cannot be null.");
        var defectIds = new HashSet<Guid>();
        var defectKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var defect in document.Defects)
        {
            if (defect is null || defect.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(defect.Key) || defect.Key.Length > 80 ||
                string.IsNullOrWhiteSpace(defect.Title) || defect.Title.Length > 250 ||
                defect.Description is null || defect.Description.Length > 20000 ||
                !Enum.IsDefined(defect.Severity) || !Enum.IsDefined(defect.Status) ||
                !defectIds.Add(defect.Id) || !defectKeys.Add(defect.Key.Trim()) ||
                (defect.TestCaseId.HasValue && !identifiers.Contains(defect.TestCaseId.Value)))
                throw new InvalidDataException("Invalid or duplicate defect, or unknown linked test.");
        }
        ValidateExtensions(document, identifiers);
    }

    public const int MaxAuditEntries = 5000;

    private static void ValidateExtensions(WorkspaceDocument document, HashSet<Guid> testIds)
    {
        if (document.Language is not ("en" or "it"))
            throw new InvalidDataException("Unsupported workspace language.");
        if (document.DefaultTester is null || document.DefaultTester.Length > 120)
            throw new InvalidDataException("Invalid default tester.");
        if (document.Templates is null || document.Plans is null || document.Audit is null)
            throw new InvalidDataException("Workspace collections cannot be null.");

        var templateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var templateIds = new HashSet<Guid>();
        foreach (var template in document.Templates)
        {
            if (template is null || template.Id == Guid.Empty || !templateIds.Add(template.Id) ||
                string.IsNullOrWhiteSpace(template.Name) || template.Name.Length > 120 ||
                template.Icon is null || template.Icon.Length > 16 ||
                template.Sections is null || template.Sections.Count > 50 ||
                !templateNames.Add(template.Name.Trim()))
                throw new InvalidDataException("Invalid or duplicate template.");
            foreach (var section in template.Sections)
            {
                if (section is null || string.IsNullOrWhiteSpace(section.Name) || section.Name.Length > 120 ||
                    section.Fields is null || section.Fields.Count > 100)
                    throw new InvalidDataException("Invalid template section.");
                foreach (var field in section.Fields)
                    if (field is null || string.IsNullOrWhiteSpace(field.Name) || field.Name.Length > 120)
                        throw new InvalidDataException("Invalid template field.");
            }
        }

        var planIds = new HashSet<Guid>();
        foreach (var plan in document.Plans)
        {
            if (plan is null || plan.Id == Guid.Empty || !planIds.Add(plan.Id) ||
                string.IsNullOrWhiteSpace(plan.Name) || plan.Name.Length > 150 ||
                plan.Description is null || plan.Description.Length > 5000 ||
                plan.TestIds is null || plan.TestIds.Count > 5000 ||
                plan.TestIds.Distinct().Count() != plan.TestIds.Count ||
                plan.TestIds.Any(id => !testIds.Contains(id)))
                throw new InvalidDataException("Invalid or duplicate test plan, or unknown test in plan.");
        }

        if (document.Audit.Count > MaxAuditEntries)
            throw new InvalidDataException("The audit log is too large.");
        foreach (var entry in document.Audit)
            if (entry is null || entry.Actor is null || entry.Action is null || entry.Subject is null || entry.Detail is null ||
                entry.Actor.Length > 120 || entry.Action.Length > 60 || entry.Subject.Length > 250 || entry.Detail.Length > 2000)
                throw new InvalidDataException("Invalid audit entry.");

        foreach (var test in document.TestCases)
        {
            if (!Enum.IsDefined(test.Priority) || test.Owner is null || test.Owner.Length > 120 ||
                test.Tags is null || test.Tags.Count > 50 ||
                test.Tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 40) ||
                test.FieldValues is null || test.FieldValues.Count > 500 ||
                test.FieldValues.Any(kv => string.IsNullOrWhiteSpace(kv.Key) || kv.Key.Length > 250 ||
                                           kv.Value is null || kv.Value.Length > 20000))
                throw new InvalidDataException("Invalid test metadata.");

            foreach (var execution in test.Executions)
            {
                if (execution.Tester is null || execution.Tester.Length > 120 ||
                    execution.Environment is null || execution.Environment.Length > 250 ||
                    execution.BuildVersion is null || execution.BuildVersion.Length > 120 ||
                    execution.Notes is null || execution.Notes.Length > 20000 ||
                    execution.DurationSeconds < 0 || execution.DurationSeconds > 31_536_000 ||
                    execution.StepResults is null || execution.Evidence is null ||
                    execution.StepResults.Count > 1000 || execution.Evidence.Count > 50)
                    throw new InvalidDataException("Invalid execution details.");
                var seenSteps = new HashSet<Guid>();
                foreach (var result in execution.StepResults)
                    if (result is null || !Enum.IsDefined(result.Result) || result.Note is null || result.Note.Length > 2000 ||
                        !seenSteps.Add(result.StepId))
                        throw new InvalidDataException("Invalid step result.");
                var evidenceIds = new HashSet<Guid>();
                foreach (var evidence in execution.Evidence)
                    if (!EvidenceStore.IsValid(evidence) || !evidenceIds.Add(evidence.Id))
                        throw new InvalidDataException("Invalid evidence attachment.");
            }
        }
    }
}

/// <summary>
/// A local workspace has changed after this process loaded it. Never overwrite
/// other changes automatically; the UI must inform the user to reload.
/// </summary>
public sealed class WorkspaceConflictException : IOException
{
    public WorkspaceConflictException(string message) : base(message) { }
}
