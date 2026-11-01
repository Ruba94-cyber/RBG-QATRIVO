using System.IO.Compression;
using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class FeatureSetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom-Feat-" + Guid.NewGuid().ToString("N"));

    public FeatureSetTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string TempFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ---------- presets & starter ----------

    [Theory]
    [InlineData("software-qa", "en", false)]
    [InlineData("hardware", "it", true)]
    [InlineData("manufacturing", "it", false)]
    [InlineData("laboratory", "en", true)]
    [InlineData("blank", "it", true)]
    public void EveryPresetBuildsAValidStartingWorkspace(string preset, string language, bool samples)
    {
        var doc = WorkspaceStarter.Create("Demo", preset, language, samples, "Ada");
        WorkspaceStore.Validate(doc);
        Assert.Equal(language, doc.Language);
        Assert.Equal("Ada", doc.DefaultTester);
        Assert.Single(doc.Plans);
        Assert.Equal(TestLoomWorkspacePresets.Find(preset)!.SuggestedTestTemplates.Count, doc.Templates.Count);
        Assert.Equal(samples ? 1 : 0, doc.TestCases.Count);
        Assert.Single(doc.Audit);
        // The start must survive a save/load round-trip untouched.
        var store = new WorkspaceStore(Path.Combine(_root, preset + language + samples));
        store.Save(doc);
        var back = store.Load()!;
        Assert.Equal(doc.Templates.Select(t => t.Name), back.Templates.Select(t => t.Name));
    }

    [Fact]
    public void ItalianStartTranslatesTemplatesAndVocabulary()
    {
        var doc = WorkspaceStarter.Create("Demo", "software-qa", "it");
        Assert.Contains(doc.Templates, t => t.Name == "Test smoke");
        var card = TestLoomPresetInfo.Card("software-qa", "it");
        Assert.Equal("Qualità software", card.DisplayName);
        Assert.Equal("Applicazione", card.SubjectLabel);
        Assert.Contains("Test smoke", card.TestTemplates);
    }

    [Fact]
    public void PresetCardsCoverEveryPresetInEveryLanguage()
    {
        foreach (var language in TestLoomPresetInfo.Languages)
        {
            var cards = TestLoomPresetInfo.Cards(language);
            Assert.Equal(TestLoomWorkspacePresets.All.Count, cards.Count);
            Assert.All(cards, c =>
            {
                Assert.False(string.IsNullOrWhiteSpace(c.DisplayName));
                Assert.False(string.IsNullOrWhiteSpace(c.Description));
            });
        }
        Assert.Throws<ArgumentException>(() => TestLoomPresetInfo.Card("nope", "en"));
        Assert.Equal("en", TestLoomPresetInfo.NormalizeLanguage("fr"));
    }

    [Fact]
    public void LocalizationFallsBackToEnglish()
    {
        Assert.Equal("Objective", WorkspaceLocalization.T("Objective", "en"));
        Assert.Equal("Obiettivo", WorkspaceLocalization.T("Objective", "it"));
        Assert.Equal("never translated", WorkspaceLocalization.T("never translated", "it"));
        Assert.Equal("Esportazione non riuscita: x", WorkspaceLocalization.TMessage("Export failed: x", "it"));
        Assert.All(WorkspaceLocalization.ItalianTable, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value)));
    }

    // ---------- validation of the new data ----------

    [Fact]
    public void ValidationRejectsHostileExtensionData()
    {
        WorkspaceDocument Fresh()
        {
            var d = WorkspaceStarter.Create("V", "software-qa", "en", true);
            return d;
        }
        void Bad(Action<WorkspaceDocument> mutate)
        {
            var d = Fresh();
            mutate(d);
            Assert.Throws<InvalidDataException>(() => WorkspaceStore.Validate(d));
        }
        Bad(d => d.Language = "xx");
        Bad(d => d.Plans[0].TestIds.Add(Guid.NewGuid()));
        Bad(d => d.Plans[0].TestIds.Add(d.TestCases[0].Id));
        Bad(d => d.Templates.Add(new WorkspaceTemplate { Name = d.Templates[0].Name }));
        Bad(d => d.TestCases[0].Tags.Add(new string('x', 41)));
        Bad(d => d.TestCases[0].FieldValues[""] = "x");
        Bad(d => d.TestCases[0].Executions.Add(new WorkspaceExecution { DurationSeconds = -1 }));
        Bad(d => d.TestCases[0].Executions.Add(new WorkspaceExecution
        {
            Evidence = { new WorkspaceEvidence { FileName = "../x", StoredName = "bad", Sha256 = "zz" } }
        }));
        Bad(d => d.TestCases[0].Executions.Add(new WorkspaceExecution
        {
            StepResults = { new WorkspaceStepResult { StepId = Guid.NewGuid() }, new WorkspaceStepResult { StepId = Guid.Empty } },
            Tester = new string('t', 121)
        }));
    }

    [Fact]
    public void AnOldDocumentWithoutTheNewFieldsStillLoads()
    {
        var dir = Path.Combine(_root, "old");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workspace.json"),
            "{\"SchemaVersion\":1,\"Name\":\"Old\",\"PresetId\":\"blank\",\"TestCases\":[{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Title\":\"T\"}],\"Requirements\":[],\"Defects\":[]}");
        var doc = new WorkspaceStore(dir).Load()!;
        Assert.Equal("en", doc.Language);
        Assert.Equal(WorkspacePriority.Medium, doc.TestCases[0].Priority);
        Assert.Empty(doc.Plans);
    }

    // ---------- templates ----------

    [Fact]
    public void RequiredTemplateFieldsAreReported()
    {
        var doc = WorkspaceStarter.Create("T", "software-qa");
        var template = doc.Templates[0];
        template.Sections[0].Fields[0].Required = true;
        var test = new WorkspaceTestCase { Title = "x", TemplateName = template.Name };
        var key = WorkspaceTemplates.FieldKey(template.Sections[0].Name, template.Sections[0].Fields[0].Name);
        Assert.Equal(new[] { key }, WorkspaceTemplates.MissingRequired(test, template));
        WorkspaceTemplates.SetValue(test, key, "  filled ");
        Assert.Empty(WorkspaceTemplates.MissingRequired(test, template));
        Assert.Equal("filled", test.FieldValues[key]);
        WorkspaceTemplates.SetValue(test, key, " ");
        Assert.Empty(test.FieldValues);
        Assert.Equal(0, WorkspaceTemplates.SeedFromPreset(doc));
    }

    // ---------- step-wise execution ----------

    [Fact]
    public void StepwiseOutcomeIsDerivedFromEveryStep()
    {
        var test = new WorkspaceTestCase { Title = "x" };
        test.Steps.AddRange(new[] { new WorkspaceTestStep { Action = "a", ExpectedResult = "b" }, new WorkspaceTestStep { Action = "c", ExpectedResult = "d" } });
        WorkspaceStepResult R(int i, ExecutionResult r) => new() { StepId = test.Steps[i].Id, Result = r };

        Assert.Null(StepwiseExecution.Overall(test, new[] { R(0, ExecutionResult.Passed) }));
        Assert.Equal(ExecutionResult.Passed, StepwiseExecution.Overall(test, new[] { R(0, ExecutionResult.Passed), R(1, ExecutionResult.Passed) }));
        Assert.Equal(ExecutionResult.Failed, StepwiseExecution.Overall(test, new[] { R(0, ExecutionResult.Blocked), R(1, ExecutionResult.Failed) }));
        Assert.Equal(ExecutionResult.Blocked, StepwiseExecution.Overall(test, new[] { R(0, ExecutionResult.Passed), R(1, ExecutionResult.Blocked) }));
        Assert.Throws<InvalidOperationException>(() => StepwiseExecution.Build(test, new[] { R(0, ExecutionResult.Passed) }));
        var run = StepwiseExecution.Build(test, new[] { R(0, ExecutionResult.Passed), R(1, ExecutionResult.Passed) }, "Ada", "lab", "1.2", 90);
        Assert.Equal(ExecutionResult.Passed, run.Result);
        Assert.Equal(90, run.DurationSeconds);
        Assert.Null(StepwiseExecution.Overall(new WorkspaceTestCase { Title = "empty" }, Array.Empty<WorkspaceStepResult>()));
    }

    // ---------- plans ----------

    [Fact]
    public void PlanProgressCountsLatestOutcomesOfActiveMembers()
    {
        var doc = WorkspaceStarter.Create("P", "blank");
        var a = new WorkspaceTestCase { Title = "a" }; a.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Passed });
        var b = new WorkspaceTestCase { Title = "b" }; b.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Failed });
        var c = new WorkspaceTestCase { Title = "c" };
        var d = new WorkspaceTestCase { Title = "d", Archived = true };
        doc.TestCases.AddRange(new[] { a, b, c, d });
        var plan = doc.Plans[0];
        foreach (var t in doc.TestCases) Assert.True(TestPlanProgress.SetMembership(doc, plan.Id, t.Id, true));
        Assert.False(TestPlanProgress.SetMembership(doc, plan.Id, a.Id, true));
        var report = TestPlanProgress.Calculate(doc, plan);
        Assert.Equal((3, 1, 1, 0, 1), (report.Total, report.Passed, report.Failed, report.Blocked, report.NotRun));
        Assert.Equal(66.7, report.CompletionPercent);
        Assert.Equal(50, report.PassRatePercent);
        Assert.True(TestPlanProgress.SetMembership(doc, plan.Id, d.Id, false));
        Assert.Throws<ArgumentException>(() => TestPlanProgress.SetMembership(doc, Guid.NewGuid(), a.Id, true));
        Assert.Throws<ArgumentException>(() => TestPlanProgress.SetMembership(doc, plan.Id, Guid.NewGuid(), true));
        WorkspaceStore.Validate(doc);
    }

    // ---------- clone, filter, audit, matrix ----------

    [Fact]
    public void CloneCopiesContentWithNewIdsAndNoHistory()
    {
        var src = new WorkspaceTestCase { Title = "Login", Priority = WorkspacePriority.Critical, Owner = "Ada", Tags = { "ui" } };
        src.Steps.Add(new WorkspaceTestStep { Action = "a", ExpectedResult = "b" });
        src.FieldValues["S/F"] = "v";
        src.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Failed });
        var copy = TestCaseCloner.Clone(src);
        Assert.Equal("Copy of Login", copy.Title);
        Assert.NotEqual(src.Id, copy.Id);
        Assert.NotEqual(src.Steps[0].Id, copy.Steps[0].Id);
        Assert.Empty(copy.Executions);
        Assert.Equal("v", copy.FieldValues["S/F"]);
        copy.Tags.Add("other");
        Assert.Single(src.Tags);
        Assert.Equal(250, TestCaseCloner.Clone(new WorkspaceTestCase { Title = new string('x', 250) }).Title.Length);
    }

    [Fact]
    public void FilterUnderstandsPriorityTagOwnerAndPlan()
    {
        var doc = WorkspaceStarter.Create("F", "blank");
        var a = new WorkspaceTestCase { Title = "a", Priority = WorkspacePriority.High, Owner = "Grace", Tags = { "Smoke" } };
        var b = new WorkspaceTestCase { Title = "b" };
        doc.TestCases.AddRange(new[] { a, b });
        TestPlanProgress.SetMembership(doc, doc.Plans[0].Id, b.Id, true);
        Assert.Equal(new[] { a }, TestCaseFilter.Apply(doc, new TestCaseFilter(Priority: WorkspacePriority.High)));
        Assert.Equal(new[] { a }, TestCaseFilter.Apply(doc, new TestCaseFilter(Tag: "smoke")));
        Assert.Equal(new[] { a }, TestCaseFilter.Apply(doc, new TestCaseFilter(Text: "grace")));
        Assert.Equal(new[] { b }, TestCaseFilter.Apply(doc, new TestCaseFilter(PlanId: doc.Plans[0].Id)));
    }

    [Fact]
    public void AuditIsCappedAndClipped()
    {
        var doc = WorkspaceStarter.Create("A", "blank");
        for (var i = 0; i < WorkspaceStore.MaxAuditEntries + 20; i++)
            WorkspaceAudit.Record(doc, "me", "test.run", "s" + i, new string('d', 5000));
        Assert.Equal(WorkspaceStore.MaxAuditEntries, doc.Audit.Count);
        Assert.Equal("s20", doc.Audit[0].Subject);
        Assert.Equal(2000, doc.Audit[^1].Detail.Length);
        WorkspaceStore.Validate(doc);
    }

    [Fact]
    public void TraceabilityMatrixShowsLatestOutcomePerLinkAndNeutralisesFormulas()
    {
        var doc = WorkspaceStarter.Create("M", "blank");
        var req = new WorkspaceRequirement { Key = "R1", Title = "=cmd" };
        doc.Requirements.Add(req);
        var t = new WorkspaceTestCase { Title = "T1" };
        t.RequirementIds.Add(req.Id);
        t.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Passed });
        doc.TestCases.Add(t);
        var csv = TraceabilityMatrix.ExportCsv(doc);
        Assert.Contains("\"'=cmd\"", csv);
        Assert.Contains("\"Passed\"", csv);
    }

    // ---------- CSV import ----------

    [Fact]
    public void ImportsRequirementsFromSemicolonSeparatedCsvAndSkipsDuplicates()
    {
        var doc = WorkspaceStarter.Create("I", "blank");
        doc.Requirements.Add(new WorkspaceRequirement { Key = "REQ-1", Title = "existing" });
        var csv = "﻿Codice;Titolo;Descrizione\r\nREQ-1;dup;x\r\nREQ-2;\"Two; with \"\"quotes\"\"\";desc\r\n";
        var result = WorkspaceCsvImport.ImportRequirements(doc, csv);
        Assert.True(result.Success);
        Assert.Equal((1, 1), (result.Added, result.Skipped));
        Assert.Equal("Two; with \"quotes\"", doc.Requirements[1].Title);
    }

    [Fact]
    public void ImportIsAllOrNothing()
    {
        var doc = WorkspaceStarter.Create("I", "blank");
        var before = doc.Requirements.Count;
        var result = WorkspaceCsvImport.ImportRequirements(doc, "Requirement ID,Title\r\nA,fine\r\n,missing id\r\n");
        Assert.False(result.Success);
        Assert.Equal(before, doc.Requirements.Count);
        Assert.Contains("Row 3", result.Errors[0]);
        Assert.False(WorkspaceCsvImport.ImportRequirements(doc, "Foo,Bar\r\n1,2").Success);
        Assert.False(WorkspaceCsvImport.ImportRequirements(doc, "").Success);
        Assert.False(WorkspaceCsvImport.ImportRequirements(doc, "Requirement ID,Title\r\n\"unclosed,x").Success);
        Assert.False(WorkspaceCsvImport.ImportRequirements(doc, new string('x', WorkspaceCsvImport.MaxChars + 1)).Success);
    }

    [Fact]
    public void ImportsTestsWithPriorityTagsAndRequirementLinks()
    {
        var doc = WorkspaceStarter.Create("I", "blank");
        doc.Requirements.Add(new WorkspaceRequirement { Key = "REQ-1", Title = "r" });
        var csv = "Title,Template,Priority,Tags,Requirement IDs,Owner\r\n" +
                  "Login,,High,ui|smoke,REQ-1,Ada\r\n" +
                  "'=Weird,,bassa,,,\r\n";
        var result = WorkspaceCsvImport.ImportTests(doc, csv);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(2, doc.TestCases.Count);
        Assert.Equal(WorkspacePriority.High, doc.TestCases[0].Priority);
        Assert.Equal(new[] { "ui", "smoke" }, doc.TestCases[0].Tags);
        Assert.Single(doc.TestCases[0].RequirementIds);
        Assert.Equal("=Weird", doc.TestCases[1].Title);
        Assert.Equal(WorkspacePriority.Low, doc.TestCases[1].Priority);

        Assert.False(WorkspaceCsvImport.ImportTests(doc, "Title,Priority\r\nZ,urgent").Success);
        Assert.False(WorkspaceCsvImport.ImportTests(doc, "Title,Requirement IDs\r\nY,NOPE").Success);
        Assert.Equal(2, doc.TestCases.Count);
        Assert.Equal(1, WorkspaceCsvImport.ImportTests(doc, "Title\r\nlogin\r\n").Skipped);
    }

    // ---------- evidence ----------

    [Fact]
    public void EvidenceIsCopiedHashedAndVerified()
    {
        var store = new EvidenceStore(Path.Combine(_root, "ws"));
        var source = TempFile("shot.PNG", "pixels");
        var item = store.Add(source);
        Assert.True(EvidenceStore.IsValid(item));
        Assert.EndsWith(".png", item.StoredName);
        Assert.Equal("shot.PNG", item.FileName);
        Assert.Equal(6, item.SizeBytes);
        Assert.True(store.Verify(item));
        File.Delete(source);
        Assert.True(store.Verify(item));
        File.WriteAllText(store.PathOf(item), "tamper");
        Assert.False(store.Verify(item));
        store.Remove(item);
        Assert.False(File.Exists(store.PathOf(item)));
    }

    [Fact]
    public void EvidenceRejectsMissingOversizedAndUnsafeInput()
    {
        var store = new EvidenceStore(Path.Combine(_root, "ws2"));
        Assert.Throws<FileNotFoundException>(() => store.Add(Path.Combine(_root, "missing.txt")));
        var big = Path.Combine(_root, "big.bin");
        using (var f = File.Create(big)) f.SetLength(EvidenceStore.MaxFileBytes + 1);
        Assert.Throws<InvalidDataException>(() => store.Add(big));
        Assert.Empty(Directory.Exists(store.FolderPath) ? Directory.GetFiles(store.FolderPath) : Array.Empty<string>());
        var odd = store.Add(TempFile("weird.name with spaces.@@@@@@@@@@@@", "x"));
        Assert.EndsWith(".bin", odd.StoredName);
        Assert.False(EvidenceStore.IsValid(new WorkspaceEvidence { FileName = "a", StoredName = "../../x.png", Sha256 = new string('a', 64) }));
        Assert.Throws<InvalidDataException>(() => store.PathOf(new WorkspaceEvidence { FileName = "a", StoredName = "..\\x", Sha256 = "" }));
    }

    // ---------- backup with evidence ----------

    private (WorkspaceStore Store, WorkspaceEvidence Evidence) WorkspaceWithEvidence(string folder)
    {
        var store = new WorkspaceStore(Path.Combine(_root, folder));
        var doc = WorkspaceStarter.Create("B", "blank", "en", true);
        var evidence = new EvidenceStore(Path.Combine(_root, folder)).Add(TempFile(folder + ".txt", "proof"));
        doc.TestCases[0].Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Passed, Evidence = { evidence } });
        store.Save(doc);
        store.Load();
        return (store, evidence);
    }

    [Fact]
    public void BackupCarriesEvidenceAndRestoresItVerified()
    {
        var (store, evidence) = WorkspaceWithEvidence("src");
        var zip = Path.Combine(_root, "b.zip");
        WorkspaceBackup.Create(store, zip);
        var restoredDir = Path.Combine(_root, "restored");
        var restored = WorkspaceBackup.Restore(zip, restoredDir);
        Assert.Single(restored.TestCases[0].Executions[0].Evidence);
        Assert.True(new EvidenceStore(restoredDir).Verify(evidence));
    }

    [Fact]
    public void RestoreRejectsTamperedOrUnreferencedEvidence()
    {
        var (store, evidence) = WorkspaceWithEvidence("src2");
        var zip = Path.Combine(_root, "b2.zip");
        WorkspaceBackup.Create(store, zip);

        var tampered = Path.Combine(_root, "tampered.zip");
        File.Copy(zip, tampered);
        using (var a = ZipFile.Open(tampered, ZipArchiveMode.Update))
        {
            var entry = a.GetEntry("evidence/" + evidence.StoredName)!;
            entry.Delete();
            var replacement = a.CreateEntry("evidence/" + evidence.StoredName);
            using var w = new StreamWriter(replacement.Open()); w.Write("forged");
        }
        Assert.Throws<InvalidDataException>(() => WorkspaceBackup.Restore(tampered, Path.Combine(_root, "r1")));
        Assert.False(File.Exists(Path.Combine(_root, "r1", "workspace.json")));

        var extra = Path.Combine(_root, "extra.zip");
        File.Copy(zip, extra);
        using (var a = ZipFile.Open(extra, ZipArchiveMode.Update))
        {
            var e = a.CreateEntry("evidence/" + new string('a', 32) + ".txt");
            using var w = new StreamWriter(e.Open()); w.Write("x");
        }
        Assert.Throws<InvalidDataException>(() => WorkspaceBackup.Restore(extra, Path.Combine(_root, "r2")));

        var traversal = Path.Combine(_root, "trav.zip");
        File.Copy(zip, traversal);
        using (var a = ZipFile.Open(traversal, ZipArchiveMode.Update))
        {
            var e = a.CreateEntry("evidence/../../evil.txt");
            using var w = new StreamWriter(e.Open()); w.Write("x");
        }
        Assert.Throws<InvalidDataException>(() => WorkspaceBackup.Restore(traversal, Path.Combine(_root, "r3")));
    }
}
