using TestLoom.App.Models;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura P1 per i permessi Master vs operatore non Master: fino a questa iterazione ogni test
/// FlaUI avviava RBG - QATRIVO.exe come Master (<see cref="TestWorkspace.CreateLoggedIn"/>), quindi nessuno verificava
/// mai dalla UI reale la sola lettura di un operatore Standard/Viewer. Usa il nuovo
/// <see cref="TestWorkspace.CreateLoggedInAs"/>.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
public sealed class RuoliPermessiTests : UiTestBase
{
    public RuoliPermessiTests() : base(TestWorkspace.CreateLoggedInAs(nameof(RuoliPermessiTests), RuoloOperatore.Standard)) { }

    [Fact]
    public void OperatoreStandard_NonVedeRecovery_NonPuoModificareOperatori_PinNascosto()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var impostazioni = shell.GoToImpostazioni();
            Assert.True(impostazioni.IsShown, "Impostazioni non mostrata a un operatore Standard.");

            // Recovery è inserita nella sidebar solo se _state.EMaster (ImpostazioniViewModel costruttore):
            // un operatore Standard non deve vederla affatto, non solo trovarla disabilitata.
            Assert.False(impostazioni.HaSottoSezione("recovery"), "'Recovery' non dovrebbe comparire nella sidebar di un operatore Standard.");

            var operatori = impostazioni.VaiAOperatori();
            Assert.False(operatori.PuoModificare(), "'Modifica' non dovrebbe comparire in Operatori per un operatore Standard (PuoModificare = EMaster && PuoScrivere).");

            // PIN/chiave di recupero altrui: il blocco riservato al Master non deve comparire, al suo posto
            // solo il messaggio "riservato al Master" (vedi OperatoriView.axaml, commento sul bug scoperto in
            // collaudo 2026-08-24).
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Oper_PinRiservatoMaster_Text"),
                "il messaggio 'PIN riservato al Master' compare per un operatore Standard");
            Assert.False(ElementFinders.ExistsById(window, "Oper_PinBlockMaster_Border"),
                "Il blocco PIN/chiave di recupero (riservato al Master) non deve essere raggiungibile da un operatore Standard.");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(OperatoreStandard_NonVedeRecovery_NonPuoModificareOperatori_PinNascosto), ex);
        }
    }

    [Fact]
    public void OperatoreStandard_PuoNavigareTestEAnomalie_SolaLetturaSuMasterData()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);

            // La navigazione base (Test, Anomalie) resta accessibile: la restrizione Master riguarda le
            // azioni di scrittura sui dati anagrafici, non la lettura operativa quotidiana.
            var testList = shell.GoToTestList();
            Assert.False(shell.HasNavigationError, "Errore di navigazione su Test per un operatore Standard.");

            var anomalie = shell.GoToAnomalie();
            Assert.True(anomalie.IsTitleVisible, "Titolo Anomalie non visibile per un operatore Standard.");

            var componente = shell.GoToImpostazioni().VaiAEcu();
            Assert.False(componente.PuoSalvare(), "Salva non dovrebbe comparire su componente per un operatore Standard in sola lettura.");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(OperatoreStandard_PuoNavigareTestEAnomalie_SolaLetturaSuMasterData), ex);
        }
    }
}
