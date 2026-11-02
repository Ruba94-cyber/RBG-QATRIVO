using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Wait = TestLoom.App.UITests.Infrastructure.Wait;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// 6.7.2: un campo di sistema di tipo Lista marcato «usabile come filtro» ha nella Lista Test un filtro a selezione
/// multipla, come in Home e Report, con le opzioni del catalogo (per esempio VCI Connection). Il filtro è sempre visibile
/// fuori dai filtri avanzati; scegliendo due opzioni la lista mostra i Test di entrambe (unione), senza il Test privo del
/// valore. Dati sintetici: un campo con tre opzioni nel catalogo, un template che lo usa, tre Test (due con valore).
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
[Trait("Area", "TestLista")]
public sealed class ListaTestFiltroCampoSistemaUiTests : IDisposable
{
    private const string EtichettaCampo = "VCI Connection";
    private const string OpzioneUsb = "VCI USB";
    private const string OpzioneLan = "VCI LAN";
    private const string OpzioneWifi = "VCI Wi-Fi";
    private const string TestUsb = "T-VCI1";
    private const string TestLan = "T-VCI2";
    private const string TestSenzaValore = "T-VCI3";

    private static readonly string ChiaveCampo =
        CampoSistemaCatalogoService.GeneraChiaveDaEtichetta(EtichettaCampo, Enumerable.Empty<string>());
    private static readonly string IdFiltro = $"FiltroCampoSistema_{ChiaveCampo}_Button";

    private readonly TestWorkspace _workspace;

    public ListaTestFiltroCampoSistemaUiTests() => _workspace = Semina();

    public void Dispose() => _workspace.Dispose();

    /// <summary>Catalogo con il campo di tipo Lista (filtrabile), template che lo usa, tre Test: due con valore diverso, uno senza.</summary>
    private static TestWorkspace Semina()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(ListaTestFiltroCampoSistemaUiTests), seedTestFixtures: true);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);

        var campi = state.Master.CampiSistemaDettagli.Load();
        campi.Add(new CampoSistemaDefinizione
        {
            Chiave = ChiaveCampo,
            Etichetta = EtichettaCampo,
            Gruppo = GruppoCampoSistema.Dettagli,
            Tipo = TipoCampoStrumento.Lista,
            OpzioniLista = new List<string> { OpzioneUsb, OpzioneLan, OpzioneWifi },
            UsabileComeFiltro = true,
            ModificatoDa = "UITests",
        });
        state.Master.CampiSistemaDettagli.Save(campi);

        var templates = state.Master.TestTemplate.Load();
        var template = templates.First(t => !t.Archiviata);
        template.Sezioni.First(s => s.Nome == "Dettagli").Campi.Add(
            new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = ChiaveCampo, Obbligatorio = false });
        state.Master.TestTemplate.Save(templates);

        foreach (var (id, valore) in new[] { (TestUsb, OpzioneUsb), (TestLan, OpzioneLan), (TestSenzaValore, (string?)null) })
        {
            var test = new TestCase
            {
                Id = id,
                DescrizioneBreve = $"Test {id}",
                Componente = new EcuSelection([TestWorkspace.DefaultEcuName]),
                TestTemplateUid = template.Uid,
                DataCreazione = DateTime.Now,
                UltimaModifica = DateTime.Now,
                ModificatoDa = "UITests",
            };
            if (valore != null) test.CampiCustom[ChiaveCampo] = valore;
            state.TestCases.Save(test, isNew: true);
        }
        return ws;
    }

    [Fact]
    public void FiltroCampoSistemaLista_SelezioneMultiplaRestringeLaListaTest()
    {
        using var app = TestLoomApplication.Launch(_workspace);
        try
        {
            var principale = app.WaitForMainWindow(TimeSpan.FromSeconds(30));
            Wait.ForCondition(() => MainShellPage.IsShown(principale), "shell principale visibile", TimeSpan.FromSeconds(30));
            var shell = new MainShellPage(principale, app);
            shell.DismissNotificationsIfShown();
            var lista = shell.GoToTestList();
            lista.ApriFiltriAvanzati();

            Wait.ForCondition(() => lista.VisibleRowIds().Count == 3, "tre Test visibili senza filtri", TimeSpan.FromSeconds(20));

            SelezionaValore(principale, app, OpzioneUsb);
            Wait.ForCondition(() => SonoQuelli(lista.VisibleRowIds(), TestUsb),
                $"solo '{TestUsb}' con '{OpzioneUsb}'", TimeSpan.FromSeconds(15));

            // Seconda opzione: selezione multipla, la lista mostra i Test di entrambe le opzioni.
            SelezionaValore(principale, app, OpzioneLan);
            Wait.ForCondition(() => SonoQuelli(lista.VisibleRowIds(), TestUsb, TestLan),
                $"'{TestUsb}' e '{TestLan}' dopo la selezione di entrambe le opzioni", TimeSpan.FromSeconds(15));
            Assert.DoesNotContain(TestSenzaValore, lista.VisibleRowIds());
        }
        catch (Exception ex)
        {
            var dir = FailureArtifacts.Capture(app, nameof(FiltroCampoSistemaLista_SelezioneMultiplaRestringeLaListaTest), ex, step: "filtro campo di sistema Lista");
            throw new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
        }
    }

    [Fact]
    public void FiltroCampoSistemaLista_InHome_LaSceltaDelleOpzioniCompareNelFiltro()
    {
        using var app = TestLoomApplication.Launch(_workspace);
        try
        {
            var principale = app.WaitForMainWindow(TimeSpan.FromSeconds(30));
            Wait.ForCondition(() => MainShellPage.IsShown(principale), "shell principale visibile", TimeSpan.FromSeconds(30));
            var shell = new MainShellPage(principale, app);
            shell.DismissNotificationsIfShown();
            var home = shell.GoToHome();
            home.ApriFiltriAvanzati();

            // Stesso filtro della Lista Test, con le opzioni del catalogo, anche in Home: il pulsante riporta la scelta.
            SelezionaValore(principale, app, OpzioneUsb);
            Wait.ForCondition(() => ElementFinders.WaitButtonById(principale, IdFiltro).Name.Contains(OpzioneUsb, StringComparison.Ordinal),
                $"filtro del campo in Home con '{OpzioneUsb}'", TimeSpan.FromSeconds(15));

            // Bug f2: verifica che il riepilogo del filtro attivo della Home indichi 1 test (corrispondente al filtro "VCI USB"), non 0.
            Wait.ForCondition(() =>
            {
                var riepilogo = ElementFinders.WaitById(principale, "Home_FiltroAttivo_Text");
                return riepilogo.Name.Contains("1 test corrispondente", StringComparison.Ordinal);
            }, "riepilogo Home mostra 1 test corrispondente per il filtro VCI USB", TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            var dir = FailureArtifacts.Capture(app, nameof(FiltroCampoSistemaLista_InHome_LaSceltaDelleOpzioniCompareNelFiltro), ex, step: "filtro campo di sistema Lista in Home");
            throw new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
        }
    }

    /// <summary>Apre il flyout del filtro, sceglie l'opzione e lo chiude con Esc (come nei filtri di Home e Report).</summary>
    private static void SelezionaValore(Window principale, TestLoomApplication app, string valore)
    {
        ElementFinders.WaitButtonById(principale, IdFiltro).SafeInvoke();
        var flyout = new FiltroMultiSelectPage(app);
        flyout.SelezionaVoce(valore);
        ElementActions.PressKey(principale, VirtualKeyShort.ESCAPE);
    }

    private static bool SonoQuelli(IReadOnlyList<string> righe, params string[] attese) =>
        righe.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(attese.OrderBy(x => x, StringComparer.Ordinal));
}
