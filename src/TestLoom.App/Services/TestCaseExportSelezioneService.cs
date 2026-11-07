using ClosedXML.Excel;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Definizione e applicazione della scelta interattiva Test: sezioni,
/// colonne e, opzionalmente, singoli campi degli strumenti configurati nel Master.
/// La selezione e' su ID di colonna stabili (nome del campo Excel) e non richiede
/// una copia modificata dei dati sorgente.</summary>
public static class TestCaseExportSelezioneService
{
    public const string Pagina = "test-list-complete";
    public const string SingoliCampiStrumento = "selezione-campi-strumento";
    public const string SingoliCampiCustom = "selezione-campi-custom";

    private static readonly string[] Fogli =
    [
        "Guida", "Test", "Revisioni", "Operazioni", "Campi strumenti",
        "Definizioni strumenti", "Campi custom", "Punteggi requisiti", "Allegati",
        "Ticket", "Cronologia Test", "Note pianificazione", "Campi"
    ];

    public static ExportDescrittore CreaDescrittore(
        IReadOnlyCollection<StrumentoDiagnostico>? catalogoStrumenti = null,
        IReadOnlyCollection<TestTemplate>? catalogoTemplate = null,
        string? riepilogoFiltri = null,
        IReadOnlyList<ExportFiltro>? filtri = null,
        Action<Action>? applicaFiltriInBlocco = null)
    {
        // La stessa funzione che costruisce l'Excel determina le intestazioni:
        // aggiungere una colonna futura non richiede sincronizzare due cataloghi.
        using var wb = TestCaseCompleteExportService.CreaWorkbook([]);
        var sezioni = new List<ExportSezione>();
        foreach (var nome in Fogli)
        {
            var foglio = wb.Worksheet(nome);
            var colonne = nome == "Guida" ? null : foglio.Row(1).CellsUsed()
                .Select(c => new ExportColonna(c.GetString(), c.GetString())).ToList();
            // Il foglio Campi contiene il JSON COMPLETO e puo' rivelare valori
            // che l'operatore ha deselezionato in altri fogli: va incluso
            // soltanto su richiesta esplicita, non per default nell'export interattivo.
            var raw = nome == "Campi";
            var etichetta = raw
                ? LocalizationService.Instance.Scegli(
                    "Campi (JSON integrale: include anche campi non selezionati)",
                    "Fields (full JSON: includes unselected fields)",
                    "Champs (JSON intégral : inclut les champs non sélectionnés)")
                : nome;
            sezioni.Add(new ExportSezione(nome, etichetta, colonne, PredefinitaAttiva: !raw));
        }

        var campi = (catalogoStrumenti ?? [])
            .SelectMany(s => s.Campi.Select(c => new
            {
                Id = s.Nome + "::" + c.Nome,
                Etichetta = s.Nome + " · " + c.Nome + " (" + c.Ambito + ")"
            }))
            .Where(c => !string.IsNullOrWhiteSpace(c.Id))
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Etichetta, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ExportColonna(c.Id, c.Etichetta)).ToList();
        if (campi.Count > 0)
            sezioni.Add(new ExportSezione(SingoliCampiStrumento,
                LocalizationService.Instance.Scegli("Limita a singoli campi strumento",
                    "Limit to individual instrument fields",
                    "Limiter à certains champs des outils"),
                campi, PredefinitaAttiva: false));

        var custom = (catalogoTemplate ?? [])
            .SelectMany(t => t.Sezioni.SelectMany(s => s.Campi)
                .Concat(t.CampiPianificazione).Concat(t.CampiRiferimenti))
            .Where(c => c.Tipo == TipoCampoTemplate.CampoLibero
                || c.Tipo == TipoCampoTemplate.CampoSistema
                && !string.IsNullOrWhiteSpace(c.CampoSistemaChiave)
                && !CampiSistema.Chiavi.Contains(c.CampoSistemaChiave)
                && !CampiSistema.ChiaviPianificazione.Contains(c.CampoSistemaChiave)
                && !CampiSistema.ChiaviRiferimenti.Contains(c.CampoSistemaChiave))
            .Select(c => new
            {
                Id = c.Tipo == TipoCampoTemplate.CampoLibero ? c.Uid.ToString() : c.CampoSistemaChiave!,
                Etichetta = string.IsNullOrWhiteSpace(c.Nome)
                    ? (c.Tipo == TipoCampoTemplate.CampoLibero ? c.Uid.ToString() : c.CampoSistemaChiave!)
                    : c.Nome
            })
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Etichetta, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ExportColonna(c.Id, c.Etichetta)).ToList();
        if (custom.Count > 0)
            sezioni.Add(new ExportSezione(SingoliCampiCustom,
                LocalizationService.Instance.Scegli("Limita a singoli campi personalizzati",
                    "Limit to individual custom fields",
                    "Limiter à certains champs personnalisés"),
                custom, PredefinitaAttiva: false));

        return new ExportDescrittore(Pagina,
            LocalizationService.Instance.Scegli("Esporta Test · campi e filtri",
                "Export Tests · fields and filters",
                "Exporter les tests · champs et filtres"),
            sezioni,
            riepilogoFiltri ?? "", filtri, ApplicaFiltriInBlocco: applicaFiltriInBlocco);
    }

    public static IReadOnlySet<string>? ChiaviStrumentoSelezionate(
        ExportConfigurazione opzioni, ExportDescrittore descrittore)
    {
        if (!opzioni.HaSezione(SingoliCampiStrumento)) return null;
        var sezione = descrittore.Sezioni.FirstOrDefault(s => s.Id == SingoliCampiStrumento);
        return sezione == null ? null
            : ExportSelezione.ColonneEsportate(opzioni, sezione)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlySet<string>? ChiaviCustomSelezionate(
        ExportConfigurazione opzioni, ExportDescrittore descrittore)
    {
        if (!opzioni.HaSezione(SingoliCampiCustom)) return null;
        var sezione = descrittore.Sezioni.FirstOrDefault(s => s.Id == SingoliCampiCustom);
        return sezione == null ? null
            : ExportSelezione.ColonneEsportate(opzioni, sezione)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Registra nello stesso file i criteri effettivamente confermati.
    /// La selezione e' separata dalle impostazioni correnti dell'app: il workbook
    /// conserva il contesto di consultazione anche quando in seguito cambiano i filtri.</summary>
    public static void ScriviContesto(
        XLWorkbook workbook, ExportConfigurazione? opzioni,
        string riepilogoFiltri, int numeroTest)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        var guida = workbook.Worksheet("Guida");
        guida.Cell(19, 1).Value = "Modalita";
        guida.Cell(19, 2).Value = opzioni == null || opzioni.IgnoraFiltri
            ? "Tutti i Test attivi autorizzati (nessun filtro utente)"
            : "Test attivi autorizzati corrispondenti ai filtri confermati";
        guida.Cell(20, 1).Value = "Filtri confermati";
        // Non trasformare una ricerca in formula Excel e non oltrepassare il limite di cella.
        guida.Cell(20, 2).Value = "Filtri: " + (riepilogoFiltri ?? "")[..Math.Min(
            (riepilogoFiltri ?? "").Length, 30_000)];
        guida.Cell(21, 1).Value = "Totale Test";
        guida.Cell(21, 2).Value = numeroTest;
        guida.Cell(22, 1).Value = "Fogli inclusi";
        guida.Cell(22, 2).Value = opzioni == null ? "Tutti (export legacy)"
            : string.Join(", ", opzioni.Sezioni.Where(nome => Fogli.Contains(nome, StringComparer.OrdinalIgnoreCase)));
        guida.Cell(23, 1).Value = "Selezione colonne";
        guida.Cell(23, 2).Value = opzioni == null ? "Tutte"
            : "Consultare le intestazioni di ciascun foglio: sono presenti solo le colonne selezionate.";
        guida.Cell(24, 1).Value = "Campi singoli";
        guida.Cell(24, 2).Value = opzioni == null ? "Tutti"
            : (opzioni.HaSezione(SingoliCampiStrumento)
                ? "Campi degli strumenti selezionati singolarmente"
                : "Tutti i campi degli strumenti")
                + " · " + (opzioni.HaSezione(SingoliCampiCustom)
                    ? "campi personalizzati selezionati singolarmente"
                    : "tutti i campi personalizzati");
    }

    /// <summary>Elimina i fogli non richiesti e le colonne non selezionate.
    /// Si applica al workbook finito: tutti i fogli e le intestazioni condividono
    /// l'unica implementazione di TestCaseCompleteExportService.</summary>
    public static void Applica(XLWorkbook wb, ExportConfigurazione opzioni)
    {
        ArgumentNullException.ThrowIfNull(wb);
        ArgumentNullException.ThrowIfNull(opzioni);
        if (!Fogli.Any(opzioni.HaSezione))
            throw new ArgumentException("Selezionare almeno un foglio reale dell'Excel.", nameof(opzioni));

        // I fogli estesi dell'ExcelExportTableWriter hanno il nome del foglio
        // principale seguito da " dettagli estesi"; se escluso il principale,
        // il dettaglio associato non deve rimanere orfano.
        foreach (var foglio in wb.Worksheets.ToList())
        {
            var origine = Fogli.FirstOrDefault(nome =>
                string.Equals(foglio.Name, nome, StringComparison.OrdinalIgnoreCase)
                || foglio.Name.StartsWith(nome + " (", StringComparison.OrdinalIgnoreCase)
                || foglio.Name.StartsWith(nome + " dettagli", StringComparison.OrdinalIgnoreCase));
            if (origine == null) continue;
            if (!opzioni.HaSezione(origine))
            {
                wb.Worksheets.Delete(foglio.Name);
                continue;
            }
            var scelte = opzioni.ColonneDi(origine);
            if (scelte == null) continue;
            // Quando si toglie una colonna, ExcelExportTableWriter puo' averne
            // scritto il valore esteso in un foglio di dettaglio. Quel contenuto
            // NON deve sopravvivere alla selezione: eliminare anche le righe
            // tecniche che si riferiscono a colonne escluse.
            if (foglio.Name.StartsWith(origine + " dettagli", StringComparison.OrdinalIgnoreCase))
            {
                var consentite = scelte.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var ultime = foglio.LastRowUsed()?.RowNumber() ?? 1;
                var righeDaEliminare = new List<int>();
                for (var r = 2; r <= ultime; r++)
                    if (!consentite.Contains(foglio.Cell(r, 2).GetString()))
                        righeDaEliminare.Add(r);
                EliminaRigheConsecutive(foglio, righeDaEliminare);
                continue;
            }
            if (origine == "Guida" || !string.Equals(foglio.Name, origine, StringComparison.OrdinalIgnoreCase)
                && !foglio.Name.StartsWith(origine + " (", StringComparison.OrdinalIgnoreCase))
                continue;

            var colonne = foglio.Row(1).CellsUsed().ToList();
            var set = scelte.ToHashSet(StringComparer.OrdinalIgnoreCase);
            // ClosedXML sconsiglia Column.Delete ripetuto in un loop sui grandi
            // fogli: elimina in blocchi consecutivi, partendo da destra per
            // conservare gli indici originali e limitare gli spostamenti.
            var daEliminare = Enumerable.Range(1, colonne.Count)
                .Where(i => !set.Contains(colonne[i - 1].GetString())).ToArray();
            for (var pos = daEliminare.Length - 1; pos >= 0;)
            {
                var fine = daEliminare[pos--];
                var inizio = fine;
                while (pos >= 0 && daEliminare[pos] == inizio - 1)
                {
                    inizio--;
                    pos--;
                }
                foglio.Columns(inizio, fine).Delete();
            }
        }
    }

    /// <summary>Elimina intervalli contigui da destra in modo che gli indici
    /// non cambino durante la scansione; applicato solo ai fogli di dettaglio
    /// di testo per evitare residui di colonne deselezionate.</summary>
    private static void EliminaRigheConsecutive(IXLWorksheet foglio, IReadOnlyList<int> righe)
    {
        for (var pos = righe.Count - 1; pos >= 0;)
        {
            var fine = righe[pos--];
            var inizio = fine;
            while (pos >= 0 && righe[pos] == inizio - 1)
            {
                inizio--;
                pos--;
            }
            foglio.Rows(inizio, fine).Delete();
        }
    }
}
