using Avalonia.Controls;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class CampoSistemaTipoClickHeadlessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_CampoSistemaTipoClick_" + Guid.NewGuid().ToString("N"));
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "TestLoom_CampoSistemaTipoClickConfig_" + Guid.NewGuid().ToString("N"));
    private AppState? _state;
    private CampoSistemaDefinizioneViewModel? _viewModel;

    public void Dispose()
    {
        _viewModel?.Dispose();
        _state?.Dispose();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, recursive: true); } catch { } }
        if (Directory.Exists(_configDir)) { try { Directory.Delete(_configDir, recursive: true); } catch { } }
    }

    [Fact]
    public void CambioTipoListaDaMenuPuntatore_ApreEditorAncheSenzaFocusTastiera() => LayoutHeadlessSessione.Esegui(() =>
    {
        var state = CreaStatoConCampoTest();
        var vm = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        _viewModel = vm;
        LayoutHeadlessSessione.AttendiComando(vm.IniziaModificaCommand.ExecuteAsync(null), TimeSpan.FromSeconds(30));
        Assert.True(vm.InModifica);

        var view = new CampoSistemaDefinizioneView { DataContext = vm };
        var window = new Window { Width = 1440, Height = 900, Content = view };
        try
        {
            LayoutHeadlessSessione.Mostra(window);
            var combo = view.GetVisualDescendants().OfType<ComboBox>()
                .Single(c => c.DataContext is CampoSistemaDefinizione { Chiave: "test-pointer-list" });

            combo.IsDropDownOpen = true;
            LayoutHeadlessSessione.Assesta(window);
            Assert.False(combo.IsKeyboardFocusWithin, "il test deve esercitare il menu aperto senza focus tastiera sulla cella");

            combo.SelectedItem = TipoCampoStrumento.Lista;
            LayoutHeadlessSessione.Assesta(window);

            var editor = Assert.Single(window.OwnedWindows.OfType<ListaOpzioniEditorWindow>());
            Assert.True(editor.IsVisible);
            Console.WriteLine($"CAMPO_SISTEMA_CLICK|dropDown={combo.IsDropDownOpen}|focusTastiera={combo.IsKeyboardFocusWithin}|editorOpzioni={editor.IsVisible}");
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Hide();
        }
    });

    private AppState CreaStatoConCampoTest()
    {
        SeedDemoDataService.Genera(_dir);
        Directory.CreateDirectory(_configDir);
        var state = new AppState(new AppConfigService(Path.Combine(_configDir, "config.json")), runBackgroundMaintenance: false);
        _state = state;
        state.InizializzaCartella(_dir);
        var master = state.Master.Operatori.Load().Single(o => o.Ruolo == RuoloOperatore.Master);
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));

        var campi = state.Master.CampiSistemaDettagli.Load();
        campi.Add(new CampoSistemaDefinizione
        {
            Chiave = "test-pointer-list", Etichetta = "AAA Pointer Test", Gruppo = GruppoCampoSistema.Dettagli,
            Ruolo = RuoloCampoSistema.Nessuno, Tipo = TipoCampoStrumento.Testo,
        });
        state.Master.CampiSistemaDettagli.Save(campi);
        return state;
    }
}
