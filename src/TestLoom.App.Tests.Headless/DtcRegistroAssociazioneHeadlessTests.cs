using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Pagina Registro guasto con associazione a requisito Extra: pulsante "Da requisito Extra", selezione multipla che abilita
/// "Associa le N selezionate" e colonna con lo stato Da associare / Associato.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class DtcRegistroAssociazioneHeadlessTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 5, 4, 9, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_DtcAssocHeadless_" + Guid.NewGuid().ToString("N"));
    private AppState? _state;

    public void Dispose()
    {
        _state?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AppState CreaStato()
    {
        Directory.CreateDirectory(_dir);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "ECM" } });
        var r = new Requisito { Id = "R-Extra", Testo = "t", DtcConteggio = 0 };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        state.Master.Requisiti.Save(new List<Requisito> { r });
        foreach (var (id, guasto) in new[] { ("T-1", "P0100"), ("T-2", "P0200") })
        {
            var test = new TestCase { Id = id, Uid = Guid.NewGuid(), DataCreazione = Day, UltimaModifica = Day, ModificatoDa = "Master Prova",
                Veicolo = "V1", Veicoli = ["V1"], Componente = new EcuSelection(["ECM"]) };
            test.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = Day, Operatore = "Master Prova", Esito = Esito.Conforme,
                Condizioni = [new Condizione { Guasto = guasto, DtcPasso = PassoDtc.Validazione, DtcVeicolo = "V1", Esito = Esito.Conforme, Componente = new EcuSelection(["ECM"]) }] });
            state.TestCases.Save(test, isNew: true);
        }
        return state;
    }

    private static Button Pulsante(Control radice, string id) =>
        radice.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == id);

    [Fact]
    public void Pagina_MostraDaRequisito_ESelezioneMultiplaAbilitaAssociaSelezionate() => LayoutHeadlessSessione.Esegui(() =>
    {
        _state = CreaStato();
        using var vm = new DtcRegistroViewModel(_state);
        var vista = new DtcRegistroView { DataContext = vm };
        var finestra = new Window { Width = 1500, Height = 900, Content = vista };
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            // Il caricamento delle righe termina sul thread UI: si fa girare il dispatcher finché non sono arrivate.
            for (var i = 0; i < 300 && (vm.Caricamento || vm.Righe.Count != 2); i++) { LayoutHeadlessSessione.Assesta(finestra); Thread.Sleep(50); }
            Assert.Equal(2, vm.Righe.Count);
            LayoutHeadlessSessione.Assesta(finestra);

            Assert.True(Pulsante(vista, "DtcRegistro_AssociaDaRequisito_Button").IsEnabled);
            Assert.False(Pulsante(vista, "DtcRegistro_AssociaSelezionate_Button").IsVisible);

            var griglia = vista.GetVisualDescendants().OfType<DataGrid>().Single(g => AutomationProperties.GetAutomationId(g) == "DtcRegistro_Righe_Grid");
            Assert.Equal(DataGridSelectionMode.Extended, griglia.SelectionMode);
            griglia.SelectedItems.Add(vm.Righe[0]);
            griglia.SelectedItems.Add(vm.Righe[1]);
            LayoutHeadlessSessione.Assesta(finestra);

            Assert.Equal(2, vm.RigheSelezionate.Count);
            var associa = Pulsante(vista, "DtcRegistro_AssociaSelezionate_Button");
            Assert.True(associa.IsVisible);
            Assert.Contains("2", associa.Content?.ToString());
            Assert.Contains(griglia.Columns, c => c.Header?.ToString() == LocalizationService.Instance.Traduci("DtcRegistroColAssociazione"));
            Assert.All(vm.Righe, r => Assert.Equal(LocalizationService.Instance.Traduci("DtcRegistroDaAssociare"), r.StatoAssociazioneTesto));
        }
        finally { finestra.Hide(); }
    });
}
