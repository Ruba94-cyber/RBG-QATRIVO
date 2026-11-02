using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Matrice del Report con piu' campi per asse (6.6.1, fase 1): il secondo campo dell'asse colonne si aggiunge dal pulsante
/// dedicato, il selettore "Contenuto delle celle" passa a Stato e mostra la legenda, e la cella aggregata su due veicoli mostra
/// il riepilogo "Misto". Il seme e' diretto via AppState: 1 componente, 2 Veicoli, 1 Funzionalita' in categoria "Diag. base" e due Test
/// con esiti diversi (Conforme sul primo veicolo, Non Conforme sul secondo). Compilato con la suite FlaUI; l'esecuzione e' a cura
/// dell'orchestratore sul PC di sviluppo.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Report")]
public sealed class ReportMatriceCampiUiTests : UiTestBase
{
    private const string Componente = "Componente-MC1";
    private const string Veicolo1 = "Veicolo-MC1";
    private const string Veicolo2 = "Veicolo-MC2";
    private const string Funzionalita = "Funzionalita-MC1";
    private const string Categoria = "Diag. base";

    public ReportMatriceCampiUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(ReportMatriceCampiUiTests), seedTestFixtures: false);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);

        state.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = Veicolo1, GammaNome = "LCV", ModificatoDa = "UITests" },
            new() { Nome = Veicolo2, GammaNome = "LCV", ModificatoDa = "UITests" },
        });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = Componente, VeicoliAssociati = { Veicolo1, Veicolo2 }, ModificatoDa = "UITests" } });
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = Funzionalita, Categorie = { Categoria }, ModificatoDa = "UITests" },
        });
        state.TestCases.Save(SeminaTest("T-MC1", Veicolo1, Esito.Conforme, new DateTime(2026, 1, 1)), isNew: true);
        state.TestCases.Save(SeminaTest("T-MC2", Veicolo2, Esito.NonConforme, new DateTime(2026, 1, 2)), isNew: true);
        return ws;
    }

    private static TestCase SeminaTest(string id, string veicolo, Esito esito, DateTime data)
    {
        var tc = new TestCase
        {
            Id = id,
            Componente = new EcuSelection([Componente]),
            Veicoli = [veicolo],
            Funzionalita = [Funzionalita],
            ModificatoDa = "UITests",
        };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = data,
            Esito = esito,
            Condizioni = [new Condizione { Funzionalita = Funzionalita, Esito = esito, Componente = new EcuSelection([Componente]) }],
        });
        return tc;
    }

    private ReportPage ApriReport(Window window) => new MainShellPage(window, App).GoToReport();

    private static bool CellaContieneStato(string testo, string stato)
    {
        var posizione = 0;
        while ((posizione = testo.IndexOf(stato, posizione, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var fine = posizione + stato.Length;
            var inizioVoce = posizione == 0 || !char.IsLetter(testo[posizione - 1]);
            var fineVoce = fine == testo.Length || !char.IsLetter(testo[fine]);
            if (inizioVoce && fineVoce) return true;
            posizione++;
        }

        return false;
    }

    [Fact]
    public void Matrice_AggiungeUnSecondoCampoEMostraLaLegendaInModalitaStato()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var pagina = ApriReport(window);

            pagina.AggiungiCampoColonne();
            Assert.True(ElementFinders.ExistsById(window, "Report_MatriceAsseX_Campo2_ComboBox"));

            pagina.ImpostaModalitaStato();
            // Il pannello legenda (StackPanel) non è esposto a UI Automation: si attende il titolo, che ha un AutomationId proprio.
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Report_MatriceLegenda_Titolo"), "legenda dello stato visibile in modalità Stato");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Matrice_AggiungeUnSecondoCampoEMostraLaLegendaInModalitaStato), ex);
        }
    }

    [Fact]
    public void Matrice_CellaAggregataSuDueVeicoliMostraMisto()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var pagina = ApriReport(window);

            pagina.ImpostaModalitaStato();
            // Categoria "Diag. base" × componente: la cella senza filtro veicolo aggrega i due veicoli (OK su uno, NOK sull'altro).
            var testo = pagina.TestoCella(Categoria, Componente);
            Assert.True(CellaContieneStato(testo, "OK"));
            Assert.True(CellaContieneStato(testo, "NOK"));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Matrice_CellaAggregataSuDueVeicoliMostraMisto), ex);
        }
    }

    [Fact]
    public void Matrice_NascondeLeVociDiStatoSoloNellaVistaInTutteLeModalita()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var pagina = ApriReport(window);
            var stati = new[]
            {
                (Id: "Ok", Etichetta: "OK"),
                (Id: "Nok", Etichetta: "NOK"),
                (Id: "Dev", Etichetta: "dev."),
                (Id: "DaEseguire", Etichetta: "da eseg."),
                (Id: "NonTestato", Etichetta: "non test."),
                (Id: "DaDecidere", Etichetta: "!"),
            };

            foreach (var (stato, _) in stati)
            {
                var id = $"Report_Matrice_Nascondi{stato}_CheckBox";
                var filtroStato = ElementFinders.WaitById(window, id).AsCheckBox();
                Assert.False(filtroStato.IsChecked,
                    $"Lo stato {stato} deve essere visibile all'apertura del report.");
            }

            var testoIniziale = pagina.TestoCella(Categoria, Componente);
            Assert.True(CellaContieneStato(testoIniziale, "OK"));
            Assert.True(CellaContieneStato(testoIniziale, "NOK"));
            Assert.Contains("50%", testoIniziale);

            foreach (var (stato, etichetta) in stati)
            {
                pagina.ImpostaStatoNascosto(stato, true);
                pagina.AttendiTestoCella(Categoria, Componente,
                    testo => !CellaContieneStato(testo, etichetta),
                    $"il filtro {stato} nasconde la propria voce");
                pagina.ImpostaStatoNascosto(stato, false);
                pagina.AttendiTestoCella(Categoria, Componente,
                    testo => CellaContieneStato(testo, etichetta),
                    $"il filtro {stato} ripristina la voce");
            }

            foreach (var (stato, _) in stati.Skip(1))
                pagina.ImpostaStatoNascosto(stato, true);
            pagina.AttendiTestoCella(Categoria, Componente,
                testo => CellaContieneStato(testo, "OK")
                    && stati.Skip(1).All(s => !CellaContieneStato(testo, s.Etichetta))
                    && testo.Contains("50%", StringComparison.Ordinal),
                "Stato % mantiene la quota sul totale originale mostrando solo OK");

            pagina.ImpostaModalitaMatrice("Stato");
            pagina.AttendiTestoCella(Categoria, Componente,
                testo => CellaContieneStato(testo, "OK")
                    && stati.Skip(1).All(s => !CellaContieneStato(testo, s.Etichetta)),
                "Stato mostra solo OK senza modificare i valori");

            pagina.ImpostaModalitaMatrice("Test %");
            pagina.AttendiTestoCella(Categoria, Componente,
                testo => CellaContieneStato(testo, "OK")
                    && stati.Skip(1).All(s => !CellaContieneStato(testo, s.Etichetta))
                    && testo.Contains("50%", StringComparison.Ordinal)
                    && testo.Contains("(2 test)", StringComparison.OrdinalIgnoreCase),
                "Test % mantiene percentuali e conteggio originale mostrando solo OK");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Matrice_NascondeLeVociDiStatoSoloNellaVistaInTutteLeModalita), ex);
        }
    }
}
