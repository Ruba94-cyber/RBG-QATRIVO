using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata al punto 23 (feedback utente 2026-08-20, aggiunto a metà sessione): il foglio
/// report di un nuovo template deve generarsi da solo dai campi definiti, senza che il Master debba configurarlo
/// a mano — inverte la scelta del 17/08 ("sempre manuale"), ma resta personalizzabile: un template che ha già
/// almeno un foglio (manuale o auto-generato prima) non viene più toccato.</summary>
public sealed class FoglioReportAutoGenServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_FoglioReportAutoGen_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateMasterState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Master Test", "1234");
        return state;
    }

    private static TestTemplate CreaTemplateConCampi() => new()
    {
        Nome = "Template Prova",
        CampiPianificazione = new List<CampoTemplate> { new() { Nome = "Assegnatario", Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = "Assegnatario", Ordine = 0 } },
        Sezioni = new List<SezioneTemplate>
        {
            new() { Nome = "Dettagli", Ordine = 0, Campi = new List<CampoTemplate>
            {
                new() { Nome = "Note", Tipo = TipoCampoTemplate.CampoLibero, TipoLibero = TipoCampoStrumento.Testo, Ordine = 0 },
            }}
        }
    };

    [Fact]
    public void GeneraSeAssente_TemplateSenzaFogli_NeCreaUno()
    {
        var template = CreaTemplateConCampi();
        FoglioReportAutoGenService.GeneraSeAssente(template);

        var foglio = Assert.Single(template.FogliReport);
        Assert.Equal(2, foglio.Colonne.Count);
        Assert.Equal("Assegnatario", foglio.Colonne[0].Intestazione);
        Assert.Equal("Note", foglio.Colonne[1].Intestazione);
    }

    [Fact]
    public void GeneraSeAssente_CampoSistema_OrigineCampoSistemaConChiaveGiusta()
    {
        var template = CreaTemplateConCampi();
        FoglioReportAutoGenService.GeneraSeAssente(template);

        var colonna = template.FogliReport[0].Colonne[0];
        Assert.Equal(OrigineColonnaReport.CampoSistema, colonna.Origine);
        Assert.Equal("Assegnatario", colonna.Chiave);
    }

    [Fact]
    public void GeneraSeAssente_CampoLibero_OrigineCampoTemplateConUidComeChiave()
    {
        var template = CreaTemplateConCampi();
        var campoLibero = template.Sezioni[0].Campi[0];

        FoglioReportAutoGenService.GeneraSeAssente(template);

        var colonna = template.FogliReport[0].Colonne[1];
        Assert.Equal(OrigineColonnaReport.CampoTemplate, colonna.Origine);
        Assert.Equal(campoLibero.Uid.ToString(), colonna.Chiave);
    }

    [Fact]
    public void GeneraSeAssente_TemplateConFoglioGiaPresente_NonVieneToccato()
    {
        var template = CreaTemplateConCampi();
        var foglioManuale = new FoglioReportTemplate { NomeFoglio = "Foglio scelto dal Master", Colonne = new List<ColonnaReportTemplate> { new() { Intestazione = "Solo questa" } } };
        template.FogliReport.Add(foglioManuale);

        FoglioReportAutoGenService.GeneraSeAssente(template);

        Assert.Single(template.FogliReport);
        Assert.Equal("Foglio scelto dal Master", template.FogliReport[0].NomeFoglio);
    }

    [Fact]
    public void GeneraSeAssente_TemplateSenzaCampi_NonCreaFoglioVuoto()
    {
        var template = new TestTemplate { Nome = "Vuoto" };
        FoglioReportAutoGenService.GeneraSeAssente(template);
        Assert.Empty(template.FogliReport);
    }

    // Verifica end-to-end: il Master crea un template, aggiunge campi, salva — nessuna azione esplicita
    // sul foglio report, eppure al salvataggio è già presente.
    [Fact]
    public async Task TestTemplateViewModel_Salva_GeneraAutomaticamenteIlFoglioReport()
    {
        var state = CreateMasterState();
        var vm = new TestTemplateViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.NuovoCommand.Execute(null);
        var nuovo = vm.Items.Last();
        nuovo.Nome = "Template Nuovo";
        nuovo.Sezioni.Add(new SezioneTemplate
        {
            Nome = "Dettagli",
            Campi = new List<CampoTemplate> { new() { Nome = "Esito", Tipo = TipoCampoTemplate.CampoLibero, TipoLibero = TipoCampoStrumento.Lista, OpzioniListaLibero = new List<string> { "OK", "KO" } } }
        });

        await vm.SalvaCommand.ExecuteAsync(null);

        var salvato = state.Master.TestTemplate.Load().First(t => t.Nome == "Template Nuovo");
        Assert.Single(salvato.FogliReport);
        Assert.Contains(salvato.FogliReport[0].Colonne, c => c.Intestazione == "Esito");
    }
}
