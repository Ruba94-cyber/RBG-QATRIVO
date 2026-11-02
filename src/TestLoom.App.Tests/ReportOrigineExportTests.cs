using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using System.IO.Compression;
using TestLoom.App.Views;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Fogli "Filtri" e "Origine" dell'export Excel (6.4.3): l'origine deve essere esattamente l'insieme
/// dei dati che contribuiscono alla vista, stessi filtri, con l'operazione vincente marcata.</summary>
public sealed class ReportOrigineExportTests
{
    [Fact]
    public void SnapshotTestCases_RilevaAggiunteERicordificaPayloadConStessaRevisione()
    {
        var originale = T("T-snapshot", "Flash", "EDC", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme);
        var snapshot = new[] { System.Text.Json.JsonSerializer.Deserialize<TestCase>(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(originale, JsonIo.Options), JsonIo.Options)! };
        Assert.True(ReportService.SnapshotTestCasesUguale(snapshot, [originale]));
        var firme = ReportService.CatturaFirmeTestCases([originale]);
        Assert.True(ReportService.FirmeTestCasesUguali(firme, [originale]));

        var modificato = System.Text.Json.JsonSerializer.Deserialize<TestCase>(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(originale, JsonIo.Options), JsonIo.Options)!;
        modificato.DescrizioneBreve = "modifica senza cambiare revisione";
        Assert.Equal(originale.RevisioneDati, modificato.RevisioneDati);
        Assert.False(ReportService.SnapshotTestCasesUguale(snapshot, [modificato]));
        Assert.False(ReportService.SnapshotTestCasesUguale(snapshot, [originale, T("nuovo", "Flash", "EDC", "Daily A", DateTime.Today, Esito.Conforme)]));
        Assert.False(ReportService.FirmeTestCasesUguali(firme, [modificato]));
        Assert.False(ReportService.FirmeTestCasesUguali(firme, [originale, T("nuovo", "Flash", "EDC", "Daily A", DateTime.Today, Esito.Conforme)]));
    }

    private static readonly List<Funzionalita> Funz =
    [
        new() { Nome = "Lettura guasto", Categorie = ["Diag. Base"] },
        new() { Nome = "Flash", Categorie = ["Programming"] }
    ];

    private static TestCase T(string id, string funz, string componente, string veicolo, DateTime data, Esito esito,
        OrigineTest origine = OrigineTest.TestLoom)
    {
        var tc = new TestCase { Id = id, Origine = origine, DescrizioneBreve = "d-" + id, Componente = new EcuSelection([componente]), Veicoli = [veicolo], Funzionalita = [funz] };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = data, Operatore = "Mario", Esito = esito, Note = "n-" + id,
            Condizioni = [new Condizione { Funzionalita = funz, Esito = esito, Componente = new EcuSelection([componente]) }]
        });
        return tc;
    }

    private static readonly ConfigurazioneMatriceReport EcuPerCategoria = new()
    {
        AsseX = DimensioniMatrice.Componente,
        AsseY = DimensioniMatrice.CategoriaFunzionalita
    };

    [Fact]
    public void Origine_ContieneTuttiIDatiCheContribuisconoEMarcaLOperazioneVincente()
    {
        var tests = new[]
        {
            T("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.NonConforme),
            T("T-2", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 5), Esito.Conforme),
            T("U-1", "Flash", "EDC", "Daily A", new DateTime(2026, 2, 1), Esito.Conforme, OrigineTest.ImportStatoUdt)
        };

        var (righeLazy, _, totale) = ReportMatriceService.Origine(tests, EcuPerCategoria, [], Funz, [], 100);
        var righe = righeLazy.ToList();

        Assert.Equal(3, totale);
        Assert.Equal(3, righe.Count);
        Assert.False(righe.Single(r => r.TestId == "T-1").DeterminaStato);
        Assert.True(righe.Single(r => r.TestId == "T-2").DeterminaStato);
        var unitlogger = righe.Single(r => r.TestId == "U-1");
        Assert.Equal(OrigineTest.ImportStatoUdt, unitlogger.Origine);
        Assert.True(unitlogger.DeterminaStato);
        Assert.Equal("Programming", unitlogger.Riga);
        Assert.Equal("EDC", unitlogger.Colonna);
        Assert.Equal("Mario", unitlogger.Operatore);
        Assert.Equal("n-U-1", unitlogger.Note);
        // Una sola operazione vincente per combinazione, e il conteggio della vista coincide.
        Assert.Equal(2, righe.Count(r => r.DeterminaStato));
        var vista = ReportMatriceService.Genera(tests, EcuPerCategoria, [], Funz, []);
        Assert.Equal(2, vista.Righe.SelectMany(r => r.Celle).Sum(c => c.Totali));
    }

    [Fact]
    public void Origine_RispettaIFiltriDellaVista_ELAsseEsito()
    {
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Esito, AsseY = DimensioniMatrice.Funzionalita };
        config.Filtri[DimensioniMatrice.Componente] = ["EDC"];
        var tests = new[]
        {
            T("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme),
            T("T-2", "Flash", "EDC", "Daily A", new DateTime(2026, 1, 1), Esito.NonConforme)
        };

        var (righeLazy, _, totale) = ReportMatriceService.Origine(tests, config, [], Funz, [], 100);
        var righe = righeLazy.ToList();

        Assert.Equal(1, totale);
        var r = Assert.Single(righe);
        Assert.Equal("T-2", r.TestId);
        Assert.Equal("Flash", r.Riga);
        Assert.Equal(Esito.NonConforme.Label(), r.Colonna);
    }

    /// <summary>Review 6.4.3 punto 1: con il tetto, TUTTI i vincitori (uno per combinazione) restano inclusi,
    /// anche se in ordine di iterazione i non-vincitori li precederebbero; il resto riempie fino al tetto.</summary>
    [Fact]
    public void Origine_ConTetto_IncludeSempreTuttiIVincitori()
    {
        // 6 test sulla STESSA combinazione (vince l'ultimo per data) + 1 altra combinazione.
        var tests = Enumerable.Range(0, 6).Select(i =>
            T("T-" + i, "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1 + i), Esito.Conforme)).ToList();
        tests.Add(T("T-9", "Flash", "EDC", "Daily A", new DateTime(2026, 3, 1), Esito.Conforme));

        var (righeLazy, mostrate, totale) = ReportMatriceService.Origine(tests, EcuPerCategoria, [], Funz, [], 3);
        var righe = righeLazy.ToList();

        Assert.Equal(7, totale);
        Assert.Equal(3, mostrate);
        Assert.Equal(3, righe.Count);
        Assert.Equal(2, righe.Count(r => r.DeterminaStato));
        Assert.Contains(righe, r => r.TestId == "T-5" && r.DeterminaStato); // il piu' recente, ultimo in iterazione
        Assert.Contains(righe, r => r.TestId == "T-9" && r.DeterminaStato);
    }

    [Fact]
    public void SceltaSogliaExport_ConteggiaETroncaSoloDopoSceltaConEntrambiICasiOltreLimite()
    {
        const int limite = 10_000;
        var decisiveEntroLimite = Enumerable.Range(0, 1_800)
            .Select(i => OrigineRiga($"D{i}", true))
            .ToArray();
        var aggiuntiveOltreLimite = Enumerable.Range(0, 20_000)
            .Select(i => OrigineRiga($"A{i}", false))
            .ToArray();

        var conteggi = ReportOrigineExportService.CalcolaConteggiLimitati(
            decisiveEntroLimite.Length, aggiuntiveOltreLimite.Length, limite);
        Assert.True(conteggi.RichiedeScelta);
        Assert.Equal(1_800, conteggi.DeterminantiIncluse);
        Assert.Equal(10_000, conteggi.AggiuntiveIncluse);
        Assert.Equal(0, conteggi.DeterminantiEscluse);
        Assert.Equal(10_000, conteggi.AggiuntiveEscluse);
        var mantenute = ReportOrigineExportService.SelezionaRigheLimitate(
            decisiveEntroLimite.Concat(aggiuntiveOltreLimite).ToArray(), limite);
        Assert.Equal(11_800, mantenute.Count);
        Assert.Equal(1_800, mantenute.Count(r => r.DeterminaStato));
        Assert.Equal(10_000, mantenute.Count(r => !r.DeterminaStato));

        var decisiveOltreLimite = Enumerable.Range(0, 20_000)
            .Select(i => OrigineRiga($"D{i}", true))
            .ToArray();
        var aggiuntive = Enumerable.Range(0, 100)
            .Select(i => OrigineRiga($"A{i}", false))
            .ToArray();
        conteggi = ReportOrigineExportService.CalcolaConteggiLimitati(
            decisiveOltreLimite.Length, aggiuntive.Length, limite);
        Assert.True(conteggi.RichiedeScelta);
        Assert.Equal(10_000, conteggi.DeterminantiIncluse);
        Assert.Equal(0, conteggi.AggiuntiveIncluse);
        Assert.Equal(10_000, conteggi.DeterminantiEscluse);
        Assert.Equal(100, conteggi.AggiuntiveEscluse);
        mantenute = ReportOrigineExportService.SelezionaRigheLimitate(
            decisiveOltreLimite.Concat(aggiuntive).ToArray(), limite);
        Assert.Equal(10_000, mantenute.Count);
        Assert.All(mantenute, r => Assert.True(r.DeterminaStato));

        conteggi = ReportOrigineExportService.CalcolaConteggiLimitati(1_800, 9_000, limite);
        Assert.False(conteggi.RichiedeScelta);
    }

    private static ReportMatriceService.OrigineDato OrigineRiga(string id, bool determinaStato) => new(
        id, "descrizione", OrigineTest.TestLoom, 1, DateTime.UnixEpoch, "operatore", "veicolo", "gamma", "Componente",
        "funzionalita", "categoria", "OK", "OK", determinaStato, "riga", "colonna", "note", false);

    /// <summary>Review punto 4: stesso Test/revisione/tentativo che genera due candidati con la stessa chiave
    /// core (funzionalita' duplicata) -> un solo "Determina lo stato" per combinazione.</summary>
    [Fact]
    public void Origine_CandidatiDuplicatiSuStessaCombinazione_UnSoloVincitore()
    {
        var tc = T("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme);
        tc.Funzionalita = ["Lettura guasto", "Lettura guasto"];
        tc.Esecuzioni[0].Condizioni[0].Funzionalita = ""; // eredita le funzionalita' del Test (duplicate)
        var (righeLazy, _, _) = ReportMatriceService.Origine([tc], EcuPerCategoria, [], Funz, [], 100);
        var righe = righeLazy.ToList();
        Assert.True(righe.Count >= 2);
        Assert.Equal(1, righe.Count(r => r.DeterminaStato));
    }

    [Fact]
    public void Origine_LimiteRiguardaSoloLeRigheAggiuntive_EMantieneTuttiIVincitori()
    {
        var precedente = T("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.NonConforme);
        var corrente = T("T-2", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 5), Esito.Conforme);
        var (righe, mostrate, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive([precedente, corrente], EcuPerCategoria, [], Funz, [], limiteRigheAggiuntive: 0);
        var risultato = righe.ToList();

        Assert.Equal(2, totale);
        Assert.Equal(1, mostrate);
        Assert.Single(risultato);
        Assert.True(risultato[0].DeterminaStato);
        Assert.Equal("T-2", risultato[0].TestId);
    }

    [Fact]
    public void Origine_DeterminantiOltreIlTetto_RestanoTuttiPresenti()
    {
        var tests = Enumerable.Range(0, 7).Select(i =>
            T($"T-{i}", "Lettura guasto", "BCM", $"Veicolo-{i}", new DateTime(2026, 1, 1), Esito.Conforme)).ToList();
        var (righeLazy, mostrate, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(
            tests, EcuPerCategoria, [], Funz, [], limiteRigheAggiuntive: 2);
        var righe = righeLazy.ToList();

        Assert.Equal(7, totale);
        Assert.Equal(7, mostrate);
        Assert.Equal(7, righe.Count(r => r.DeterminaStato));
    }

    [Fact]
    public void CalcolaOrigineCompleta_UsaMasterEFiltroNaDalloSnapshotAncheSeIlLiveCambia()
    {
        using var state = TestStateFactory.Create();
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReportSnapshot_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            state.InizializzaCartella(dir);
            var veicoli = new[] { new Veicolo { Nome = "Daily A", GammaNome = "Gamma preflight" } };
            var funzionalita = new[] { new Funzionalita { Nome = "Lettura guasto", Categorie = ["Diag. Base"] } };
            var template = Array.Empty<TestTemplate>();
            var manuali = Array.Empty<StatoFunzionalitaEcuVeicolo>();
            var na = Array.Empty<CellaNonApplicabile>();
            var config = new ConfigurazioneMatriceReport { AsseY = DimensioniMatrice.Gamma, AsseX = DimensioniMatrice.Componente };
            var test = T("T-SNAPSHOT", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme);
            state.Master.Veicoli.Save([new Veicolo { Nome = "Daily A", GammaNome = "Gamma live modificata" }]);

            var origine = ReportOrigineExportService.CalcolaOrigineCompleta([test], state, config,
                veicoliSnapshot: veicoli, funzionalitaSnapshot: funzionalita, templatesSnapshot: template,
                manualiSnapshot: manuali, marcatureSnapshot: na);

            Assert.Equal("Gamma preflight", Assert.Single(origine.Righe).Riga);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void CalcolaOrigine_CancellaPreflightPrimaDiScansionare()
    {
        using var state = TestStateFactory.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ReportOrigineExportService.CalcolaOrigine([], state,
            new ConfigurazioneMatriceReport(), cancellation.Token));
    }

    [Fact]
    public void ReportExportDialogAutomation_OffreTreScelteEAnnullamentoSenzaEffetti()
    {
        Assert.Equal("Report_ExportPreflight_Dialog", ReportView.IdDialogoPreflightExport);
        Assert.Equal("Report_ExportPreflight_Cancel_Button", ReportView.IdAnnullaPreflightExport);
        Assert.Equal("Report_ExportLarge_Dialog", ReportView.IdDialogoExportGrande);
        Assert.Equal(new[] { 0, 1, 2 }, ReportView.IdScelteExportGrande.Keys.OrderBy(x => x));
        Assert.Equal(3, ReportView.IdScelteExportGrande.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.False(ReportView.SceltaExportAnnullata(0));
        Assert.False(ReportView.SceltaExportAnnullata(1));
        Assert.True(ReportView.SceltaExportAnnullata(2));
        Assert.True(ReportView.SceltaExportAnnullata(null)); // chiusura del dialogo non apre il file picker

        using var cancellation = new CancellationTokenSource();
        var notificata = false;
        ReportView.AnnullaPreflightExport(cancellation, () => notificata = true);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(notificata);
    }

    [Fact]
    public void ReportExportErroreSnapshot_VieneEspostoNelBannerDellaPagina()
    {
        using var state = TestStateFactory.Create();
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReportErrore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        using var vm = new ReportViewModel(state, aggiornaInBackground: false);
        ReportView.SegnalaErroreExportReport(vm, new InvalidOperationException("i dati sono cambiati"));

        Assert.True(vm.HaMessaggioErrore);
        Assert.Contains("i dati sono cambiati", vm.MessaggioErrore);
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task ExportReport_SnapshotMasterModificato_FallisconoXlsxEZipeDestinazioneRestaIntatta()
    {
        using var state = TestStateFactory.Create();
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReportGuard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            state.InizializzaCartella(dir);
            state.Master.Veicoli.Save([
                new Veicolo { Nome = "Daily A", GammaNome = "Gamma A" },
                new Veicolo { Nome = "Daily B", GammaNome = "Gamma B" }
            ]);
            state.Master.Funzionalita.Save(Funz);
            state.TestCases.Save(T("T-GUARD-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme), isNew: true);
            state.TestCases.Save(T("T-GUARD-2", "Flash", "EDC", "Daily B", new DateTime(2026, 1, 2), Esito.NonConforme), isNew: true);
            using var vm = new ReportViewModel(state, aggiornaInBackground: false);
            var (filtro, config, origine) = await vm.PreparaOrigineExportAsync();
            Assert.NotEmpty(origine.Righe);
            var troncata = ReportOrigineExportService.CreaPreparataDaSnapshot(origine, origine.Righe.Take(1).ToList());
            Assert.Single(troncata.Righe);
            Assert.Equal(origine.TotaleCompleto, troncata.TotaleCompleto);
            Assert.Same(origine.TestCasesVista, troncata.TestCasesVista);
            Assert.Same(origine.TestCasesMatrice, troncata.TestCasesMatrice);
            Assert.Same(origine.VeicoliSnapshot, troncata.VeicoliSnapshot);
            Assert.Same(origine.NonConformitaCompletaSnapshot, troncata.NonConformitaCompletaSnapshot);

            var veicoliModificati = state.Master.Veicoli.Load().ToList();
            foreach (var veicolo in veicoliModificati) veicolo.GammaNome = "Gamma cambiata";
            state.Master.Veicoli.Save(veicoliModificati);

            foreach (var estensione in new[] { ".xlsx", ".zip" })
            {
                var destinazione = Path.Combine(dir, "report" + estensione);
                File.WriteAllText(destinazione, "precedente");
                Assert.Throws<InvalidOperationException>(() => EsportazioneFile.ScriviAtomico(destinazione, temporaneo =>
                {
                    if (estensione == ".xlsx")
                        File.WriteAllBytes(temporaneo, ReportService.GeneraExcelVista(filtro, state, "Test", config, troncata, CancellationToken.None));
                    else
                    {
                        using var stream = File.Create(temporaneo);
                        ReportService.ScriviExcelVistaPartizionato(stream, filtro, state, "Test", config, troncata.Righe.Concat(troncata.Righe).ToList(),
                            troncata.TotaleCompleto, 1, CancellationToken.None, troncata);
                    }
                }));
                Assert.Equal("precedente", File.ReadAllText(destinazione));
            }
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Origine_CalcoloCompletoEsponeTotaleEsattoQuandoDecisiviSuperanoLaSoglia()
    {
        var testCases = new[]
        {
            T("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.NonConforme),
            T("T-2", "Flash", "EDC", "Daily A", new DateTime(2026, 1, 2), Esito.Conforme)
        };
        var (righe, _, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(testCases, EcuPerCategoria, [], Funz, [], int.MaxValue);
        var origine = righe.ToList();
        Assert.Equal(origine.Count, totale);
        Assert.True(origine.Count > 0);
    }

    [Fact]
    public void ReportPartizionato_CreaWorkbookCompletiConFiltriEPartizioniOrigineDisgiunte()
    {
        var testCases = new[]
        {
            T("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.NonConforme),
            T("T-2", "Flash", "EDC", "Daily A", new DateTime(2026, 1, 2), Esito.Conforme),
            T("T-3", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 3), Esito.Conforme)
        };
        var config = new ConfigurazioneMatriceReport();
        var full = ReportMatriceService.Origine(testCases, config, [], Funz, [], int.MaxValue).Righe.ToList();
        Assert.True(full.Count > 1);
        using var ms = new MemoryStream();
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReportZip_" + Guid.NewGuid().ToString("N"));
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        ReportService.ScriviExcelVistaPartizionato(ms, new ReportFilter(), state, "Test", config,
            full, full.Count, 1, CancellationToken.None);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(full.Count, zip.Entries.Count);
        var ids = new List<string>();
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var wb = new XLWorkbook(stream);
            Assert.Contains(wb.Worksheets, w => w.Name == "Matrice configurabile");
            Assert.Contains(wb.Worksheets, w => w.Name == LocalizationService.Instance.Traduci("OrigineFoglioFiltri"));
            var origine = wb.Worksheet(LocalizationService.Instance.Traduci("OrigineFoglioOrigine"));
            Assert.Equal(6, origine.LastRowUsed()!.RowNumber());
            ids.Add(origine.Cell(6, 1).GetString());
        }
        Assert.Equal(full.Count, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Origine_TestPercentuale_ConservaTuttiICandidatiCheContribuiscono()
    {
        var testCases = new[]
        {
            T("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.NonConforme),
            T("T-2", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 2), Esito.Conforme),
            T("U-1", "Flash", "EDC", "Daily A", new DateTime(2026, 1, 3), Esito.Conforme, OrigineTest.ImportStatoUdt),
        };
        var config = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Componente,
            AsseY = DimensioniMatrice.CategoriaFunzionalita,
            ModalitaCella = ModalitaCellaMatrice.TestPercentuale
        };

        var (righe, mostrate, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(
            testCases, config, [], Funz, [], limiteRigheAggiuntive: 0);
        var righeRisultato = righe.ToList();

        Assert.Equal(totale, mostrate);
        Assert.Equal(2, righeRisultato.Count);
        Assert.DoesNotContain(righeRisultato, r => r.TestId == "U-1");
        Assert.All(righeRisultato, r => Assert.True(r.DeterminaStato));
    }

    [Fact]
    public void Matrice_TestIdsDuplicatiMantieneSelezioneGraficiDistintaPerUid()
    {
        var primo = T("T-DUP", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme);
        var secondo = T("T-DUP", "Flash", "EDC", "Daily A", new DateTime(2026, 1, 2), Esito.NonConforme);
        var config = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Componente,
            AsseY = DimensioniMatrice.CategoriaFunzionalita,
            ModalitaCella = ModalitaCellaMatrice.TestPercentuale
        };

        var preparata = ReportMatriceService.Prepara([primo, secondo], config, [], Funz, []);

        Assert.Contains(primo.Uid, preparata.TestUidsVista);
        Assert.Contains(secondo.Uid, preparata.TestUidsVista);
        Assert.Single(preparata.TestIdsVista);
    }

    [Fact]
    public void Origine_StatoCoordinateSeguonoIlRappresentanteMaMantengonoLaFonteStorica()
    {
        var storico = T("T-OLD", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.NonConforme);
        storico.Assegnatario = "Storico";
        var corrente = T("T-NEW", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 2), Esito.Conforme);
        corrente.Assegnatario = "Corrente";
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Assegnatario };

        var (righe, _, _) = ReportMatriceService.Origine([storico, corrente], config, [], Funz, [], 100);
        var rigaStorica = righe.Single(r => r.TestId == "T-OLD");

        Assert.Equal("T-OLD", rigaStorica.TestId); // i dati sorgente restano della riga storica
        Assert.Equal("Corrente", rigaStorica.Riga);
        Assert.Equal("BCM", rigaStorica.Colonna);
    }

    [Fact]
    public void Origine_RigheAggiuntiveRispettanoFiltriNonCore()
    {
        var storico = T("T-BOB", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.NonConforme);
        storico.Assegnatario = "Bob";
        var corrente = T("T-ALICE", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 2), Esito.Conforme);
        corrente.Assegnatario = "Alice";
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.CategoriaFunzionalita };
        config.Filtri[DimensioniMatrice.Assegnatario] = ["Alice"];

        var (righe, mostrate, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(
            [storico, corrente], config, [], Funz, [], limiteRigheAggiuntive: 5);
        var risultato = righe.ToList();

        Assert.Equal(1, totale);
        Assert.Equal(1, mostrate);
        Assert.Single(risultato);
        Assert.Equal("T-ALICE", risultato[0].TestId);
    }

    [Fact]
    public void Origine_ManualeMantieneIdentitaTraPassateEIncludeIlVincitoreConLimiteZero()
    {
        var assegnato = T("T-MANUAL-REP", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme);
        assegnato.Assegnatario = "Alice";
        var precedenti = Enumerable.Range(0, 3).Select(i =>
        {
            var test = T("T-MATCHED-OLD-" + i, "Flash", "EDC", "Daily A", new DateTime(2025, 12, 20 + i), Esito.NonConforme);
            test.Assegnatario = "Alice";
            return test;
        }).ToArray();
        var altraCombinazione = T("T-MATCHED", "Flash", "EDC", "Daily A", new DateTime(2026, 1, 2), Esito.Conforme);
        altraCombinazione.Assegnatario = "Alice";
        var selezione = new[] { assegnato }.Concat(precedenti).Append(altraCombinazione).ToArray();
        var manuale = new StatoFunzionalitaEcuVeicolo
        {
            Uid = Guid.NewGuid(), Funzionalita = "Lettura guasto", Componente = "BCM", Veicolo = "Daily A",
            Esito = Esito.NonConforme, Nota = "decisione manuale", Operatore = "Master", DataDecisione = new DateTime(2026, 1, 3)
        };

        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.CategoriaFunzionalita };
        config.Filtri[DimensioniMatrice.Assegnatario] = ["Alice"];
        var (righe, mostrate, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(
            selezione, config, [], Funz, [], 0, manuali: [manuale]);
        var risultatoZero = righe.ToList();

        Assert.Equal(6, totale); // cinque righe filtrate + il determinante manuale fuori filtro
        Assert.Equal(2, mostrate);
        Assert.Equal(mostrate, risultatoZero.Count);
        Assert.Contains(risultatoZero, r => r.DeterminaStato && r.Operatore == "Master");
        Assert.Contains(risultatoZero, r => r.TestId == "T-MATCHED" && r.DeterminaStato);

        var (righeConAggiuntive, mostrateConAggiuntive, totaleConAggiuntive) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(
            selezione, config, [], Funz, [], 3, manuali: [manuale]);
        var risultatoConAggiuntive = righeConAggiuntive.ToList();
        Assert.Equal(6, totaleConAggiuntive);
        Assert.Equal(5, mostrateConAggiuntive);
        Assert.Equal(mostrateConAggiuntive, risultatoConAggiuntive.Count);
        Assert.Contains(risultatoConAggiuntive, r => r.DeterminaStato && r.Operatore == "Master");
    }

    [Fact]
    public void Origine_TempoIncludeIlTentativoNonEffettivoCheContribuisceAlTotale()
    {
        var tc = T("T-REPEAT", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme);
        var primo = tc.Esecuzioni[0].Condizioni[0];
        primo.Esito = Esito.NonConforme;
        primo.DurataMinuti = 20;
        primo.DescrizioneProblema = "tentativo non effettivo incluso nel tempo";
        tc.Esecuzioni[0].Condizioni.Add(new Condizione
        {
            Uid = Guid.NewGuid(), OperazioneOrigineUid = primo.Uid, Funzionalita = "Lettura guasto",
            Componente = new EcuSelection(["BCM"]), Esito = Esito.Conforme, DurataMinuti = 10,
            DescrizioneProblema = "tentativo effettivo conclusivo"
        });
        var config = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Componente,
            AsseY = DimensioniMatrice.CategoriaFunzionalita,
            ModalitaCella = ModalitaCellaMatrice.TempoTotale,
            BaseTempo = BaseTempoMatrice.TutteLeEsecuzioni
        };

        var matrice = ReportMatriceService.Genera([tc], config, [], Funz, []);
        var tempo = matrice.Righe.Single().Celle.Single().Tempo;
        var (righe, mostrate, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive([tc], config, [], Funz, [], 0);
        var origine = righe.ToList();

        Assert.Equal(30, tempo.SommaMinuti);
        Assert.Equal(2, totale);
        Assert.Equal(2, mostrate);
        Assert.Equal(2, origine.Count);
        Assert.Contains(origine, r => r.Note.Contains("tentativo non effettivo incluso nel tempo", StringComparison.Ordinal));
        Assert.Contains(origine, r => r.Note.Contains("tentativo effettivo conclusivo", StringComparison.Ordinal));
        Assert.All(origine, r => Assert.True(r.DeterminaStato));

        config.Filtri[DimensioniMatrice.Esito] = [Esito.Conforme.Label()];
        var (filtrate, mostrateFiltrate, totaleFiltrate) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(
            [tc], config, [], Funz, [], 0);
        var risultatoFiltrato = filtrate.ToList();
        Assert.Equal(1, totaleFiltrate);
        Assert.Equal(1, mostrateFiltrate);
        Assert.Single(risultatoFiltrato);
        Assert.Contains("tentativo effettivo conclusivo", risultatoFiltrato[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Origine_TempoTutteLeEsecuzioniEscludeUdtETentativiFuoriFiltroEsito()
    {
        var dt = new DateTime(2026, 1, 1);
        var normale = T("T-TIME-FILTER", "Lettura guasto", "BCM", "Daily A", dt, Esito.Conforme);
        normale.Esecuzioni[0].Condizioni[0].DurataMinuti = 10;
        var unitlogger = T("U-TIME-FILTER", "Flash", "EDC", "Daily A", dt.AddDays(1), Esito.Conforme, OrigineTest.ImportStatoUdt);
        unitlogger.Esecuzioni[0].Condizioni[0].DurataMinuti = 50;
        var config = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Componente,
            AsseY = DimensioniMatrice.CategoriaFunzionalita,
            ModalitaCella = ModalitaCellaMatrice.TempoTotale,
            BaseTempo = BaseTempoMatrice.TutteLeEsecuzioni
        };
        config.Filtri[DimensioniMatrice.Esito] = [Esito.NonConforme.Label()];

        var (righe, mostrate, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(
            [normale, unitlogger], config, [], Funz, [], 5);

        Assert.Empty(righe);
        Assert.Equal(0, mostrate);
        Assert.Equal(0, totale);
    }

    [Fact]
    public void Origine_TempoTutteLeEsecuzioniRispettaIlGateDelRappresentanteComeLaMatrice()
    {
        var storicoAlice = T("T-ALICE-OLD", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.NonConforme);
        storicoAlice.Assegnatario = "Alice";
        storicoAlice.Esecuzioni[0].Condizioni[0].DurataMinuti = 20;
        var correnteBob = T("T-BOB-CURRENT", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 2), Esito.Conforme);
        correnteBob.Assegnatario = "Bob";
        correnteBob.Esecuzioni[0].Condizioni[0].DurataMinuti = 10;
        var config = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Componente,
            AsseY = DimensioniMatrice.CategoriaFunzionalita,
            ModalitaCella = ModalitaCellaMatrice.TempoTotale,
            BaseTempo = BaseTempoMatrice.TutteLeEsecuzioni
        };
        config.Filtri[DimensioniMatrice.Assegnatario] = ["Alice"];

        var matrice = ReportMatriceService.Genera([storicoAlice, correnteBob], config, [], Funz, []);
        var (righe, mostrate, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(
            [storicoAlice, correnteBob], config, [], Funz, [], 10);
        var origine = righe.ToList();

        Assert.Empty(matrice.Righe);
        Assert.Empty(origine);
        Assert.Equal(0, mostrate);
        Assert.Equal(0, totale);
    }

    [Fact]
    public void Origine_TempoTutteLeEsecuzioni_AsseEsitoUsaLesitoDelTentativoStorico()
    {
        var tc = T("T-ESITO-TIME", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme);
        var storico = tc.Esecuzioni[0].Condizioni[0];
        storico.Esito = Esito.NonConforme;
        storico.DurataMinuti = 20;
        var corrente = new Condizione
        {
            Uid = Guid.NewGuid(), OperazioneOrigineUid = storico.Uid, Funzionalita = "Lettura guasto",
            Componente = new EcuSelection(["BCM"]), Esito = Esito.Conforme, DurataMinuti = 10
        };
        tc.Esecuzioni[0].Condizioni.Add(corrente);
        var config = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Esito,
            AsseY = DimensioniMatrice.Componente,
            ModalitaCella = ModalitaCellaMatrice.TempoTotale,
            BaseTempo = BaseTempoMatrice.TutteLeEsecuzioni
        };

        var (righe, _, _) = ReportMatriceService.OrigineConLimiteRigheAggiuntive([tc], config, [], Funz, [], 0);
        var origine = righe.ToList();

        Assert.Equal(2, origine.Count);
        Assert.Contains(origine, r => r.Colonna.Contains("Non conforme", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(origine, r => r.Colonna.Contains("Conforme", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Origine_TempoTutteLeEsecuzioni_ConservaTuttiITentativiCheAlimentanoLaDurata()
    {
        var tc = T("T-TEMPO", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme);
        tc.Esecuzioni[0].Condizioni[0].DurataMinuti = 10;
        tc.Esecuzioni[0].Condizioni.Add(new Condizione
        {
            Funzionalita = "Lettura guasto", Componente = new EcuSelection(["BCM"]), Esito = Esito.Conforme, DurataMinuti = 20
        });
        var config = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Componente,
            AsseY = DimensioniMatrice.CategoriaFunzionalita,
            ModalitaCella = ModalitaCellaMatrice.TempoTotale,
            BaseTempo = BaseTempoMatrice.TutteLeEsecuzioni
        };

        var (righe, mostrate, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(
            [tc], config, [], Funz, [], limiteRigheAggiuntive: 0);
        var righeRisultato = righe.ToList();

        Assert.Equal(2, totale);
        Assert.Equal(2, mostrate);
        Assert.Equal(2, righeRisultato.Count);
        Assert.All(righeRisultato, r => Assert.True(r.DeterminaStato));
    }

    [Fact]
    public void Origine_TestPrevistoSenzaEsecuzione_ConservaIlRappresentanteCheDeterminaDaEseguire()
    {
        var pianificato = new TestCase
        {
            Id = "T-PIANIFICATO", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"]
        };

        var (righe, mostrate, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(
            [pianificato], EcuPerCategoria, [], Funz, [], limiteRigheAggiuntive: 0);
        var riga = Assert.Single(righe);

        Assert.Equal(1, mostrate);
        Assert.Equal(1, totale);
        Assert.True(riga.DeterminaStato);
        Assert.Equal("Non testato", riga.StatoCombinazione);
    }

    [Fact]
    public void Origine_AnnullamentoDuranteLaScansioneVieneRispettato()
    {
        using var cts = new CancellationTokenSource();
        var moltiTentativi = new TestCase
        {
            Id = "T-CANCEL",
            Componente = new EcuSelection(["BCM"]),
            Funzionalita = ["Lettura guasto"],
            Esecuzioni =
            [
                new Esecuzione
                {
                    Condizioni = Enumerable.Range(0, 256).Select(_ => new Condizione
                    {
                        Funzionalita = "Lettura guasto", Componente = new EcuSelection(["BCM"]), Esito = Esito.Conforme
                    }).ToList()
                }
            ]
        };
        IEnumerable<TestCase> TestCases()
        {
            yield return moltiTentativi;
            cts.Cancel();
            yield return T("T-CANCEL-SECOND", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 2), Esito.Conforme);
        }

        Assert.Throws<OperationCanceledException>(() => ReportMatriceService.OrigineConLimiteRigheAggiuntive(
            TestCases(), EcuPerCategoria, [], Funz, [], limiteRigheAggiuntive: 1, ct: cts.Token));
    }

    [Fact]
    public void Origine_DueUidConStessoIdNonDuplicanoIlContrassegnoVincitore()
    {
        var primo = T("ID-LEGACY-DUP", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme);
        var secondo = T("ID-LEGACY-DUP", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 2), Esito.Conforme);
        Assert.NotEqual(primo.Uid, secondo.Uid);

        var (righe, _, _) = ReportMatriceService.Origine([primo, secondo], EcuPerCategoria, [], Funz, [], int.MaxValue);

        Assert.Equal(1, righe.Count(r => r.DeterminaStato));
    }

    /// <summary>Review punto 3: la colonna N.A. e' coerente con le marcature della vista (cella esclusa dai totali).</summary>
    [Fact]
    public void Origine_MarcaNonApplicabileLeRigheInCelleMarcate()
    {
        var tests = new[]
        {
            T("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme),
            T("T-2", "Flash", "EDC", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme)
        };
        var marcature = new List<CellaNonApplicabile> { new() { ValoreAsseX = "BCM", ValoreAsseY = "Diag. Base" } };

        var (righeLazy, _, _) = ReportMatriceService.Origine(tests, EcuPerCategoria, [], Funz, [], 100, marcature);
        var righe = righeLazy.ToList();

        Assert.True(righe.Single(r => r.TestId == "T-1").NonApplicabile);
        Assert.False(righe.Single(r => r.TestId == "T-2").NonApplicabile);
    }

    /// <summary>Review punto 6: testi che iniziano con "=" restano testo nel foglio, mai formule; la colonna N.A. c'e'.</summary>
    [Fact]
    public void FoglioOrigine_TestoCheIniziaConUgualeNonDiventaFormula()
    {
        var tc = T("T-1", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.Conforme);
        tc.Esecuzioni[0].Note = "=1+1";
        var (righe, mostrate, totale) = ReportMatriceService.Origine([tc], EcuPerCategoria, [], Funz, [], 100);
        using var wb = new XLWorkbook();
        typeof(ReportOrigineExportService).GetMethod("ScriviOrigine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [wb, righe, mostrate, totale, EcuPerCategoria, System.Threading.CancellationToken.None, 1_048_571]);
        var ws = wb.Worksheet(LocalizationService.Instance.Traduci("OrigineFoglioOrigine"));
        var cella = ws.Cell(6, 17);
        Assert.False(cella.HasFormula);
        Assert.Equal("=1+1", cella.GetString());
        Assert.Equal(XLDataType.Text, cella.DataType);
        Assert.Equal(LocalizationService.Instance.Traduci("OrigineColNonApplicabile"), ws.Cell(5, 18).GetString());
    }

    /// <summary>End-to-end sul dataset di semina: la Vista esportata (foglio "Matrice configurabile") coincide
    /// con il risultato della matrice, e "Origine" ha una riga per dato con il totale dichiarato.</summary>
    [Trait("Category", "Lento")]
    public sealed class SuDatasetSeminato : IDisposable
    {
        private readonly string _dir;
        private readonly AppState _state;

        public SuDatasetSeminato()
        {
            _dir = Path.Combine(Path.GetTempPath(), "TestLoomOrigineTests_" + Guid.NewGuid().ToString("N"));
            SeedDemoDataService.Genera(_dir);
            _state = TestStateFactory.Create();
            Directory.CreateDirectory(_dir);
            _state.InizializzaCartella(_dir);
        }

        public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

        [Theory]
        [InlineData(DimensioniMatrice.Componente, DimensioniMatrice.CategoriaFunzionalita)]
        [InlineData(DimensioniMatrice.Esito, DimensioniMatrice.Funzionalita)]
        public void VistaEOrigine_CoincidonoConLaMatrice(string asseX, string asseY)
        {
            var config = new ConfigurazioneMatriceReport { AsseX = asseX, AsseY = asseY };
            var bytes = ReportService.GeneraExcel(new ReportFilter(), _state, "Operatore Prova A", config);
            using var wb = new XLWorkbook(new MemoryStream(bytes));

            var modalitaStato = config.ModalitaCella != ModalitaCellaMatrice.TestPercentuale;
            var atteso = ReportMatriceService.Genera(_state.TestCases.GetAll().Where(t => !t.Eliminato), config,
                _state.Master.Veicoli.Load(), _state.Master.Funzionalita.Load(), _state.Master.TestTemplate.Load(),
                MatriceNonApplicabileService.Leggi(_state.Config, config.CampiX, config.CampiY),
                modalitaStato ? _state.Master.Componente.Load() : null,
                modalitaStato ? _state.Master.StatiFunzionalitaEcuVeicolo.Load() : null);
            var vista = wb.Worksheet("Matrice configurabile");
            for (var r = 0; r < atteso.Righe.Count; r++)
                for (var c = 0; c < atteso.Righe[r].Celle.Count; c++)
                    Assert.Equal(atteso.Righe[r].Celle[c].Testo, vista.Cell(r + 5, c + 2).GetString());

            var origine = wb.Worksheet(LocalizationService.Instance.Traduci("OrigineFoglioOrigine"));
            var righeOrigine = origine.LastRowUsed()!.RowNumber() - 5;
            Assert.True(righeOrigine > 0);
            var totaleCombinazioni = atteso.Righe.SelectMany(x => x.Celle).Sum(x => x.Totali);
            Assert.True(righeOrigine >= totaleCombinazioni / 3, "Origine deve avere almeno un dato per combinazione");
            Assert.NotNull(wb.Worksheet(LocalizationService.Instance.Traduci("OrigineFoglioFiltri")));
        }

        [Fact]
        public void FoglioFiltri_CampoCustomConEtichettaDelCatalogo_NonLaChiaveInterna()
        {
            // "progetto-demo" e' la Chiave del campo "Progetto" del seme: il foglio Filtri deve mostrare l'Etichetta.
            var filtro = new ReportFilter { CampiCustom = { ["progetto-demo"] = ["Phoenix"] } };
            var bytes = ReportService.GeneraExcel(filtro, _state, "Operatore Prova A", new ConfigurazioneMatriceReport());
            using var wb = new XLWorkbook(new MemoryStream(bytes));
            var ws = wb.Worksheet(LocalizationService.Instance.Traduci("OrigineFoglioFiltri"));
            var celle = ws.CellsUsed().Select(c => c.GetString()).ToList();
            Assert.Contains("Progetto", celle);
            Assert.Contains("Phoenix", celle);
            Assert.DoesNotContain("progetto-demo", celle);
        }

        [Fact]
        public void EsportazioneVista_IncludeLaSelezioneCompletaSottoIlLimiteAggiuntive()
        {
            using var wb = new XLWorkbook();
            var testCases = _state.TestCases.GetAll().ToList();
            var expected = ReportMatriceService.Origine(testCases, new ConfigurazioneMatriceReport(),
                _state.Master.Veicoli.Load(), _state.Master.Funzionalita.Load(), _state.Master.TestTemplate.Load(), int.MaxValue,
                manuali: _state.Master.StatiFunzionalitaEcuVeicolo.Load()).Totale;
            ReportOrigineExportService.Scrivi(
                wb, _state.TestCases.GetAll().ToList(), _state, new ReportFilter(), new ConfigurazioneMatriceReport(),
                "Operatore Prova A", DateTime.Now);
            var ws = wb.Worksheet(LocalizationService.Instance.Traduci("OrigineFoglioOrigine"));
            Assert.Equal(expected, ws.LastRowUsed()!.RowNumber() - 5);
        }

        [Fact]
        public void NoteLunghe_DueRigheDelloStessoTest_HannoRiferimentiUnivociERoundTripCompleto()
        {
            var notaA = "A" + new string('a', ExcelExportTableWriter.LimiteCaratteriCella);
            var notaB = "B" + new string('b', ExcelExportTableWriter.LimiteCaratteriCella);
            var tc = new TestCase
            {
                Id = "TC-NOTE-LUNGHE",
                Componente = new EcuSelection(["BCM"]),
                Funzionalita = ["Lettura guasto"],
                Esecuzioni =
                [
                    new Esecuzione { Iterazione = 1, Note = notaA, Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Componente = new EcuSelection(["BCM"]), Esito = Esito.NonConforme }] },
                    new Esecuzione { Iterazione = 2, Note = notaB, Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Componente = new EcuSelection(["BCM"]), Esito = Esito.Conforme }] }
                ]
            };
            using var wb = new XLWorkbook();
            ReportOrigineExportService.Scrivi(wb, [tc], _state, new ReportFilter(), new ConfigurazioneMatriceReport(),
                "Operatore", DateTime.Now);

            var origine = wb.Worksheet(LocalizationService.Instance.Traduci("OrigineFoglioOrigine"));
            var righe = origine.Rows(6, origine.LastRowUsed()!.RowNumber()).ToList();
            var marker = righe.Select(r => r.Cell(17).GetString()).Where(s => s.StartsWith("Testo esteso:", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, marker.Count);
            Assert.NotEqual(marker[0], marker[1]);
            var dettagli = wb.Worksheets.Where(w => w.Name.Contains("dettagli estesi", StringComparison.OrdinalIgnoreCase))
                .SelectMany(w => w.RowsUsed().Skip(1))
                .ToList();
            Assert.Contains(dettagli, r => r.Cell(1).GetString().Contains("!R6 (TC-NOTE-LUNGHE)", StringComparison.Ordinal)
                && r.Cell(4).GetString().StartsWith("A", StringComparison.Ordinal));
            Assert.Contains(dettagli, r => r.Cell(1).GetString().Contains("!R7 (TC-NOTE-LUNGHE)", StringComparison.Ordinal)
                && r.Cell(4).GetString().StartsWith("B", StringComparison.Ordinal));
            Assert.Equal(notaA.Length, dettagli.Where(r => r.Cell(1).GetString().Contains("!R6 (TC-NOTE-LUNGHE)", StringComparison.Ordinal))
                .Sum(r => r.Cell(4).GetString().Length));
            Assert.Equal(notaB.Length, dettagli.Where(r => r.Cell(1).GetString().Contains("!R7 (TC-NOTE-LUNGHE)", StringComparison.Ordinal))
                .Sum(r => r.Cell(4).GetString().Length));
            Assert.Contains(wb.Worksheets.Single(w => w.Name.Contains("dettagli estesi", StringComparison.OrdinalIgnoreCase)).Name,
                wb.Worksheet(LocalizationService.Instance.Traduci("OrigineFoglioOrigine")).Cell(6, 17).GetString());
        }

        [Fact]
        public void EsportazioneVista_AnnullamentoPrimaDellaScansioneNonProduceIlWorkbook()
        {
            using var wb = new XLWorkbook();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => ReportOrigineExportService.Scrivi(
                wb, _state.TestCases.GetAll().ToList(), _state, new ReportFilter(), new ConfigurazioneMatriceReport(),
                "Operatore", DateTime.Now, cts.Token));
            Assert.Empty(wb.Worksheets);
        }

        [Fact]
        public void Origine_ScriveAvvisoConIlNumeroDiRigheAggiuntiveEscluse()
        {
            var testCases = new[]
            {
                T("T-OLD", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 1), Esito.NonConforme),
                T("T-NEW", "Lettura guasto", "BCM", "Daily A", new DateTime(2026, 1, 2), Esito.Conforme),
            };
            var (righe, mostrate, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(
                testCases, EcuPerCategoria, [], Funz, [], limiteRigheAggiuntive: 0);
            using var wb = new XLWorkbook();
            ReportOrigineExportService.ScriviOrigine(wb, righe, mostrate, totale, EcuPerCategoria, CancellationToken.None);

            var nota = wb.Worksheet(LocalizationService.Instance.Traduci("OrigineFoglioOrigine")).Cell(3, 1).GetString();
            Assert.Contains("1", nota);
            Assert.Contains("determinano", nota, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Origine_OltreIlNumeroRigheConfigurato_CreaFogliSuccessiviCompleti()
        {
            var config = new ConfigurazioneMatriceReport();
            var (righe, mostrate, totale) = ReportMatriceService.Origine(_state.TestCases.GetAll(), config,
                _state.Master.Veicoli.Load(), _state.Master.Funzionalita.Load(), _state.Master.TestTemplate.Load(), int.MaxValue,
                manuali: _state.Master.StatiFunzionalitaEcuVeicolo.Load());
            Assert.True(mostrate >= 3);
            using var wb = new XLWorkbook();
            ReportOrigineExportService.ScriviOrigine(wb, righe, mostrate, totale, config, CancellationToken.None, righeDatiMassime: 2);

            var baseNome = LocalizationService.Instance.Traduci("OrigineFoglioOrigine");
            var foglio1 = wb.Worksheet(baseNome);
            var foglio2 = wb.Worksheet($"{baseNome} (2)");
            Assert.Equal(7, foglio1.LastRowUsed()!.RowNumber());
            Assert.Equal(7, foglio2.LastRowUsed()!.RowNumber());
            Assert.Equal(mostrate, foglio1.LastRowUsed()!.RowNumber() - 5 + foglio2.LastRowUsed()!.RowNumber() - 5
                + wb.Worksheets.Where(w => w.Name.StartsWith(baseNome, StringComparison.Ordinal) && w.Name != baseNome && w.Name != $"{baseNome} (2)")
                    .Sum(w => (w.LastRowUsed()?.RowNumber() ?? 5) - 5));
        }
    }
}
