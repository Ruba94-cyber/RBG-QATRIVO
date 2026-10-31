using TestLoom.Core;

// Standalone, cross-platform, dependency-free smoke verification of the
// reusable core. No private application code or workspace is loaded.
Require(FieldNameNormalizer.Normalize("Test step 1 - Rev.2") == "TESTSTEP1REV2",
    "Field labels must preserve invariant normalization.");

var fields = new Dictionary<string, string> { ["existing"] = "before", ["empty"] = "" };
var undo = FieldCopyTransaction.Apply(fields, new[]
{
    new KeyValuePair<string, string>("existing", "after"),
    new KeyValuePair<string, string>("new-field", "created"),
    new KeyValuePair<string, string>("empty", "filled")
});
Require(fields["existing"] == "after" && fields["new-field"] == "created",
    "Field copy must apply all selected updates.");
Require(undo.IsUnchangedAfterApply, "Untouched copy should remain undoable.");
undo.Undo();
Require(fields["existing"] == "before" && fields["empty"] == "" &&
        !fields.ContainsKey("new-field"), "Undo must restore originals and absent keys.");

var pathA = EditionStoragePaths.ConfigFile("appdata", "InternalProduct");
var pathB = EditionStoragePaths.ConfigFile("appdata", "TestLoom");
Require(pathA != pathB, "Independent products must not share a default config path.");
Require(EditionStoragePaths.WorkspaceCacheDirectory("local", "InternalProduct") !=
        EditionStoragePaths.WorkspaceCacheDirectory("local", "TestLoom"),
    "Independent products must not share cache directories.");

bool unsafeFolderRejected = false;
try { _ = EditionStoragePaths.ConfigDirectory("appdata", "../unsafe"); }
catch (ArgumentException) { unsafeFolderRejected = true; }
Require(unsafeFolderRejected, "Unsafe edition folder must be rejected.");

var definitions = TestLoomTemplateBlueprints.Create("hardware");
Require(definitions.Count == 3, "Hardware preset must offer three test templates.");
Require(definitions.All(t => t.Sections.Count == 1 &&
    t.Sections[0].Fields.Count == 3), "Template fields must be editable blueprints.");
Require(TestLoomTemplateBlueprints.Create("blank").Count == 0,
    "Blank workspace must not prepopulate test templates.");
Require(TestLoomWorkspacePresets.All.Count == 5,
    "All five generic workspace profiles must remain available.");

var disposableRoot = Path.Combine(Path.GetTempPath(), "TestLoom-Smoke-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new WorkspaceStore(disposableRoot);
    var workspace = WorkspaceStore.Create("Synthetic workspace", "software-qa");
    var requirement = new WorkspaceRequirement
    {
        Key = "REQ-SMOKE-1", Title = "Application should allow valid sign-in"
    };
    workspace.Requirements.Add(requirement);
    var newCase = new WorkspaceTestCase { Title = "Login accepts valid credentials", TemplateName = "Smoke test" };
    newCase.RequirementIds.Add(requirement.Id);
    newCase.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Passed, Notes = "Synthetic result" });
    newCase.Steps.Add(new WorkspaceTestStep {
        Action = "Enter valid credentials",
        ExpectedResult = "User is signed in"
    });
    workspace.TestCases.Add(newCase);
    Require(RequirementLinks.SetLinked(workspace, newCase.Id, requirement.Id, linked: false),
        "Existing tests must allow safe requirement unlinking.");
    Require(!RequirementLinks.SetLinked(workspace, newCase.Id, requirement.Id, linked: false),
        "Repeated unlinking must not duplicate or corrupt links.");
    Require(RequirementLinks.SetLinked(workspace, newCase.Id, requirement.Id, linked: true),
        "Existing tests must allow requirement relinking.");
    workspace.Defects.Add(new WorkspaceDefect {
        Key = "BUG-SMOKE-1",
        Title = "Synthetic issue",
        TestCaseId = newCase.Id,
        Severity = WorkspaceDefectSeverity.Low
    });
    store.Save(workspace);
    var loaded = store.Load() ?? throw new InvalidOperationException("Save/reload failed.");
    Require(loaded.Name == workspace.Name && loaded.TestCases.Count == 1,
        "Workspace must round-trip with the original test case.");
    Require(loaded.TestCases[0].Executions[0].Result == ExecutionResult.Passed,
        "Execution history must round-trip without changing the outcome.");
    Require(File.Exists(store.FilePath), "Workspace must have a durable JSON file.");
    Require(loaded.TestCases[0].Steps.Count == 1 &&
            loaded.TestCases[0].Steps[0].ExpectedResult == "User is signed in",
        "Structured steps must survive a standalone round trip.");
    Require(loaded.Defects.Count == 1 &&
            loaded.Defects[0].TestCaseId == loaded.TestCases[0].Id,
        "Linked defects must preserve stable identifiers on reload.");
    Require(WorkspaceCsvReport.ExportTestSteps(loaded).Contains("Enter valid credentials") &&
            WorkspaceCsvReport.ExportDefects(loaded).Contains("BUG-SMOKE-1"),
        "Standalone step and defect CSV exports must work.");

    var coverage = RequirementCoverage.Calculate(loaded);
    Require(coverage.TotalRequirements == 1 &&
            coverage.RequirementsWithPassingTest == 1,
        "A passing execution must contribute to linked requirement coverage.");

    var otherSession = new WorkspaceStore(disposableRoot);
    var stale = otherSession.Load() ?? throw new InvalidOperationException("Secondary load failed.");
    loaded.Name = "Updated in first session";
    store.Save(loaded);
    stale.Name = "Stale update";
    bool conflictRejected = false;
    try { otherSession.Save(stale); }
    catch (WorkspaceConflictException) { conflictRejected = true; }
    Require(conflictRejected, "Concurrent workspace changes must never be silently overwritten.");
    Require(File.Exists(store.FilePath + ".bak"), "Updating a workspace must retain its previous version.");
    // The independent edition can recover invalid local JSON without ever
    // deleting the corrupted bytes or accessing any other application's data.
    File.WriteAllText(store.FilePath, "not valid JSON");
    var recoveredStore = new WorkspaceStore(disposableRoot);
    var recovered = recoveredStore.RecoverCorruptedFromBackup();
    Require(recovered.Name == "Synthetic workspace",
        "Recovery must use only the last validated previous-version backup.");
    Require(Directory.GetFiles(disposableRoot, "workspace.json.corrupt-*").Length == 1,
        "Recovery must preserve the exact damaged workspace separately.");
    Require(new WorkspaceStore(disposableRoot).Load()!.Name == recovered.Name,
        "Recovered workspace must remain readable after restart.");

}
finally
{
    if (Directory.Exists(disposableRoot)) Directory.Delete(disposableRoot, recursive: true);
}

Console.WriteLine("PASS: standalone TestLoom.Core smoke verification.");
return;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
