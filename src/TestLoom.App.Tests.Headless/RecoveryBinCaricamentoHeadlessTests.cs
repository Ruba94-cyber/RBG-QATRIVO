using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Cestino (recovery-bin) nella shell reale a 1280x672 (Category=LayoutHeadless). Mentre la lettura gira in background
/// l'elenco e' ancora vuoto: la pagina mostra l'indicatore e non "nessun elemento". L'elenco e' un ListBox virtualizzato.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class RecoveryBinCaricamentoHeadlessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_RecBin_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState StatoMaster()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Cestino", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        state.Master.Operatori.Save(new List<Operatore> { op });
        state.ImpostaOperatore("Master Cestino", "1234");
        return state;
    }

    /// <summary>Archivia nel cestino condiviso un numero di segnalazioni, con lo stesso percorso di archiviazione dell'app.</summary>
    private static void ArchiviaSegnalazioni(AppState state, int quante)
    {
        var paths = state.Paths;
        for (var i = 0; i < quante; i++)
        {
            var sorgente = paths.SegnalazioneFile(Guid.NewGuid());
            File.WriteAllText(sorgente, "{\"Uid\":\"" + Guid.NewGuid() + "\",\"RevisioneDati\":2}");
            DomainRecoveryService.ArchiveOnly(paths, sorgente, DomainRecoveryCategory.Segnalazione, "Master Cestino");
        }
    }

    private static T Automazione<T>(Visual radice, string automationId) where T : Visual =>
        radice.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    [Fact]
    public async Task Cestino_MentreLeggeMostraIndicatoreENonNessunElemento_A1280x672()
    {
        using var blocco = new ManualResetEventSlim(false);
        var (vm, finestra, vista) = LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var stato = StatoMaster();
            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 672);
            var vm = new RecoveryBinViewModel(stato);
            // Lettura trattenuta fino a Set: l'elenco resta vuoto come durante la lettura reale in background.
            vm.GateLetturaPerTest = () => blocco.Wait();
            vm.AggiornaCommand.Execute(null);
            var vista = new RecoveryBinView();
            LayoutHeadlessSessione.Mostra(finestra);
            vista.DataContext = vm;   // dopo la finestra: la pagina legge il modo di layout reale alla creazione
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            return (vm, finestra, vista);
        });
        try
        {
            LayoutHeadlessSessione.Esegui(() =>
            {
                Assert.True(vm.CaricamentoInCorso);
                Assert.True(Automazione<StackPanel>(vista, "Recovery_Caricamento_Indicatore").IsEffectivelyVisible,
                    "indicatore spento mentre la lettura e' in corso");
                Assert.False(Automazione<TextBlock>(vista, "Recovery_NessunElemento_Text").IsEffectivelyVisible,
                    "'nessun elemento' mostrato mentre la lettura e' in corso");
                Assert.False(Automazione<ListBox>(vista, "Recovery_Elenco_ListBox").IsEffectivelyVisible);
            });

            blocco.Set();
            await vm.AttendiAggiornamentoAsync();
            LayoutHeadlessSessione.Esegui(() =>
            {
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.False(vm.CaricamentoInCorso);
                Assert.False(Automazione<StackPanel>(vista, "Recovery_Caricamento_Indicatore").IsEffectivelyVisible,
                    "indicatore ancora acceso a lettura conclusa");
                Assert.True(Automazione<TextBlock>(vista, "Recovery_NessunElemento_Text").IsEffectivelyVisible,
                    "cestino vuoto a lettura conclusa: manca il messaggio");
            });
        }
        finally
        {
            blocco.Set();
            LayoutHeadlessSessione.Esegui(() => finestra.Hide());
        }
    }

    [Fact]
    public async Task Cestino_ConMoltiElementi_ElencoVirtualizzato_A1280x672()
    {
        const int quanti = 60;
        var (vm, finestra, vista) = LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            var stato = StatoMaster();
            ArchiviaSegnalazioni(stato, quanti);
            var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(new MainViewModel(stato) { MostraPrimoAvvio = false }, 1280, 672);
            var vm = new RecoveryBinViewModel(stato);
            var vista = new RecoveryBinView();
            LayoutHeadlessSessione.Mostra(finestra);
            vista.DataContext = vm;
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = vista;
            LayoutHeadlessSessione.Assesta(finestra);
            return (vm, finestra, vista);
        });
        try
        {
            await vm.AttendiAggiornamentoAsync();
            LayoutHeadlessSessione.Esegui(() =>
            {
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.False(vm.CaricamentoInCorso);
                Assert.Equal(quanti, vm.Totale);

                var elenco = Automazione<ListBox>(vista, "Recovery_Elenco_ListBox");
                Assert.True(elenco.IsEffectivelyVisible);
                Assert.True(elenco.GetVisualDescendants().OfType<VirtualizingStackPanel>().Any(), "elenco senza VirtualizingStackPanel");
                // Solo le righe nell'area visibile sono materializzate, non tutte le sessanta.
                var materializzate = elenco.GetVisualDescendants().OfType<ListBoxItem>().Count();
                Assert.InRange(materializzate, 1, quanti - 1);
            });
        }
        finally
        {
            LayoutHeadlessSessione.Esegui(() => finestra.Hide());
        }
    }
}
