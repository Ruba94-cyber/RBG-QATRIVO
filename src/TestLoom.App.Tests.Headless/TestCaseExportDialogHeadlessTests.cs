using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TestLoom.App.Services;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class TestCaseExportDialogHeadlessTests
{
    private static Button Bottone(Window finestra, string id) =>
        Assert.Single(finestra.GetVisualDescendants().OfType<Button>(),
            c => AutomationProperties.GetAutomationId(c) == id);

    [Fact]
    public void DialogoTest_ParteDaiFiltriVisibili_IgnoraFiltriSoloSeSpuntato()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ExportDlg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        AppState? state = null;
        Window? owner = null;
        EsportaOpzioniDialogo? dialogo = null;
        Task? chiusura = null;
        var filtroAttivo = true;
        var applicazioni = 0;
        try
        {
            LayoutHeadlessSessione.Esegui(() =>
            {
                state = TestStateFactory.Create();
                state.InizializzaCartella(dir);
                ExportConfigurazioniService.SalvaUltimaSelezione(state, TestCaseExportSelezioneService.Pagina,
                    new ExportConfigurazione { IgnoraFiltri = true, Sezioni = ["Test"] });
                owner = new Window { Width = 880, Height = 700 };
                owner.Show();
                var descrittore = TestCaseExportSelezioneService.CreaDescrittore(
                    filtri:
                    [
                        new ExportFiltroBooleano("attivo", "Test attivi",
                            () => filtroAttivo, v => filtroAttivo = v)
                    ],
                    applicaFiltriInBlocco: applica => { applicazioni++; applica(); });
                dialogo = new EsportaOpzioniDialogo(state, descrittore);
                chiusura = dialogo.Finestra.ShowDialog(owner);
                var ignora = Assert.Single(dialogo.Finestra.GetVisualDescendants().OfType<CheckBox>(),
                    c => AutomationProperties.GetAutomationId(c) == "EsportaOpzioni_TuttiITest");
                Assert.False(ignora.IsChecked == true); // anche se ultimo export era "tutti"
                var filtro = Assert.Single(dialogo.Finestra.GetVisualDescendants().OfType<CheckBox>(),
                    c => AutomationProperties.GetAutomationId(c) == "EsportaOpzioni_Filtro_attivo");
                Assert.True(filtro.IsChecked == true);
                ignora.IsChecked = true;
                Assert.False(filtro.IsEnabled);
                Bottone(dialogo.Finestra, "EsportaOpzioni_Esporta")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            var opzioni = dialogo!.Risultato.GetAwaiter().GetResult();
            Assert.NotNull(opzioni);
            Assert.True(opzioni.IgnoraFiltri);
            Assert.Equal(0, applicazioni);
            Assert.True(filtroAttivo); // ignorare non cambia la pagina
            LayoutHeadlessSessione.Esegui(() => LayoutHeadlessSessione.AttendiComando(chiusura!, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            LayoutHeadlessSessione.Esegui(() => owner?.Close());
            state?.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void DialogoTest_ConfermaCriteriSoloAllaFine_EAnnullaNonLiTocca()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ExportDlgFilters_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        AppState? state = null;
        Window? owner = null;
        var filtroAttivo = true;
        var applicazioni = 0;
        try
        {
            LayoutHeadlessSessione.Esegui(() =>
            {
                state = TestStateFactory.Create();
                state.InizializzaCartella(dir);
                owner = new Window { Width = 880, Height = 700 };
                owner.Show();
            });

            void Esegui(bool annulla)
            {
                EsportaOpzioniDialogo? dialogo = null;
                Task? chiusura = null;
                LayoutHeadlessSessione.Esegui(() =>
                {
                    var descrittore = TestCaseExportSelezioneService.CreaDescrittore(
                        filtri:
                        [
                            new ExportFiltroBooleano("attivo", "Test attivi",
                                () => filtroAttivo, v => filtroAttivo = v)
                        ],
                        applicaFiltriInBlocco: applica => { applicazioni++; applica(); });
                    dialogo = new EsportaOpzioniDialogo(state!, descrittore);
                    chiusura = dialogo.Finestra.ShowDialog(owner!);
                    var casella = Assert.Single(dialogo.Finestra.GetVisualDescendants().OfType<CheckBox>(),
                        c => AutomationProperties.GetAutomationId(c) == "EsportaOpzioni_Filtro_attivo");
                    casella.IsChecked = false;
                    Assert.True(filtroAttivo); // la pagina non cambia mentre si modifica il dialogo
                    Bottone(dialogo.Finestra, annulla ? "EsportaOpzioni_Annulla" : "EsportaOpzioni_Esporta")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                Assert.Equal(!annulla, dialogo!.Risultato.GetAwaiter().GetResult() != null);
                LayoutHeadlessSessione.Esegui(() => LayoutHeadlessSessione.AttendiComando(chiusura!, TimeSpan.FromSeconds(2)));
            }

            Esegui(annulla: true);
            Assert.True(filtroAttivo);
            Assert.Equal(0, applicazioni);
            Esegui(annulla: false);
            Assert.False(filtroAttivo);
            Assert.Equal(1, applicazioni);
        }
        finally
        {
            LayoutHeadlessSessione.Esegui(() => owner?.Close());
            state?.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
