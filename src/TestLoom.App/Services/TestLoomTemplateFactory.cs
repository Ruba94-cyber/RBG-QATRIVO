using TestLoom.App.Models;
using TestLoom.App.Setup;

namespace TestLoom.App.Services;

/// <summary>
/// Builds the editable test templates of a "starting point" (a kind of testing: software, hardware, manufacturing,
/// laboratory, blank). Each template starts from the application's own standard structure, so every page, filter and
/// report works on it exactly as on any template, and then gets the sections of the starting point. Nothing here
/// touches existing data: the caller decides what to save.
/// </summary>
public static class TestLoomTemplateFactory
{
    public static IReadOnlyList<TestTemplate> Create(string presetId)
    {
        var blueprints = TestLoomTemplateBlueprints.Create(presetId);
        // The blank starting point has no suggested templates: it still needs one to create the first test with.
        if (blueprints.Count == 0)
            blueprints = new[] { new TestTemplateBlueprint("Test", "🧪", Array.Empty<TestSectionBlueprint>()) };

        var result = new List<TestTemplate>();
        for (var index = 0; index < blueprints.Count; index++)
        {
            var blueprint = blueprints[index];
            // A fresh instance with the standard sections and fields; the tool pre-selection and the links to other
            // templates of the standard set do not belong to a neutral starting point.
            var template = TestTemplateDefaults.Seed()[0];
            template.Uid = Guid.NewGuid();
            template.Nome = blueprint.Name;
            template.Icona = blueprint.Icon;
            template.Ordine = index;
            template.StrumentiPreselezionati = new List<string>();
            foreach (var section in blueprint.Sections)
            {
                template.Sezioni.Add(new SezioneTemplate
                {
                    Nome = section.Name,
                    Ordine = template.Sezioni.Count,
                    Campi = section.Fields.Select((field, fieldIndex) => new CampoTemplate
                    {
                        Nome = field.Name,
                        Ordine = fieldIndex,
                        Tipo = TipoCampoTemplate.CampoLibero,
                        TipoLibero = TipoCampoStrumento.Testo,
                        Obbligatorio = field.Required
                    }).ToList()
                });
            }
            result.Add(template);
        }
        return result;
    }
}
