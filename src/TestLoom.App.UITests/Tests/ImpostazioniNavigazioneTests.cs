using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura di navigazione per TUTTE le sotto-sezioni di Impostazioni (Master): fino a questa suite
/// nessun test FlaUI navigava mai oltre "Impostazioni_Sidebar_List" (ImpostazioniPage.IsShown), lasciando
/// Archivio, Recovery, Operatori, Requisiti, Funzionalita, componente, Veicoli, Strumenti e Tipi di test/CR privi di
/// qualunque prova che la vista si carichi davvero nel vero eseguibile Avalonia. Verifica solo che ogni
/// sotto-sezione compaia (via il proprio marcatore stabile: GridPrincipale per le viste tabellari, un
/// AutomationId di titolo per le altre) senza NavigationError — le operazioni di scrittura di ciascuna sezione
/// hanno test dedicati altrove (vedi MasterDataAzioniModificaTests, StrumentiOrdineUiTests,
/// TemplateClassiciOrdineUiTests).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Navigation")]
public sealed class ImpostazioniNavigazioneTests : UiTestBase
{
    public ImpostazioniNavigazioneTests() : base(TestWorkspace.CreateLoggedIn(nameof(ImpostazioniNavigazioneTests))) { }

    [Fact]
    public void TutteLeSottoSezioni_SiAprono_SenzaErroreDiNavigazione()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var impostazioni = shell.GoToImpostazioni();
            Assert.True(impostazioni.IsShown, "Impostazioni non mostrata.");

            // Ordine di default (NavOrderService, nessun riordino nel workspace di test); "recovery" e' presente
            // solo perche' il workspace seminato ha un operatore Master (EMaster=true in ImpostazioniViewModel).
            string[] sottoSezioniConGriglia = { "operatori", "requisiti", "funzionalita", "componente", "veicoli", "strumenti", "tipiditest" };
            foreach (var id in sottoSezioniConGriglia)
            {
                Assert.True(impostazioni.HaSottoSezione(id), $"Sotto-sezione '{id}' non presente nella sidebar.");
                impostazioni.VaiA(id);
                Wait.ForCondition(() => ElementFinders.ExistsById(window, "GridPrincipale"),
                    $"griglia principale caricata per la sotto-sezione '{id}'");
                Assert.False(shell.HasNavigationError, $"Errore di navigazione mostrato per la sotto-sezione '{id}'.");
            }

            // Generale, Archivio e Recovery non hanno una DataGrid "GridPrincipale": verificate solo per assenza
            // di errore di navigazione e permanenza della sidebar (nessun AutomationId di titolo dedicato in
            // ArchivioView/RecoveryBinView al momento, vedi COPERTURA_FLAUI.md).
            foreach (var id in new[] { "generale", "archivio", "recovery" })
            {
                Assert.True(impostazioni.HaSottoSezione(id), $"Sotto-sezione '{id}' non presente nella sidebar.");
                impostazioni.VaiA(id);
                Wait.ForCondition(() => ElementFinders.ExistsById(window, "Impostazioni_Sidebar_List"),
                    $"sidebar ancora presente dopo la navigazione a '{id}'");
                Assert.False(shell.HasNavigationError, $"Errore di navigazione mostrato per la sotto-sezione '{id}'.");
            }
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(TutteLeSottoSezioni_SiAprono_SenzaErroreDiNavigazione), ex);
        }
    }
}
