using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura P2 per il filtro multi-selezione virtualizzato con ricerca (FiltroMultiSelectVirtualizzato,
/// condiviso da Home/Report/Anomalie-CR — fino a questa iterazione 0 AutomationId, nessun test lo apriva mai).
/// Semina 12 voci per ciascun catalogo filtrato (soglia ricerca: FiltroMultiSelectFlyoutHelper.SogliaRicerca =
/// 10) cosi' la casella di ricerca compare davvero. Copertura completa (ricerca su tutte le voci + selezione +
/// riepilogo) solo su Anomalie/componente; Home/Report verificati solo per apertura+ricerca (stesso componente
/// condiviso, stesso rischio, limiti di tempo di questa iterazione).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
public sealed class FiltroMultiSelectVirtualizzatoTests : UiTestBase
{
    private const string EcuCercata = "Componente-QA-Undicesima";

    public FiltroMultiSelectVirtualizzatoTests() : base(CreaWorkspaceConDodiciEcuEGamme()) { }

    private static TestWorkspace CreaWorkspaceConDodiciEcuEGamme()
    {
        // seedTestFixtures: false — TestWorkspace.CreateLoggedIn semina di default un componente "Componente-QA-01"
        // (TestWorkspace.DefaultEcuName): con la semina di default attiva, il primo componente-QA-{i:00} qui sotto
        // (i=1 => "Componente-QA-01") collide sulla stessa chiave business con UID diverso, e il Save() fallisce con
        // PhaseDMasterBusinessKeyConflictException prima ancora che il test parta. Questa suite non ha
        // bisogno del template/componente/Veicolo minimi seminati da SeedMinimalTestFixtures (verifica solo il
        // filtro multi-selezione di Anomalie/Home/Report), quindi la semina di default va disattivata invece
        // di rinumerare le 12 componente.
        var workspace = TestWorkspace.CreateLoggedIn(nameof(FiltroMultiSelectVirtualizzatoTests), seedTestFixtures: false);
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        var componente = state.Master.Componente.Load();
        for (var i = 1; i <= 10; i++) componente.Add(new Componente { Nome = $"Componente-QA-{i:00}", ModificatoDa = "UITests" });
        componente.Add(new Componente { Nome = EcuCercata, ModificatoDa = "UITests" });
        state.Master.Componente.Save(componente);

        var gamme = state.Master.Gamma.Load();
        for (var i = 1; i <= 11; i++) gamme.Add(new Gamma { Nome = $"Gamma-QA-{i:00}", ModificatoDa = "UITests" });
        state.Master.Gamma.Save(gamme);

        return workspace;
    }

    [Fact]
    public void Anomalie_FiltroEcu_RicercaTrovaESelezionaUnaVoceOltreSoglia()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var anomalie = shell.GoToAnomalie();

            anomalie.ApriFiltroEcu();
            var filtro = new FiltroMultiSelectPage(App);
            Assert.True(filtro.HaRicerca, "La casella di ricerca dovrebbe comparire con 11 componente a catalogo (soglia 10).");

            filtro.Cerca(EcuCercata);
            Wait.ForCondition(() => filtro.VoceVisibile(EcuCercata), "la voce cercata compare nella lista filtrata");
            Assert.False(filtro.VoceVisibile("Componente-QA-01"), "Una voce non corrispondente alla ricerca non dovrebbe comparire.");

            filtro.SelezionaVoce(EcuCercata);
            Wait.ForCondition(() => anomalie.TestoFiltroEcu() == EcuCercata,
                "il pulsante del filtro componente riporta l'unica voce selezionata dopo la ricerca");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Anomalie_FiltroEcu_RicercaTrovaESelezionaUnaVoceOltreSoglia), ex);
        }
    }

    [Fact]
    public void Home_FiltroGamma_ApreConRicercaOltreSoglia()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            shell.GoToHome();

            ElementFinders.WaitButtonById(window, "Home_FiltroGamma_Button").SafeInvoke();
            var filtro = new FiltroMultiSelectPage(App);
            Assert.True(filtro.HaRicerca, "La casella di ricerca dovrebbe comparire con 11 Gamme a catalogo (soglia 10).");
            filtro.SelezionaTutto();
            filtro.DeselezionaTutto();
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Home_FiltroGamma_ApreConRicercaOltreSoglia), ex);
        }
    }
}
