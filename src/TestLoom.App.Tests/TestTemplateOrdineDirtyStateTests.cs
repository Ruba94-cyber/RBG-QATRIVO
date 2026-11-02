using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata alla revisione F2/F3 (.ai/artifacts/review_riordino.json): aprire l'editor di
/// un template su dati con Ordine non ancora allineato non deve, da solo, marcare l'editor come "modificato"
/// né scrivere nulla — solo un'azione di riordino esplicita dell'utente lo fa. CampoOrdinamentoService non
/// normalizza più nulla alla sola lettura (OrdinePerVisualizzazione), quindi qui verifichiamo l'invariante a
/// livello di MasterDataEditorViewModelBase.HaModificheNonSalvate(), che è ciò che pilota sia il prompt
/// "conferma abbandono modifiche" su Annulla sia cosa finisce nel Changelog al Salva.</summary>
public sealed class TestTemplateOrdineDirtyStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_TplOrdineDirty_" + Guid.NewGuid().ToString("N"));

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

    /// <summary>Template con Ordine "legacy": tutte le Sezioni/Campi a Ordine=0 (mai riordinati prima di
    /// questa funzionalità) — il caso che avrebbe fatto scattare una Normalizza scrivente nella versione
    /// precedente della finestra.</summary>
    private static TestTemplate TemplateConOrdineLegacy() => new()
    {
        Nome = "Template Legacy",
        Sezioni = new List<SezioneTemplate>
        {
            new() { Nome = "S1", Campi = new List<CampoTemplate> { new() { Nome = "C1", Tipo = TipoCampoTemplate.CampoLibero }, new() { Nome = "C2", Tipo = TipoCampoTemplate.CampoLibero } } },
            new() { Nome = "S2" },
        },
    };

    [Fact]
    public void IniziaModifica_SenzaAlcunRiordinoUtente_NonRisultaModificato()
    {
        var state = CreateMasterState();
        var templates = state.Master.TestTemplate.Load();
        templates.Add(TemplateConOrdineLegacy());
        state.Master.TestTemplate.Save(templates);

        var vm = new TestTemplateViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();

        // Simula l'apertura di "Gestisci contenuto...": solo LETTURE (OrdinePerVisualizzazione), come fa ora
        // TestTemplateEditorWindow all'apertura — nessuna Normalizza scrivente.
        var template = vm.Items.First(t => t.Nome == "Template Legacy");
        _ = CampoOrdinamentoService.OrdinePerVisualizzazione(template.Sezioni);
        _ = CampoOrdinamentoService.OrdinePerVisualizzazione(template.Sezioni[0].Campi);

        Assert.False(vm.HaModificheNonSalvate());
    }

    [Fact]
    public void SpostaSuEsplicito_RisultaModificato()
    {
        var state = CreateMasterState();
        var templates = state.Master.TestTemplate.Load();
        templates.Add(TemplateConOrdineLegacy());
        state.Master.TestTemplate.Save(templates);

        var vm = new TestTemplateViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var template = vm.Items.First(t => t.Nome == "Template Legacy");

        CampoOrdinamentoService.SpostaSu(template.Sezioni[0].Campi, template.Sezioni[0].Campi[1]);

        Assert.True(vm.HaModificheNonSalvate());
    }

    [Fact]
    public async Task SpostaSuEsplicito_PersisteLOrdineAlSalvataggio()
    {
        var state = CreateMasterState();
        var templates = state.Master.TestTemplate.Load();
        templates.Add(TemplateConOrdineLegacy());
        state.Master.TestTemplate.Save(templates);

        var vm = new TestTemplateViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var template = vm.Items.First(t => t.Nome == "Template Legacy");
        CampoOrdinamentoService.SpostaSu(template.Sezioni[0].Campi, template.Sezioni[0].Campi[1]);

        await vm.SalvaCommand.ExecuteAsync(null);

        var salvato = state.Master.TestTemplate.Load().First(t => t.Nome == "Template Legacy");
        var visuale = CampoOrdinamentoService.OrdinePerVisualizzazione(salvato.Sezioni[0].Campi);
        Assert.Equal("C2", visuale[0].Nome);
        Assert.Equal("C1", visuale[1].Nome);
    }
}
