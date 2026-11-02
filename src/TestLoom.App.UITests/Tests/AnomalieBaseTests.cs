using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Prima copertura FlaUI per la navigazione/i filtri base della pagina Anomalie/CR: NonConformitaPage
/// esisteva gia' (usata da FiltroAmbitoSenzaTestRealeTests su un archivio reale da 100k), ma nessun test la
/// esercitava su un workspace isolato/vuoto. Verifica solo che la pagina si apra, che la ricerca testuale e il
/// filtro componente rispondano senza eccezioni su un catalogo vuoto, e che la barra di paginazione riporti "0" Non
/// Conformita'. Le transizioni di stato (NC/CR/Segnalazione, workflow InVerifica→...→Conclusa) e la creazione
/// di una NC/CR/Segnalazione dalla UI restano GAP (vedi COPERTURA_FLAUI.md): richiedono ChangeRequestEditorView
/// e SegnalazioneReteEditorView, che non hanno ancora AutomationId sui controlli di scrittura.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Navigation")]
public sealed class AnomalieBaseTests : UiTestBase
{
    public AnomalieBaseTests() : base(TestWorkspace.CreateLoggedIn(nameof(AnomalieBaseTests))) { }

    [Fact]
    public void PaginaAnomalie_SiApre_ERicercaEFiltroEcuRispondonoSuCatalogoVuoto()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var anomalie = shell.GoToAnomalie();

            Assert.True(anomalie.IsTitleVisible, "Titolo Anomalie non visibile.");
            Assert.False(shell.HasNavigationError, "Errore di navigazione mostrato per Anomalie.");

            Assert.Equal(0, anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 300));

            anomalie.Cerca("QA-INESISTENTE");
            Wait.ForCondition(() => anomalie.TestoPaginazione().Length > 0, "barra di paginazione aggiornata dopo la ricerca");
            Assert.Equal(0, anomalie.TotaleFiltrato());

            anomalie.ApriFiltroEcu();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_FiltroComponente_Button"),
                "il pulsante del filtro componente resta presente dopo l'apertura della tendina");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(PaginaAnomalie_SiApre_ERicercaEFiltroEcuRispondonoSuCatalogoVuoto), ex);
        }
    }
}
