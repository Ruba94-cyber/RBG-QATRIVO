using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.Views;
using Avalonia;

namespace TestLoom.App.Tests;

/// <summary>Finestra semplificata di risoluzione dei conflitti Test: riepilogo senza decisioni, solo le decisioni rimaste,
/// conferma abilitata a scelte complete, preselezione del valore piu' recente.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class TestMergeDecisionsWindowHeadlessTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 9, 0, 0);

    private static T PerId<T>(Visual radice, string id) where T : Visual =>
        radice.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);

    private static byte[] Test(Guid uid, Action<TestCase> mod, params Esecuzione[] rev)
    {
        var tc = new TestCase { Id = "T-H-1", Uid = uid, Descrizione = "base", DescrizioneBreve = "breve", CreatoDa = "x", ModificatoDa = "x" };
        tc.Esecuzioni.AddRange(rev);
        mod(tc);
        return JsonSerializer.SerializeToUtf8Bytes(tc, JsonIo.Options);
    }

    private static Esecuzione Rev(int it, int giorno, string op) =>
        new() { Iterazione = it, Data = T0.AddDays(giorno), Operatore = op, Matricola = "V" };

    private static MergeUiRequest Richiesta(byte[] mia, byte[] altra) =>
        new("Test T-H-1", "", RecordMergeEngine.Prepare(RecordMergeKind.TestCase, mia, altra).Plan!, "Questa copia", "Altra copia");

    [Fact]
    public void SenzaDecisioni_SoloRiepilogoEConferma()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            var uid = Guid.NewGuid();
            var w = new TestMergeDecisionsWindow(Richiesta(
                Test(uid, _ => { }, Rev(1, 0, "Anna"), Rev(2, 3, "Anna")),
                Test(uid, _ => { }, Rev(1, 0, "Anna"), Rev(2, 2, "Bruno"))));
            try
            {
                LayoutHeadlessSessione.Mostra(w);
                var testo = PerId<TextBlock>(w, "TestMerge_Riepilogo").Text;
                Assert.Contains("Unite 3 REV da 2 copie, nessun dato scartato", testo);
                Assert.True(PerId<Button>(w, "TestMerge_Conferma_Button").IsEnabled);
                Assert.DoesNotContain(w.GetVisualDescendants().OfType<RadioButton>(), _ => true);
                Assert.True(w.Width <= 1024 && w.Height <= 700);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void ConDueDecisioni_ConfermaDisabilitataFinoAScelteComplete_EPreselezioneDelPiuRecente()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            var uid = Guid.NewGuid();
            // Descrizione e DescrizioneBreve diverse; autore/data: la copia mia e' piu' recente per Descrizione, l'altra per
            // DescrizioneBreve? Le date sono per copia (UltimaModifica): qui la mia e' piu' vecchia -> preselezione sull'altra.
            var mia = Test(uid, t => { t.Descrizione = "mia d"; t.DescrizioneBreve = "mia b"; t.ModificatoDa = "Anna"; t.UltimaModifica = T0.AddDays(1); }, Rev(1, 0, "Anna"));
            var altra = Test(uid, t => { t.Descrizione = "loro d"; t.DescrizioneBreve = "loro b"; t.ModificatoDa = "Bruno"; t.UltimaModifica = T0.AddDays(2); }, Rev(1, 0, "Anna"));
            var richiesta = Richiesta(mia, altra);
            Assert.Equal(2, richiesta.Plan.DecisioniManuali.Count);
            var w = new TestMergeDecisionsWindow(richiesta);
            try
            {
                LayoutHeadlessSessione.Mostra(w);
                // Preselezionato l'altro valore (piu' recente) per entrambe: conferma gia' abilitata.
                Assert.True(PerId<RadioButton>(w, "TestMerge_Decisione_Descrizione_Altra").IsChecked);
                Assert.True(PerId<RadioButton>(w, "TestMerge_Decisione_DescrizioneBreve_Altra").IsChecked);
                Assert.True(PerId<Button>(w, "TestMerge_Conferma_Button").IsEnabled);
                Assert.Contains("Bruno", string.Join("|", w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)));
                PerId<RadioButton>(w, "TestMerge_Decisione_Descrizione_Mia").IsChecked = true;
                LayoutHeadlessSessione.Assesta(w);
                Assert.Equal(RecordFieldChoice.Mia, new[] { "Descrizione" }.Select(n => MergeScelta(w, n)).Single());
            }
            finally { w.Close(); }
        });
    }

    private static RecordFieldChoice MergeScelta(TestMergeDecisionsWindow w, string campo) =>
        PerId<RadioButton>(w, "TestMerge_Decisione_" + campo + "_Mia").IsChecked == true ? RecordFieldChoice.Mia : RecordFieldChoice.Loro;

    [Fact]
    public void DateUguali_NessunaPreselezione_ConfermaDisabilitataFinoAlleScelte()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            var uid = Guid.NewGuid();
            var mia = Test(uid, t => { t.Descrizione = "mia d"; t.DescrizioneBreve = "mia b"; t.UltimaModifica = T0; }, Rev(1, 0, "Anna"));
            var altra = Test(uid, t => { t.Descrizione = "loro d"; t.DescrizioneBreve = "loro b"; t.UltimaModifica = T0; }, Rev(1, 0, "Anna"));
            var w = new TestMergeDecisionsWindow(Richiesta(mia, altra));
            try
            {
                LayoutHeadlessSessione.Mostra(w);
                var conferma = PerId<Button>(w, "TestMerge_Conferma_Button");
                Assert.False(conferma.IsEnabled);
                PerId<RadioButton>(w, "TestMerge_Decisione_Descrizione_Mia").IsChecked = true;
                LayoutHeadlessSessione.Assesta(w);
                Assert.False(conferma.IsEnabled); // ne resta una
                PerId<RadioButton>(w, "TestMerge_Decisione_DescrizioneBreve_Altra").IsChecked = true;
                LayoutHeadlessSessione.Assesta(w);
                Assert.True(conferma.IsEnabled);
            }
            finally { w.Close(); }
        });
    }
}
