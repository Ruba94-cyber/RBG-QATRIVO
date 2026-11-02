using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura P1 per le azioni di scrittura (Modifica/Annulla/Salva) delle sotto-sezioni Master data
/// prive finora di qualunque test FlaUI (Operatori, componente, Veicoli, Requisiti, Funzionalita): fino a questa
/// suite i soli pulsanti azione con AutomationId erano quelli di Strumenti/Tipi di test. Verifica il vero
/// eseguibile Avalonia sui due comportamenti che un test unitario sul ViewModel non prova: il toggle reale dei
/// pulsanti Modifica/Annulla/Salva a schermo e la sopravvivenza dei dati seminati dopo un ciclo Modifica→Salva
/// (nessuna scrittura eseguita da questi test: si limita a rientrare in sola lettura sullo stesso dato
/// seminato, senza toccare celle della DataGrid — l'aggiunta di una riga nuova via UI richiede l'editing diretto
/// delle celle DataGrid, non ancora coperto, vedi COPERTURA_FLAUI.md).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
public sealed class MasterDataAzioniModificaTests : UiTestBase
{
    private const string EcuSeminata = "Componente-Seed-Modifica";
    private const string VeicoloSeminato = "Veicolo-Seed-Modifica";
    private const string RequisitoSeminato = "Requisito seminato per il test di modifica";
    private const string FunzionalitaSeminata = "Funzionalita-Seed-Modifica";
    private const string OperatoreSeminato = "Operatore Seed";

    public MasterDataAzioniModificaTests() : base(CreaWorkspaceConDatiMaster()) { }

    private static TestWorkspace CreaWorkspaceConDatiMaster()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(MasterDataAzioniModificaTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        var componente = state.Master.Componente.Load();
        componente.Add(new Componente { Nome = EcuSeminata, ModificatoDa = "UITests" });
        state.Master.Componente.Save(componente);

        var veicoli = state.Master.Veicoli.Load();
        veicoli.Add(new Veicolo { Nome = VeicoloSeminato, ModificatoDa = "UITests" });
        state.Master.Veicoli.Save(veicoli);

        var requisiti = state.Master.Requisiti.Load();
        requisiti.Add(new Requisito { Id = "REQ-SEED-1", Testo = RequisitoSeminato, ApplicabileBanco = true, ModificatoDa = "UITests" });
        state.Master.Requisiti.Save(requisiti);

        var funzionalita = state.Master.Funzionalita.Load();
        funzionalita.Add(new Funzionalita { Nome = FunzionalitaSeminata, ModificatoDa = "UITests" });
        state.Master.Funzionalita.Save(funzionalita);

        var operatori = state.Master.Operatori.Load();
        var seminato = new Operatore
        {
            Nome = OperatoreSeminato,
            Iniziali = "OS",
            PinHash = PinService.Hash("4242"),
            RecoveryCodeHash = PinService.Hash(PinService.GeneraCodiceRecupero()),
            Ruolo = RuoloOperatore.Standard,
            RichiedeCambioPin = false,
            ModificatoDa = "UITests",
        };
        RuoloSigilloService.Sigilla(seminato); // 6.4.2: ruolo sigillato, altrimenti vale Viewer.
        operatori.Add(seminato);
        state.Master.Operatori.Save(operatori);

        return workspace;
    }

    [Theory]
    [Trait("Category", "Regression")]
    [InlineData("componente", "Componente", EcuSeminata)]
    [InlineData("veicoli", "Veic", VeicoloSeminato)]
    [InlineData("requisiti", "Requis", RequisitoSeminato)]
    [InlineData("funzionalita", "Funz", FunzionalitaSeminata)]
    [InlineData("operatori", "Oper", OperatoreSeminato)]
    public void CicloModificaAnnulla_TornaInSolaLettura_SenzaPerdereIlDatoSeminato(string sottoSezioneId, string prefix, string valoreAtteso)
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var impostazioni = shell.GoToImpostazioni();
            impostazioni.VaiA(sottoSezioneId);

            var editor = new MasterDataEditorPage(window, prefix);
            Assert.True(editor.PuoModificare(), $"Pulsante Modifica ('{prefix}_Modifica_Button') non presente in sola lettura.");
            Assert.False(editor.PuoSalvare(), "Salva non dovrebbe essere visibile prima di entrare in modifica.");

            Wait.ForCondition(() => TrovaTesto(editor.Grid, valoreAtteso) != null,
                $"il dato seminato '{valoreAtteso}' compare in griglia");

            editor.IniziaModifica();
            Wait.ForCondition(editor.PuoSalvare, "pulsante Salva visibile dopo Modifica");
            Assert.False(editor.PuoModificare(), "Modifica non dovrebbe restare visibile durante la modifica.");

            editor.Annulla();
            Wait.ForCondition(editor.PuoModificare, "torna in sola lettura dopo Annulla");

            // Il dato seminato deve essere ancora in tabella: Annulla non deve aver cancellato nulla.
            Wait.ForCondition(() => TrovaTesto(editor.Grid, valoreAtteso) != null,
                $"il dato seminato '{valoreAtteso}' e' ancora presente dopo Annulla");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(CicloModificaAnnulla_TornaInSolaLettura_SenzaPerdereIlDatoSeminato), ex);
        }
    }

    private static FlaUI.Core.AutomationElements.AutomationElement? TrovaTesto(FlaUI.Core.AutomationElements.AutomationElement scope, string testo) =>
        scope.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
             .FirstOrDefault(e => (e.Name ?? string.Empty).Contains(testo, StringComparison.OrdinalIgnoreCase));
}
