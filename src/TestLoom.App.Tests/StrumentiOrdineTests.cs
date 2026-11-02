using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata all'ordine degli STRUMENTI stessi (spec riordino campi 2026-09-23 §2, seguito):
/// StrumentoDiagnostico.Ordine, ▲/▼ in StrumentiViewModel (pagina master), e lettura dello stesso ordine dal
/// modulo Test (TestCaseEditorViewModel.CaricaStrumenti). Non copre i campi DI uno strumento (già coperti da
/// CampoOrdinamentoServiceTests/TestFormPreviewRendererTests), solo l'elenco degli strumenti stessi.</summary>
[Collection("Localization")]
public sealed class StrumentiOrdineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_StrumentiOrdine_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateMasterState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Master Test", "1234");
        return state;
    }

    [Fact]
    public void Strumenti_MaiRiordinati_TabellaMasterMostraOrdineAlfabeticoStorico()
    {
        // Nessun Ordine impostato (dati come prima di questa modifica): la tabella master deve continuare a
        // mostrare l'ordine alfabetico di sempre — nessun cambio visibile.
        var state = CreateMasterState();
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = "ValidationTool" },
            new() { Nome = "Breakout Box" },
            new() { Nome = "CaptureTool" },
        });

        var vm = new StrumentiViewModel(state);

        Assert.Equal(new[] { "Breakout Box", "CaptureTool", "ValidationTool" }, vm.ItemsAttivi.Select(s => s.Nome));
    }

    [Fact]
    public void SpostaSuStrumento_CambiaOrdineNellaTabellaMaster()
    {
        var state = CreateMasterState();
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = "ValidationTool" },
            new() { Nome = "Breakout Box" },
            new() { Nome = "CaptureTool" },
        });

        var vm = new StrumentiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var capturetool = vm.ItemsAttivi.First(s => s.Nome == "CaptureTool");

        // Ordine alfabetico di partenza: Breakout Box, CaptureTool, ValidationTool. Sposta CaptureTool su -> deve precedere
        // Breakout Box.
        vm.SpostaSuStrumentoCommand.Execute(capturetool);

        Assert.Equal(new[] { "CaptureTool", "Breakout Box", "ValidationTool" }, vm.ItemsAttivi.Select(s => s.Nome));
    }

    [Fact]
    public async Task SpostaSuStrumento_SalvaEPersisteLOrdine()
    {
        var state = CreateMasterState();
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = "ValidationTool" },
            new() { Nome = "Breakout Box" },
        });

        var vm = new StrumentiViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var validationtool = vm.ItemsAttivi.First(s => s.Nome == "ValidationTool");
        vm.SpostaSuStrumentoCommand.Execute(validationtool); // ValidationTool davanti a Breakout Box
        await vm.SalvaCommand.ExecuteAsync(null);

        var ricaricati = state.Master.Strumenti.Load();
        var venusRicaricato = ricaricati.First(s => s.Nome == "ValidationTool");
        var breakoutRicaricato = ricaricati.First(s => s.Nome == "Breakout Box");
        Assert.True(venusRicaricato.Ordine < breakoutRicaricato.Ordine);
    }

    // Nota: non testiamo qui l'ordine "grezzo" di CaricaStrumenti quando Ordine è tutto a zero (nessun
    // riordino mai fatto) confrontandolo con l'ordine fisico di scrittura — l'ordine restituito da
    // MasterDataRepository.Load() in quel caso è un dettaglio dello storage condiviso (Phase D), non una
    // garanzia di questo servizio: l'unica garanzia di CampoOrdinamentoService è che sia STABILE (qualunque
    // ordine arrivi in ingresso, quello resta), già coperta da CampoOrdinamentoServiceTests. Qui testiamo solo
    // il comportamento che dipende dal nostro codice: una volta scritto un Ordine esplicito (via ▲/▼ e Salva),
    // il modulo Test lo rispetta — vedi il test seguente.

    [Fact]
    public async Task TestCaseEditor_DopoRiordinoInStrumentiViewModel_ElencoStrumentiSeguelOrdineSalvato()
    {
        var state = CreateMasterState();
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = "ValidationTool" },
            new() { Nome = "Breakout Box" },
        });

        // Ordine alfabetico di partenza in tabella master: Breakout Box, ValidationTool. Sposta ValidationTool su (era già
        // secondo alfabeticamente, quindi questo SCRIVE davvero un Ordine esplicito diverso da zero) — solo
        // così il modulo Test (che legge Ordine puro, senza base alfabetica) riflette la scelta reale.
        var strumentiVm = new StrumentiViewModel(state);
        strumentiVm.IniziaModificaCommand.AttendiModifica();
        var validationtool = strumentiVm.ItemsAttivi.First(s => s.Nome == "ValidationTool");
        strumentiVm.SpostaSuStrumentoCommand.Execute(validationtool); // ValidationTool davanti a Breakout Box
        await strumentiVm.SalvaCommand.ExecuteAsync(null);

        var editorVm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        Assert.Equal(new[] { "ValidationTool", "Breakout Box" }, editorVm.StrumentiOptions.Select(o => o.Valore));
    }
}
