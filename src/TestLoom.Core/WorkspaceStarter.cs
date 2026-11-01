namespace TestLoom.Core;

/// <summary>Builds a ready-to-use workspace from a preset: templates, a first plan and (optionally) example content.</summary>
public static class WorkspaceStarter
{
    public static WorkspaceDocument Create(string name, string presetId, string language = "en",
        bool includeSamples = false, string? tester = null)
    {
        var lang = TestLoomPresetInfo.NormalizeLanguage(language);
        var document = WorkspaceStore.Create(name, presetId);
        document.Language = lang;
        document.DefaultTester = (tester ?? "").Trim();
        if (document.DefaultTester.Length > 120) document.DefaultTester = document.DefaultTester[..120];

        WorkspaceTemplates.SeedFromPreset(document);
        if (lang == "it")
            foreach (var template in document.Templates)
            {
                template.Name = WorkspaceLocalization.T(template.Name, "it");
                foreach (var section in template.Sections)
                {
                    section.Name = WorkspaceLocalization.T(section.Name, "it");
                    foreach (var field in section.Fields) field.Name = WorkspaceLocalization.T(field.Name, "it");
                }
            }

        var card = TestLoomPresetInfo.Card(presetId, lang);
        document.Plans.Add(new WorkspaceTestPlan { Name = card.PlanLabel });

        if (includeSamples) AddSamples(document, card, lang);
        WorkspaceAudit.Record(document, document.DefaultTester, "workspace.created", document.Name, card.DisplayName);
        WorkspaceStore.Validate(document);
        return document;
    }

    /// <summary>Whether the document still carries the tests that <c>includeSamples</c> adds.</summary>
    private static void AddSamples(WorkspaceDocument document, TestLoomPresetCard card, string lang)
    {
        var it = lang == "it";
        var requirement = new WorkspaceRequirement
        {
            Key = "REQ-001",
            Title = it ? $"{card.SubjectLabel}: comportamento di base verificato" : $"{card.SubjectLabel}: basic behaviour verified",
            Description = it ? "Requisito di esempio, modificabile o eliminabile." : "Example requirement; edit or remove it."
        };
        document.Requirements.Add(requirement);

        var template = document.Templates.FirstOrDefault();
        var test = new WorkspaceTestCase
        {
            Title = it ? $"Verifica di base — {card.SubjectLabel}" : $"Basic check — {card.SubjectLabel}",
            TemplateName = template?.Name ?? "",
            Description = it ? "Test di esempio per iniziare." : "Example test to get started.",
            Priority = WorkspacePriority.High,
            Tags = new List<string> { it ? "esempio" : "example" }
        };
        test.RequirementIds.Add(requirement.Id);
        test.Steps.Add(new WorkspaceTestStep
        {
            Action = it ? $"Prepara {card.SubjectLabel.ToLowerInvariant()} per la prova" : $"Prepare the {card.SubjectLabel.ToLowerInvariant()} for the test",
            ExpectedResult = it ? "Pronto per la prova" : "Ready for testing"
        });
        test.Steps.Add(new WorkspaceTestStep
        {
            Action = it ? $"Esegui la verifica su {card.ComponentLabel.ToLowerInvariant()}" : $"Run the check on the {card.ComponentLabel.ToLowerInvariant()}",
            ExpectedResult = it ? "Il risultato corrisponde all'atteso" : "The result matches what is expected"
        });
        document.TestCases.Add(test);
        document.Plans[0].TestIds.Add(test.Id);
    }
}
