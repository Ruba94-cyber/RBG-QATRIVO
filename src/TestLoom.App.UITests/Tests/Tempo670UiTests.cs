using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Feature "Tempo" della 6.7.0 (durata delle operazioni, Report e Home) nella vera UI di TestLoom.exe.
/// Tre classi, una per workspace seminato, così ogni Fact parte da un'app isolata:
/// - <see cref="Tempo670UiTestsEditor"/>: durata di un'operazione nell'editor del Test (inserimento, +5, persistenza, rifiuto).
/// - <see cref="Tempo670UiTestsReport"/>: modalità della matrice (Stato %, Stato, Test %, Tempo totale/medio) e filtri Tempo del Report.
/// - <see cref="Tempo670UiTestsHome"/>: filtri Tempo nella sezione "Altri filtri" della Home.
/// Dati seminati: tre Test sulla stessa combinazione Funzionalità × componente × Veicolo, con una sola esecuzione ciascuno:
/// T-TMC (Conforme, durata vuota, il più vecchio), T-TMA (Conforme, 30 min), T-TMB (Non Conforme, 10 min, il più recente).
/// Attese: Tempo totale Tutte le esecuzioni 40 min · 2/3; Tempo medio 20 min · 2/3; Ultimo stato 10 min · 1/1; Stato = Non Conforme.
/// </summary>
internal static class Tempo670Dati
{
    public const string Componente = "Componente-TM1";
    public const string Veicolo = "Veicolo-TM1";
    public const string Funzionalita = "Funzionalita-TM1";
    public const string Categoria = "Diag. tempo";

    public static TestCase Test(string id, int? durataMinuti, Esito esito, DateTime data)
    {
        var tc = new TestCase
        {
            Id = id,
            Componente = new EcuSelection([Componente]),
            Veicoli = [Veicolo],
            Funzionalita = [Funzionalita],
            ModificatoDa = "UITests",
            // Il filtro data della Home usa DataCreazione: senza valore il Test cadrebbe fuori dall'anno corrente.
            DataCreazione = data,
            UltimaModifica = data,
        };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = data,
            Condizioni = [new Condizione { Funzionalita = Funzionalita, Esito = esito, Componente = new EcuSelection([Componente]), DurataMinuti = durataMinuti }],
        });
        return tc;
    }

    /// <summary>Workspace con la matrice seminata (nessun template: come ReportMatriceCampiUiTests).</summary>
    public static TestWorkspace CreaWorkspaceMatrice(string nome)
    {
        var ws = TestWorkspace.CreateLoggedIn(nome, seedTestFixtures: false);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = Veicolo, GammaNome = "LCV", ModificatoDa = "UITests" } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = Componente, VeicoliAssociati = { Veicolo }, ModificatoDa = "UITests" } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = Funzionalita, Categorie = { Categoria }, ModificatoDa = "UITests" } });
        state.TestCases.Save(Test("T-TMC", null, Esito.Conforme, DateTime.Today.AddDays(-3)), isNew: true);
        state.TestCases.Save(Test("T-TMA", 30, Esito.Conforme, DateTime.Today.AddDays(-2)), isNew: true);
        state.TestCases.Save(Test("T-TMB", 10, Esito.NonConforme, DateTime.Today.AddDays(-1)), isNew: true);
        return ws;
    }
}

/// <summary>Durata di un'operazione nell'editor del Test (scheda Reiterazioni), con persistenza su disco.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Tempo")]
public sealed class Tempo670UiTestsEditor : UiTestBase
{
    private const string IdTest = "T-TE1";

    public Tempo670UiTestsEditor() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(Tempo670UiTestsEditor), seedTestFixtures: true);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        // Ogni operazione deve coprire un componente del Test (validazione dell'editor): la condizione porta la stessa componente.
        var tc = new TestCase
        {
            Id = IdTest,
            DescrizioneBreve = "QA Tempo editor",
            TestTemplateUid = TestCoverageSeed.UidTemplatePrincipale(state),
            Componente = new EcuSelection([TestWorkspace.DefaultEcuName]),
            CreatoDa = TestWorkspace.DefaultOperatorName,
            ModificatoDa = "UITests",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1,
            Data = DateTime.Now,
            Operatore = "UITests",
            Esito = Esito.Conforme,
            Condizioni = [new Condizione { Esito = Esito.Conforme, Componente = new EcuSelection([TestWorkspace.DefaultEcuName]) }],
        });
        state.TestCases.Save(tc, isNew: true);
        return ws;
    }

    private TestEditorPage ApriInModifica(MainShellPage shell)
    {
        var editor = shell.GoToTestList().OpenOnlyRow();
        editor.EnterEditMode();
        return editor;
    }

    private int? DurataSalvataSuDisco()
    {
        var tc = TestCoverageSeed.TrovaTestCase(Workspace, IdTest) ?? throw new InvalidOperationException("Test non trovato su disco");
        return tc.Esecuzioni[^1].Condizioni[0].DurataMinuti;
    }

    [Fact]
    public void Durata_InseritaConPiu5_SalvataERiaperta_MostraEPersisteIMinuti()
    {
        try
        {
            var shell = new MainShellPage(App.WaitForMainWindow());
            var editor = ApriInModifica(shell);
            editor.SetDurataCondizione(0, "40");
            editor.ClickDurataPiu5(0);
            Wait.ForCondition(() => editor.DurataCondizioneTesto(0) == "45", "il pulsante +5 porta la durata a 45");
            editor.Save();

            var riaperto = new TestListPage(App.WaitForMainWindow(), App.WaitForMainWindow()).OpenOnlyRow();
            Wait.ForCondition(() => riaperto.DurataCondizioneTesto(0) == "45", "durata 45 mostrata dopo la riapertura");
            Assert.Equal((int?)45, DurataSalvataSuDisco());
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Durata_InseritaConPiu5_SalvataERiaperta_MostraEPersisteIMinuti), ex); }
    }

    [Fact]
    public void Durata_ValoreNonValido_EvidenziatoESalvataggioRifiutato()
    {
        try
        {
            var shell = new MainShellPage(App.WaitForMainWindow());
            var editor = ApriInModifica(shell);

            foreach (var invalido in new[] { "abc", "-1", "2.5", "1000001" })
            {
                editor.SetDurataCondizione(0, invalido);
                Wait.ForCondition(() => editor.HaErroreDurata(0), $"errore di durata visibile per '{invalido}'");
            }

            editor.Save();
            // Il salvataggio è bloccato dal campo non valido: l'editor resta aperto e il file non cambia.
            Assert.True(editor.CanSave, "l'editor deve restare aperto dopo un salvataggio con durata non valida");
            Assert.Null(DurataSalvataSuDisco());
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Durata_ValoreNonValido_EvidenziatoESalvataggioRifiutato), ex); }
    }

    [Fact]
    public void Durata_Zero_ENotaEVuota_SonoDueValoriDiversi()
    {
        try
        {
            var shell = new MainShellPage(App.WaitForMainWindow());
            var editor = ApriInModifica(shell);
            editor.SetDurataCondizione(0, "0");
            Wait.ForCondition(() => !editor.HaErroreDurata(0), "0 è una durata valida");
            editor.Save();

            var riaperto = new TestListPage(App.WaitForMainWindow(), App.WaitForMainWindow()).OpenOnlyRow();
            Wait.ForCondition(() => riaperto.DurataCondizioneTesto(0) == "0", "lo zero noto resta 0 dopo la riapertura");
            Assert.Equal((int?)0, DurataSalvataSuDisco());
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Durata_Zero_ENotaEVuota_SonoDueValoriDiversi), ex); }
    }
}

/// <summary>Modalità della matrice Report e filtri Tempo del Report (Test % come contatore dei Test conteggiati).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Tempo")]
public sealed class Tempo670UiTestsReport : UiTestBase
{
    private const string Riga = Tempo670Dati.Categoria;
    private const string Colonna = Tempo670Dati.Componente;

    public Tempo670UiTestsReport() : base(Tempo670Dati.CreaWorkspaceMatrice(nameof(Tempo670UiTestsReport))) { }

    [Fact]
    public void Matrice_CambioModalitaCambiaIContenutiDelleCelle()
    {
        try
        {
            var pagina = new MainShellPage(App.WaitForMainWindow(), App).GoToReport();

            pagina.ImpostaModalitaMatrice("Stato");
            pagina.AttendiTestoCella(Riga, Colonna, t => t == Esito.NonConforme.Label(), "stato = ultimo esito (Non Conforme)");

            pagina.ImpostaModalitaMatrice("Test %");
            pagina.AttendiTestoCella(Riga, Colonna, t => t.EndsWith("(3 test)", StringComparison.Ordinal), "Test % conta 3 Test");

            pagina.ImpostaModalitaMatrice("Tempo totale");
            pagina.ImpostaBaseTempo("Tutte le esecuzioni");
            pagina.AttendiTestoCella(Riga, Colonna, t => t == "40 min · 2/3", "tempo totale 30+10 min, 2 noti su 3");

            pagina.ImpostaModalitaMatrice("Tempo medio");
            pagina.AttendiTestoCella(Riga, Colonna, t => t == "20 min · 2/3", "tempo medio 40/2 min, 2 noti su 3");

            pagina.ImpostaModalitaMatrice("Tempo totale");
            pagina.ImpostaBaseTempo("Ultimo stato");
            pagina.AttendiTestoCella(Riga, Colonna, t => t == "10 min · 1/1", "base Ultimo stato: solo l'operazione che determina lo stato");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Matrice_CambioModalitaCambiaIContenutiDelleCelle), ex); }
    }

    [Fact]
    public void FiltriTempo_Report_CambianoIConteggiDiTestEUnValoreVuotoNonCorrisponde()
    {
        try
        {
            var pagina = new MainShellPage(App.WaitForMainWindow(), App).GoToReport();
            pagina.ImpostaModalitaMatrice("Test %");
            pagina.AttendiTestoCella(Riga, Colonna, t => t.EndsWith("(3 test)", StringComparison.Ordinal), "senza filtri: 3 Test");

            // Tempo operazione maggiore di 20: solo T-TMA (30 min). T-TMC non ha durata e non corrisponde.
            pagina.ImpostaFiltroTempoOperazione("maggiore di", "20");
            pagina.AttendiTestoCella(Riga, Colonna, t => t.EndsWith("(1 test)", StringComparison.Ordinal), "Tempo operazione > 20: 1 Test");

            // Tempo operazione minore di 20: solo T-TMB (10 min); il vuoto di T-TMC non entra mai.
            pagina.ImpostaFiltroTempoOperazione("minore di", "20");
            pagina.AttendiTestoCella(Riga, Colonna, t => t.EndsWith("(1 test)", StringComparison.Ordinal), "Tempo operazione < 20: 1 Test");
            pagina.ImpostaFiltroTempoOperazione("minore di", "");
            pagina.AttendiTestoCella(Riga, Colonna, t => t.EndsWith("(3 test)", StringComparison.Ordinal), "filtro operazione vuoto: di nuovo 3 Test");

            // Tempo totale Test maggiore di 25: solo T-TMA (30 min).
            pagina.ImpostaFiltroTempoTotaleTest("maggiore di", "25");
            pagina.AttendiTestoCella(Riga, Colonna, t => t.EndsWith("(1 test)", StringComparison.Ordinal), "Tempo totale > 25: 1 Test");

            // Tempo totale Test minore di 1000: T-TMA e T-TMB; T-TMC senza tempo totale noto non corrisponde.
            pagina.ImpostaFiltroTempoTotaleTest("minore di", "1000");
            pagina.AttendiTestoCella(Riga, Colonna, t => t.EndsWith("(2 test)", StringComparison.Ordinal), "Tempo totale < 1000: 2 Test, il vuoto escluso");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltriTempo_Report_CambianoIConteggiDiTestEUnValoreVuotoNonCorrisponde), ex); }
    }
}

/// <summary>Filtri Tempo nella sezione "Altri filtri" della Home: il riepilogo "Filtro attivo: N test corrispondenti".</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Tempo")]
public sealed class Tempo670UiTestsHome : UiTestBase
{
    public Tempo670UiTestsHome() : base(Tempo670Dati.CreaWorkspaceMatrice(nameof(Tempo670UiTestsHome))) { }

    [Fact]
    public void AltriFiltri_TempoOperazioneETotaleTest_CambianoIlDashboardFiltrato()
    {
        try
        {
            var home = new MainShellPage(App.WaitForMainWindow()).GoToHome();
            home.ApriAltriFiltri();

            home.ImpostaFiltroTempoOperazione("maggiore di", "20");
            Wait.ForCondition(() => home.TestoFiltroAttivo == "Filtro attivo: 1 test corrispondente.",
                "Tempo operazione > 20 lascia 1 Test (T-TMA)");

            // Vuoto: nessun filtro, il riepilogo sparisce.
            home.ImpostaFiltroTempoOperazione("maggiore di", "");
            Wait.ForCondition(() => home.TestoFiltroAttivo == null, "filtro operazione vuoto: nessun filtro attivo");

            home.ImpostaFiltroTempoTotaleTest("minore di", "25");
            Wait.ForCondition(() => home.TestoFiltroAttivo == "Filtro attivo: 1 test corrispondente.",
                "Tempo totale Test < 25 lascia 1 Test (T-TMB)");

            home.ImpostaFiltroTempoTotaleTest("minore di", "1000");
            Wait.ForCondition(() => home.TestoFiltroAttivo == "Filtro attivo: 2 test corrispondenti.",
                "Tempo totale Test < 1000 lascia 2 Test, T-TMC esclusa perché senza tempo");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(AltriFiltri_TempoOperazioneETotaleTest_CambianoIlDashboardFiltrato), ex); }
    }
}
