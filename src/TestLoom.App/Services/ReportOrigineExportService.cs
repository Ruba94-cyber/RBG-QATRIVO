using ClosedXML.Excel;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Fogli "Filtri" e "Origine" dell'export Excel (feedback utente 6.4.3: "esporta ciò che sto vedendo + l'origine
/// di quei dati"). La VISTA è il foglio esistente "Matrice configurabile" (stesse dimensioni righe/colonne,
/// stessi filtri, stessi conteggi/percentuali/totali dell'anteprima: qualunque dimensione — componente, Categoria,
/// Funzionalità, Veicolo, Gamma, Esito... — può stare su righe o colonne). Qui si aggiungono solo i due fogli
/// di supporto, senza un secondo calcolo: "Origine" viene dalla stessa passata di ReportMatriceService
/// (CostruisciCombinazioni). I Test "Importato da UnitLogger" compaiono con la loro origine.
/// </summary>
public static class ReportOrigineExportService
{
    /// <summary>Numero predefinito di righe aggiuntive oltre a tutte quelle che determinano lo stato. Modificabile finché l'utente non definisce la soglia finale.</summary>
    public static int LimiteRigheAggiuntive { get; set; } = 10_000;

    public sealed record SelezioneOrigine(HashSet<string> TestIds, HashSet<string> TestIdsNonOk, int Totale);
    internal sealed record ConteggiOrigineLimitata(int DeterminantiIncluse, int AggiuntiveIncluse,
        int DeterminantiEscluse, int AggiuntiveEscluse)
    {
        public bool RichiedeScelta => DeterminantiEscluse > 0 || AggiuntiveEscluse > 0;
    }
    public sealed record OriginePreparata(IReadOnlyList<ReportMatriceService.OrigineDato> Righe, SelezioneOrigine Selezione, int Mostrate)
    {
        public IReadOnlyList<TestCase>? TestCasesVista { get; init; }
        public IReadOnlyList<TestCase>? TestCasesMatrice { get; init; }
        public IReadOnlyList<TestCase>? TestCasesCompletiSnapshot { get; init; }
        public IReadOnlyDictionary<Guid, string>? FirmeTestCompleti { get; init; }
        public IReadOnlyList<NonConformita>? NonConformitaSnapshot { get; init; }
        public string? FirmaConfig { get; init; }
        public string? FirmaMaster { get; init; }
        public Dictionary<string, string>? FirmeMaster { get; init; }
        public string? FirmaNc { get; init; }
        public IReadOnlyList<NonConformita>? NonConformitaCompletaSnapshot { get; init; }
        public Dictionary<string, string>? FirmeTuttiMaster { get; init; }
        public IReadOnlyList<Requisito>? RequisitiSnapshot { get; init; }
        public IReadOnlyList<Veicolo>? VeicoliSnapshot { get; init; }
        public IReadOnlyList<Funzionalita>? FunzionalitaSnapshot { get; init; }
        public IReadOnlyList<TestTemplate>? TemplatesSnapshot { get; init; }
        public IReadOnlyList<Componente>? EcuSnapshot { get; init; }
        public IReadOnlyList<StatoFunzionalitaEcuVeicolo>? StatiManualiSnapshot { get; init; }
        public IReadOnlyList<StrumentoDiagnostico>? StrumentiSnapshot { get; init; }
        public IReadOnlyList<CampoSistemaDefinizione>? CampiDettagliSnapshot { get; init; }
        public IReadOnlyList<CampoSistemaDefinizione>? CampiPianificazioneSnapshot { get; init; }
        public IReadOnlyList<CellaNonApplicabile>? MarcatureSnapshot { get; init; }
        public int TotaleCompleto { get; init; } = Selezione.Totale;
    }

    private static string T(string k) => LocalizationService.Instance.Traduci(k);

    /// <summary>Nomi dei campi di un asse, nell'ordine di annidamento ("—" se l'asse non ha campi).</summary>
    private static string NomeCampi(IReadOnlyList<string> campi) => campi.Count == 0 ? "—" : string.Join(" › ", campi.Select(NomeDimensione));

    private static string NomeDimensione(string id) => id.StartsWith(DimensioniMatrice.CampoCustomPrefix, StringComparison.OrdinalIgnoreCase)
        ? id[DimensioniMatrice.CampoCustomPrefix.Length..]
        : T(id switch
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
            _ => id
        });

    public static SelezioneOrigine Scrivi(XLWorkbook wb, List<TestCase> testCases, AppState state, ReportFilter filtro,
        ConfigurazioneMatriceReport configurazione, string operatore, DateTime dataExport, CancellationToken ct = default,
        IReadOnlyList<ReportMatriceService.OrigineDato>? righeSelezionate = null, int? totaleOverride = null,
        OriginePreparata? originePreparata = null, IReadOnlyDictionary<string, string>? etichetteCampi = null,
        Action<OriginePreparata>? suOrigineScritta = null)
    {
        ct.ThrowIfCancellationRequested();
        ScriviFiltri(wb, filtro, configurazione, operatore, dataExport, etichetteCampi ?? CampoSistemaCatalogoService.EtichettePerChiave(state));
        // Marcature N.A. della vista (stesso overlay di ScriviMatriceConfigurabile): la colonna dedicata
        // dell'Origine dice se il dato ricade solo in celle escluse dai totali.
        var origine = originePreparata ?? (righeSelezionate == null
            ? CalcolaOrigine(testCases, state, configurazione, ct: ct)
            : CreaPreparata(righeSelezionate, totaleOverride ?? righeSelezionate.Count, ct));
        ScriviOrigine(wb, origine.Righe, origine.Mostrate, origine.Selezione.Totale, configurazione, ct);
        suOrigineScritta?.Invoke(origine);
        return origine.Selezione;
    }

    public static OriginePreparata CalcolaOrigine(List<TestCase> testCases, AppState state,
        ConfigurazioneMatriceReport configurazione, CancellationToken ct = default,
        IReadOnlyList<Veicolo>? veicoliSnapshot = null, IReadOnlyList<Funzionalita>? funzionalitaSnapshot = null,
        IReadOnlyList<TestTemplate>? templatesSnapshot = null, IReadOnlyList<StatoFunzionalitaEcuVeicolo>? manualiSnapshot = null,
        IReadOnlyList<CellaNonApplicabile>? marcatureSnapshot = null)
    {
        var limite = LimiteRigheAggiuntive;
        if (limite < 0) throw new InvalidOperationException("Il limite di righe aggiuntive dell'origine non può essere negativo.");
        ct.ThrowIfCancellationRequested();
        var marcature = marcatureSnapshot ?? MatriceNonApplicabileService.Leggi(state.Config, configurazione.CampiX, configurazione.CampiY);
        var (righe, mostrate, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(testCases, configurazione,
            veicoliSnapshot ?? state.Master.Veicoli.Load(), funzionalitaSnapshot ?? state.Master.Funzionalita.Load(), templatesSnapshot ?? state.Master.TestTemplate.Load(), limite, marcature,
            configurazione.ModalitaCella != ModalitaCellaMatrice.TestPercentuale ? manualiSnapshot ?? state.Master.StatiFunzionalitaEcuVeicolo.Load() : null, ct);
        var selezionate = righe.ToList();
        return new OriginePreparata(selezionate, Selezione(selezionate, selezionate.Count, ct), mostrate) { TotaleCompleto = totale };
    }

    public static OriginePreparata CalcolaOrigineCompleta(List<TestCase> testCases, AppState state,
        ConfigurazioneMatriceReport configurazione, CancellationToken ct = default,
        IReadOnlyList<Veicolo>? veicoliSnapshot = null, IReadOnlyList<Funzionalita>? funzionalitaSnapshot = null,
        IReadOnlyList<TestTemplate>? templatesSnapshot = null, IReadOnlyList<StatoFunzionalitaEcuVeicolo>? manualiSnapshot = null,
        IReadOnlyList<CellaNonApplicabile>? marcatureSnapshot = null)
    {
        ct.ThrowIfCancellationRequested();
        var marcature = marcatureSnapshot ?? MatriceNonApplicabileService.Leggi(state.Config, configurazione.CampiX, configurazione.CampiY);
        var (righe, _, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(testCases, configurazione,
            veicoliSnapshot ?? state.Master.Veicoli.Load(), funzionalitaSnapshot ?? state.Master.Funzionalita.Load(),
            templatesSnapshot ?? state.Master.TestTemplate.Load(), int.MaxValue,
            marcature, configurazione.ModalitaCella != ModalitaCellaMatrice.TestPercentuale
                ? manualiSnapshot ?? state.Master.StatiFunzionalitaEcuVeicolo.Load() : null, ct);
        var tutte = righe.ToList();
        return new OriginePreparata(tutte, Selezione(tutte, totale, ct), tutte.Count) { TotaleCompleto = tutte.Count };
    }

    public static int ContaOrigine(List<TestCase> testCases, AppState state,
        ConfigurazioneMatriceReport configurazione, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var marcature = MatriceNonApplicabileService.Leggi(state.Config, configurazione.CampiX, configurazione.CampiY);
        var (_, _, totale) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(testCases, configurazione, state.Master.Veicoli.Load(),
            state.Master.Funzionalita.Load(), state.Master.TestTemplate.Load(), 0, marcature,
            configurazione.ModalitaCella != ModalitaCellaMatrice.TestPercentuale ? state.Master.StatiFunzionalitaEcuVeicolo.Load() : null, ct);
        return totale;
    }

    public static int ContaRigheDecisive(List<TestCase> testCases, AppState state,
        ConfigurazioneMatriceReport configurazione, CancellationToken ct = default,
        IReadOnlyList<Veicolo>? veicoliSnapshot = null, IReadOnlyList<Funzionalita>? funzionalitaSnapshot = null,
        IReadOnlyList<TestTemplate>? templatesSnapshot = null, IReadOnlyList<StatoFunzionalitaEcuVeicolo>? manualiSnapshot = null,
        IReadOnlyList<CellaNonApplicabile>? marcatureSnapshot = null)
    {
        ct.ThrowIfCancellationRequested();
        var marcature = marcatureSnapshot ?? MatriceNonApplicabileService.Leggi(state.Config, configurazione.CampiX, configurazione.CampiY);
        var (righe, _, _) = ReportMatriceService.OrigineConLimiteRigheAggiuntive(testCases, configurazione,
            veicoliSnapshot ?? state.Master.Veicoli.Load(), funzionalitaSnapshot ?? state.Master.Funzionalita.Load(),
            templatesSnapshot ?? state.Master.TestTemplate.Load(), 0, marcature,
            configurazione.ModalitaCella != ModalitaCellaMatrice.TestPercentuale
                ? manualiSnapshot ?? state.Master.StatiFunzionalitaEcuVeicolo.Load() : null, ct);
        var count = 0;
        foreach (var riga in righe.ConAnnullamento(ct)) if (riga.DeterminaStato) count++;
        return count;
    }

    public static OriginePreparata CreaPreparata(IReadOnlyList<ReportMatriceService.OrigineDato> righe, int totale, CancellationToken ct = default) =>
        new(righe, Selezione(righe, totale, ct), righe.Count);

    internal static ConteggiOrigineLimitata CalcolaConteggiLimitati(int determinantiTotali, int aggiuntiveTotali, int limite)
    {
        if (determinantiTotali < 0 || aggiuntiveTotali < 0) throw new ArgumentOutOfRangeException(nameof(determinantiTotali));
        if (limite < 0) throw new ArgumentOutOfRangeException(nameof(limite));
        var determinantiIncluse = Math.Min(determinantiTotali, limite);
        var aggiuntiveIncluse = determinantiTotali > limite ? 0 : Math.Min(aggiuntiveTotali, limite);
        return new ConteggiOrigineLimitata(determinantiIncluse, aggiuntiveIncluse,
            determinantiTotali - determinantiIncluse, aggiuntiveTotali - aggiuntiveIncluse);
    }

    internal static List<ReportMatriceService.OrigineDato> SelezionaRigheLimitate(
        IReadOnlyList<ReportMatriceService.OrigineDato> righe, int limite, CancellationToken ct = default)
    {
        var determinanti = new List<ReportMatriceService.OrigineDato>();
        var aggiuntive = new List<ReportMatriceService.OrigineDato>();
        foreach (var riga in righe.ConAnnullamento(ct))
            (riga.DeterminaStato ? determinanti : aggiuntive).Add(riga);
        if (determinanti.Count > limite) return determinanti.Take(limite).ToList();
        return determinanti.Concat(aggiuntive.Take(limite)).ToList();
    }

    /// <summary>Crea una vista troncata conservando la fotografia immutabile che lega matrice, dati origine, NC e cataloghi.
    /// Senza questo passaggio la generazione potrebbe ripiegare silenziosamente sui dati live.</summary>
    internal static OriginePreparata CreaPreparataDaSnapshot(OriginePreparata snapshot,
        IReadOnlyList<ReportMatriceService.OrigineDato> righe, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var subset = CreaPreparata(righe, snapshot.Selezione.Totale, ct);
        return snapshot with
        {
            Righe = subset.Righe,
            Selezione = subset.Selezione,
            Mostrate = subset.Mostrate,
            TotaleCompleto = snapshot.TotaleCompleto
        };
    }

    private static SelezioneOrigine Selezione(IEnumerable<ReportMatriceService.OrigineDato> righe, int totale, CancellationToken ct)
    {
        var testIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var testIdsNonOk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var riga in righe.ConAnnullamento(ct))
        {
            testIds.Add(riga.TestId);
            if (riga.DeterminaStato && !riga.NonApplicabile
                && (riga.StatoCombinazione == Esito.NonConforme.Label()
                    || riga.StatoCombinazione == Esito.ConformeConDeviazioni.Label()))
                testIdsNonOk.Add(riga.TestId);
        }
        return new SelezioneOrigine(testIds, testIdsNonOk, totale);
    }

    private static void ScriviFiltri(XLWorkbook wb, ReportFilter filtro, ConfigurazioneMatriceReport configurazione, string operatore, DateTime dataExport,
        IReadOnlyDictionary<string, string> etichetteCampi)
    {
        var ws = wb.Worksheets.Add(T("OrigineFoglioFiltri"));
        ws.Cell(1, 1).Value = T("OrigineFoglioFiltri");
        ws.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 14;
        var r = 3;
        void Riga(string chiave, string valore)
        {
            ws.Cell(r, 1).Value = chiave;
            ws.Cell(r, 1).Style.Font.SetBold();
            ws.Cell(r, 2).Value = valore;
            r++;
        }
        static string Elenco(IEnumerable<string> v) { var l = v.ToList(); return l.Count == 0 ? "—" : string.Join(", ", l); }
        Riga(T("OrigineDataExport"), dataExport.ToString("dd/MM/yyyy HH:mm"));
        Riga(T("OrigineOperatore"), operatore);
        Riga(T("OrigineAsseRighe"), NomeCampi(configurazione.CampiY));
        Riga(T("OrigineAsseColonne"), NomeCampi(configurazione.CampiX));
        r++;
        ws.Cell(r++, 1).Value = T("OrigineFiltriReport");
        ws.Cell(r - 1, 1).Style.Font.SetBold().Font.FontSize = 12;
        Riga(T("OrigineFiltroEcu"), Elenco(filtro.Componente));
        Riga(T("OrigineFiltroVeicoli"), Elenco(filtro.Veicoli));
        Riga(T("OrigineFiltroDataDa"), filtro.DataDa?.ToString("dd/MM/yyyy") ?? "—");
        Riga(T("OrigineFiltroDataA"), filtro.DataA?.ToString("dd/MM/yyyy") ?? "—");
        Riga(T("OrigineFiltroTemplate"), filtro.TemplateUid.Count == 0 ? "—" : filtro.TemplateUid.Count.ToString());
        foreach (var (chiave, valori) in filtro.CampiCustom.Where(x => x.Value.Count > 0))
            Riga(etichetteCampi.TryGetValue(chiave, out var etichetta) && !string.IsNullOrWhiteSpace(etichetta) ? etichetta : chiave, Elenco(valori));
        r++;
        ws.Cell(r++, 1).Value = T("OrigineFiltriMatrice");
        ws.Cell(r - 1, 1).Style.Font.SetBold().Font.FontSize = 12;
        var attivi = configurazione.Filtri.Where(x => x.Value.Count > 0).ToList();
        if (attivi.Count == 0) Riga("—", "—");
        foreach (var (dimensione, valori) in attivi) Riga(NomeDimensione(dimensione), Elenco(valori));
        ws.Columns().AdjustToContents(8, 70);
    }

    internal static void ScriviOrigine(XLWorkbook wb, IEnumerable<ReportMatriceService.OrigineDato> righe, int mostrate, int totale,
        ConfigurazioneMatriceReport configurazione, CancellationToken ct, int righeDatiMassime = 1_048_571)
    {
        const int intestazioneRiga = 5;
        if (righeDatiMassime < 1 || righeDatiMassime > ExcelExportTableWriter.LimiteRigheFoglio - intestazioneRiga)
            throw new ArgumentOutOfRangeException(nameof(righeDatiMassime));
        string[] intest =
        {
            "OrigineColTestId", "OrigineColDescrizione", "OrigineColOrigine", "OrigineColRevisione", "OrigineColData",
            "OrigineColOperatore", "OrigineColVeicolo", "OrigineColGamma", "OrigineColEcu", "OrigineColFunzionalita",
            "OrigineColCategoria", "OrigineColEsitoOperazione", "OrigineColStato", "OrigineColDeterminaStato",
            "OrigineColRiga", "OrigineColColonna", "OrigineColNote", "OrigineColNonApplicabile"
        };
        var fogliOrigine = new List<IXLWorksheet>();
        IXLWorksheet CreaFoglio(int numero)
        {
            var nomeBase = T("OrigineFoglioOrigine");
            var suffisso = numero == 1 ? "" : $" ({numero})";
            var nome = nomeBase[..Math.Min(nomeBase.Length, 31 - suffisso.Length)] + suffisso;
            var wsNuovo = wb.Worksheets.Add(nome);
            fogliOrigine.Add(wsNuovo);
            wsNuovo.Cell(1, 1).Value = nome;
            wsNuovo.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 14;
            wsNuovo.Cell(2, 1).Value = T("OrigineNota");
            wsNuovo.Cell(2, 1).Style.Font.SetItalic().Font.FontColor = XLColor.Gray;
            if (totale > mostrate)
            {
                wsNuovo.Cell(3, 1).Value = string.Format(T("OrigineTroncatoFmt"), mostrate, totale - mostrate);
                wsNuovo.Cell(3, 1).Style.Font.SetBold().Font.FontColor = XLColor.Red;
            }
            for (var i = 0; i < intest.Length; i++) wsNuovo.Cell(intestazioneRiga, i + 1).Value = T(intest[i]);
            wsNuovo.Cell(intestazioneRiga, 15).Value = $"{T("OrigineColRiga")} ({NomeCampi(configurazione.CampiY)})";
            wsNuovo.Cell(intestazioneRiga, 16).Value = $"{T("OrigineColColonna")} ({NomeCampi(configurazione.CampiX)})";
            wsNuovo.Range(intestazioneRiga, 1, intestazioneRiga, intest.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightBlue);
            wsNuovo.SheetView.FreezeRows(intestazioneRiga);
            var larghezzeNuove = new double[] { 16, 40, 16, 10, 12, 18, 16, 14, 14, 30, 22, 20, 20, 12, 24, 24, 50, 16 };
            for (var i = 0; i < larghezzeNuove.Length; i++) wsNuovo.Column(i + 1).Width = larghezzeNuove[i];
            return wsNuovo;
        }

        var si = T("OrigineSi");
        var no = T("OrigineNo");
        var unitlogger = T("ImportatoDaUdtBadge");
        var fogliDettagli = new List<IXLWorksheet>();
        var marcatoriDettagli = new List<(IXLWorksheet Foglio, int Riga, int Colonna, string Riferimento, string Campo)>();
        var rigaDettaglio = 2;
        void SetTesto(IXLWorksheet foglio, int riga, int colonna, string id, string campo, string valore)
        {
            if (valore.Length <= ExcelExportTableWriter.LimiteCaratteriCella
                && valore.Count(c => c == '\n') <= 253)
            {
                foglio.Cell(riga, colonna).SetValue(valore);
                return;
            }

            var blocchi = new List<string>();
            for (var offset = 0; offset < valore.Length;)
            {
                var fine = Math.Min(offset + 30_000, valore.Length);
                var newline = 0;
                for (var indice = offset; indice < fine; indice++)
                {
                    if (valore[indice] != '\n') continue;
                    if (++newline > 253) { fine = indice; break; }
                }
                if (fine == offset) fine = Math.Min(offset + 30_000, valore.Length);
                if (fine < valore.Length && char.IsHighSurrogate(valore[fine - 1])) fine--;
                blocchi.Add(valore[offset..fine]);
                offset = fine;
            }

            for (var parte = 0; parte < blocchi.Count; parte++)
            {
                ct.ThrowIfCancellationRequested();
                if (fogliDettagli.Count == 0 || rigaDettaglio > ExcelExportTableWriter.LimiteRigheFoglio)
                {
                    var nomeBase = T("OrigineFoglioOrigine") + " dettagli estesi";
                    var nome = nomeBase[..Math.Min(nomeBase.Length, 31)];
                    var suffisso = fogliDettagli.Count + 1;
                    while (wb.Worksheets.Any(x => string.Equals(x.Name, nome, StringComparison.OrdinalIgnoreCase)))
                    {
                        var finale = $" ({suffisso++})";
                        nome = nomeBase[..Math.Min(nomeBase.Length, 31 - finale.Length)] + finale;
                    }
                    var dettaglio = wb.Worksheets.Add(nome);
                    dettaglio.Cell(1, 1).Value = "Riga origine";
                    dettaglio.Cell(1, 2).Value = "Campo";
                    dettaglio.Cell(1, 3).Value = "Parte";
                    dettaglio.Cell(1, 4).Value = "Testo completo";
                    dettaglio.Range(1, 1, 1, 4).Style.Font.SetBold();
                    dettaglio.Column(1).Width = 22;
                    dettaglio.Column(2).Width = 24;
                    dettaglio.Column(3).Width = 12;
                    dettaglio.Column(4).Width = 100;
                    fogliDettagli.Add(dettaglio);
                    rigaDettaglio = 2;
                }
                var dettaglioAttivo = fogliDettagli[^1];
                var riferimentoRiga = $"{foglio.Name}!R{riga} ({id})";
                dettaglioAttivo.Cell(rigaDettaglio, 1).SetValue(riferimentoRiga);
                dettaglioAttivo.Cell(rigaDettaglio, 2).SetValue(campo);
                dettaglioAttivo.Cell(rigaDettaglio, 3).SetValue($"{parte + 1}/{blocchi.Count}");
                dettaglioAttivo.Cell(rigaDettaglio, 4).SetValue(blocchi[parte]);
                rigaDettaglio++;
            }
            var riferimentoMarker = $"{foglio.Name}!R{riga} ({id})";
            marcatoriDettagli.Add((foglio, riga, colonna, riferimentoMarker, campo));
        }
        // Inserimento a blocchi (molto più rapido di una Cell() per valore su decine di migliaia di righe).
        // Streaming: le righe sono prodotte pigramente e scritte una alla volta (nessuna lista di record né
        // matrice di object[] intermedia); i testi sono scritti sempre come testo (SetValue(string)), così
        // "=..." nei campi liberi non diventa mai una formula.
        var scritte = 0;
        var numeroFoglio = 0;
        var ws = CreaFoglio(++numeroFoglio);
        foreach (var x in righe.ConAnnullamento(ct))
        {
            if (scritte > 0 && scritte % righeDatiMassime == 0)
                ws = CreaFoglio(++numeroFoglio);
            var r = intestazioneRiga + 1 + scritte % righeDatiMassime;
            scritte++;
            ws.Cell(r, 1).SetValue(x.TestId);
            SetTesto(ws, r, 2, x.TestId, "Descrizione", x.Descrizione);
            ws.Cell(r, 3).SetValue(x.Origine == OrigineTest.ImportStatoUdt ? unitlogger : "TestLoom");
            if (x.Revisione > 0) ws.Cell(r, 4).SetValue(x.Revisione);
            if (x.Data.HasValue) ws.Cell(r, 5).SetValue(x.Data.Value.ToString("dd/MM/yyyy"));
            SetTesto(ws, r, 6, x.TestId, "Operatore", x.Operatore);
            SetTesto(ws, r, 7, x.TestId, "Veicolo", x.Veicolo);
            SetTesto(ws, r, 8, x.TestId, "Gamma", x.Gamma);
            SetTesto(ws, r, 9, x.TestId, "Componente", x.Componente);
            SetTesto(ws, r, 10, x.TestId, "Funzionalità", x.Funzionalita);
            SetTesto(ws, r, 11, x.TestId, "Categorie", x.Categorie);
            ws.Cell(r, 12).SetValue(x.EsitoOperazione);
            ws.Cell(r, 13).SetValue(x.StatoCombinazione);
            ws.Cell(r, 14).SetValue(x.DeterminaStato ? si : no);
            SetTesto(ws, r, 15, x.TestId, "Riga matrice", x.Riga);
            SetTesto(ws, r, 16, x.TestId, "Colonna matrice", x.Colonna);
            SetTesto(ws, r, 17, x.TestId, "Note", x.Note);
            ws.Cell(r, 18).SetValue(x.NonApplicabile ? si : no);
        }
        foreach (var foglio in fogliOrigine)
        {
            var numero = fogliOrigine.IndexOf(foglio);
            var righeFoglio = Math.Min(righeDatiMassime, Math.Max(0, scritte - numero * righeDatiMassime));
            if (righeFoglio > 0) foglio.Range(intestazioneRiga, 1, intestazioneRiga + righeFoglio, intest.Length).SetAutoFilter();
        }
        foreach (var foglio in fogliDettagli)
            foglio.Range(1, 1, foglio.LastRowUsed()!.RowNumber(), 4).SetAutoFilter();
        if (fogliDettagli.Count > 0)
        {
            var gruppoDettagli = fogliDettagli.Count == 1
                ? fogliDettagli[0].Name
                : $"{fogliDettagli[0].Name} – {fogliDettagli[^1].Name}";
            foreach (var marker in marcatoriDettagli)
            {
                marker.Foglio.Cell(marker.Riga, marker.Colonna).SetValue(
                    $"Testo esteso: riga origine '{marker.Riferimento}', campo '{marker.Campo}', fogli '{gruppoDettagli}'.");
            }
        }
    }
}
