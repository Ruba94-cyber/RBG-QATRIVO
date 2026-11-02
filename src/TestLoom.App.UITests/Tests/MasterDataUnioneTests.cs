using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura FlaUI del pannello Unisci/Rinomina/Dividi nel catalogo componente: visibilità delle azioni,
/// merge e split completati dalla UI con verifica dei componenti e dei riferimenti persistiti.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
public sealed class MasterDataUnioneTests : UiTestBase
{
    private const string EcuA = "Componente-Unione-A";
    private const string EcuB = "Componente-Unione-B";
    private const string EcuSplitA = "Componente-Unione-A-Split-1";
    private const string EcuSplitB = "Componente-Unione-A-Split-2";
    private const string NomeFunzionalita = "Funz-componente-Unione-QA";
    private const string TestId = "TC-componente-UNIONE-QA";

    public MasterDataUnioneTests() : base(CreaWorkspaceConDueEcu()) { }

    private static TestWorkspace CreaWorkspaceConDueEcu()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(MasterDataUnioneTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        var componente = state.Master.Componente.Load();
        componente.Add(new Componente { Nome = EcuA, ModificatoDa = "UITests" });
        componente.Add(new Componente { Nome = EcuB, ModificatoDa = "UITests" });
        state.Master.Componente.Save(componente);

        var funzionalita = state.Master.Funzionalita.Load();
        funzionalita.Add(new Funzionalita
        {
            Nome = NomeFunzionalita,
            TutteLeEcu = false,
            EcuAssociate = { EcuA },
            ModificatoDa = "UITests",
        });
        state.Master.Funzionalita.Save(funzionalita);
        TestCoverageSeed.SeedTest(state, TestId, "Test collegato al componente da unire o dividere",
            TestCoverageSeed.UidTemplatePrincipale(state), EcuA, Esito.Conforme);
        return workspace;
    }

    [Fact]
    public void SelezionareUnaRiga_MostraLeAzioniUnisciRinominaDividi()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var impostazioni = shell.GoToImpostazioni();
            var componente = impostazioni.VaiAEcu();

            var griglia = componente.Grid;
            Wait.ForCondition(() => TrovaTesto(griglia, EcuA) != null, "il componente seminata compare in griglia");

            // Nessun AutomationId sulla cella (DataGridTextColumn semplice, stesso limite gia' noto per
            // StrumentiOrdineUiTests): selezione via click reale sul testo della cella, che Avalonia DataGrid
            // traduce in selezione riga (ElementoSelezionato -> Unione.ChiaveSelezionata).
            TrovaTesto(griglia, EcuA)!.SafeInvoke();

            // Pannello in un Expander chiuso di default (feedback 2026-10-01: la griglia recupera spazio): le
            // azioni compaiono solo dopo averlo aperto.
            var expander = ElementFinders.WaitById(window, "ComponenteUnioneExpander");
            Assert.False(ElementFinders.ExistsById(window, "ComponenteComboUnisciIn"), "Azioni Unisci visibili con il pannello ancora chiuso.");
            expander.Patterns.ExpandCollapse.Pattern.Expand();

            Wait.ForCondition(() => ElementFinders.ExistsById(window, "ComponenteComboUnisciIn"),
                "il combo 'Unisci in' compare dopo aver selezionato una riga");
            Assert.True(ElementFinders.ExistsById(window, "ComponenteBtnUnisci"), "Pulsante Unisci non trovato dopo la selezione.");
            Assert.True(ElementFinders.ExistsById(window, "ComponenteTextRinominaIn"), "Campo Rinomina in non trovato dopo la selezione.");
            Assert.True(ElementFinders.ExistsById(window, "ComponenteBtnRinomina"), "Pulsante Rinomina non trovato dopo la selezione.");
            Assert.True(ElementFinders.ExistsById(window, "ComponenteTextDividiIn"), "Campo Dividi in non trovato dopo la selezione.");
            Assert.True(ElementFinders.ExistsById(window, "ComponenteBtnDividi"), "Pulsante Dividi non trovato dopo la selezione.");

            var comboUnisciIn = ElementFinders.WaitComboBoxById(window, "ComponenteComboUnisciIn");
            Wait.ForCondition(() => comboUnisciIn.Items.Any(i => i.Text == EcuB),
                "l'altra componente seminata (EcuB) e' proponibile come bersaglio dell'unione");

            // In modifica il pannello sparisce del tutto (non solo disabilitato) e riappare dopo Annulla.
            componente.IniziaModifica();
            Wait.ForCondition(() => !ElementFinders.ExistsById(window, "ComponenteUnioneExpander"),
                "il pannello Unisci/Rinomina/Dividi e' nascosto in modifica");
            componente.Annulla();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "ComponenteUnioneExpander"),
                "il pannello Unisci/Rinomina/Dividi riappare uscendo dalla modifica");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(SelezionareUnaRiga_MostraLeAzioniUnisciRinominaDividi), ex);
        }
    }

    [Fact]
    public void Componente_UnisciDaCatalogo_RiassegnaIRiferimentiEToglieLaVoceDuplicata()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToImpostazioni().VaiAEcu();
            var pannello = new MasterUnionePannelloPage(App, window, "Componente");
            pannello.Espandi();
            pannello.SelezionaVoce(EcuA);
            pannello.UnisciIn(EcuB);

            Wait.ForCondition(() =>
            {
                var catalogo = EcuPersistite();
                var funzionalita = FunzionalitaPersistite().SingleOrDefault(f => f.Nome == NomeFunzionalita);
                var test = TestCoverageSeed.TrovaTestCase(Workspace, TestId);
                return !catalogo.Any(e => e.Nome == EcuA)
                    && catalogo.Any(e => e.Nome == EcuB)
                    && funzionalita?.EcuAssociate.SequenceEqual(new[] { EcuB }) == true
                    && test?.Componente.SequenceEqual(new[] { EcuB }) == true;
            }, "il merge componente rimuove il duplicato e aggiorna funzionalità e Test", TimeSpan.FromSeconds(30));

            Assert.DoesNotContain(EcuA, EcuPersistite().Select(e => e.Nome));
            Assert.Contains(EcuB, EcuPersistite().Select(e => e.Nome));
            Assert.Equal(new[] { EcuB }, Assert.Single(FunzionalitaPersistite(), f => f.Nome == NomeFunzionalita).EcuAssociate);
            Assert.Equal(new[] { EcuB }, TestCoverageSeed.TrovaTestCase(Workspace, TestId)!.Componente);
            Assert.Null(pannello.Errore);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Componente_UnisciDaCatalogo_RiassegnaIRiferimentiEToglieLaVoceDuplicata), ex);
        }
    }

    [Fact]
    public void Componente_DividiDaCatalogo_CreaLeVociECopiaIRiferimentiSenzaRimuovereLOriginale()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToImpostazioni().VaiAEcu();
            var pannello = new MasterUnionePannelloPage(App, window, "Componente");
            pannello.Espandi();
            pannello.SelezionaVoce(EcuA);
            pannello.Dividi(EcuSplitA, EcuSplitB);

            var nomiAttesi = new[] { EcuA, EcuSplitA, EcuSplitB }.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Wait.ForCondition(() =>
            {
                var catalogo = EcuPersistite().Select(e => e.Nome).ToHashSet(StringComparer.Ordinal);
                var funzionalita = FunzionalitaPersistite().SingleOrDefault(f => f.Nome == NomeFunzionalita);
                var test = TestCoverageSeed.TrovaTestCase(Workspace, TestId);
                return nomiAttesi.All(catalogo.Contains)
                    && funzionalita?.EcuAssociate.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(nomiAttesi) == true
                    && test?.Componente.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(nomiAttesi) == true;
            }, "lo split componente mantiene l'originale e propaga i riferimenti alle nuove voci", TimeSpan.FromSeconds(30));

            Assert.Equal(nomiAttesi, EcuPersistite().Where(e => nomiAttesi.Contains(e.Nome)).Select(e => e.Nome)
                .OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(nomiAttesi, Assert.Single(FunzionalitaPersistite(), f => f.Nome == NomeFunzionalita).EcuAssociate
                .OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(nomiAttesi, TestCoverageSeed.TrovaTestCase(Workspace, TestId)!.Componente
                .OrderBy(n => n, StringComparer.Ordinal));
            Assert.Null(pannello.Errore);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Componente_DividiDaCatalogo_CreaLeVociECopiaIRiferimentiSenzaRimuovereLOriginale), ex);
        }
    }

    private IReadOnlyList<Componente> EcuPersistite() =>
        new MasterDataRepository<Componente>(new DataPaths(Workspace.WorkspaceDir).EcuFile).Load();

    private IReadOnlyList<Funzionalita> FunzionalitaPersistite() =>
        new MasterDataRepository<Funzionalita>(new DataPaths(Workspace.WorkspaceDir).FunzionalitaFile).Load();

    private static FlaUI.Core.AutomationElements.AutomationElement? TrovaTesto(FlaUI.Core.AutomationElements.AutomationElement scope, string testo) =>
        scope.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).FirstOrDefault(e => e.Name == testo);
}
