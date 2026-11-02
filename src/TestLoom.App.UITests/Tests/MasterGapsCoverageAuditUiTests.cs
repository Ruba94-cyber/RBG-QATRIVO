using System.Text.RegularExpressions;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Keyboard = FlaUI.Core.Input.Keyboard;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Copertura FlaUI del dettaglio guasto dell'Audit non ancora esercitata: filtri componente e Stato (flyout multi-selezione dentro
/// "Altri filtri"), "Solo non completi" e esportazione CSV del dettaglio verso un file temporaneo del workspace di test.
/// Seme: un requisito Extra con tre guasto (P0100 Completo su componente-1, P0200 Validato su componente-1, P0300 Da fare su componente-2) ottenuti
/// da Condizioni guasto reali (validazione e devalidazione) su un Test eseguito per il veicolo di fixture.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Audit")]
public sealed class MasterGapsCoverageAuditUiTests : UiTestBase
{
    private const string Req = "REQ-Extra-AUD";
    private const string Componente1 = TestWorkspace.DefaultEcuName;
    private const string Componente2 = "Componente-QA-02";
    private const string Veh = TestWorkspace.DefaultVehicleName;
    private const string Guasto1 = "P0100";
    private const string Guasto2 = "P0200";
    private const string Guasto3 = "P0300";

    public MasterGapsCoverageAuditUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(MasterGapsCoverageAuditUiTests));
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);

        var componente = state.Master.Componente.Load();
        componente.First(e => e.Nome == Componente1).VeicoliAssociati = new List<string> { Veh };
        componente.Add(new TestLoom.App.Models.Componente { Nome = Componente2, VeicoliAssociati = new List<string> { Veh }, ModificatoDa = "UITests" });
        state.Master.Componente.Save(componente);

        var extra = new Requisito
        {
            Id = Req, Testo = "Requisito Extra con guasto", ApplicabileBanco = true, ModificatoDa = "UITests",
            Guasto = new List<DtcRequisito> { new() { Codice = Guasto1, Componente = Componente1 }, new() { Codice = Guasto2, Componente = Componente1 }, new() { Codice = Guasto3, Componente = Componente2 } },
        };
        RequisitoApplicabilita.ImpostaTipo(extra, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(extra, new[] { Veh });
        state.Master.Requisiti.Save(new List<Requisito> { extra });

        var tpl = state.Master.TestTemplate.Load().First(t => t.Nome == TestWorkspace.DefaultTemplateName);
        var tc = new TestCase
        {
            Id = "TC-AUD-guasto", DescrizioneBreve = "Test guasto audit", Componente = Componente1, Veicolo = Veh, Veicoli = { Veh },
            RequisitoId = { Req }, TestTemplateUid = tpl.Uid, ModificatoDa = "UITests",
        };
        var esecuzione = new Esecuzione
        {
            Iterazione = 1, Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V-AUD",
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = Req, Valore = PunteggioConformita.Conforme } },
        };
        esecuzione.Condizioni.Add(CondizioneDtc(Guasto1, PassoDtc.Validazione, Componente1));
        esecuzione.Condizioni.Add(CondizioneDtc(Guasto1, PassoDtc.Devalidazione, Componente1));
        esecuzione.Condizioni.Add(CondizioneDtc(Guasto2, PassoDtc.Validazione, Componente1));
        tc.Esecuzioni.Add(esecuzione);
        state.TestCases.Save(tc, true);
        return ws;
    }

    private static Condizione CondizioneDtc(string guasto, PassoDtc passo, string componente) => new()
    {
        Guasto = guasto, DtcRequisitoId = Req, DtcPasso = passo, DtcVeicolo = Veh, Esito = Esito.Conforme,
        Componente = new EcuSelection(new[] { componente }),
    };

    private static readonly Regex CodiceDtc = new(@"^P\d{4}$", RegexOptions.CultureInvariant);

    /// <summary>Codici guasto visibili nella lista del dettaglio (il testo del codice e' un TextBlock nella riga).
    /// La lista e' virtualizzata e il dettaglio sta in fondo alla pagina Audit: le righe fuori dal viewport non sono
    /// nell'albero UIA, quindi prima si scorre lo ScrollViewer della pagina fino in fondo (pattern Scroll, nessun click).</summary>
    private static HashSet<string> CodiciVisibili(Window window)
    {
        var lista = ElementFinders.TryFindById(window, "Audit_Guasto_Lista");
        if (lista == null) return new HashSet<string>();
        PortaInVista(lista);
        return lista.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Select(t => t.Name ?? string.Empty).Where(n => CodiceDtc.IsMatch(n)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Scorre in fondo il primo antenato con scorrimento verticale (lo ScrollViewer esterno della pagina Audit).</summary>
    private static void PortaInVista(AutomationElement elemento)
    {
        for (var nodo = elemento.Parent; nodo != null; nodo = nodo.Parent)
        {
            if (nodo.Patterns.Scroll.TryGetPattern(out var scorrimento) && scorrimento.VerticallyScrollable.Value)
            {
                scorrimento.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 100);
                return;
            }
        }
    }

    private static void AttendiCodici(Window window, string description, params string[] attesi) =>
        Wait.ForCondition(() => CodiciVisibili(window).SetEquals(attesi),
            $"{description}: codici guasto visibili [{string.Join(", ", attesi)}]");

    /// <summary>Apre Audit, seleziona il veicolo di fixture e il dettaglio guasto del requisito Extra.</summary>
    private AuditPage ApriDettaglioDtc(Window window)
    {
        var audit = new MainShellPage(window, App).GoToAudit();
        audit.SelezionaVeicolo(Veh);
        ElementFinders.WaitButtonById(window, $"Audit_Extra_Req_{Req}").SafeInvoke();
        ElementFinders.WaitById(window, "Audit_Guasto_Lista");
        AttendiCodici(window, "dettaglio guasto appena aperto", Guasto1, Guasto2, Guasto3);
        return audit;
    }

    /// <summary>Espande "Altri filtri" (intestazione dell'Expander con il riepilogo) se non ancora aperto.</summary>
    private static void EspandiAltriFiltri(Window window)
    {
        var espansore = ElementFinders.WaitById(window, "Audit_Guasto_AltriFiltri_Expander");
        if (!ElementFinders.ExistsById(window, "Audit_Guasto_FiltroComponente_Button")) espansore.SafeInvoke();
        ElementFinders.WaitById(window, "Audit_Guasto_FiltroComponente_Button");
    }

    /// <summary>Sceglie una voce nel flyout di filtro del dettaglio guasto e lo chiude con Esc (senza coprire la pagina).</summary>
    private void SelezionaVoceFiltroDtc(string idBottoneFiltro, string voce)
    {
        ElementFinders.WaitButtonById(App.WaitForMainWindow(), idBottoneFiltro).SafeInvoke();
        var flyout = new FiltroMultiSelectPage(App);
        flyout.SelezionaVoce(voce);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Wait.ForCondition(() => App.IsWindowClosedByContent("FiltroMultiSelect_Lista"), "chiusura del flyout del filtro guasto");
    }

    [Fact]
    public void FiltroEcu_RestringeIlDettaglioAllaSolaEcuEAzzeraIlFiltro()
    {
        try
        {
            var window = App.WaitForMainWindow();
            ApriDettaglioDtc(window);
            EspandiAltriFiltri(window);

            SelezionaVoceFiltroDtc("Audit_Guasto_FiltroComponente_Button", Componente2);
            AttendiCodici(window, "filtro componente " + Componente2, Guasto3);

            ElementFinders.WaitButtonById(window, "Audit_Guasto_Azzera_Button").SafeInvoke();
            AttendiCodici(window, "filtri azzerati", Guasto1, Guasto2, Guasto3);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(FiltroEcu_RestringeIlDettaglioAllaSolaEcuEAzzeraIlFiltro), ex);
        }
    }

    [Fact]
    public void FiltroStato_MostraSoloIDtcNelloStatoScelto()
    {
        try
        {
            var window = App.WaitForMainWindow();
            ApriDettaglioDtc(window);
            EspandiAltriFiltri(window);

            SelezionaVoceFiltroDtc("Audit_Guasto_FiltroStato_Button", "Validato");
            AttendiCodici(window, "filtro Stato Validato", Guasto2);

            ElementFinders.WaitButtonById(window, "Audit_Guasto_Azzera_Button").SafeInvoke();
            AttendiCodici(window, "filtri azzerati dopo Stato", Guasto1, Guasto2, Guasto3);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(FiltroStato_MostraSoloIDtcNelloStatoScelto), ex);
        }
    }

    [Fact]
    public void SoloNonCompleti_NascondeIDtcCompletiESiRiattivaSenzaFiltri()
    {
        try
        {
            var window = App.WaitForMainWindow();
            ApriDettaglioDtc(window);

            var soloNonCompleti = ElementFinders.WaitById(window, "Audit_Guasto_SoloNonCompleti_Check").AsCheckBox();
            soloNonCompleti.SafeInvoke();
            Wait.ForCondition(() => soloNonCompleti.IsChecked == true, "casella 'solo non completi' attiva");
            AttendiCodici(window, "solo non completi", Guasto2, Guasto3);

            soloNonCompleti.SafeInvoke();
            Wait.ForCondition(() => soloNonCompleti.IsChecked != true, "casella 'solo non completi' disattiva");
            AttendiCodici(window, "solo non completi disattivato", Guasto1, Guasto2, Guasto3);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(SoloNonCompleti_NascondeIDtcCompletiESiRiattivaSenzaFiltri), ex);
        }
    }

    [Fact]
    public void EsportaCsvDelDettaglio_ScriveUnFileConITreDtcNelWorkspaceDiTest()
    {
        try
        {
            var window = App.WaitForMainWindow();
            ApriDettaglioDtc(window);
            var percorso = Path.Combine(Workspace.RootDir, "audit-guasto-dettaglio.csv");

            ElementFinders.WaitButtonById(window, "Audit_Guasto_EsportaCsv_Button").SafeInvoke();
            NativeFileDialogPage.Attendi(App).ConfermaPercorso(percorso);

            Wait.ForCondition(() => File.Exists(percorso) && new FileInfo(percorso).Length > 0, "CSV del dettaglio guasto scritto");
            var contenuto = File.ReadAllText(percorso);
            foreach (var guasto in new[] { Guasto1, Guasto2, Guasto3 })
                Assert.Contains(guasto, contenuto);
            Assert.Contains(Req, contenuto);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(EsportaCsvDelDettaglio_ScriveUnFileConITreDtcNelWorkspaceDiTest), ex);
        }
    }
}
