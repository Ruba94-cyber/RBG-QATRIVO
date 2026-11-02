using ClosedXML.Excel;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Cosa aggiungere all'export Report oltre ai fogli storici, e con quali colonne del foglio Dati. Senza opzioni: tutto.</summary>
public sealed record OpzioniArricchimentiReport(bool Dati = true, bool Riepilogo = true, bool Info = true, IReadOnlyList<string>? ColonneDati = null);

/// <summary>
/// Fogli di supporto dell'export Report per elaborazioni successive (reportistica, pivot): la tabella completa dei dati sorgente con
/// intestazioni in riga 1 e filtri (Test UID compreso), i riepiloghi con formule vive sul foglio Dati, il foglio Info e, a parte,
/// un file con tabelle pivot reali. Le intestazioni seguono la lingua dell'interfaccia. Le colonne del foglio Dati si scelgono per
/// identificativo; i riepiloghi usano solo quelle presenti.
/// </summary>
public static class ReportExportArricchimentiService
{
    private static string T(string chiave) => LocalizationService.Instance.Traduci(chiave);

    public static IReadOnlyList<ExportColonna> CampiPivotPersonalizzata(bool categorie = true) => ExportPivotService.Campi(
        ("veicolo", "Veicolo", "Vehicle", "Véhicule"), ("componente", "Componente", "Componente", "Componente"),
        ("funzionalita", "Funzionalità", "Function", "Fonction"), ("categoria", "Categoria", "Category", "Catégorie"),
        ("stato", "Stato", "Status", "Statut")).Where(c => categorie || c.Id != "categoria").ToList();

    /// <summary>Colonne del foglio Dati nell'ordine canonico: identificativo stabile (salvato nelle configurazioni) e chiave dell'etichetta.</summary>
    private static readonly (string Id, string Chiave)[] Colonne =
    {
        ("uid", "ExpColTestUid"), ("test", "OrigineColTestId"), ("descrizione", "OrigineColDescrizione"), ("origine", "OrigineColOrigine"),
        ("revisione", "OrigineColRevisione"), ("data", "OrigineColData"), ("operatore", "OrigineColOperatore"), ("veicolo", "OrigineColVeicolo"),
        ("gamma", "OrigineColGamma"), ("componente", "OrigineColEcu"), ("funzionalita", "OrigineColFunzionalita"), ("categorie", "OrigineColCategoria"),
        ("esito", "OrigineColEsitoOperazione"), ("stato", "OrigineColStato"), ("determina", "OrigineColDeterminaStato"),
        ("riga", "OrigineColRiga"), ("colonna", "OrigineColColonna"), ("note", "OrigineColNote"), ("na", "OrigineColNonApplicabile"),
    };

    /// <summary>Colonne scelte dall'operatore, nell'ordine canonico: la scelta nulla o senza colonne valide equivale a tutte.</summary>
    private static List<string> IdColonne(IReadOnlyList<string>? scelte)
    {
        var tutte = Colonne.Select(c => c.Id).ToList();
        if (scelte == null || scelte.Count == 0) return tutte;
        var valide = tutte.Where(id => scelte.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        return valide.Count == 0 ? tutte : valide;
    }

    /// <summary>Colonne esportabili del foglio Dati, per il dialogo di export.</summary>
    public static IReadOnlyList<ExportColonna> ColonneEsportabili() => Colonne.Select(c => new ExportColonna(c.Id, T(c.Chiave))).ToList();

    public static List<string> Intestazioni(IReadOnlyList<string>? scelte = null) =>
        IdColonne(scelte).Select(id => T(Colonne.First(c => c.Id == id).Chiave)).ToList();

    private static object? Valore(ReportMatriceService.OrigineDato x, string id, string si, string no, string unitlogger) => id switch
    {
        "uid" => x.TestUid == Guid.Empty ? null : x.TestUid.ToString(),
        "test" => x.TestId,
        "descrizione" => x.Descrizione,
        "origine" => x.Origine == OrigineTest.ImportStatoUdt ? unitlogger : "TestLoom",
        "revisione" => x.Revisione > 0 ? x.Revisione : null,
        "data" => x.Data,
        "operatore" => x.Operatore,
        "veicolo" => x.Veicolo,
        "gamma" => x.Gamma,
        "componente" => x.Componente,
        "funzionalita" => x.Funzionalita,
        "categorie" => x.Categorie,
        "esito" => x.EsitoOperazione,
        "stato" => x.StatoCombinazione,
        "determina" => x.DeterminaStato ? si : no,
        "riga" => x.Riga,
        "colonna" => x.Colonna,
        "note" => x.Note,
        "na" => x.NonApplicabile ? si : no,
        _ => null,
    };

    /// <summary>Scrive la tabella Dati (intestazioni in riga 1, filtro automatico, testi lunghi nei fogli di dettaglio) e ne
    /// restituisce il writer, da cui ricavare nome del foglio e numero di righe, e le colonne effettivamente scritte.</summary>
    public static (ExcelExportTableWriter Writer, IReadOnlyList<string> Colonne) ScriviDati(XLWorkbook wb,
        IEnumerable<ReportMatriceService.OrigineDato> righe, CancellationToken ct, IReadOnlyList<string>? colonneScelte = null)
    {
        var si = T("OrigineSi");
        var no = T("OrigineNo");
        var unitlogger = T("ImportatoDaUdtBadge");
        var ids = IdColonne(colonneScelte);
        var writer = new ExcelExportTableWriter(wb, T("ExpFoglioDati"), Intestazioni(ids));
        foreach (var x in righe)
        {
            ct.ThrowIfCancellationRequested();
            writer.AggiungiRiga(x.TestId, ids.Select(id => Valore(x, id, si, no, unitlogger)).ToList());
        }
        writer.Completa();
        return (writer, ids);
    }

    /// <summary>Dati, Riepilogo e Info di un export Report, secondo le opzioni. I fogli esistenti non vengono toccati: questi si
    /// aggiungono in coda. Un riepilogo richiede il foglio Dati (le sue formule lo leggono): si scrive anche se non scelto.</summary>
    public static void Scrivi(XLWorkbook wb, IReadOnlyList<ReportMatriceService.OrigineDato> righe, int totaleOrigine,
        string operatore, DateTime quando, CancellationToken ct, OpzioniArricchimentiReport? opzioni = null)
    {
        opzioni ??= new OpzioniArricchimentiReport();
        if (!opzioni.Dati && !opzioni.Riepilogo && !opzioni.Info) return;
        (ExcelExportTableWriter Writer, IReadOnlyList<string> Colonne)? dati = null;
        if (opzioni.Dati || opzioni.Riepilogo) dati = ScriviDati(wb, righe, ct, opzioni.ColonneDati);
        if (opzioni.Riepilogo && dati is { } d) ScriviRiepilogo(wb, righe, d.Writer, d.Colonne, ct);
        if (opzioni.Info) ScriviInfo(wb, dati?.Writer, totaleOrigine, operatore, quando);
    }

    private static string Rif(string foglio) => "'" + foglio.Replace("'", "''") + "'!";

    private static string Intervallo(string foglio, int colonna, long ultimaRiga)
    {
        var lettera = XLHelper.GetColumnLetterFromNumber(colonna);
        return $"{Rif(foglio)}${lettera}$2:${lettera}${Math.Max(2, ultimaRiga + 1)}";
    }

    private static void ScriviRiepilogo(XLWorkbook wb, IReadOnlyList<ReportMatriceService.OrigineDato> righe, ExcelExportTableWriter dati,
        IReadOnlyList<string> colonne, CancellationToken ct)
    {
        var ws = wb.Worksheets.Add(NomeLibero(wb, T("ExpFoglioRiepilogo")));
        ws.Cell(1, 1).Value = T("ExpFoglioRiepilogo");
        ws.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 14;
        if (dati.SuPiuFogli)
        {
            ws.Cell(2, 1).Value = T("ExpRiepilogoNonDisponibile");
            ws.Cell(2, 1).Style.Font.SetItalic().Font.FontColor = XLColor.Red;
            return;
        }
        ws.Cell(2, 1).Value = T("ExpRiepilogoTitolo");
        ws.Cell(2, 1).Style.Font.SetItalic().Font.FontColor = XLColor.Gray;

        int Pos(string id) { var i = colonne.ToList().IndexOf(id); return i < 0 ? 0 : i + 1; }
        var foglio = dati.NomeFoglioPrincipale;
        var ultima = dati.RigheScritte;
        var si = T("OrigineSi");
        var vuoto = T("ExpRiepilogoVuoto");
        var rigaCorrente = 4;
        var posDetermina = Pos("determina");

        void Blocco(string titolo, string idColonna, Func<ReportMatriceService.OrigineDato, string> valore)
        {
            var colonna = Pos(idColonna);
            if (colonna == 0) return; // colonna non esportata: il riepilogo non ha dati su cui contare
            ct.ThrowIfCancellationRequested();
            ws.Cell(rigaCorrente, 1).Value = titolo;
            ws.Cell(rigaCorrente, 1).Style.Font.SetBold().Font.FontSize = 12;
            rigaCorrente++;
            ws.Cell(rigaCorrente, 1).Value = T("ExpRiepilogoValore");
            ws.Cell(rigaCorrente, 2).Value = T("ExpRiepilogoRighe");
            if (posDetermina > 0) ws.Cell(rigaCorrente, 3).Value = T("ExpRiepilogoDeterminanti");
            ws.Range(rigaCorrente, 1, rigaCorrente, posDetermina > 0 ? 3 : 2).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightBlue);
            rigaCorrente++;
            var primaRiga = rigaCorrente;
            var intervallo = Intervallo(foglio, colonna, ultima);
            var determina = posDetermina > 0 ? Intervallo(foglio, posDetermina, ultima) : "";
            var valori = righe.Select(valore).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var v in valori)
            {
                if (string.IsNullOrEmpty(v))
                {
                    ws.Cell(rigaCorrente, 1).Value = vuoto;
                    ws.Cell(rigaCorrente, 2).FormulaA1 = $"COUNTIF({intervallo},\"\")";
                    if (posDetermina > 0) ws.Cell(rigaCorrente, 3).FormulaA1 = $"COUNTIFS({intervallo},\"\",{determina},\"{si}\")";
                }
                else
                {
                    ws.Cell(rigaCorrente, 1).SetValue(v);
                    ws.Cell(rigaCorrente, 2).FormulaA1 = $"COUNTIF({intervallo},$A{rigaCorrente})";
                    if (posDetermina > 0) ws.Cell(rigaCorrente, 3).FormulaA1 = $"COUNTIFS({intervallo},$A{rigaCorrente},{determina},\"{si}\")";
                }
                rigaCorrente++;
            }
            ws.Cell(rigaCorrente, 1).Value = T("ExpRiepilogoTotale");
            ws.Cell(rigaCorrente, 2).FormulaA1 = $"SUM(B{primaRiga}:B{Math.Max(primaRiga, rigaCorrente - 1)})";
            if (posDetermina > 0) ws.Cell(rigaCorrente, 3).FormulaA1 = $"SUM(C{primaRiga}:C{Math.Max(primaRiga, rigaCorrente - 1)})";
            ws.Range(rigaCorrente, 1, rigaCorrente, posDetermina > 0 ? 3 : 2).Style.Font.SetBold();
            rigaCorrente += 2;
        }

        Blocco(T("ExpRiepilogoPerGamma"), "gamma", x => x.Gamma);
        Blocco(T("ExpRiepilogoPerVeicolo"), "veicolo", x => x.Veicolo);
        Blocco(T("ExpRiepilogoPerEcu"), "componente", x => x.Componente);
        Blocco(T("ExpRiepilogoPerStato"), "stato", x => x.StatoCombinazione);

        // Gamma per stato, solo righe determinanti: la stessa lettura della matrice del Report.
        var posGamma = Pos("gamma");
        var posStato = Pos("stato");
        if (posGamma > 0 && posStato > 0 && posDetermina > 0)
        {
            ws.Cell(rigaCorrente, 1).Value = T("ExpRiepilogoGammaPerStato");
            ws.Cell(rigaCorrente, 1).Style.Font.SetBold().Font.FontSize = 12;
            rigaCorrente++;
            var gamme = righe.Select(x => x.Gamma).Where(g => !string.IsNullOrEmpty(g)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
            var stati = righe.Select(x => x.StatoCombinazione).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList();
            var intestazioneRiga = rigaCorrente;
            ws.Cell(intestazioneRiga, 1).Value = T("OrigineColGamma");
            for (var s = 0; s < stati.Count; s++) ws.Cell(intestazioneRiga, 2 + s).SetValue(stati[s]);
            ws.Range(intestazioneRiga, 1, intestazioneRiga, 1 + Math.Max(1, stati.Count)).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightBlue);
            rigaCorrente++;
            var gammaRange = Intervallo(foglio, posGamma, ultima);
            var statoRange = Intervallo(foglio, posStato, ultima);
            var determinaRange = Intervallo(foglio, posDetermina, ultima);
            foreach (var g in gamme)
            {
                ct.ThrowIfCancellationRequested();
                ws.Cell(rigaCorrente, 1).SetValue(g);
                for (var s = 0; s < stati.Count; s++)
                {
                    var lettera = XLHelper.GetColumnLetterFromNumber(2 + s);
                    ws.Cell(rigaCorrente, 2 + s).FormulaA1 =
                        $"COUNTIFS({gammaRange},$A{rigaCorrente},{statoRange},{lettera}${intestazioneRiga},{determinaRange},\"{si}\")";
                }
                rigaCorrente++;
            }
        }
        ws.Column(1).Width = 34;
        for (var c = 2; c <= 8; c++) ws.Column(c).Width = 22;
    }

    private static void ScriviInfo(XLWorkbook wb, ExcelExportTableWriter? dati, int totaleOrigine, string operatore, DateTime quando)
    {
        var ws = wb.Worksheets.Add(NomeLibero(wb, T("ExpFoglioInfo")));
        ws.Cell(1, 1).Value = T("ExpInfoTitolo");
        ws.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 14;
        var versione = typeof(ReportExportArricchimentiService).Assembly.GetName().Version?.ToString(3) ?? "";
        var voci = new List<(string Etichetta, string Valore)>
        {
            (T("ExpInfoGenerato"), quando.ToString("dd/MM/yyyy HH:mm")),
            (T("ExpInfoOperatore"), operatore ?? ""),
            (T("ExpInfoVersione"), versione),
        };
        if (dati != null) voci.Add((T("ExpInfoRigheDati"), dati.RigheScritte.ToString()));
        voci.Add((T("ExpInfoRigheOrigine"), totaleOrigine.ToString()));
        var riga = 3;
        foreach (var (etichetta, valore) in voci)
        {
            ws.Cell(riga, 1).Value = etichetta;
            ws.Cell(riga, 1).Style.Font.SetBold();
            ws.Cell(riga, 2).SetValue(valore);
            ws.Cell(riga, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            riga++;
        }
        riga++;
        ws.Cell(riga, 1).Value = T("ExpInfoFogli");
        ws.Cell(riga, 1).Style.Font.SetBold().Font.FontSize = 12;
        riga++;
        var fogli = new List<(string Nome, string Descrizione)>();
        foreach (var f in wb.Worksheets.Where(f => !string.Equals(f.Name, ws.Name, StringComparison.Ordinal)))
            fogli.Add((f.Name, Descrizione(f.Name, dati?.NomeFoglioPrincipale)));
        fogli.Add((ws.Name, T("ExpInfoTitolo")));
        foreach (var (nome, descrizione) in fogli)
        {
            ws.Cell(riga, 1).SetValue(nome);
            ws.Cell(riga, 2).SetValue(descrizione);
            riga++;
        }
        ws.Column(1).Width = 36;
        ws.Column(2).Width = 100;
    }

    private static string Descrizione(string nomeFoglio, string? nomeDati)
    {
        if (nomeDati != null && string.Equals(nomeFoglio, nomeDati, StringComparison.Ordinal)) return T("ExpDescDati");
        if (string.Equals(nomeFoglio, T("ExpFoglioRiepilogo"), StringComparison.Ordinal)) return T("ExpDescRiepilogo");
        if (string.Equals(nomeFoglio, "Grafici", StringComparison.Ordinal)) return T("ExpDescGrafici");
        if (nomeFoglio.StartsWith(T("OrigineFoglioOrigine"), StringComparison.Ordinal)) return T("ExpDescOrigine");
        if (string.Equals(nomeFoglio, "Matrice configurabile", StringComparison.Ordinal)) return T("ExpDescVista");
        if (string.Equals(nomeFoglio, "NC matrice", StringComparison.Ordinal)) return T("ExpDescNc");
        if (string.Equals(nomeFoglio, T("OrigineFoglioFiltri"), StringComparison.Ordinal)) return T("ExpDescFiltri");
        return "";
    }

    private static string NomeLibero(XLWorkbook wb, string desiderato)
    {
        var nome = desiderato.Length <= 31 ? desiderato : desiderato[..31];
        var baseNome = nome;
        var suffisso = 2;
        while (wb.Worksheets.Any(x => string.Equals(x.Name, nome, StringComparison.OrdinalIgnoreCase)))
        {
            var finale = $" ({suffisso++})";
            nome = baseNome[..Math.Min(baseNome.Length, 31 - finale.Length)] + finale;
        }
        return nome;
    }

    private const int MassimoColonneFoglio = 16_000;

    private static void Intestazione(IXLCell cella)
    {
        cella.Style.Font.Bold = true;
        cella.Style.Fill.BackgroundColor = XLColor.LightGray;
    }

    private static int Ordina(string a, string b) => string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Tabella a blocchi: dimensione X sulle colonne, dimensione Y sulle righe, un blocco per stato (numeri e/o percentuali) piu' il
    /// blocco Totale, che e' il denominatore esplicito delle percentuali. Le celle senza combinazioni nel totale restano vuote, mai 0%.</summary>
    internal static void ScriviIncrocioBlocchi(XLWorkbook wb, string nomeFoglio, string etichettaX, string etichettaY,
        List<string> xs, List<string> ys, IReadOnlyList<string> stati, Func<string, string, string, int> conteggio,
        string notaDenominatore, bool mostraNumeri, bool mostraPercentuali, CancellationToken ct)
    {
        var loc = LocalizationService.Instance;
        xs.Sort(Ordina);
        ys.Sort(Ordina);
        if (xs.Count + 1 > MassimoColonneFoglio)
        {
            ScriviAvvisoPivot(wb, nomeFoglio, loc.Scegli(
                $"Pivot non scritta: troppi valori di {etichettaX} per un foglio Excel ({xs.Count}, massimo {MassimoColonneFoglio - 1}). Non è un risultato parziale: la tabella manca.",
                $"Pivot not written: too many {etichettaX} values for an Excel sheet ({xs.Count}, maximum {MassimoColonneFoglio - 1}). This is not a partial result: the table is missing.",
                $"Tableau non écrit : trop de valeurs {etichettaX} pour une feuille Excel ({xs.Count}, maximum {MassimoColonneFoglio - 1}). Ce n'est pas un résultat partiel : le tableau est absent."));
            return;
        }
        var ws = wb.Worksheets.Add(nomeFoglio);
        ws.Cell(1, 1).Value = $"{etichettaY} × {etichettaX}";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Value = notaDenominatore;
        var riga = 4;
        var totali = new int[ys.Count, xs.Count];
        for (var r = 0; r < ys.Count; r++)
            for (var c = 0; c < xs.Count; c++)
                totali[r, c] = stati.Sum(st => conteggio(xs[c], ys[r], st));
        void Blocco(string titolo, Func<int, int, string, double?> valore, string stato, string? formato)
        {
            ct.ThrowIfCancellationRequested();
            ws.Cell(riga, 1).Value = titolo;
            ws.Cell(riga, 1).Style.Font.Bold = true;
            riga++;
            ws.Cell(riga, 1).Value = etichettaY;
            Intestazione(ws.Cell(riga, 1));
            for (var c = 0; c < xs.Count; c++) { ws.Cell(riga, c + 2).Value = xs[c]; Intestazione(ws.Cell(riga, c + 2)); }
            riga++;
            for (var r = 0; r < ys.Count; r++)
            {
                ws.Cell(riga, 1).Value = ys[r];
                for (var c = 0; c < xs.Count; c++)
                    if (valore(r, c, stato) is { } v)
                    {
                        ws.Cell(riga, c + 2).Value = v;
                        if (formato != null) ws.Cell(riga, c + 2).Style.NumberFormat.Format = formato;
                    }
                riga++;
            }
            riga++;
        }
        Blocco(loc.Scegli("Totale combinazioni (denominatore delle percentuali)", "Total combinations (denominator of the percentages)",
            "Total des combinaisons (dénominateur des pourcentages)"), (r, c, _) => totali[r, c], "", null);
        foreach (var stato in stati)
        {
            if (mostraNumeri)
                Blocco($"{stato} - {loc.Scegli("Numero", "Count", "Nombre")}", (r, c, st) => totali[r, c] == 0 ? null : conteggio(xs[c], ys[r], st), stato, null);
            if (mostraPercentuali)
                Blocco($"{stato} - %", (r, c, st) => totali[r, c] == 0 ? null : (double)conteggio(xs[c], ys[r], st) / totali[r, c], stato, "0.0%");
        }
        if (stati.Count == 0)
            ws.Cell(riga, 1).Value = loc.Scegli("Nessuna combinazione per i filtri selezionati.", "No combinations for the selected filters.", "Aucune combinaison pour les filtres sélectionnés.");
        ws.Column(1).Width = 32;
        ws.SheetView.FreezeColumns(1);
    }

    /// <summary>componente e funzionalita' sull'asse X (due livelli di intestazione), gli stati sulle righe: per ogni stato una riga di numeri e/o
    /// una di percentuali sul totale della stessa coppia componente/funzionalita' (riga Totale).</summary>
    internal static void ScriviEcuFunzionalitaPerStato(XLWorkbook wb, string nomeFoglio,
        Dictionary<(string Componente, string Funzione, string Stato), int> dati, IReadOnlyList<string> stati,
        string notaDenominatore, bool mostraNumeri, bool mostraPercentuali, CancellationToken ct)
    {
        var loc = LocalizationService.Instance;
        var colonne = dati.Keys.Select(k => (k.Componente, k.Funzione)).Distinct()
            .OrderBy(k => k.Componente, StringComparer.CurrentCultureIgnoreCase).ThenBy(k => k.Funzione, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (colonne.Count + 1 > MassimoColonneFoglio)
        {
            ScriviAvvisoPivot(wb, nomeFoglio, loc.Scegli(
                $"Pivot non scritta: troppe coppie componente/funzionalità per un foglio Excel ({colonne.Count}, massimo {MassimoColonneFoglio - 1}). Non è un risultato parziale: la tabella manca.",
                $"Pivot not written: too many componente/function pairs for an Excel sheet ({colonne.Count}, maximum {MassimoColonneFoglio - 1}). This is not a partial result: the table is missing.",
                $"Tableau non écrit : trop de paires componente/fonction pour une feuille Excel ({colonne.Count}, maximum {MassimoColonneFoglio - 1}). Ce n'est pas un résultat partiel : le tableau est absent."));
            return;
        }
        var ws = wb.Worksheets.Add(nomeFoglio);
        ws.Cell(1, 1).Value = loc.Scegli("Componente e funzionalità per stato", "Componente and function by status", "Componente et fonction par statut");
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Value = notaDenominatore;
        ws.Cell(4, 1).Value = "Componente";
        ws.Cell(5, 1).Value = loc.Scegli("Funzionalità", "Function", "Fonction");
        Intestazione(ws.Cell(4, 1));
        Intestazione(ws.Cell(5, 1));
        for (var c = 0; c < colonne.Count; c++)
        {
            ct.ThrowIfCancellationRequested();
            ws.Cell(4, c + 2).Value = colonne[c].Componente;
            ws.Cell(5, c + 2).Value = colonne[c].Funzione;
            Intestazione(ws.Cell(4, c + 2));
            Intestazione(ws.Cell(5, c + 2));
        }
        var totali = colonne.Select(k => stati.Sum(st => dati.GetValueOrDefault((k.Componente, k.Funzione, st)))).ToArray();
        var riga = 6;
        ws.Cell(riga, 1).Value = loc.Scegli("Totale (denominatore)", "Total (denominator)", "Total (dénominateur)");
        ws.Cell(riga, 1).Style.Font.Bold = true;
        for (var c = 0; c < colonne.Count; c++) ws.Cell(riga, c + 2).Value = totali[c];
        riga++;
        foreach (var stato in stati)
        {
            ct.ThrowIfCancellationRequested();
            if (mostraNumeri)
            {
                ws.Cell(riga, 1).Value = $"{stato} - {loc.Scegli("Numero", "Count", "Nombre")}";
                for (var c = 0; c < colonne.Count; c++)
                    if (totali[c] > 0) ws.Cell(riga, c + 2).Value = dati.GetValueOrDefault((colonne[c].Componente, colonne[c].Funzione, stato));
                riga++;
            }
            if (mostraPercentuali)
            {
                ws.Cell(riga, 1).Value = $"{stato} - %";
                for (var c = 0; c < colonne.Count; c++)
                    if (totali[c] > 0)
                    {
                        ws.Cell(riga, c + 2).Value = (double)dati.GetValueOrDefault((colonne[c].Componente, colonne[c].Funzione, stato)) / totali[c];
                        ws.Cell(riga, c + 2).Style.NumberFormat.Format = "0.0%";
                    }
                riga++;
            }
        }
        if (colonne.Count == 0)
            ws.Cell(riga, 1).Value = loc.Scegli("Nessuna combinazione per i filtri selezionati.", "No combinations for the selected filters.", "Aucune combinaison pour les filtres sélectionnés.");
        ws.Column(1).Width = 32;
        ws.SheetView.FreezeColumns(1);
    }

    /// <summary>Foglio con un avviso esplicito al posto di una tabella che non puo' essere scritta (limiti di Excel): mai un sottoinsieme presentato come completo.</summary>
    internal static IXLWorksheet ScriviAvvisoPivot(XLWorkbook wb, string nomeFoglio, string messaggio)
    {
        var ws = wb.Worksheets.Add(NomeLibero(wb, nomeFoglio));
        ws.Cell(1, 1).Value = LocalizationService.Instance.Scegli("Avviso", "Warning", "Avertissement");
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Value = messaggio;
        ws.Column(1).Width = 120;
        return ws;
    }

    /// <summary>Etichetta mostrata nelle pivot Report per le funzionalita' senza categoria (al posto della stringa vuota).</summary>
    internal static string EtichettaSenzaCategoria => LocalizationService.Instance.Scegli("Senza categoria", "No category", "Sans catégorie");

    /// <summary>Pivot sullo stato corrente: una riga sorgente per combinazione e, nel secondo foglio,
    /// una per categoria associata. I dati sono nel workbook e i campi restano filtrabili in Excel.</summary>
    public static (IXLWorksheet Core, IXLWorksheet Categorie, bool Troncata) ScriviPivotStato(XLWorkbook wb, ReportMatriceService.MatricePreparata matrice,
        ConfigurazioneMatriceReport config, IReadOnlyList<Funzionalita> funzionalita,
        IReadOnlyList<CellaNonApplicabile>? marcature, CancellationToken ct, bool mostraNumeri = true, bool mostraPercentuali = true)
    {
        if (!mostraNumeri && !mostraPercentuali)
            throw new ArgumentException(LocalizationService.Instance.Scegli("Le pivot standard devono mostrare almeno i numeri o le percentuali.",
                "Standard pivots must show at least counts or percentages.", "Les tableaux croisés standard doivent afficher au moins les nombres ou les pourcentages."));
        var loc = LocalizationService.Instance;
        string S(string it, string en, string fr) => loc.Scegli(it, en, fr);
        var intestazioni = new[] { S("Veicolo", "Vehicle", "Véhicule"), "Componente",
            S("Funzionalità", "Function", "Fonction"), S("Categoria", "Category", "Catégorie"),
            S("Stato", "Status", "Statut"), S("Combinazioni", "Combinations", "Combinaisons") };
        var catalogo = funzionalita.GroupBy(f => f.Nome, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var filtroCategoria = config.Filtri.TryGetValue(DimensioniMatrice.CategoriaFunzionalita, out var filtro) && filtro.Count > 0
            ? filtro.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var posX = config.CampiX.IndexOf(DimensioniMatrice.CategoriaFunzionalita);
        var posY = config.CampiY.IndexOf(DimensioniMatrice.CategoriaFunzionalita);
        IXLWorksheet CreaSorgente(string nome)
        {
            var ws = wb.Worksheets.Add(NomeLibero(wb, nome));
            for (var col = 0; col < intestazioni.Length; col++) ws.Cell(1, col + 1).Value = intestazioni[col];
            ws.SheetView.FreezeRows(1);
            ws.Columns(1, 5).Width = 25;
            ws.Column(6).Width = 16;
            return ws;
        }
        var wsCore = CreaSorgente(S("Origine pivot", "Pivot source", "Source pivot"));
        var wsCategorie = CreaSorgente(S("Origine categorie", "Category source", "Source catégories"));
        var rigaCore = 2;
        var rigaCategoria = 2;
        // Aggregati per le tabelle a blocchi: stati in ordine di enum, conteggi per (dimensioni, stato).
        var stati = new SortedDictionary<int, string>();
        var ecuFunzione = new Dictionary<(string Componente, string Funzione, string Stato), int>();
        var ecuCategoria = new Dictionary<(string Categoria, string Componente, string Stato), int>();
        var veicoloCategoria = new Dictionary<(string Categoria, string Veicolo, string Stato), int>();
        void Conta<TKey>(Dictionary<TKey, int> d, TKey chiave) where TKey : notnull => d[chiave] = d.GetValueOrDefault(chiave) + 1;
        var troncata = false;
        void Aggiungi(IXLWorksheet ws, ref int riga, CombinazioneCellaMatrice c, string categoria, string stato)
        {
            // Oltre il limite di righe del foglio la sorgente si ferma e viene dichiarata troncata (avviso sotto); i conteggi aggregati restano completi.
            if (riga > 1_048_576) { troncata = true; return; }
            ws.Cell(riga, 1).Value = c.Veicolo;
            ws.Cell(riga, 2).Value = c.Componente;
            ws.Cell(riga, 3).Value = c.Funzionalita;
            ws.Cell(riga, 4).Value = categoria;
            ws.Cell(riga, 5).Value = stato;
            ws.Cell(riga++, 6).Value = 1;
        }
        foreach (var (c, celle) in matrice.CombinazioniPerExport(marcature, ct))
        {
            var stato = ReportMatriceService.EtichettaStatoDisponibile(c.Stato);
            stati[(int)c.Stato] = stato;
            Aggiungi(wsCore, ref rigaCore, c, "", stato);
            Conta(ecuFunzione, (c.Componente, c.Funzionalita, stato));
            var valori = catalogo.TryGetValue(c.Funzionalita, out var f)
                ? f.Categorie.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            if (valori.Count == 0) valori.Add("");
            foreach (var categoria in valori)
            {
                ct.ThrowIfCancellationRequested();
                if (filtroCategoria != null && !filtroCategoria.Contains(categoria)) continue;
                bool Ammessa(string cella)
                {
                    var parti = cella.Split('\u0001');
                    if (parti.Length != 2) return false;
                    bool Asse(string asse, int pos)
                    {
                        if (pos < 0) return true;
                        var livelli = asse.Split(ReportMatriceService.SepLivello);
                        return pos < livelli.Length && string.Equals(livelli[pos], categoria, StringComparison.OrdinalIgnoreCase);
                    }
                    return Asse(parti[0], posX) && Asse(parti[1], posY);
                }
                if ((posX >= 0 || posY >= 0) && !celle.Any(Ammessa)) continue;
                var etichettaCategoria = categoria.Length == 0 ? EtichettaSenzaCategoria : categoria;
                Aggiungi(wsCategorie, ref rigaCategoria, c, etichettaCategoria, stato);
                Conta(ecuCategoria, (etichettaCategoria, c.Componente, stato));
                Conta(veicoloCategoria, (etichettaCategoria, c.Veicolo, stato));
            }
        }
        IXLRange CompletaSorgente(IXLWorksheet ws, int riga)
        {
            // Excel richiede almeno una riga di dati per la cache; la riga vuota pesa zero.
            if (riga == 2) { ws.Cell(2, 6).Value = 0; riga++; }
            var range = ws.Range(1, 1, riga - 1, 6);
            range.SetAutoFilter();
            return range;
        }
        var sorgenteCore = CompletaSorgente(wsCore, rigaCore);
        var sorgenteCategorie = CompletaSorgente(wsCategorie, rigaCategoria);
        var elencoStati = stati.Values.ToList();
        var denominatoreEcuCategoria = S("% = combinazioni nello stato / totale combinazioni della stessa componente e categoria (blocco Totale).",
            "% = combinations in the status / total combinations of the same component and category (Total block).",
            "% = combinaisons dans le statut / total des combinaisons de la même componente et catégorie (bloc Total).");
        var denominatoreVeicoloCategoria = S("% = combinazioni nello stato / totale combinazioni dello stesso veicolo e categoria (blocco Totale).",
            "% = combinations in the status / total combinations of the same vehicle and category (Total block).",
            "% = combinaisons dans le statut / total des combinaisons du même véhicule et catégorie (bloc Total).");
        var denominatoreEcuFunzione = S("% = combinazioni nello stato / totale combinazioni della stessa componente e funzionalità (riga Totale).",
            "% = combinations in the status / total combinations of the same component and function (Total row).",
            "% = combinaisons dans le statut / total des combinaisons de la même componente et fonction (ligne Total).");
        ScriviIncrocioBlocchi(wb, NomeLibero(wb, S("Componente x categoria", "Componente x category", "Componente x catégorie")), "Componente", S("Categoria", "Category", "Catégorie"),
            ecuCategoria.Select(k => k.Key.Componente).Distinct().ToList(), ecuCategoria.Select(k => k.Key.Categoria).Distinct().ToList(),
            elencoStati, (x, y, st) => ecuCategoria.GetValueOrDefault((y, x, st)), denominatoreEcuCategoria, mostraNumeri, mostraPercentuali, ct);
        ScriviIncrocioBlocchi(wb, NomeLibero(wb, S("Veicolo x categoria", "Vehicle x category", "Véhicule x catégorie")), S("Veicolo", "Vehicle", "Véhicule"),
            S("Categoria", "Category", "Catégorie"), veicoloCategoria.Select(k => k.Key.Veicolo).Distinct().ToList(),
            veicoloCategoria.Select(k => k.Key.Categoria).Distinct().ToList(), elencoStati,
            (x, y, st) => veicoloCategoria.GetValueOrDefault((y, x, st)), denominatoreVeicoloCategoria, mostraNumeri, mostraPercentuali, ct);
        ScriviEcuFunzionalitaPerStato(wb, NomeLibero(wb, S("Componente e funzionalità", "Componente and function", "Componente et fonction")),
            ecuFunzione, elencoStati, denominatoreEcuFunzione, mostraNumeri, mostraPercentuali, ct);
        if (troncata)
        {
            var avviso = S(
                "Le sorgenti pivot superano il limite di righe di Excel e sono TRONCATE: non sono complete. I fogli di riepilogo (componente x categoria, Veicolo x categoria, componente e funzionalità) sono calcolati sull'universo completo; la pivot personalizzata non è stata generata.",
                "The pivot sources exceed Excel's row limit and are TRUNCATED: they are not complete. The summary sheets (component x category, Vehicle x category, component and function) are computed on the full universe; the custom pivot was not generated.",
                "Les sources de tableaux croisés dépassent la limite de lignes d'Excel et sont TRONQUÉES : elles ne sont pas complètes. Les feuilles de synthèse (componente x catégorie, Véhicule x catégorie, componente et fonction) sont calculées sur l'univers complet ; le tableau personnalisé n'a pas été généré.");
            ScriviAvvisoPivot(wb, S("Avviso pivot", "Pivot warning", "Avertissement pivot"), avviso);
            wsCore.Cell(1, 8).Value = avviso;
            wsCategorie.Cell(1, 8).Value = avviso;
        }
        return (wsCore, wsCategorie, troncata);
    }
}
