using Avalonia.Controls;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

[Collection("LayoutHeadless")]
public sealed class TestTemplateEditorHeadlessTests
{
    [Fact]
    public void EditorRemovesReportControlsAndPreservesStoredSheetsAfterOtherEdit()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_TemplateEditor_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var state = TestStateFactory.Create();
        state.InizializzaCartella(root);
        Assert.True(state.InizializzazioneWorkspaceCompletata);
        var templates = state.Master.TestTemplate.Load();
        var template = templates.First(t => !t.IsChangeRequest && t.FogliReport.Count > 0);
        var expected = template.FogliReport.Select(f => (f.Uid, f.NomeFoglio,
            Columns: f.Colonne.Select(c => (c.Uid, c.Intestazione, c.Chiave)).ToArray())).ToArray();
        LayoutHeadlessSessione.Esegui(() =>
        {
            var window = TestTemplateEditorWindow.Crea(state, template, templates, state.Master.Strumenti.Load());
            try
            {
                LayoutHeadlessSessione.Mostra(window);
                var descendants = window.GetVisualDescendants().ToList();
                Assert.DoesNotContain(descendants.OfType<TextBlock>(), x =>
                    x.Text == LocalizationService.Instance.Traduci("TplFogliTitolo"));
                Assert.DoesNotContain(descendants.OfType<Button>(), x =>
                    x.Content?.ToString() == LocalizationService.Instance.Traduci("TplAggiungiFoglio"));
                var cr = Assert.Single(descendants.OfType<CheckBox>(), x =>
                    x.Content?.ToString() == LocalizationService.Instance.Traduci("TplChangeRequestCheck"));
                cr.IsChecked = true;
                Assert.True(template.IsChangeRequest);
            }
            finally { window.Close(); }
        });
        state.Master.TestTemplate.Save(templates);
        var saved = state.Master.TestTemplate.Load().Single(t => t.Uid == template.Uid);
        Assert.True(saved.IsChangeRequest);
        Assert.Equal(expected.Length, saved.FogliReport.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Uid, saved.FogliReport[i].Uid);
            Assert.Equal(expected[i].NomeFoglio, saved.FogliReport[i].NomeFoglio);
            Assert.Equal(expected[i].Columns, saved.FogliReport[i].Colonne.Select(c => (c.Uid, c.Intestazione, c.Chiave)).ToArray());
        }
    }
}
