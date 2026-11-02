using ClosedXML.Excel;
using TestLoom.App.Models;
using System.IO.Compression;

namespace TestLoom.App.Services;

public static class ReportService
{
    // ---------------------------------------------------------------- Excel

    /// <summary>Workbook della pagina Report: vista matrice, grafici del filtro applicato, origine dei Test
    /// e NC collegate ai risultati non OK che determinano una cella della matrice.</summary>
    public static byte[] GeneraExcelVista(ReportFilter filtro, AppState state, string operatoreGenerazione,
        ConfigurazioneMatriceReport? configurazioneMatrice = null, CancellationToken ct = default)
        => GeneraExcelVista(filtro, state, operatoreGenerazione, configurazioneMatrice, null, ct);

    /// <summary>Come <see cref="GeneraExcelVista(ReportFilter, AppState, string, ConfigurazioneMatriceReport?, CancellationToken)"/>,
    /// con una partizione esplicita delle righe d'origine. Tutte le parti conservano la stessa matrice, filtri e riferimenti NC.</summary>
    public static byte[] GeneraExcelVista(ReportFilter filtro, AppState state, string operatoreGenerazione,
        ConfigurazioneMatriceReport? configurazioneMatrice, ReportOrigineExportService.OriginePreparata? righeOrigine,
        CancellationToken ct, ExportConfigurazione? scelta = null, bool pivotStandardInQuestoFile = true)
    {
        ct.ThrowIfCancellationRequested();
        configurazioneMatrice ??= new ConfigurazioneMatriceReport();
        if (righeOrigine?.TestCasesMatrice != null && !SnapshotExtraReportUguale(state, righeOrigine,
                righeOrigine.FirmaNc ?? string.Empty))
            throw new InvalidOperationException("Snapshot completo del report non disponibile. Ripetere la preparazione.");
        var tutti = righeOrigine?.TestCasesVista?.ToList() ?? state.TestCases.GetAll();
        if (righeOrigine?.TestCasesVista is { } testCasesVistaSnapshot)
            tutti = testCasesVistaSnapshot.ToList();
        var testCasesGraficiGlobali = (righeOrigine?.TestCasesVista ?? tutti.Where(filtro.Corrisponde).ToList()).ToList();
        // I criteri globali filtrano l'universo; i criteri di durata per operazione sono valutati dalla matrice,
        // così come nella vista interattiva (la configurazione porta gli stessi valori del ViewModel).
        var testCasesMatrice = righeOrigine?.TestCasesMatrice?.ToList() ?? tutti.Where(filtro.CorrispondeMatrice).ToList();
        var requisiti = righeOrigine?.RequisitiSnapshot?.ToList() ?? state.Master.Requisiti.Load();
        var ncTutte = righeOrigine?.NonConformitaCompletaSnapshot?.ToList() ?? state.NonConformita.GetAll();
        if (righeOrigine?.NonConformitaCompletaSnapshot is { } ncs && !SnapshotNonConformitaUguale(ncs, state.NonConformita.GetAll()))
            throw new InvalidOperationException("Le NC sono cambiate durante la preparazione del report. Riprovare.");
        using var wb = new XLWorkbook();

        var matricePreparata = ScriviMatriceConfigurabile(wb, testCasesMatrice, state, configurazioneMatrice, filtro, ct,
            righeOrigine?.VeicoliSnapshot, righeOrigine?.FunzionalitaSnapshot, righeOrigine?.TemplatesSnapshot,
            righeOrigine?.EcuSnapshot, righeOrigine?.StatiManualiSnapshot, righeOrigine?.MarcatureSnapshot);
        ReportOrigineExportService.OriginePreparata? origineScritta = null;
        var selezioneOrigine = ReportOrigineExportService.Scrivi(wb, testCasesMatrice, state, filtro, configurazioneMatrice,
            operatoreGenerazione, DateTime.Now, ct: ct, originePreparata: righeOrigine,
            etichetteCampi: righeOrigine?.CampiDettagliSnapshot == null || righeOrigine.CampiPianificazioneSnapshot == null ? null
                : CampoSistemaCatalogoService.EtichettePerChiave(righeOrigine.CampiPianificazioneSnapshot, righeOrigine.CampiDettagliSnapshot),
            suOrigineScritta: o => origineScritta = o);

        // La matrice dimensiona ulteriormente la selezione globale. I grafici e le NC usano
        // gli stessi Test effettivamente presenti nell'origine della matrice.
        var testUidsVista = matricePreparata.TestUidsVista;
        var testCasesGrafici = testCasesGraficiGlobali.Where(tc => testUidsVista.Contains(tc.Uid)).ToList();
        var graficiNativi = ScriviGrafici(wb, state, filtro, requisiti, testCasesGrafici, tutti.Count, ncTutte, operatoreGenerazione, ct,
            etichetteCampi: righeOrigine?.CampiDettagliSnapshot == null || righeOrigine.CampiPianificazioneSnapshot == null ? null
                : CampoSistemaCatalogoService.EtichettePerChiave(righeOrigine.CampiPianificazioneSnapshot, righeOrigine.CampiDettagliSnapshot));
        if (righeOrigine?.TestCasesMatrice is not null && !SnapshotExtraReportUguale(state, righeOrigine, righeOrigine.FirmaNc ?? string.Empty))
            throw new InvalidOperationException("I dati sono cambiati durante la generazione. Riprovare.");
        if (righeOrigine?.TestCasesMatrice is not null && !SnapshotExtraReportUguale(state, righeOrigine,
                righeOrigine.FirmaNc ?? string.Empty))
            throw new InvalidOperationException("I dati sono cambiati durante la generazione. Riprovare.");
        var descrizioneFiltriMatrice = string.Join("; ", configurazioneMatrice.Filtri
            .Where(x => x.Value.Count > 0)
            .Select(x => $"{x.Key}: {string.Join(", ", x.Value)}"));
        if (!string.IsNullOrWhiteSpace(descrizioneFiltriMatrice))
            wb.Worksheet("Grafici").Cell(2, 1).Value += $" · {descrizioneFiltriMatrice}";

        // L'NC è pertinente solo se un Test collegato determina un esito non conforme o con deviazioni
        // in una cella non N.A.; gli altri record NC non vengono inclusi nel report filtrato.
        var idsNonOk = selezioneOrigine.TestIdsNonOk;
        var ncPertinenti = ncTutte.Where(nc => (!string.IsNullOrWhiteSpace(nc.TestCaseId) && idsNonOk.Contains(nc.TestCaseId))
            || nc.ProveCollegate.Any(p => idsNonOk.Contains(p.TestId))).ToList();
        NonConformitaExportService.ScriviFoglio(wb, ncPertinenti, state, "NC matrice", ct,
            testCasesSnapshot: righeOrigine?.TestCasesMatrice,
            veicoliSnapshot: righeOrigine?.VeicoliSnapshot);

        // Sezioni scelte nel dialogo di export (nessuna scelta = tutto): i fogli non scelti si tolgono prima di scrivere Dati/Riepilogo/Info,
        // cosi' l'elenco del foglio Info descrive il file reale.
        bool Ha(string id) => scelta == null || scelta.HaSezione(id);
        void Togli(Func<string, bool> corrisponde)
        {
            foreach (var nome in wb.Worksheets.Select(w => w.Name).Where(corrisponde).ToList()) wb.Worksheet(nome).Delete();
        }
        if (!Ha(ReportSezioniExport.Vista)) Togli(n => n == "Matrice configurabile");
        if (!Ha(ReportSezioniExport.Origine))
            Togli(n => n == LocalizationService.Instance.Traduci("OrigineFoglioFiltri")
                || n.StartsWith(LocalizationService.Instance.Traduci("OrigineFoglioOrigine"), StringComparison.Ordinal));
        if (!Ha(ReportSezioniExport.Grafici)) { Togli(n => n == "Grafici"); graficiNativi.Clear(); }
        if (!Ha(ReportSezioniExport.Nc)) Togli(n => n.StartsWith("NC matrice", StringComparison.Ordinal));

        // Per elaborazioni successive: tabella Dati con intestazioni in riga 1 (Test UID compreso), riepiloghi con formule e Info.
        // Un file che resterebbe senza fogli (per esempio solo il file pivot scelto) riceve almeno il foglio Info.
        if (origineScritta != null)
        {
            var info = Ha(ReportSezioniExport.Info) || (wb.Worksheets.Count == 0 && !Ha(ReportSezioniExport.Dati) && !Ha(ReportSezioniExport.Riepilogo));
            ReportExportArricchimentiService.Scrivi(wb, origineScritta.Righe, origineScritta.Selezione.Totale, operatoreGenerazione,
                DateTime.Now, ct,
                new OpzioniArricchimentiReport(Ha(ReportSezioniExport.Dati), Ha(ReportSezioniExport.Riepilogo), info, scelta?.ColonneDi(ReportSezioniExport.Dati)));
        }

        // Le pivot standard sono calcolate UNA volta sull'universo completo e scritte nel file principale (nell'export partizionato: solo
        // nella parte 1, vedi ScriviExcelVistaPartizionato), indipendentemente dalle colonne/sezioni selezionate.
        // Calcolano lo stato corrente anche quando la vista usa la modalità tempo o Test %.
        var fogliPrimaPivot = wb.Worksheets.Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loc = LocalizationService.Instance;
        // Parti successive alla prima: niente ricalcolo; la nota nel foglio Info indica dove sono le pivot.
        if (pivotStandardInQuestoFile)
        {
            var configPivot = new ConfigurazioneMatriceReport
            {
                CampiX = configurazioneMatrice.CampiX.ToList(), CampiY = configurazioneMatrice.CampiY.ToList(),
                Filtri = configurazioneMatrice.Filtri, ModalitaCella = ModalitaCellaMatrice.Percentuale,
                FiltroTempoOperazione = configurazioneMatrice.FiltroTempoOperazione,
                FiltroTempoTotaleTest = configurazioneMatrice.FiltroTempoTotaleTest,
            };
            var funzioniPivot = righeOrigine?.FunzionalitaSnapshot ?? state.Master.Funzionalita.Load();
            ReportMatriceService.MatricePreparata? matricePivot = null;
            try
            {
                matricePivot = ReportMatriceService.Prepara(testCasesMatrice, configPivot,
                    righeOrigine?.VeicoliSnapshot ?? state.Master.Veicoli.Load(), funzioniPivot,
                    righeOrigine?.TemplatesSnapshot ?? state.Master.TestTemplate.Load(),
                    righeOrigine?.EcuSnapshot ?? state.Master.Componente.Load(),
                    righeOrigine?.StatiManualiSnapshot ?? state.Master.StatiFunzionalitaEcuVeicolo.Load(), filtro: filtro, ct: ct, limiteUniversoExport: 1_048_575);
            }
            catch (LimiteUniversoExportSuperatoException)
            {
                // Degradazione esplicita: nessuna pivot (mai una pivot su un sottoinsieme), foglio di avviso e resto dell'export intatto.
                ReportExportArricchimentiService.ScriviAvvisoPivot(wb, loc.Scegli("Avviso pivot", "Pivot warning", "Avertissement pivot"), loc.Scegli(
                    "Le pivot standard NON sono state generate: l'universo delle combinazioni supera il limite di righe di Excel (1.048.575). Restringere i filtri per ottenerle.",
                    "The standard pivots were NOT generated: the combinations universe exceeds Excel's row limit (1,048,575). Narrow the filters to get them.",
                    "Les tableaux croisés standard n'ont PAS été générés : l'univers des combinaisons dépasse la limite de lignes d'Excel (1 048 575). Restreignez les filtres pour les obtenir."));
            }
            if (matricePivot != null)
            {
                var marcaturePivot = righeOrigine?.MarcatureSnapshot ?? MatriceNonApplicabileService.Leggi(state.Config, configPivot.CampiX, configPivot.CampiY);
                var fontiPivot = ReportExportArricchimentiService.ScriviPivotStato(wb, matricePivot, configPivot, funzioniPivot, marcaturePivot, ct,
                    scelta?.PivotMostraNumeri ?? true, scelta?.PivotMostraPercentuali ?? true);
                // Con sorgenti troncate la pivot personalizzata non si genera (l'avviso e' gia' nel file): mai un sottoinsieme presentato come completo.
                if (scelta?.HaSezione(ReportSezioniExport.Pivot) == true && !fontiPivot.Troncata)
                {
                    var categorie = scelta.PivotFonte == "categorie";
                    var fonte = categorie ? fontiPivot.Categorie : fontiPivot.Core;
                    IEnumerable<IReadOnlyList<object?>> RighePivot()
                    {
                        var ultima = fonte.LastRowUsed()?.RowNumber() ?? 1;
                        for (var r = 2; r <= ultima; r++)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (fonte.Cell(r, 6).GetDouble() == 0) continue;
                            yield return (categorie ? new[] { 1, 2, 3, 4, 5 } : new[] { 1, 2, 3, 5 })
                                .Select(c => (object?)fonte.Cell(r, c).GetString()).ToArray();
                        }
                    }
                    ExportPivotService.Scrivi(wb, ReportExportArricchimentiService.CampiPivotPersonalizzata(categorie), RighePivot(),
                        scelta, ct);
                }
            }
        }
        if (wb.TryGetWorksheet(LocalizationService.Instance.Traduci("ExpFoglioInfo"), out var infoPivot))
        {
            var riga = (infoPivot.LastRowUsed()?.RowNumber() ?? 0) + 1;
            foreach (var foglio in wb.Worksheets.Where(w => !fogliPrimaPivot.Contains(w.Name)))
                infoPivot.Cell(riga++, 1).Value = foglio.Name;
            if (!pivotStandardInQuestoFile)
                infoPivot.Cell(riga, 1).Value = loc.Scegli(
                    "Le pivot standard dell'universo completo sono nella parte 1 di questo export.",
                    "The standard pivots of the full universe are in part 1 of this export.",
                    "Les tableaux croisés standard de l'univers complet se trouvent dans la partie 1 de cet export.");
        }
        if (righeOrigine?.TestCasesMatrice is not null && !SnapshotExtraReportUguale(state, righeOrigine, righeOrigine.FirmaNc ?? string.Empty))
            throw new InvalidOperationException("I dati sono cambiati durante la generazione. Riprovare.");

        ct.ThrowIfCancellationRequested();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ExcelChartInjector.Inietta(ms.ToArray(), graficiNativi);
    }

    /// <summary>Genera un archivio ZIP atomico di workbook completi con partizioni disgiunte e ordinate del foglio Origine.</summary>
    public static void ScriviExcelVistaPartizionato(Stream output, ReportFilter filtro, AppState state, string operatoreGenerazione,
        ConfigurazioneMatriceReport configurazioneMatrice, IReadOnlyList<ReportMatriceService.OrigineDato> righeOrigine,
        int totaleOrigine, int massimoRighePerParte, CancellationToken ct = default,
        ReportOrigineExportService.OriginePreparata? snapshot = null, ExportConfigurazione? scelta = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (massimoRighePerParte <= 0) throw new ArgumentOutOfRangeException(nameof(massimoRighePerParte));
        var parti = (righeOrigine.Count + massimoRighePerParte - 1) / massimoRighePerParte;
        if (parti < 2) throw new ArgumentException("La partizione deve produrre almeno due file.", nameof(righeOrigine));
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        for (var indice = 0; indice < parti; indice++)
        {
            ct.ThrowIfCancellationRequested();
            var segmento = righeOrigine.Skip(indice * massimoRighePerParte).Take(massimoRighePerParte).ToList();
            var preparata = ReportOrigineExportService.CreaPreparata(segmento, totaleOrigine, ct);
            if (snapshot != null)
                preparata = preparata with
                {
                    TestCasesVista = snapshot.TestCasesVista, TestCasesMatrice = snapshot.TestCasesMatrice,
                    TestCasesCompletiSnapshot = snapshot.TestCasesCompletiSnapshot, FirmeTestCompleti = snapshot.FirmeTestCompleti,
                    NonConformitaSnapshot = snapshot.NonConformitaSnapshot,
                    NonConformitaCompletaSnapshot = snapshot.NonConformitaCompletaSnapshot,
                    FirmaConfig = snapshot.FirmaConfig, FirmaMaster = snapshot.FirmaMaster,
                    FirmeMaster = snapshot.FirmeMaster, FirmeTuttiMaster = snapshot.FirmeTuttiMaster,
                    FirmaNc = snapshot.FirmaNc, RequisitiSnapshot = snapshot.RequisitiSnapshot,
                    VeicoliSnapshot = snapshot.VeicoliSnapshot, FunzionalitaSnapshot = snapshot.FunzionalitaSnapshot,
                    TemplatesSnapshot = snapshot.TemplatesSnapshot, EcuSnapshot = snapshot.EcuSnapshot,
                    StatiManualiSnapshot = snapshot.StatiManualiSnapshot, StrumentiSnapshot = snapshot.StrumentiSnapshot,
                    CampiDettagliSnapshot = snapshot.CampiDettagliSnapshot,
                    CampiPianificazioneSnapshot = snapshot.CampiPianificazioneSnapshot,
                    MarcatureSnapshot = snapshot.MarcatureSnapshot
                };
            var nome = $"TestLoom_Report_parte_{indice + 1:000}_di_{parti:000}.xlsx";
            var entry = archive.CreateEntry(nome, CompressionLevel.Fastest);
            using var stream = entry.Open();
            // Pivot standard dell'universo completo: calcolate e scritte una sola volta (parte 1); le altre parti non le ricalcolano.
            var bytes = GeneraExcelVista(filtro, state, operatoreGenerazione, configurazioneMatrice, preparata, ct, scelta,
                pivotStandardInQuestoFile: indice == 0);
            stream.Write(bytes, 0, bytes.Length);
        }
        ct.ThrowIfCancellationRequested();
    }

    internal static bool SnapshotTestCasesUguale(IReadOnlyList<TestCase> snapshot, IReadOnlyList<TestCase> correnti) =>
        SnapshotUguale(snapshot, correnti, tc => tc.Uid.ToString("N"));

    internal static Dictionary<Guid, string> CatturaFirmeTestCases(IEnumerable<TestCase> tests, CancellationToken ct = default)
    {
        var firme = new Dictionary<Guid, string>();
        foreach (var test in tests)
        {
            ct.ThrowIfCancellationRequested();
            if (!firme.TryAdd(test.Uid, FirmaPayload(test)))
                throw new InvalidOperationException($"UID Test duplicato: {test.Uid}");
        }
        return firme;
    }

    internal static bool FirmeTestCasesUguali(IReadOnlyDictionary<Guid, string> snapshot, IEnumerable<TestCase> correnti, CancellationToken ct = default)
    {
        var firmeCorrenti = CatturaFirmeTestCases(correnti, ct);
        return snapshot.Count == firmeCorrenti.Count && snapshot.All(kv => firmeCorrenti.TryGetValue(kv.Key, out var firma)
            && string.Equals(firma, kv.Value, StringComparison.Ordinal));
    }

    internal static bool SnapshotNonConformitaUguale(IReadOnlyList<NonConformita> snapshot, IReadOnlyList<NonConformita> correnti) =>
        SnapshotUguale(snapshot, correnti, nc => nc.Uid.ToString("N"));

    internal static string SnapshotNonConformitaFirma(IReadOnlyList<NonConformita> nc) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            nc.Select(x => (Uid: x.Uid, Payload: FirmaPayload(x))).OrderBy(x => x.Uid).ThenBy(x => x.Payload, StringComparer.Ordinal))));

    private static bool SnapshotUguale<T>(IReadOnlyList<T> snapshot, IReadOnlyList<T> correnti, Func<T, string> uid)
    {
        if (snapshot.Count != correnti.Count) return false;
        // Impronte canoniche ordinate: rileva inserimenti, eliminazioni e modifiche in-place anche
        // quando RevisioneDati non cambia, in O(n log n) senza confronti incrociati O(n²).
        var prima = snapshot.Select(x => (Uid: uid(x), Payload: FirmaPayload(x)))
            .OrderBy(x => x.Uid, StringComparer.Ordinal).ThenBy(x => x.Payload, StringComparer.Ordinal);
        var dopo = correnti.Select(x => (Uid: uid(x), Payload: FirmaPayload(x)))
            .OrderBy(x => x.Uid, StringComparer.Ordinal).ThenBy(x => x.Payload, StringComparer.Ordinal);
        return prima.SequenceEqual(dopo);
    }

    private static string FirmaPayload<T>(T value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, JsonIo.Options)));

    internal static bool SnapshotExtraReportUguale(AppState state, ReportOrigineExportService.OriginePreparata snapshot, string firmaNc) =>
        string.Equals(snapshot.FirmaConfig, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { state.Config.MatriceNonApplicabili, state.Config.OperatoreAttivo }))), StringComparison.Ordinal)
        && snapshot.FirmeTuttiMaster != null
        && snapshot.FirmeTuttiMaster.All(x => !string.IsNullOrWhiteSpace(x.Value)
            && string.Equals(x.Value, FirmaMasterCorrente(state, x.Key), StringComparison.Ordinal))
        && string.Equals(firmaNc, SnapshotNonConformitaFirma(state.NonConformita.GetAll()), StringComparison.Ordinal)
        && snapshot.RequisitiSnapshot != null && snapshot.VeicoliSnapshot != null
        && snapshot.FunzionalitaSnapshot != null && snapshot.TemplatesSnapshot != null
        && snapshot.EcuSnapshot != null && snapshot.StatiManualiSnapshot != null
        && snapshot.StrumentiSnapshot != null && snapshot.CampiDettagliSnapshot != null
        && snapshot.CampiPianificazioneSnapshot != null && snapshot.MarcatureSnapshot != null;

    private static string FirmaMasterCorrente(AppState state, string chiave) => chiave switch
    {
        nameof(state.Master.Veicoli) => state.Master.Veicoli.CatturaImprontaLettura(),
        nameof(state.Master.Funzionalita) => state.Master.Funzionalita.CatturaImprontaLettura(),
        nameof(state.Master.TestTemplate) => state.Master.TestTemplate.CatturaImprontaLettura(),
        nameof(state.Master.Componente) => state.Master.Componente.CatturaImprontaLettura(),
        nameof(state.Master.Requisiti) => state.Master.Requisiti.CatturaImprontaLettura(),
        nameof(state.Master.CampiSistemaDettagli) => state.Master.CampiSistemaDettagli.CatturaImprontaLettura(),
        nameof(state.Master.CampiSistemaPianificazione) => state.Master.CampiSistemaPianificazione.CatturaImprontaLettura(),
        nameof(state.Master.StatiFunzionalitaEcuVeicolo) => state.Master.StatiFunzionalitaEcuVeicolo.CatturaImprontaLettura(),
        nameof(state.Master.Gamma) => state.Master.Gamma.CatturaImprontaLettura(),
        nameof(state.Master.Strumenti) => state.Master.Strumenti.CatturaImprontaLettura(),
        _ => throw new InvalidOperationException($"Catalogo report non riconosciuto: {chiave}")
    };

    /// <summary><paramref name="ct"/> viene controllato tra un foglio e l'altro e dentro i cicli sulle righe: un
    /// annullamento interrompe la generazione senza produrre alcun risultato (vedi EsportazioneInBackground).</summary>
    public static byte[] GeneraExcel(ReportFilter filtro, AppState state, string operatoreGenerazione, ConfigurazioneMatriceReport? configurazioneMatrice = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tuttiTestCases = state.TestCases.GetAll();
        var testCases = tuttiTestCases.Where(filtro.Corrisponde).ToList();
        // TIME (6.6.1): la matrice e la sua origine ricevono i Test dei criteri non TIME e del totale; la durata delle operazioni la applica
        // la matrice per combinazione, Test o tentativo, come sulla vista (vedi ReportViewModel.CalcolaGiro).
        var testCasesMatrice = tuttiTestCases.Where(filtro.CorrispondeMatrice).ToList();
        var requisiti = state.Master.Requisiti.Load();
        var nonConformita = state.NonConformita.GetAll();
        var templates = state.Master.TestTemplate.Load();

        using var wb = new XLWorkbook();

        ScriviLegenda(wb, operatoreGenerazione);
        ScriviAnomalieEsta(wb, nonConformita, ct);
        ScriviChangeRequest(wb, state.ChangeRequests.GetAll(), templates, ct);
        ScriviCaricoOperatori(wb, testCases);
        ScriviCopertura(wb, requisiti, testCases, nonConformita, ct);
        ScriviAuditConformita(wb, requisiti, testCases, ct);
        ScriviFogliTemplate(wb, testCases, templates, CampiUdtStrumentoReale(state), ct);
        ScriviTestCollegati(wb, testCases, templates, ct);
        ScriviNonConformita(wb, nonConformita, ct);
        var graficiNativi = ScriviGrafici(wb, state, filtro, requisiti, testCases, tuttiTestCases.Count(), nonConformita, operatoreGenerazione, ct);
        ScriviMatriceConfigurabile(wb, testCasesMatrice, state, configurazioneMatrice ?? new ConfigurazioneMatriceReport(), filtro, ct);
        // 6.4.3: "Filtri" e "Origine" della vista appena esportata (stessa configurazione, stessi filtri).
        ReportOrigineExportService.Scrivi(wb, testCasesMatrice, state, filtro, configurazioneMatrice ?? new ConfigurazioneMatriceReport(),
            operatoreGenerazione, DateTime.Now, ct: ct);

        ct.ThrowIfCancellationRequested();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ExcelChartInjector.Inietta(ms.ToArray(), graficiNativi);
    }

    private static ReportMatriceService.MatricePreparata ScriviMatriceConfigurabile(XLWorkbook wb, List<TestCase> testCases, AppState state, ConfigurazioneMatriceReport configurazione, ReportFilter filtro, CancellationToken ct,
        IReadOnlyList<Veicolo>? veicoliSnapshot = null, IReadOnlyList<Funzionalita>? funzSnapshot = null,
        IReadOnlyList<TestTemplate>? templatesSnapshot = null, IReadOnlyList<Componente>? ecuSnapshot = null,
        IReadOnlyList<StatoFunzionalitaEcuVeicolo>? manualiSnapshot = null,
        IReadOnlyList<CellaNonApplicabile>? marcatureSnapshot = null)
    {
        ct.ThrowIfCancellationRequested();
        // Addendum 6.4.0 (totali di intestazione, punto 5): l'elenco marcature N.A. è passato a Genera() PRIMA
        // del calcolo dei Totali (che dipendono dal gate N.A. per colonna/riga/generale — punto 3), poi lo
        // stesso overlay viene applicato al risultato per le celle come già avveniva.
        // 6.6.1: campi annidati (fino a tre per asse). Con un campo per asse il foglio resta quello di prima; con più campi
        // le intestazioni di gruppo sono unite; con un asse senza campi il foglio è un elenco piatto con filtro automatico.
        var campiX = configurazione.CampiX;
        var campiY = configurazione.CampiY;
        var marcature = marcatureSnapshot ?? MatriceNonApplicabileService.Leggi(state.Config, campiX, campiY);
        var catalogoEcu = configurazione.ModalitaCella != ModalitaCellaMatrice.TestPercentuale ? (ecuSnapshot ?? state.Master.Componente.Load()) : null;
        var preparata = ReportMatriceService.Prepara(testCases, configurazione, veicoliSnapshot ?? state.Master.Veicoli.Load(),
            funzSnapshot ?? state.Master.Funzionalita.Load(), templatesSnapshot ?? state.Master.TestTemplate.Load(), catalogoEcu,
            configurazione.ModalitaCella != ModalitaCellaMatrice.TestPercentuale ? (manualiSnapshot ?? state.Master.StatiFunzionalitaEcuVeicolo.Load()) : null,
            filtro: filtro, ct: ct, limiteUniversoExport: 1_048_575);
        var risultato = preparata.Completa(marcature);
        // Overlay N.A. (decisione utente 6.4.0, punto 4): applicato DOPO il calcolo, in un servizio separato
        // che non tocca in alcun modo i conteggi prodotti da ReportMatriceService.
        MatriceNonApplicabileService.Applica(risultato, marcature);
        var inStato = configurazione.ModalitaCella == ModalitaCellaMatrice.Stato;
        var ws = wb.Worksheets.Add("Matrice configurabile");
        ws.Cell(1, 1).Value = $"Matrice di conformità — {LocalizationService.Instance.Traduci(ReportMatriceService.ChiaveEtichettaModalita(configurazione.ModalitaCella))}";
        ws.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 14;
        ws.Cell(2, 1).Value = $"Righe: {string.Join(" › ", campiY)} — Colonne: {string.Join(" › ", campiX)}";
        // Decisione utente 6.4.0: chiarire che il conteggio è per combinazione funzionalità×componente×veicolo con
        // l'esito più recente, non per operazione eseguita (vedi ReportMatriceService.CombinazioneAccumulatore).
        ws.Cell(3, 1).Value = MatriceTooltipService.DescrizioneModalita(configurazione.ModalitaCella);
        ws.Cell(3, 1).Style.Font.SetItalic().Font.FontColor = XLColor.Gray;
        ws.Cell(3, 1).GetComment().AddText(MatriceTooltipService.DescrizioneModalita(configurazione.ModalitaCella));
        if (campiX.Count == 0 || campiY.Count == 0)
            ScriviElencoPiatto(ws, risultato, configurazione, ct);
        else
            ScriviMatriceConBande(ws, risultato, configurazione, ct);
        return preparata;
    }

    /// <summary>Matrice con uno o più campi per asse: una riga di intestazione per livello di colonna (gruppi uniti in orizzontale),
    /// una colonna di intestazione per livello di riga (gruppi uniti in verticale), celle con testo e colore (percentuale o stato).</summary>
    private static void ScriviMatriceConBande(IXLWorksheet ws, RisultatoMatriceReport risultato, ConfigurazioneMatriceReport configurazione, CancellationToken ct)
    {
        var inStato = configurazione.ModalitaCella == ModalitaCellaMatrice.Stato;
        var livelliX = configurazione.CampiX.Count;
        var livelliY = configurazione.CampiY.Count;
        var primaRigaDati = 4 + livelliX;
        var primaColonnaDati = livelliY + 1;

        // Angolo: totale generale, su tutte le intestazioni di riga e di colonna (stessa forma di prima con un campo).
        ws.Cell(4, 1).Value = $"{string.Join(" › ", configurazione.CampiY)} \\ {string.Join(" › ", configurazione.CampiX)}\nTotale: {risultato.TotaleGenerale.Testo}";
        ws.Cell(4, 1).Style.Alignment.SetWrapText(true);
        ws.Range(4, 1, 3 + livelliX, livelliY).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightBlue);
        UnisciSeNecessario(ws, 4, 1, 3 + livelliX, livelliY);

        // Intestazioni di colonna: un livello per riga; le fasce di livello superiore sono unite sulle colonne che coprono.
        for (var livello = 1; livello <= livelliX; livello++)
        {
            var riga = 3 + livello;
            var colonna = primaColonnaDati;
            foreach (var fascia in risultato.FasceColonne[livello - 1])
            {
                ct.ThrowIfCancellationRequested();
                var testo = fascia.Etichetta;
                if (livello == livelliX)
                {
                    var indice = colonna - primaColonnaDati;
                    var totale = indice < risultato.TotaliColonna.Count ? risultato.TotaliColonna[indice].Testo : "—";
                    testo = $"{fascia.Etichetta}\n{totale}";
                }
                var celletta = ws.Cell(riga, colonna);
                celletta.Value = testo;
                celletta.Style.Alignment.SetWrapText(true);
                celletta.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                celletta.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightBlue);
                if (livello == livelliX)
                    celletta.GetComment().AddText(MatriceTooltipService.DescrizioneModalita(configurazione.ModalitaCella));
                UnisciSeNecessario(ws, riga, colonna, riga, colonna + fascia.Span - 1);
                colonna += fascia.Span;
            }
        }

        // Intestazioni di riga: un livello per colonna; le fasce di livello superiore sono unite sulle righe che coprono.
        for (var livello = 1; livello <= livelliY; livello++)
        {
            var colonna = livello;
            var riga = primaRigaDati;
            foreach (var fascia in risultato.FasceRighe[livello - 1])
            {
                ct.ThrowIfCancellationRequested();
                var testo = fascia.Etichetta;
                if (livello == livelliY)
                {
                    var indice = riga - primaRigaDati;
                    var totale = indice < risultato.Righe.Count ? risultato.Righe[indice].TotaleRiga.Testo : "—";
                    testo = $"{fascia.Etichetta}\n{totale}";
                }
                var celletta = ws.Cell(riga, colonna);
                celletta.Value = testo;
                celletta.Style.Alignment.SetWrapText(true);
                UnisciSeNecessario(ws, riga, colonna, riga + fascia.Span - 1, colonna);
                riga += fascia.Span;
            }
        }

        // Celle: testo visualizzato (percentuale oppure stato) e colore; N.A. grigio con nota come commento.
        for (var r = 0; r < risultato.Righe.Count; r++)
        {
            ct.ThrowIfCancellationRequested();
            for (var c = 0; c < risultato.Righe[r].Celle.Count; c++)
            {
                var cella = risultato.Righe[r].Celle[c];
                var xlCella = ws.Cell(primaRigaDati + r, primaColonnaDati + c);
                xlCella.Value = cella.TestoVisualizzato;
                if (!string.IsNullOrWhiteSpace(cella.NotaManuale)) xlCella.GetComment().AddText("Manuale: " + cella.NotaManuale);
                if (cella.NonApplicabile)
                {
                    // Distinta dagli esiti calcolati (grigio, corsivo) e con un commento facoltativo per la
                    // motivazione breve (Addendum piano 6.4.0, punto 4: "nota opzionale utile").
                    xlCella.Style.Fill.SetBackgroundColor(XLColor.LightGray);
                    xlCella.Style.Font.SetItalic();
                    if (!string.IsNullOrWhiteSpace(cella.NotaNonApplicabile))
                        xlCella.GetComment().AddText(cella.NotaNonApplicabile);
                }
                else if (inStato) xlCella.Style.Fill.SetBackgroundColor(ColoreStatoExcel(cella.CodiceStato));
                // Modalita' percentuale: nessun colore, come nella vista (CellaStatoPennelloConverter colora solo la modalita' Stato).
            }
        }

        if (inStato) ScriviLegendaStatoExcel(ws, primaRigaDati + risultato.Righe.Count + 1);
        ws.SheetView.FreezeRows(3 + livelliX);
        ws.SheetView.FreezeColumns(livelliY);
        ws.Columns().AdjustToContents(10, 35);
    }

    /// <summary>Elenco piatto (un asse senza campi): una riga per combinazione dei campi dell'altro asse, con una colonna per
    /// livello e una colonna Stato (o % conforme). Nessuna cella unita, valori ripetuti su ogni riga, filtro automatico.</summary>
    private static void ScriviElencoPiatto(IXLWorksheet ws, RisultatoMatriceReport risultato, ConfigurazioneMatriceReport configurazione, CancellationToken ct)
    {
        var inStato = configurazione.ModalitaCella == ModalitaCellaMatrice.Stato;
        var righeSonoCombinazioni = configurazione.CampiX.Count == 0;
        var campi = righeSonoCombinazioni ? configurazione.CampiY : configurazione.CampiX;
        var intestazioni = campi.Select(EtichettaDimensioneExcel).ToList();
        intestazioni.Add(LocalizationService.Instance.Traduci(ReportMatriceService.ChiaveEtichettaModalita(configurazione.ModalitaCella)));
        for (var c = 0; c < intestazioni.Count; c++)
        {
            ws.Cell(4, c + 1).Value = intestazioni[c];
            ws.Cell(4, c + 1).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightBlue);
        }
        ws.Cell(4, intestazioni.Count).GetComment().AddText(MatriceTooltipService.DescrizioneModalita(configurazione.ModalitaCella));
        // Una voce per riga dell'asse con campi (asse X vuoto: le righe sono le combinazioni; asse Y vuoto: le colonne).
        var voci = righeSonoCombinazioni
            ? risultato.Righe.Select(r => (Chiave: r.Riga, Cella: r.Celle.FirstOrDefault()))
            : risultato.Colonne.Select((k, i) => (Chiave: k, Cella: risultato.Righe.FirstOrDefault()?.Celle.ElementAtOrDefault(i)));
        var riga = 5;
        foreach (var (chiave, cella) in voci)
        {
            ct.ThrowIfCancellationRequested();
            var parti = ReportMatriceService.SpezzaPercorso(chiave);
            for (var c = 0; c < campi.Count; c++)
                ws.Cell(riga, c + 1).Value = c < parti.Length ? parti[c] : "";
            var xlCella = ws.Cell(riga, campi.Count + 1);
            xlCella.Value = cella?.TestoVisualizzato ?? "";
            if (!string.IsNullOrWhiteSpace(cella?.NotaManuale)) xlCella.GetComment().AddText("Manuale: " + cella.NotaManuale);
            if (cella != null)
            {
                if (cella.NonApplicabile)
                {
                    xlCella.Style.Fill.SetBackgroundColor(XLColor.LightGray);
                    xlCella.Style.Font.SetItalic();
                    if (!string.IsNullOrWhiteSpace(cella.NotaNonApplicabile)) xlCella.GetComment().AddText(cella.NotaNonApplicabile);
                }
                else if (inStato) xlCella.Style.Fill.SetBackgroundColor(ColoreStatoExcel(cella.CodiceStato));
                // Modalita' percentuale: nessun colore, come nella vista (vedi ScriviMatriceConfigurabile).
            }
            riga++;
        }
        var ultimaRiga = Math.Max(4, riga - 1);
        ws.Range(4, 1, ultimaRiga, intestazioni.Count).SetAutoFilter();
        ws.SheetView.FreezeRows(4);
        ws.Columns().AdjustToContents(10, 40);
    }

    /// <summary>Unisce un intervallo di celle solo se contiene più di una cella (ClosedXML rifiuta l'unione di una cella sola).</summary>
    private static void UnisciSeNecessario(IXLWorksheet ws, int rigaIniziale, int colonnaIniziale, int rigaFinale, int colonnaFinale)
    {
        if (rigaFinale > rigaIniziale || colonnaFinale > colonnaIniziale)
            ws.Range(rigaIniziale, colonnaIniziale, rigaFinale, colonnaFinale).Merge().Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
    }

    private static XLColor ColoreStatoExcel(StatoCellaMatrice stato) => stato switch
    {
        StatoCellaMatrice.Conforme => XLColor.FromHtml("#DCFCE7"),
        StatoCellaMatrice.NonConforme => XLColor.FromHtml("#FEE2E2"),
        StatoCellaMatrice.ConformeConDeviazioni => XLColor.FromHtml("#FEF3C7"),
        StatoCellaMatrice.DaEseguire => XLColor.FromHtml("#DBEAFE"),
        StatoCellaMatrice.NonTestato => XLColor.FromHtml("#F1F5F9"),
        StatoCellaMatrice.Misto => XLColor.FromHtml("#EDE9FE"),
        StatoCellaMatrice.DaDecidere => XLColor.FromHtml("#FFF7ED"),
        _ => XLColor.NoColor,
    };

    private static void ScriviLegendaStatoExcel(IXLWorksheet ws, int riga)
    {
        ws.Cell(riga, 1).Value = LocalizationService.Instance.Traduci("MatriceLegendaTitolo");
        ws.Cell(riga, 1).Style.Font.SetBold();
        var voci = new (StatoCellaMatrice Stato, string Chiave)[]
        {
            (StatoCellaMatrice.Conforme, "MatriceLegendaConforme"),
            (StatoCellaMatrice.NonConforme, "MatriceLegendaNonConforme"),
            (StatoCellaMatrice.ConformeConDeviazioni, "MatriceLegendaDeviazioni"),
            (StatoCellaMatrice.DaEseguire, "MatriceLegendaDaEseguire"),
            (StatoCellaMatrice.NonTestato, "MatriceLegendaNonTestato"),
            (StatoCellaMatrice.Misto, "MatriceLegendaMisto"),
        };
        for (var i = 0; i < voci.Length; i++)
        {
            ws.Cell(riga + 1 + i, 1).Value = LocalizationService.Instance.Traduci(voci[i].Chiave);
            ws.Cell(riga + 1 + i, 1).Style.Fill.SetBackgroundColor(ColoreStatoExcel(voci[i].Stato));
            ws.Cell(riga + 1 + i, 1).GetComment().AddText(MatriceTooltipService.DescrizioneStato(voci[i].Stato));
        }
        ws.Cell(riga + 1 + voci.Length, 1).Value = LocalizationService.Instance.Traduci("MatriceStatoLegendaNota");
        ws.Cell(riga + 1 + voci.Length, 1).Style.Font.SetItalic().Font.FontColor = XLColor.Gray;
    }

    /// <summary>Etichetta leggibile di una dimensione della matrice, per le intestazioni dei fogli nuovi (elenco piatto).</summary>
    private static string EtichettaDimensioneExcel(string dimensione)
    {
        var chiave = dimensione switch
        {
            DimensioniMatrice.Gamma => "MatriceDimGamma",
            DimensioniMatrice.Sottoveicolo => "MatriceDimSottoveicolo",
            DimensioniMatrice.Componente => "MatriceDimEcu",
            DimensioniMatrice.CategoriaFunzionalita => "MatriceDimCategoriaFunzionalita",
            DimensioniMatrice.Funzionalita => "MatriceDimFunzionalita",
            DimensioniMatrice.Template => "MatriceDimTemplate",
            DimensioniMatrice.Esito => "MatriceDimEsito",
            DimensioniMatrice.Assegnatario => "MatriceDimAssegnatario",
            DimensioniMatrice.Requisito => "MatriceDimRequisito",
            _ => "",
        };
        if (chiave.Length > 0) return LocalizationService.Instance.Traduci(chiave);
        return dimensione.StartsWith(DimensioniMatrice.CampoCustomPrefix, StringComparison.OrdinalIgnoreCase)
            ? dimensione[DimensioniMatrice.CampoCustomPrefix.Length..]
            : dimensione;
    }

    private static void ScriviChangeRequest(XLWorkbook wb, IReadOnlyList<ChangeRequest> richieste, IReadOnlyList<TestTemplate> templates, CancellationToken ct)
    {
        var nomiTemplate = templates.ToDictionary(t => t.Uid, t => t.Nome);
        var ws = wb.Worksheets.Add("Change Request");
        ws.Cell(1, 1).Value = "KPI Change Request";
        ws.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 14;
        ws.Cell(2, 1).Value = "Totale attive"; ws.Cell(2, 2).Value = richieste.Count(c => c.Stato is not (StatoChangeRequest.Chiusa or StatoChangeRequest.Annullata));
        ws.Cell(3, 1).Value = "Da validare"; ws.Cell(3, 2).Value = richieste.Count(c => c.Stato == StatoChangeRequest.DaValidare);
        ws.Cell(4, 1).Value = "Validate"; ws.Cell(4, 2).Value = richieste.Count(c => c.Stato == StatoChangeRequest.Validata);
        ws.Cell(5, 1).Value = "Chiuse"; ws.Cell(5, 2).Value = richieste.Count(c => c.Stato == StatoChangeRequest.Chiusa);
        ws.Cell(6, 1).Value = "Eta media attive (giorni)";
        var attive = richieste.Where(c => c.Stato is not (StatoChangeRequest.Chiusa or StatoChangeRequest.Annullata)).ToList();
        ws.Cell(6, 2).Value = attive.Count == 0 ? 0 : Math.Round(attive.Average(c => (DateTime.Now - c.DataCreazione).TotalDays), 1);

        const int header = 8;
        string[] colonne = { "ID", "Tipo", "Titolo", "Stato", "Responsabile", "Creata il", "Eta giorni", "Veicoli", "Componente", "Strumenti", "Test obbligatori", "Test validanti", "Copertura", "Ticket", "Motivazione duplicato" };
        for (var i = 0; i < colonne.Length; i++) ws.Cell(header, i + 1).Value = colonne[i];
        ws.Range(header, 1, header, colonne.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightBlue);
        var r = header + 1;
        foreach (var cr in richieste.OrderByDescending(c => c.DataCreazione).ConAnnullamento(ct))
        {
            var obbligatori = cr.TestValidazione.Where(l => l.Obbligatorio).ToList();
            var validanti = obbligatori.Count(l => l.SnapshotValidazione != null);
            ws.Cell(r, 1).Value = cr.Id;
            ws.Cell(r, 2).Value = nomiTemplate.TryGetValue(cr.TemplateUid, out var nomeTemplate) ? nomeTemplate : "?";
            ws.Cell(r, 3).Value = cr.Titolo;
            ws.Cell(r, 4).Value = cr.Stato == StatoChangeRequest.DaValidare ? "Da validare" : cr.Stato.ToString();
            ws.Cell(r, 5).Value = cr.Responsabile;
            ws.Cell(r, 6).Value = cr.DataCreazione;
            ws.Cell(r, 6).Style.DateFormat.Format = "dd/MM/yyyy";
            ws.Cell(r, 7).Value = Math.Max(0, (int)(DateTime.Now - cr.DataCreazione).TotalDays);
            ws.Cell(r, 8).Value = string.Join(", ", cr.VeicoliImpattati);
            ws.Cell(r, 9).Value = string.Join(", ", cr.EcuImpattate);
            ws.Cell(r, 10).Value = string.Join(", ", cr.StrumentiImpattati);
            ws.Cell(r, 11).Value = obbligatori.Count;
            ws.Cell(r, 12).Value = validanti;
            ws.Cell(r, 13).Value = obbligatori.Count == 0 ? "Nessun test obbligatorio" : $"{validanti}/{obbligatori.Count}";
            ws.Cell(r, 14).Value = string.Join(", ", cr.TicketCollegati.Select(t => t.Riferimento));
            ws.Cell(r, 15).Value = cr.MotivazioneDuplicato;
            r++;
        }
        ws.SheetView.FreezeRows(header);
        ws.Range(header, 1, Math.Max(header, r - 1), colonne.Length).SetAutoFilter();
        ws.Columns().AdjustToContents(8, 48);
    }

    private static void ScriviAnomalieEsta(XLWorkbook wb, IReadOnlyList<NonConformita> anomalie, CancellationToken ct)
    {
        var ws = wb.Worksheets.Add("1 Anomalie TICKET retest");
        string[] h = { "ID", "Stato", "Severità", "Priorità", "Owner", "Test/Prove", "TICKET", "Scadenza", "Descrizione", "Motivo chiusura" };
        for (var i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i];
        ws.Range(1, 1, 1, h.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightBlue);
        var r = 2;
        foreach (var a in anomalie.OrderByDescending(a => a.DataApertura).ConAnnullamento(ct))
        {
            ws.Cell(r, 1).Value = a.Id; ws.Cell(r, 2).Value = a.StatoWorkflow.Label(); ws.Cell(r, 3).Value = a.Severita?.ToString() ?? "DA DEFINIRE";
            ws.Cell(r, 4).Value = a.Priorita.Label(); ws.Cell(r, 5).Value = a.Owner;
            ws.Cell(r, 6).Value = string.Join(", ", a.ProveCollegate.Select(p => p.TestId).Append(a.TestCaseId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
            ws.Cell(r, 7).Value = string.Join(", ", a.CollegamentiPolarion.Select(p => p.Codice)); ws.Cell(r, 8).Value = a.Scadenza?.ToString("dd/MM/yyyy") ?? "";
            ws.Cell(r, 9).Value = a.Descrizione; ws.Cell(r, 10).Value = a.MotivoChiusura; r++;
        }
        ws.SheetView.FreezeRows(1); ws.RangeUsed()?.SetAutoFilter(); ws.Columns().AdjustToContents(8, 48);
    }

    /// <summary>Test per assegnatario: "Mario" e "mario" sono la stessa persona e finiscono nello stesso gruppo.</summary>
    internal static List<IGrouping<string, TestCase>> RaggruppaPerAssegnatario(IEnumerable<TestCase> testCases) =>
        testCases.Where(t => !string.IsNullOrWhiteSpace(t.Assegnatario))
            .GroupBy(t => t.Assegnatario!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void ScriviCaricoOperatori(XLWorkbook wb, List<TestCase> testCases)
    {
        var ws = wb.Worksheets.Add("2 Carico operatori");
        string[] h = { "Operatore", "Pianificati", "In corso", "Da rivedere/retestare", "In attesa", "Conclusi", "Totale attivi" };
        for (var i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i];
        ws.Range(1, 1, 1, h.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightBlue);
        var r = 2;
        foreach (var g in RaggruppaPerAssegnatario(testCases))
        {
            ws.Cell(r, 1).Value = g.Key; ws.Cell(r, 2).Value = g.Count(t => t.StatoLavoro == StatoLavoroTest.Pianificato);
            ws.Cell(r, 3).Value = g.Count(t => t.StatoLavoro == StatoLavoroTest.InCorso);
            ws.Cell(r, 4).Value = g.Count(t => t.StatoLavoro is StatoLavoroTest.DaRivedere or StatoLavoroTest.DaRitestare);
            ws.Cell(r, 5).Value = g.Count(t => t.StatoLavoro == StatoLavoroTest.InAttesa);
            ws.Cell(r, 6).Value = g.Count(t => t.StatoLavoro == StatoLavoroTest.Concluso);
            ws.Cell(r, 7).Value = g.Count(t => t.DaFare); r++;
        }
        ws.Columns().AdjustToContents();
    }

    private static void ScriviLegenda(XLWorkbook wb, string autore)
    {
        var ws = wb.Worksheets.Add("Legenda");
        ws.Cell(1, 1).Value = "TestLoom - Report Test Diagnostici CS";
        ws.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 16;
        ws.Cell(2, 1).Value = $"Generato il {DateTime.Now:dd/MM/yyyy HH:mm} da {autore}";
        ws.Cell(4, 1).Value = "Esiti ammessi: Conforme / Non Conforme / Conforme con deviazioni / Non eseguito-N.A. / Da rivedere.";
        ws.Cell(5, 1).Value = "Una prova Non Conforme o con deviazioni crea una segnalazione da classificare. L'anomalia viene confermata o esclusa manualmente; Ticketing non è integrato via API.";
        ws.Cell(6, 1).Value = "La scheda \"Copertura Requisiti\" mostra 4 bucket: Conforme, Conforme con deviazioni, Non Conforme, Non testato (include anche gli esiti \"Da rivedere\").";
        ws.Cell(7, 1).Value = "La scheda \"Test collegati\" traccia i test creati da un'azione collegata di un template (es. escalation a veicolo) e quelli che richiedono un collegamento a un test esistente.";
        ws.Columns().AdjustToContents();
    }

    private static void ScriviCopertura(XLWorkbook wb, List<Requisito> requisiti, List<TestCase> testCases, IReadOnlyList<NonConformita> nc, CancellationToken ct)
    {
        var ws = wb.Worksheets.Add("Copertura Requisiti");
        ws.Cell(1, 1).Value = "Matrice di copertura requisiti";
        ws.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 14;

        var bucket = CoverageService.PerRequisito(requisiti, testCases, soloBanco: false, soloVeicolo: false);
        ws.Cell(3, 1).Value = "Totale requisiti applicabili:"; ws.Cell(3, 2).Value = bucket.Totale;
        ws.Cell(4, 1).Value = "Conformi:"; ws.Cell(4, 2).Value = bucket.Conformi;
        ws.Cell(5, 1).Value = "Conformi con deviazioni:"; ws.Cell(5, 2).Value = bucket.ConformiConDeviazioni;
        ws.Cell(6, 1).Value = "Non conformi:"; ws.Cell(6, 2).Value = bucket.NonConformi;
        ws.Cell(7, 1).Value = "Non testati:"; ws.Cell(7, 2).Value = bucket.NonTestati;
        ws.Cell(8, 1).Value = "% completamento:"; ws.Cell(8, 2).Value = Math.Round(bucket.PercentualeCompletamento, 1);

        var headerRow = 10;
        string[] headers = { "ID Requisito", "Descrizione", "Applicabile Banco", "Applicabile Veicolo", "Rif. test", "Esito", "Rif. segnalazione (se Non Conforme)" };
        for (int i = 0; i < headers.Length; i++) ws.Cell(headerRow, i + 1).Value = headers[i];
        ws.Range(headerRow, 1, headerRow, headers.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray);

        var r = headerRow + 1;
        // Indici costruiti una volta: niente scansione di tutti i Test e di tutte le non conformita per ogni requisito.
        var testPerRequisito = IndiciReport.TestPerRequisito(testCases);
        var ncPerTest = IndiciReport.NcPerTest(nc, testCases);
        foreach (var req in requisiti.Where(rq => !rq.Archiviata).OrderBy(rq => rq.Id).ConAnnullamento(ct))
        {
            var testDelReq = IndiciReport.TestDelRequisito(testPerRequisito, req.Id);
            var esito = CoverageService.EsitoCorrente(testDelReq);
            var ncCollegate = IndiciReport.NcAperteCollegate(testDelReq, ncPerTest, nc);

            ws.Cell(r, 1).Value = req.Id;
            ws.Cell(r, 2).Value = req.Testo;
            ws.Cell(r, 3).Value = req.ApplicabileBanco ? "Sì" : "No";
            ws.Cell(r, 4).Value = req.ApplicabileVeicolo ? "Sì" : "No";
            ws.Cell(r, 5).Value = string.Join(", ", testDelReq.Select(tc => tc.Id));
            ws.Cell(r, 6).Value = esito?.Label() ?? "Non testato";
            ws.Cell(r, 7).Value = string.Join(", ", ncCollegate);
            r++;
        }
        ws.Columns().AdjustToContents();
    }

    /// <summary>Foglio di audit (Addendum: grill-me 2026-08-18, punto 3): per ciascun Requisito, il punteggio
    /// 0-5/N.A. corrente (ultima esecuzione collegata) più lo storico completo delle evidenze — ogni coppia
    /// (TestCase, Esecuzione) che ha toccato quel requisito, con il relativo punteggio ed eventuale
    /// motivazione di correzione manuale.</summary>
    private static void ScriviAuditConformita(XLWorkbook wb, List<Requisito> requisiti, List<TestCase> testCases, CancellationToken ct)
    {
        var ws = wb.Worksheets.Add("Audit Conformità");
        ws.Cell(1, 1).Value = "Audit di conformità - punteggio 0-5/N.A. per Requisito";
        ws.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 14;

        var headerRow = 3;
        string[] headers = { "ID Requisito", "Testo", "Punteggio corrente", "Storico evidenze" };
        for (int i = 0; i < headers.Length; i++) ws.Cell(headerRow, i + 1).Value = headers[i];
        ws.Range(headerRow, 1, headerRow, headers.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray);

        var r = headerRow + 1;
        var testPerRequisito = IndiciReport.TestPerRequisito(testCases);
        foreach (var req in requisiti.Where(rq => !rq.Archiviata).OrderBy(rq => rq.Id).ConAnnullamento(ct))
        {
            var testDelRequisito = IndiciReport.TestDelRequisito(testPerRequisito, req.Id);
            var storico = testDelRequisito
                .SelectMany(tc => tc.Esecuzioni.Select(e => (Test: tc, Esecuzione: e)))
                .Where(x => x.Esecuzione.PunteggiRequisiti.Any(p => RequisitoIdConfronto.Uguali(p.RequisitoId, req.Id)))
                .OrderBy(x => x.Esecuzione.Data)
                .Select(x =>
                {
                    var p = x.Esecuzione.PunteggiRequisiti.First(pp => RequisitoIdConfronto.Uguali(pp.RequisitoId, req.Id));
                    var voce = $"{x.Test.Id}#{x.Esecuzione.Iterazione} ({x.Esecuzione.Data:dd/MM/yyyy}): {p.Valore.Label()}";
                    return p.Sovrascritto ? $"{voce} [corretto: {p.MotivazioneOverride}]" : voce;
                });

            ws.Cell(r, 1).Value = req.Id;
            ws.Cell(r, 2).Value = req.Testo;
            // Solo i Test del requisito: il punteggio ignora gli altri (stesso risultato della scansione completa).
            ws.Cell(r, 3).Value = CoverageService.PunteggioCorrentePerRequisito(req.Id, testDelRequisito)?.Label() ?? "Non valutato";
            ws.Cell(r, 4).Value = string.Join(" | ", storico);
            r++;
        }
        ws.Columns().AdjustToContents();
    }

    /// <summary>Campi dello strumento UnitLogger nel catalogo reale (6.6.1): servono a leggere le colonne UnitLogger del template che lo strumento
    /// non ha con quel nome. Null se lo strumento non c'e' nel catalogo.</summary>
    private static IReadOnlyList<DefinizioneCampoStrumento>? CampiUdtStrumentoReale(AppState state) =>
        state.Master.Strumenti.Load().FirstOrDefault(s => s.Nome.Equals(RisolutoreCampiUdt.NomeStrumento, StringComparison.OrdinalIgnoreCase))?.Campi;

    /// <summary>Sostituisce i 4 fogli hard-coded (Test On Bench/guasto Injection/Test a Veicolo/UnitLogger) con un foglio
    /// per ogni FoglioReportTemplate dichiarato da ciascun TestTemplate (Addendum redesign 2026-08, punto 2):
    /// un Master configura le colonne da Impostazioni → Tipi di test invece che nel codice. Una riga per
    /// condizione, non per test (Addendum §19): un test può provare più servizi/PIN sotto condizioni diverse.</summary>
    private static void ScriviFogliTemplate(XLWorkbook wb, List<TestCase> testCases, List<TestTemplate> templates,
        IReadOnlyList<DefinizioneCampoStrumento>? campiUdt, CancellationToken ct)
    {
        var nomiUsati = new HashSet<string>(wb.Worksheets.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var template in templates.OrderBy(t => t.Ordine))
        {
            var testDelTemplate = testCases.Where(t => t.TestTemplateUid == template.Uid && t.Esecuzioni.Count > 0).ToList();
            foreach (var foglio in template.FogliReport.OrderBy(f => f.Ordine))
            {
                var ws = wb.Worksheets.Add(NomeFoglioUnico(nomiUsati, foglio.NomeFoglio));
                ws.Cell(1, 1).Value = $"{template.Nome} - {foglio.NomeFoglio}";
                ws.Cell(1, 1).Style.Font.SetBold();

                var colonne = foglio.Colonne.OrderBy(c => c.Ordine).ToList();
                // Lettura di ogni colonna calcolata una volta per foglio (non per cella): solo le colonne campo-strumento possono cambiare.
                var letture = colonne.Select(c => c.Origine == OrigineColonnaReport.CampoStrumento
                    ? RisolutoreCampiUdt.LetturaColonna(c.Chiave, campiUdt)
                    : LetturaUdt.Invariata).ToList();
                var headers = new List<string> { "Test ID", "Operazione" };
                headers.AddRange(colonne.Select(c => c.Intestazione));
                headers.AddRange(new[] { "ID Requisito", "Esito reiterazione", "Esito test", "Nota reiterazione", "Evidenze operazione" });
                for (int i = 0; i < headers.Count; i++) ws.Cell(3, i + 1).Value = headers[i];
                ws.Range(3, 1, 3, headers.Count).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray);

                var r = 4;
                foreach (var tc in testDelTemplate.ConAnnullamento(ct))
                {
                    var ultima = tc.Esecuzioni[^1];
                    foreach (var (cond, indice) in ultima.Condizioni.Select((c, i) => (c, i)))
                    {
                        var col = 1;
                        ws.Cell(r, col++).Value = tc.Id;
                        ws.Cell(r, col++).Value = string.IsNullOrEmpty(cond.Funzionalita) ? $"#{indice + 1}" : cond.Funzionalita;
                        for (var i = 0; i < colonne.Count; i++) ws.Cell(r, col++).Value = ValoreColonna(tc, ultima, cond, colonne[i], letture[i]);
                        ws.Cell(r, col++).Value = string.Join(" / ", tc.RequisitoId);
                        ws.Cell(r, col++).Value = cond.Esito.Label();
                        ws.Cell(r, col++).Value = tc.StatoCorrente?.Label() ?? "";
                        ws.Cell(r, col++).Value = cond.Nota;
                        ws.Cell(r, col).Value = string.Join("; ", EvidenzaOperazione.Evidenze(tc, cond).Select(a => a.NomeFile));
                        r++;
                    }
                }
                ws.Columns().AdjustToContents();
            }
        }
    }

    internal static string NomeFoglioUnico(HashSet<string> nomiUsati, string nomeProposto)
    {
        var nome = string.IsNullOrWhiteSpace(nomeProposto) ? "Foglio" : nomeProposto.Length > 31 ? nomeProposto[..31] : nomeProposto;
        var candidato = nome;
        var i = 2;
        while (!nomiUsati.Add(candidato))
        {
            var suffisso = $" ({i++})";
            candidato = (nome.Length + suffisso.Length > 31 ? nome[..(31 - suffisso.Length)] : nome) + suffisso;
        }
        return candidato;
    }

    /// <summary>Legge il valore di una colonna configurata da un template: per i campi strumento, controlla in
    /// ordine il livello Condizione (PerOperazione), Esecuzione (PerRevisione) e TestCase (PerTest) — lo stesso
    /// campo vive in uno solo dei tre a seconda del suo Ambito, quindi solo uno dei tre GetValueOrDefault trova
    /// davvero qualcosa.</summary>
    private static string ValoreColonna(TestCase tc, Esecuzione ultima, Condizione cond, ColonnaReportTemplate colonna, LetturaUdt lettura) =>
        colonna.Origine switch
        {
            OrigineColonnaReport.CampoStrumento => ValoreCampoStrumento(tc, ultima, cond, colonna.Chiave, lettura),
            OrigineColonnaReport.CampoSistema => ValoreCampoSistema(tc, ultima, colonna.Chiave),
            _ => "",
        };

    /// <summary>Valore di una colonna campo-strumento. La lettura per chiave e' quella di sempre. Dal 6.6.1, la colonna UnitLogger che lo strumento
    /// reale non ha con quel nome legge il campo reale del ruolo (vedi RisolutoreCampiUdt.LetturaColonna), ma solo se trova un valore
    /// non vuoto: altrimenti resta il valore per chiave, cioe' per esempio quello scritto dal vecchio import.</summary>
    private static string ValoreCampoStrumento(TestCase tc, Esecuzione ultima, Condizione cond, string chiave, LetturaUdt lettura)
    {
        var perChiave = LeggiValoreStrumento(tc, ultima, cond, chiave);
        var reale = lettura.Modo switch
        {
            ModoLetturaUdt.ChiaveReale => LeggiValoreStrumento(tc, ultima, cond, lettura.ChiaveReale!),
            ModoLetturaUdt.WorkingUnitCombinata => RevisioneUdtService.ValoreCombinato(cond.ValoriStrumenti),
            ModoLetturaUdt.RevisioneWorkingUnit => RevisioneUdtService.VersioneDelValoreCombinato(RevisioneUdtService.ValoreCombinato(cond.ValoriStrumenti)),
            _ => null,
        };
        return string.IsNullOrEmpty(reale) ? perChiave ?? "" : reale;
    }

    /// <summary>Stessa precedenza di sempre: operazione, poi revisione, poi Test.</summary>
    private static string? LeggiValoreStrumento(TestCase tc, Esecuzione ultima, Condizione cond, string chiave) =>
        cond.ValoriStrumenti.GetValueOrDefault(chiave)
        ?? ultima.ValoriStrumenti.GetValueOrDefault(chiave)
        ?? tc.ValoriStrumentiTest.GetValueOrDefault(chiave);

    private static string ValoreCampoSistema(TestCase tc, Esecuzione ultima, string chiave) => chiave switch
    {
        CampiSistema.Componente => string.Join(", ", tc.Componente.AsEnumerable()),
        CampiSistema.Veicolo => string.Join(", ", tc.VeicoliEffettivi),
        CampiSistema.Funzionalita => string.Join(", ", tc.Funzionalita),
        CampiSistema.StrumentiUsati => string.Join(", ", tc.StrumentiUsati),
        CampiSistema.DescrizioneBreve => tc.DescrizioneBreve,
        CampiSistema.Descrizione => tc.Descrizione,
        CampiSistema.DescrizioneProblema => tc.DescrizioneProblema ?? "",
        CampiSistema.Matricola => ultima.Matricola,
        CampiSistema.BaselineSw => ultima.BaselineSw,
        CampiSistema.BaselineHw => ultima.BaselineHw,
        _ => "",
    };

    /// <summary>Sostituisce "Confronto con Banco" (Addendum redesign 2026-08, punto 2): copre qualunque
    /// template invece del solo salto fisso Banco→Veicolo, sia i test creati da un'azione collegata sia quelli
    /// che richiedono un collegamento (vincolo del template).</summary>
    private static void ScriviTestCollegati(XLWorkbook wb, List<TestCase> testCases, List<TestTemplate> templates, CancellationToken ct)
    {
        var ws = wb.Worksheets.Add("Test collegati");
        ws.Cell(1, 1).Value = "Test collegati - creati da un'azione del template o richiesti da un vincolo di collegamento";
        ws.Cell(1, 1).Style.Font.SetBold();
        string[] headers = { "Test", "Template", "Tipo test creato", "Test collegato creato", "Esito test collegato", "Test collegato richiesto (vincolo)" };
        for (int i = 0; i < headers.Length; i++) ws.Cell(3, i + 1).Value = headers[i];
        ws.Range(3, 1, 3, headers.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray);

        var r = 4;
        // Primo Test per codice nell'ordine di origine (come FirstOrDefault), senza una scansione per ogni collegamento.
        var perCodice = new Dictionary<string, TestCase>();
        foreach (var t in testCases) perCodice.TryAdd(t.Id, t);
        foreach (var tc in testCases.Where(t => t.TestCollegatiCreati.Count > 0 || !string.IsNullOrEmpty(t.TestCollegatoRichiestoId)).ConAnnullamento(ct))
        {
            var template = templates.FirstOrDefault(t => t.Uid == tc.TestTemplateUid);
            foreach (var creato in tc.TestCollegatiCreati)
            {
                var collegato = perCodice.GetValueOrDefault(creato.TestCollegatoId);
                ws.Cell(r, 1).Value = tc.Id; ws.Cell(r, 2).Value = template?.Nome ?? "";
                ws.Cell(r, 3).Value = creato.TemplateDestinazioneNome; ws.Cell(r, 4).Value = creato.TestCollegatoId;
                ws.Cell(r, 5).Value = collegato?.StatoCorrente?.Label() ?? "In attesa";
                r++;
            }
            if (!string.IsNullOrEmpty(tc.TestCollegatoRichiestoId))
            {
                ws.Cell(r, 1).Value = tc.Id; ws.Cell(r, 2).Value = template?.Nome ?? "";
                ws.Cell(r, 6).Value = tc.TestCollegatoRichiestoId;
                r++;
            }
        }
        ws.Columns().AdjustToContents();
    }

    private static void ScriviNonConformita(XLWorkbook wb, IReadOnlyList<NonConformita> nc, CancellationToken ct)
    {
        var ws = wb.Worksheets.Add("Anomalie legacy");
        string[] headers = { "ID", "Test", "Stato", "Data apertura", "Data chiusura", "Owner", "Destinatario", "Descrizione", "Azione", "Causa probabile (UnitLogger)" };
        for (int i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        ws.Range(1, 1, 1, headers.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray);

        var r = 2;
        foreach (var n in nc.OrderByDescending(x => x.DataApertura).ConAnnullamento(ct))
        {
            ws.Cell(r, 1).Value = n.Id; ws.Cell(r, 2).Value = n.TestCaseId; ws.Cell(r, 3).Value = n.Stato.Label();
            ws.Cell(r, 4).Value = n.DataApertura.ToString("dd/MM/yyyy");
            ws.Cell(r, 5).Value = n.DataChiusura?.ToString("dd/MM/yyyy") ?? "";
            ws.Cell(r, 6).Value = n.Owner; ws.Cell(r, 7).Value = n.Destinatario?.Label() ?? "";
            ws.Cell(r, 8).Value = n.Descrizione; ws.Cell(r, 9).Value = n.Azione;
            ws.Cell(r, 10).Value = n.CausaProbabile?.ToString() ?? "";
            r++;
        }
        ws.Columns().AdjustToContents();
    }

    /// <summary>Foglio "Grafici" (nome invariato, posizione invariata): i tre grafici della Home calcolati sullo stesso
    /// filtro dell'export. In testa filtri attivi e data di generazione; per ogni grafico l'immagine ad alta risoluzione
    /// (scala almeno 2, almeno 1600 px) e sotto la tabella dei dati da cui il grafico e' calcolato.</summary>
    private static List<GraficoNativoSpec> ScriviGrafici(XLWorkbook wb, AppState state, ReportFilter filtro, IReadOnlyList<Requisito> requisiti,
        IReadOnlyList<TestCase> testCases, int totaleTestCases, IReadOnlyList<NonConformita> nonConformita, string operatoreGenerazione, CancellationToken ct,
        IReadOnlyDictionary<string, string>? etichetteCampi = null)
    {
        ct.ThrowIfCancellationRequested();
        var ws = wb.Worksheets.Add("Grafici");
        var loc = LocalizationService.Instance;
        ws.Cell(1, 1).Value = loc.Traduci("FoglioGraficiTitolo");
        ws.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 14;
        ws.Cell(2, 1).Value = string.Format(loc.Traduci("FoglioGraficiFiltriFmt"), filtro.Descrizione(etichetteCampi ?? CampoSistemaCatalogoService.EtichettePerChiave(state)));
        ws.Cell(3, 1).Value = string.Format(loc.Traduci("FoglioGraficiGeneratoFmt"), GraficoRenderer.FormataDataOra(DateTime.Now), operatoreGenerazione);
        ws.Column(1).Width = 26;
        ws.Column(2).Width = 14;
        ws.Column(3).Width = 14;
        ws.Column(4).Width = 14;

        // Stesse regole della Home: copertura su tutti i test del filtro, anomalie dei test del filtro (senza filtro: tutte).
        var ncFiltrate = testCases.Count == totaleTestCases
            ? nonConformita
            : state.NonConformita.GetByTestIds(testCases.Select(tc => tc.Id).ToList());
        var grafici = GraficiDatiService.PerReport(requisiti, testCases, ncFiltrate);

        var dimensioni = GraficoRenderer.DimensioniEsportazione(FoglioLarghezzaGraficoDip, FoglioAltezzaGraficoDip);
        var riga = 5;
        var nativi = new List<GraficoNativoSpec>();
        foreach (var grafico in grafici)
        {
            ct.ThrowIfCancellationRequested();
            var rigaImmagine = riga;
            var png = GraficoRenderer.Render(grafico.Dati, grafico.Tipo, grafico.Titolo, dimensioni);
            using (var ms = new MemoryStream(png))
                ws.AddPicture(ms).MoveTo(ws.Cell(riga, 1)).WithSize((int)FoglioLarghezzaGraficoDip, (int)FoglioAltezzaGraficoDip);
            // Riga di Excel = 20 px a 96 dpi: l'immagine occupa le righe sotto il suo angolo.
            riga += (int)Math.Ceiling(FoglioAltezzaGraficoDip / 20d) + 1;
            riga = ScriviTabellaGrafico(ws, riga, grafico.Dati, grafico.Titolo, rigaImmagine, nativi);
            riga += 2;
        }
        return nativi;
    }

    /// <summary>Colonna da cui parte il grafico Excel nativo, a destra dell'immagine dello stesso grafico.</summary>
    private const int ColonnaGraficoNativo = 11;

    private const double FoglioLarghezzaGraficoDip = 760;
    private const double FoglioAltezzaGraficoDip = 380;

    /// <summary>Tabella dei dati sorgente di un grafico. Restituisce la prima riga libera sotto la tabella.</summary>
    private static int ScriviTabellaGrafico(IXLWorksheet ws, int riga, DatiGrafico dati, string titolo, int rigaImmagine, List<GraficoNativoSpec> nativi)
    {
        var loc = LocalizationService.Instance;
        var intestazione = riga;
        switch (dati)
        {
            case DatiCopertura copertura:
            {
                ScriviIntestazioneTabella(ws, riga, loc.Traduci("FoglioGraficiTabellaVoce"), loc.Traduci("FoglioGraficiTabellaRequisiti"), loc.Traduci("FoglioGraficiTabellaPercentuale"));
                riga++;
                nativi.Add(new GraficoNativoSpec(ws.Name, titolo, TipoGraficoNativo.Torta, intestazione, riga, riga + 3, 1, [2],
                    ColonnaGraficoNativo, rigaImmagine));
                var totale = Math.Max(1L, copertura.Conformi + copertura.ConformiConDeviazioni + copertura.NonConformi + copertura.NonTestati);
                var voci = new (string Nome, long Valore)[]
                {
                    ("Conforme", copertura.Conformi), ("Con deviazioni", copertura.ConformiConDeviazioni),
                    ("Non conforme", copertura.NonConformi), ("Non testato", copertura.NonTestati),
                };
                foreach (var (nome, valore) in voci)
                {
                    ws.Cell(riga, 1).Value = nome;
                    ws.Cell(riga, 2).Value = valore;
                    ws.Cell(riga, 3).Value = valore / (double)totale;
                    ws.Cell(riga, 3).Style.NumberFormat.Format = "0.0%";
                    riga++;
                }
                return riga;
            }
            case DatiAndamento andamento:
            {
                ScriviIntestazioneTabella(ws, riga, loc.Traduci("FoglioGraficiTabellaData"), loc.Traduci("FoglioGraficiTabellaAperte"), loc.Traduci("FoglioGraficiTabellaChiuse"));
                riga++;
                if (andamento.Punti.Count > 0)
                    nativi.Add(new GraficoNativoSpec(ws.Name, titolo, TipoGraficoNativo.Linee, intestazione, riga, riga + andamento.Punti.Count - 1,
                        1, [2, 3], ColonnaGraficoNativo, rigaImmagine));
                foreach (var punto in andamento.Punti)
                {
                    ws.Cell(riga, 1).Value = punto.Data;
                    ws.Cell(riga, 1).Style.DateFormat.Format = "dd/MM/yyyy";
                    ws.Cell(riga, 2).Value = punto.Aperte;
                    ws.Cell(riga, 3).Value = punto.Chiuse;
                    riga++;
                }
                return riga;
            }
            case DatiEsiti esiti:
            {
                var colonneEsito = new[] { Esito.Conforme, Esito.ConformeConDeviazioni, Esito.NonConforme, Esito.DaRivedere, Esito.NonApplicabile, Esito.DaEseguire, Esito.NonEseguitoNA };
                var intestazioni = new List<string> { loc.Traduci("FoglioGraficiTabellaVeicolo"), loc.Traduci("FoglioGraficiTabellaTotale") };
                intestazioni.AddRange(colonneEsito.Select(e => e.Label()));
                ScriviIntestazioneTabella(ws, riga, intestazioni.ToArray());
                riga++;
                var perVeicolo = esiti.PerVeicoloEsito.GroupBy(x => x.Nome)
                    .OrderByDescending(g => g.Sum(x => x.Valore)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
                var totaliEsito = new long[colonneEsito.Length];
                if (perVeicolo.Count > 0)
                    nativi.Add(new GraficoNativoSpec(ws.Name, titolo, TipoGraficoNativo.ColonneImpilate, intestazione, riga, riga + perVeicolo.Count - 1,
                        1, Enumerable.Range(3, colonneEsito.Length).ToList(), ColonnaGraficoNativo, rigaImmagine));
                foreach (var gruppo in perVeicolo)
                {
                    ws.Cell(riga, 1).Value = gruppo.Key;
                    ws.Cell(riga, 2).Value = gruppo.Sum(x => x.Valore);
                    for (int k = 0; k < colonneEsito.Length; k++)
                    {
                        var valore = gruppo.Where(x => x.Esito == colonneEsito[k]).Sum(x => x.Valore);
                        totaliEsito[k] += valore;
                        ws.Cell(riga, 3 + k).Value = valore;
                    }
                    riga++;
                }
                ws.Cell(riga, 1).Value = loc.Traduci("FoglioGraficiTabellaTotale");
                ws.Cell(riga, 1).Style.Font.SetBold();
                ws.Cell(riga, 2).Value = perVeicolo.Sum(g => g.Sum(x => x.Valore));
                for (int k = 0; k < colonneEsito.Length; k++) ws.Cell(riga, 3 + k).Value = totaliEsito[k];
                return riga + 1;
            }
            default:
                return riga;
        }
    }

    private static void ScriviIntestazioneTabella(IXLWorksheet ws, int riga, params string[] intestazioni)
    {
        for (int i = 0; i < intestazioni.Length; i++) ws.Cell(riga, i + 1).Value = intestazioni[i];
        ws.Range(riga, 1, riga, intestazioni.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightGray);
    }

    public static List<(DateTime Data, int Aperte, int Chiuse)> AndamentoNc(IReadOnlyList<NonConformita> nc)
    {
        if (nc.Count == 0) return new();
        var inizio = nc.Min(n => n.DataApertura).Date;
        var fine = DateTime.Now.Date;
        // Giorno di apertura e di chiusura calcolati una sola volta per NC (numero di giorno dal 1/1/0001; "non chiusa" e'
        // un giorno che nessun punto del grafico raggiunge). Ogni punto e' cosi' un confronto fra interi invece di due Count
        // LINQ che riscrivono .Date su tutto l'archivio: stesso risultato, senza rileggere 100k NC per ciascuno dei ~20 punti.
        var aperturaGiorno = new long[nc.Count];
        var chiusuraGiorno = new long[nc.Count];
        for (var i = 0; i < nc.Count; i++)
        {
            aperturaGiorno[i] = nc[i].DataApertura.Date.Ticks / TimeSpan.TicksPerDay;
            chiusuraGiorno[i] = nc[i].DataChiusura is { } chiusura ? chiusura.Date.Ticks / TimeSpan.TicksPerDay : long.MaxValue;
        }
        var punti = new List<(DateTime, int, int)>();
        for (var d = inizio; d <= fine; d = d.AddDays(Math.Max(1, (fine - inizio).Days / 20)))
        {
            var giorno = d.Ticks / TimeSpan.TicksPerDay;
            var aperte = 0;
            var chiuse = 0;
            for (var i = 0; i < aperturaGiorno.Length; i++)
            {
                if (aperturaGiorno[i] <= giorno && chiusuraGiorno[i] > giorno) aperte++;
                if (chiusuraGiorno[i] <= giorno) chiuse++;
            }
            punti.Add((d, aperte, chiuse));
        }
        return punti;
    }

    // ---------------------------------------------------------------- Salvataggio con timestamp, mai sovrascrive

    public static string Salva(DataPaths paths, string nomeBase, string estensione, byte[] contenuto)
    {
        Directory.CreateDirectory(paths.ReportDir);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var nomeFile = $"{nomeBase}_{timestamp}.{estensione}";
        var percorso = Path.Combine(paths.ReportDir, nomeFile);
        var i = 1;
        while (File.Exists(percorso))
        {
            nomeFile = $"{nomeBase}_{timestamp}_{i}.{estensione}";
            percorso = Path.Combine(paths.ReportDir, nomeFile);
            i++;
        }
        EsportazioneFile.ScriviBytesAtomico(percorso, contenuto);
        return percorso;
    }
}
