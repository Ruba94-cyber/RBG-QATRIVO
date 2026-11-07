using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Matrice E2E sui cinque tipi reali di Test. Ogni riga lancia un processo
/// RBG - QATRIVO.exe indipendente, crea un Test dalla UIA3, gestisce (o non gestisce)
/// la segnalazione, salva e legge i JSON autorevoli del workspace sintetico.
/// NESSUN seed di Test/Segnalazione per gli scenari "nuovo": viene creato
/// sempre tramite il form reale. La sola NC preesistente e' seminata per
/// provare l'azione di collegamento senza duplicazione.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "TestNcEndToEnd")]
[Trait("Category", "LongE2E")]
public sealed class TestEsitiNcMatriceFlaUiTests
{
    public enum GestioneSegnalazione
    {
        NonRichiesta,
        CreaNc,
        Escludi,
        CollegaEsistente,
        NonGestire
    }

    private const string NcEsistente = "NC-QA-CICLO-ESISTENTE";
    private const string MotivoEsclusione = "Segnalazione non pertinente alla verifica sintetica QA";
    private const string DescrizioneProblema = "Risposta diagnostica inattesa su prova sintetica QA";
    private const string DescrizioneDeviazione = "Deviazione osservata rispetto al valore atteso QA";
    private const string MotivazioneNa = "Configurazione veicolo non equipaggiata per questa verifica QA";

    /// <summary>Queste sono le cinque definizioni correnti in TestTemplateDefaults:
    /// quattro storiche piu' RAS UnitLogger. I risultati legacy non proposti nella
    /// ComboBox (NonEseguitoNA/DaRivedere) sono oggetto di lettura/migrazione
    /// negli unit test, non di selezione UI su un nuovo Test.</summary>
    public static readonly string[] Ambiti =
    {
        "Validazione Banco",
        "Validazione Veicolo",
        "UnitLogger",
        "UnitLogger Performance",
        TestTemplateDefaults.NomeRasUdt
    };

    /// <summary>
    /// 5 ambiti x (3 risultati senza segnalazione + 2 risultati problematici
    /// x 4 scelte: nuova NC, esclusione, link esistente, mancata classificazione)
    /// = 55 scenari di creazione reale, non semplici check su un VM seminato.
    /// </summary>
    public static IEnumerable<object[]> Scenari()
    {
        foreach (var ambito in Ambiti)
        {
            yield return new object[] { ambito, Esito.DaEseguire, GestioneSegnalazione.NonRichiesta };
            yield return new object[] { ambito, Esito.Conforme, GestioneSegnalazione.NonRichiesta };
            yield return new object[] { ambito, Esito.NonApplicabile, GestioneSegnalazione.NonRichiesta };
            foreach (var esito in new[] { Esito.NonConforme, Esito.ConformeConDeviazioni })
            {
                yield return new object[] { ambito, esito, GestioneSegnalazione.CreaNc };
                yield return new object[] { ambito, esito, GestioneSegnalazione.Escludi };
                yield return new object[] { ambito, esito, GestioneSegnalazione.CollegaEsistente };
                yield return new object[] { ambito, esito, GestioneSegnalazione.NonGestire };
            }
        }
    }

    internal static TestWorkspace WorkspacePerAmbito(string nome, bool conNcDaCollegare = false)
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(TestEsitiNcMatriceFlaUiTests));
        // Non modifichiamo la logica dei template. Rendiamo selezionabile uno
        // dei tipi ORIGINALI con i suoi campi obbligatori e i fogli report reali.
        // In questo dataset isolato togliamo soltanto la preselezione automatica
        // degli strumenti: il numero di operazioni non deve cambiare in funzione
        // del catalogo strumenti del PC, poiche' stiamo misurando il flusso NC.
        using var state = new AppState(new AppConfigService(workspace.ConfigPath),
            runBackgroundMaintenance: false);
        var templates = state.Master.TestTemplate.Load();
        var selezionato = templates.Single(t => t.Nome == nome);
        foreach (var template in templates)
            template.Archiviata = template.Uid != selezionato.Uid;
        selezionato.StrumentiPreselezionati.Clear();
        state.Master.TestTemplate.Save(templates);

        // Il filtro reale Veicolo -> componente ammette soltanto i componenti associate
        // al veicolo. La fixture deve essere un'associazione valida, non un
        // workaround della UI (il seed minimale iniziale non la imposta).
        var componente = state.Master.Componente.Load();
        var qaEcu = componente.Single(e => e.Nome == TestWorkspace.DefaultEcuName);
        if (!qaEcu.VeicoliAssociati.Contains(TestWorkspace.DefaultVehicleName,
                StringComparer.OrdinalIgnoreCase))
            qaEcu.VeicoliAssociati.Add(TestWorkspace.DefaultVehicleName);
        state.Master.Componente.Save(componente);

        if (conNcDaCollegare)
            TestCoverageSeed.SeedNonConformitaAperta(state, NcEsistente,
                "NC sintetica preesistente da collegare alla prova - QA");
        return workspace;
    }

    internal static TestEditorPage CreaTestDallaUi(
        TestLoomApplication app, string nomeAmbito, string descrizione)
    {
        var editor = new MainShellPage(app.WaitForMainWindow())
            .GoToTestList().ClickNew();
        editor.ChooseTemplate(); // un solo template non archiviato nel workspace
        editor.GoToTab(1); // Dettagli
        editor.SetDescrizioneBreve(descrizione);
        editor.SetDescrizione("Obiettivo, prerequisiti e metodologia della prova di " +
            nomeAmbito + ". Verifica funzionale sintetica riproducibile.");
        editor.ChooseVeicoloInLista(TestWorkspace.DefaultVehicleName);
        editor.ChooseEcu(app, TestWorkspace.DefaultEcuName);
        // I template originali richiedono la matricola quando il Test passa ad
        // In corso. Compilarlo nella revisione e cambiare davvero stato UI:
        // non verificare soltanto un Test fittizio rimasto Pianificato.
        editor.SetVinRevisione("VINQA000000000001");
        editor.GoToTab(0); // Pianificazione
        editor.SetStatoLavoro("In corso");
        editor.GoToTab(2); // Operazioni
        Wait.ForCondition(() => editor.NumeroCondizioni() == 1,
            "operazione indipendente iniziale del Test");
        return editor;
    }

    internal static void ImpostaEsito(TestEditorPage editor, int indice, Esito esito)
    {
        var label = esito switch
        {
            Esito.DaEseguire => "Da eseguire",
            Esito.Conforme => "Conforme",
            Esito.NonConforme => "Non Conforme",
            Esito.ConformeConDeviazioni => "Conforme con deviazioni",
            Esito.NonApplicabile => "Non applicabile",
            _ => throw new ArgumentOutOfRangeException(nameof(esito),
                "Un risultato legacy non puo' essere scelto nella UI dei nuovi Test.")
        };
        editor.SetEsitoCondizione(indice, label);
        editor.SetNotaCondizione(indice, "Nota operazione QA - " + label);
        if (esito == Esito.NonConforme)
            editor.SetDescrizioneProblema(indice, DescrizioneProblema);
        if (esito == Esito.ConformeConDeviazioni)
        {
            Wait.ForCondition(() => editor.HaDescrizioneDeviazioneVisibile(indice),
                "campo deviazione disponibile dopo esito con deviazioni");
            editor.SetDescrizioneDeviazione(indice, DescrizioneDeviazione);
        }
        if (esito == Esito.NonApplicabile)
        {
            Wait.ForCondition(() => editor.HaMotivazioneNonApplicabileVisibile(indice),
                "motivazione non applicabile disponibile");
            editor.SetMotivazioneNonApplicabile(indice, MotivazioneNa);
        }
    }

    internal static void CreaNc(TestLoomApplication app, TestEditorPage editor)
    {
        Wait.ForCondition(() => editor.NumeroSegnalazioniDaClassificare() > 0,
            "segnalazione da classificare prima della NC");
        editor.SetSeveritaSegnalazione(0, "Media");
        editor.CreaNuovaAnomaliaEsplicita(0);
        new ConfirmDialogPage(app.WaitForWindowByContent("Confirm_Yes_Button"))
            .ConfirmYes();
        Wait.ForCondition(() => app.IsWindowClosedByContent("Confirm_Yes_Button"),
            "conferma creazione NC conclusa");
    }

    [Theory]
    [MemberData(nameof(Scenari))]
    public void NuovoTest_PerAmbitoEsitoESceltaNc_ConfermaPersistenzaELegami(
        string ambito, Esito esito, GestioneSegnalazione gestione)
    {
        using var workspace = WorkspacePerAmbito(ambito,
            conNcDaCollegare: gestione == GestioneSegnalazione.CollegaEsistente);
        using var app = TestLoomApplication.Launch(workspace);
        var caso = $"{ambito}/{esito}/{gestione}";
        try
        {
            var window = app.WaitForMainWindow();
            var descrizione = "QA ciclo completo - " + caso;
            var editor = CreaTestDallaUi(app, ambito, descrizione);
            ImpostaEsito(editor, 0, esito);

            var problematico = esito is Esito.NonConforme or Esito.ConformeConDeviazioni;
            if (problematico)
            {
                Wait.ForCondition(() => editor.NumeroSegnalazioniDaClassificare() == 1,
                    "una segnalazione per l'operazione problematica");
                switch (gestione)
                {
                    case GestioneSegnalazione.CreaNc:
                        CreaNc(app, editor);
                        Wait.ForCondition(() => !editor.HaSegnalazioniDaClassificare,
                            "segnalazione classificata dopo creazione NC");
                        break;
                    case GestioneSegnalazione.Escludi:
                        editor.EscludiSegnalazione(0, MotivoEsclusione);
                        Wait.ForCondition(() => editor.SegnalazioneEsclusa(0),
                            "segnalazione esclusa con motivazione");
                        break;
                    case GestioneSegnalazione.CollegaEsistente:
                        editor.CollegaSegnalazioneAdAnomalia(0, NcEsistente);
                        Wait.ForCondition(() => !editor.HaSegnalazioniDaClassificare,
                            "segnalazione collegata alla NC preesistente");
                        break;
                    case GestioneSegnalazione.NonGestire:
                        break;
                    default:
                        throw new InvalidOperationException("Gestione NC non prevista: " + caso);
                }
            }
            else
            {
                Assert.Equal(GestioneSegnalazione.NonRichiesta, gestione);
                Assert.False(editor.HaSegnalazioniDaClassificare);
            }

            editor.GoToTab(4); // Riferimenti (Save dell'editor completo)
            editor.Save();
            // Avviso soft previsto dall'app (Funzionalita' mancante): si sceglie "Salva Test incompleto".
            TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport.ConfermaSeCompare(app, TimeSpan.FromSeconds(6));

            // Regola corrente del workflow (non mutata dal test):
            // un Test In corso puo' essere salvato anche con segnalazioni
            // DaClassificare. Solo il passaggio a Concluso e' bloccante.
            // NonGestire deve quindi persistere un pending, NON fallire Save.
            Wait.ForCondition(
                () => ElementFinders.ExistsById(window, "TestList_Title_Text"),
                "salvataggio completato e ritorno alla Lista Test", TimeSpan.FromSeconds(35));
            Wait.ForCondition(() => TestCoverageSeed.FileTestCase(workspace).Count == 1,
                "file Test unico pubblicato", TimeSpan.FromSeconds(25));

            var tc = TestCoverageSeed.LeggiTestCase(
                Assert.Single(TestCoverageSeed.FileTestCase(workspace)));
            Assert.Equal(descrizione, tc.DescrizioneBreve);
            Assert.Contains(TestWorkspace.DefaultEcuName, tc.Componente.ToList());
            Assert.Contains(TestWorkspace.DefaultVehicleName, tc.VeicoliEffettivi);
            Assert.False(string.IsNullOrWhiteSpace(tc.Descrizione));
            Assert.Equal(TestWorkspace.DefaultOperatorName, tc.Assegnatario);
            Assert.Equal(StatoLavoroTest.InCorso, tc.StatoLavoro);

            // L'UID dei tipi originali (eccetto RAS UnitLogger) viene generato una
            // volta per workspace: verificare l'identita' tramite il catalogo
            // persiste realmente, non tramite un nuovo Seed() indipendente.
            using (var verifica = new AppState(
                new AppConfigService(workspace.ConfigPath),
                runBackgroundMaintenance: false))
            {
                var tipo = verifica.Master.TestTemplate.Load()
                    .Single(t => t.Nome == ambito);
                Assert.Equal(tipo.Uid, tc.TestTemplateUid);
            }

            var rev = Assert.Single(tc.Esecuzioni);
            var cond = Assert.Single(rev.Condizioni);
            Assert.Equal("VINQA000000000001", rev.Matricola);
            Assert.Equal(esito, cond.Esito);
            Assert.Equal("Nota operazione QA - " + TestoEsito(esito), cond.Nota);
            if (esito == Esito.NonConforme)
                Assert.Equal(DescrizioneProblema, cond.DescrizioneProblema);
            if (esito == Esito.ConformeConDeviazioni)
                Assert.Equal(DescrizioneDeviazione, cond.DescrizioneDeviazione);
            if (esito == Esito.NonApplicabile)
                Assert.Equal(MotivazioneNa, cond.MotivazioneNonApplicabile);

            var segnalazioni = TestCoverageSeed.FileSegnalazioni(workspace)
                .Select(TestCoverageSeed.LeggiSegnalazione).ToList();
            var ncs = TestCoverageSeed.FileNonConformita(workspace)
                .Select(TestCoverageSeed.LeggiNonConformita).ToList();

            if (!problematico)
            {
                Assert.Empty(segnalazioni);
                Assert.Empty(ncs);
                Assert.Null(cond.SegnalazioneUid);
            }
            else
            {
                var seg = Assert.Single(segnalazioni);
                Assert.Equal(tc.Id, seg.TestId);
                Assert.Equal(cond.Uid, seg.CondizioneUid);
                Assert.Equal(seg.Uid, cond.SegnalazioneUid);
                if (gestione == GestioneSegnalazione.NonGestire)
                {
                    Assert.Equal(StatoSegnalazione.DaClassificare, seg.Stato);
                    Assert.Null(seg.AnomaliaUid);
                    Assert.Empty(ncs);
                }
                else if (gestione == GestioneSegnalazione.Escludi)
                {
                    Assert.Equal(StatoSegnalazione.Esclusa, seg.Stato);
                    Assert.Equal(MotivoEsclusione, seg.MotivazioneEsclusione);
                    Assert.Null(seg.AnomaliaUid);
                    Assert.Empty(ncs);
                }
                else
                {
                    Assert.Equal(StatoSegnalazione.Classificata, seg.Stato);
                    var nc = Assert.Single(ncs);
                    Assert.Equal(nc.Uid, seg.AnomaliaUid);
                    if (gestione == GestioneSegnalazione.CreaNc)
                    {
                        Assert.NotEqual(NcEsistente, nc.Id);
                        Assert.Equal(SeveritaAnomalia.Medium, nc.Severita);
                        Assert.Equal(StatoAnomalia.Confermata, nc.StatoWorkflow);
                    }
                    else Assert.Equal(NcEsistente, nc.Id); // no duplicate NC
                    Assert.Contains(nc.ProveCollegate, p =>
                        p.TestId == tc.Id && p.CondizioneUid == cond.Uid);
                }
            }

            // Vera riapertura dalla lista e rilettura: il test non deve essere
            // dichiarato riuscito guardando solo il pannello di creazione.
            var riaperto = new TestListPage(window, window).OpenOnlyRow();
            Wait.ForCondition(() => riaperto.NumeroCondizioni() == 1,
                "operazione persistita visibile dopo riapertura");
            Assert.True(Guid.TryParse(riaperto.GetUidCondizione(0), out var uidRiaperto)
                && uidRiaperto == cond.Uid,
                "la riapertura UI mostra esattamente la stessa operazione persistita");
            if (gestione == GestioneSegnalazione.NonGestire)
                Assert.True(riaperto.HaSegnalazioniDaClassificare,
                    "In corso conserva la segnalazione pending anche dopo la riapertura");
        }
        catch (Exception ex)
        {
            var artifacts = FailureArtifacts.Capture(app,
                nameof(NuovoTest_PerAmbitoEsitoESceltaNc_ConfermaPersistenzaELegami),
                ex, step: caso);
            throw new Exception($"Fallito scenario {caso}. Diagnostica: {artifacts}", ex);
        }
    }

    private static string TestoEsito(Esito esito) => esito switch
    {
        Esito.DaEseguire => "Da eseguire",
        Esito.Conforme => "Conforme",
        Esito.NonConforme => "Non Conforme",
        Esito.ConformeConDeviazioni => "Conforme con deviazioni",
        Esito.NonApplicabile => "Non applicabile",
        _ => throw new ArgumentOutOfRangeException(nameof(esito))
    };
}
