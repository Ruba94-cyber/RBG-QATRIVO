using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata al riordino "a blocchi" dei restanti campi classici di Dettagli (Funzionalità/
/// Descrizione breve/Descrizione) e Riferimenti (Requisiti collegati/Test correlati) — spec riordino campi
/// 2026-09-23 §2, seguito coordinamento 2026-09-24. A differenza di Veicolo/Componente/Matricola/Baseline (celle di una
/// griglia compatta) questi sono blocchi a piena larghezza che occupano righe riservate fisse in un Grid al
/// posto della precedente StackPanel — qui testiamo solo il calcolo del rango/riga (TestCaseEditorViewModel),
/// non il rendering XAML.</summary>
[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class TestCaseEditorBlocchiClassiciOrdineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_blocchiordine_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private static CampoTemplate CampoSistemaIn(TestTemplate template, string chiave) =>
        template.CampiPianificazione
            .Concat(template.Sezioni.SelectMany(s => s.Campi))
            .Concat(template.CampiRiferimenti)
            .First(c => c.Tipo == TipoCampoTemplate.CampoSistema && c.CampoSistemaChiave == chiave);

    [Fact]
    public void Default_NessunRiordino_RigheSeguelOrdineStoricoDiSempre()
    {
        // Seed 6.3.0 (TestTemplateDefaults): Funzionalita/DescrizioneBreve/Descrizione già nell'ordine fisico
        // mostrato da sempre — nessuna anomalia come Componente/Veicolo, quindi anche senza alcun riordino esplicito
        // le righe riservate devono già corrispondere alla sequenza storica (0, 4, 5).
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);

        Assert.Equal(0, vm.RigaFunzionalita);
        Assert.Equal(4, vm.RigaStrumentiUsati);
        Assert.Equal(5, vm.RigaDescrizioneBreve);
        Assert.Equal(6, vm.RigaDescrizione);
        Assert.Equal(0, vm.RigaRequisitiCollegati);
        Assert.Equal(1, vm.RigaTestCorrelati);
        Assert.Equal(2, vm.RigaTicketCollegati);
    }

    [Fact]
    public void DopoRiordinoEsplicitoNelTemplate_StrumentiUsatiPrimaDiFunzionalita_RigheSiScambiano()
    {
        // Generalizzazione (review indipendente PASS, coordinamento 2026-09-24): Strumenti usati era prima una
        // card fissa separata — ora è un blocco riordinabile come Funzionalità/Descrizioni.
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);

        var template = state.Master.TestTemplate.Load().First(t => !t.IsChangeRequest);
        var sezione = template.Sezioni.First(s => s.Campi.Any(c => c.CampoSistemaChiave == CampiSistema.StrumentiUsati));
        var funzionalita = sezione.Campi.First(c => c.CampoSistemaChiave == CampiSistema.Funzionalita);
        var strumentiUsati = sezione.Campi.First(c => c.CampoSistemaChiave == CampiSistema.StrumentiUsati);

        while (CampoOrdinamentoService.SpostaSu(sezione.Campi, strumentiUsati)
               && CampoOrdinamentoService.OrdinePerVisualizzazione(sezione.Campi).IndexOf(strumentiUsati)
                  > CampoOrdinamentoService.OrdinePerVisualizzazione(sezione.Campi).IndexOf(funzionalita)) { }
        state.Master.TestTemplate.Save(state.Master.TestTemplate.Load().Select(t => t.Uid == template.Uid ? template : t).ToList());

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili.First(t => t.Uid == template.Uid));

        Assert.True(vm.RigaStrumentiUsati < vm.RigaFunzionalita);
    }

    [Fact]
    public void DopoRiordinoEsplicitoInRiferimenti_TicketCollegatiPrimaDiRequisiti_RigheSiScambiano()
    {
        // Generalizzazione (review indipendente PASS, coordinamento 2026-09-24): Ticket collegati era prima
        // una card fissa separata — ora è un blocco riordinabile come Requisiti collegati/Test correlati.
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);

        var template = state.Master.TestTemplate.Load().First(t => !t.IsChangeRequest);
        var ticketCollegati = CampoSistemaIn(template, CampiSistema.TicketCollegati);
        var requisitiCollegati = CampoSistemaIn(template, CampiSistema.RequisitiCollegati);
        while (CampoOrdinamentoService.SpostaSu(template.CampiRiferimenti, ticketCollegati)
               && CampoOrdinamentoService.OrdinePerVisualizzazione(template.CampiRiferimenti).IndexOf(ticketCollegati)
                  > CampoOrdinamentoService.OrdinePerVisualizzazione(template.CampiRiferimenti).IndexOf(requisitiCollegati)) { }
        state.Master.TestTemplate.Save(state.Master.TestTemplate.Load().Select(t => t.Uid == template.Uid ? template : t).ToList());

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili.First(t => t.Uid == template.Uid));

        Assert.True(vm.RigaTicketCollegati < vm.RigaRequisitiCollegati);
    }

    [Fact]
    public void DopoRiordinoEsplicitoNelTemplate_DescrizioneBrevePrimaDiFunzionalita_RigheSiScambiano()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);

        var template = state.Master.TestTemplate.Load().First(t => !t.IsChangeRequest);
        var sezione = template.Sezioni.First(s => s.Campi.Any(c => c.CampoSistemaChiave == CampiSistema.Funzionalita));
        var funzionalita = sezione.Campi.First(c => c.CampoSistemaChiave == CampiSistema.Funzionalita);
        var descrizioneBreve = sezione.Campi.First(c => c.CampoSistemaChiave == CampiSistema.DescrizioneBreve);

        // Simula esattamente cosa fa TestTemplateEditorWindow quando l'utente clicca ▲/▼: sposta
        // DescrizioneBreve su finché precede Funzionalita, usando lo stesso servizio generico.
        while (CampoOrdinamentoService.SpostaSu(sezione.Campi, descrizioneBreve)
               && CampoOrdinamentoService.OrdinePerVisualizzazione(sezione.Campi).IndexOf(descrizioneBreve)
                  > CampoOrdinamentoService.OrdinePerVisualizzazione(sezione.Campi).IndexOf(funzionalita)) { }
        state.Master.TestTemplate.Save(state.Master.TestTemplate.Load().Select(t => t.Uid == template.Uid ? template : t).ToList());

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili.First(t => t.Uid == template.Uid));

        Assert.True(vm.RigaDescrizioneBreve < vm.RigaFunzionalita);
    }

    [Fact]
    public void DopoRiordinoEsplicitoInRiferimenti_TestCorrelatiPrimaDiRequisiti_RigheSiScambiano()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);

        var template = state.Master.TestTemplate.Load().First(t => !t.IsChangeRequest);
        var testCorrelati = CampoSistemaIn(template, CampiSistema.TestCorrelati);
        CampoOrdinamentoService.SpostaSu(template.CampiRiferimenti, testCorrelati);
        state.Master.TestTemplate.Save(state.Master.TestTemplate.Load().Select(t => t.Uid == template.Uid ? template : t).ToList());

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili.First(t => t.Uid == template.Uid));

        Assert.True(vm.RigaTestCorrelati < vm.RigaRequisitiCollegati);
    }
}
