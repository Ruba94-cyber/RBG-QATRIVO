using Avalonia;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Schermate di risoluzione conflitti (Category=LayoutHeadless): confronto campo per campo con anteprima, e scelta
/// della versione intera in sola lettura. Nessun JSON visibile; "Applica" solo con tutte le scelte valide.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class ConflictResolverWindowsHeadlessTests
{
    private static T PerId<T>(Visual radice, string id) where T : Visual =>
        radice.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);

    private static byte[] Bytes(NonConformita nc) => JsonSerializer.SerializeToUtf8Bytes(nc, JsonIo.Options);

    private static RecordMergePlan Piano()
    {
        var uid = Guid.NewGuid();
        var mia = Bytes(new NonConformita { Id = "NC-H1", Uid = uid, Owner = "Anna", Descrizione = "uguale", RevisioneDati = 4 });
        var altra = Bytes(new NonConformita { Id = "NC-H1", Uid = uid, Owner = "Bruno", Descrizione = "uguale", RevisioneDati = 3, Severita = SeveritaAnomalia.High });
        return RecordMergeEngine.Prepare(RecordMergeKind.NonConformita, mia, altra).Plan!;
    }

    [Fact]
    public void Confronto_ApplicaSiAbilitaSoloConTutteLeScelte_EAnteprimaSenzaJson()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            var piano = Piano();
            var w = new RecordMergeWindow(new MergeUiRequest("Non Conformità NC-H1", "Copia rilevata", piano, "Versione corrente", "Versione in conflitto"));
            try
            {
                LayoutHeadlessSessione.Mostra(w);
                var applica = PerId<Button>(w, "RecordMerge_Applica_Button");
                Assert.False(applica.IsEnabled);

                var anteprima = PerId<StackPanel>(w, "RecordMerge_Anteprima");
                var testi = string.Join("|", anteprima.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
                Assert.DoesNotContain("{", testi);
                Assert.Contains("da scegliere", testi, StringComparison.OrdinalIgnoreCase);

                foreach (var campo in piano.Differences)
                    PerId<RadioButton>(w, "RecordMerge_Campo_" + campo.Name + "_TieniAltra").IsChecked = true;
                LayoutHeadlessSessione.Assesta(w);

                Assert.True(applica.IsEnabled);
                var risultato = PerId<TextBox>(w, "RecordMerge_Campo_Owner_Risultato");
                Assert.Equal("Bruno", risultato.Text);
                Assert.True(risultato.IsReadOnly);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Confronto_ScritturaAMano_ValoreModificabileEUsatoNellAnteprima()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            var piano = Piano();
            var w = new RecordMergeWindow(new MergeUiRequest("NC", "", piano, "Mia", "Altra"));
            try
            {
                LayoutHeadlessSessione.Mostra(w);
                foreach (var campo in piano.Differences.Where(c => c.Name != "Owner"))
                    PerId<RadioButton>(w, "RecordMerge_Campo_" + campo.Name + "_TieniMia").IsChecked = true;
                PerId<RadioButton>(w, "RecordMerge_Campo_Owner_Manuale").IsChecked = true;
                LayoutHeadlessSessione.Assesta(w);

                var risultato = PerId<TextBox>(w, "RecordMerge_Campo_Owner_Risultato");
                Assert.False(risultato.IsReadOnly);
                risultato.Text = "Carla";
                LayoutHeadlessSessione.Assesta(w);

                var testi = string.Join("|", PerId<StackPanel>(w, "RecordMerge_Anteprima").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
                Assert.Contains("Carla", testi);
                Assert.True(PerId<Button>(w, "RecordMerge_Applica_Button").IsEnabled);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Confronto_PulsanteVersioneIntera_ChiudeConEsitoVersioneIntera()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            var w = new RecordMergeWindow(new MergeUiRequest("NC", "", Piano(), "Mia", "Altra"));
            LayoutHeadlessSessione.Mostra(w);
            PerId<Button>(w, "RecordMerge_VersioneIntera_Button").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(MergeUiEsito.VersioneIntera, w.Esito?.Esito);
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Anteprima_ValoriConForegroundLeggibile_NelTemaScuroEChiaro(string tema)
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            var w = new RecordMergeWindow(new MergeUiRequest("NC", "", Piano(), "Mia", "Altra"));
            try
            {
                w.RequestedThemeVariant = tema == "Dark" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                LayoutHeadlessSessione.Mostra(w);
                foreach (var campo in Piano().Differences)
                    PerId<RadioButton>(w, "RecordMerge_Campo_" + campo.Name + "_TieniMia").IsChecked = true;
                LayoutHeadlessSessione.Assesta(w);

                var valori = PerId<StackPanel>(w, "RecordMerge_Anteprima").GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => (AutomationProperties.GetAutomationId(t) ?? "").StartsWith("RecordMerge_Anteprima_Valore_")).ToList();
                Assert.NotEmpty(valori);
                foreach (var v in valori)
                {
                    var brush = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(v.Foreground);
                    Assert.True(brush.Color.A > 200, "foreground trasparente: valore invisibile nel tema " + tema);
                    // Contrasto minimo col fondo effettivo del tema (luminanza relativa approssimata).
                    var sfondo = (w.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark) ? 0.0 : 1.0;
                    var c = brush.Color;
                    var lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
                    Assert.True(Math.Abs(lum - sfondo) > 0.35, $"contrasto insufficiente nel tema {tema}: luminanza testo {lum:0.00}");
                }
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void VersioneIntera_RigheDiverseEvidenziate_EMotivoChiaroPerVersioneNonScegliibile()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            var richiesta = new WholeVersionRequest("NC", "x",
                new WholeVersionSide("Corrente", "d", "t", true, null,
                    new[] { new WholeVersionField("Owner", "Anna", true), new WholeVersionField("Descrizione", "uguale", false) }),
                new WholeVersionSide("Altra", "d", "t", false, "Questa versione non si può tenere: il campo Priorità interna ha un valore non valido: High.",
                    new[] { new WholeVersionField("Owner", "Bruno", true), new WholeVersionField("Descrizione", "uguale", false) }));
            var w = new WholeVersionWindow(richiesta);
            try
            {
                LayoutHeadlessSessione.Mostra(w);
                var diverse = w.GetVisualDescendants().OfType<Border>()
                    .Count(b => (AutomationProperties.GetAutomationId(b) ?? "").EndsWith("_RigaDiversa"));
                Assert.Equal(2, diverse);
                var avviso = PerId<TextBlock>(w, "VersioneIntera_Altra_NonLeggibile_Text");
                Assert.Contains("Priorità interna", avviso.Text);
                Assert.Contains("High", avviso.Text);
                Assert.Contains("cestino", avviso.Text);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void VersioneIntera_VersioneNonLeggibile_PuoEssereSoloScartata()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            var richiesta = new WholeVersionRequest("NC-H1", "La versione in conflitto non è leggibile.",
                new WholeVersionSide("Versione corrente", "Quella oggi salvata", "Titolo: X", true),
                new WholeVersionSide("Versione in conflitto", "Copia", "Contenuto non leggibile (12 byte, impronta ABCD1234).", false));
            var w = new WholeVersionWindow(richiesta);
            try
            {
                LayoutHeadlessSessione.Mostra(w);
                Assert.True(PerId<Button>(w, "VersioneIntera_Mia_Tieni_Button").IsEnabled);
                Assert.False(PerId<Button>(w, "VersioneIntera_Altra_Tieni_Button").IsEnabled);
                Assert.True(PerId<TextBlock>(w, "VersioneIntera_Altra_NonLeggibile_Text").IsEffectivelyVisible);
                Assert.True(PerId<TextBox>(w, "VersioneIntera_Altra_Contenuto").IsReadOnly);
                Assert.True(PerId<TextBox>(w, "VersioneIntera_Mia_Contenuto").IsReadOnly);

                PerId<Button>(w, "VersioneIntera_Mia_Tieni_Button").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(WholeVersionChoice.TieniMia, w.Scelta);
            }
            finally { w.Close(); }
        });
    }
}
