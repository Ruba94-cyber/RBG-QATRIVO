using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Una voce guasto importata, destinata a un requisito.</summary>
public sealed record DtcImportVoce(string RequisitoId, DtcRequisito Voce);

/// <summary>Report di un import guasto con colonna Requisito (e colonna opzionale Veicolo, 6.5.1): importate / duplicate / scartate (con motivo) / avvisi.</summary>
public sealed record DtcImportReport(
    IReadOnlyList<DtcImportVoce> Importate,
    int Duplicate,
    IReadOnlyList<string> Scartate,
    IReadOnlyList<string> EcuSconosciute,
    IReadOnlyList<string>? Avvisi = null)
{
    /// <summary>True se l'import e stato rifiutato per intero (struttura del file non valida, es. colonna mancante).</summary>
    public bool Rifiutato { get; init; }

    /// <summary>Quanti dei <see cref="Duplicate"/> sono guasto gia presenti nel requisito (il resto sono ripetizioni dentro il file).</summary>
    public int GiaPresenti { get; init; }

    public string Riepilogo =>
        $"Importate {Importate.Count}, duplicate {Duplicate}, scartate {Scartate.Count}" +
        (Scartate.Count > 0 ? ": " + string.Join("; ", Scartate.Take(8)) + (Scartate.Count > 8 ? $"; ... (+{Scartate.Count - 8})" : "") : "");
}

/// <summary>Conteggi dell'anteprima di un import guasto (UX-4), ricavati dal report senza toccare l'editor.</summary>
public sealed record AnteprimaDtcImport(int RequisitiToccati, int DtcNuovi, int GiaPresenti, int DuplicatiNelFile, int RigheConErrore, IReadOnlyList<string> PrimiErrori)
{
    public const int MaxErroriMostrati = 5;

    public static AnteprimaDtcImport Da(DtcImportReport report) => new(
        RequisitiToccati: report.Importate.Select(v => v.RequisitoId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        DtcNuovi: report.Importate.Count,
        GiaPresenti: report.GiaPresenti,
        DuplicatiNelFile: Math.Max(0, report.Duplicate - report.GiaPresenti),
        RigheConErrore: report.Scartate.Count,
        PrimiErrori: report.Scartate.Take(MaxErroriMostrati).ToList());
}

public static class DtcImportRequisitoService
{
    /// <summary>Stato per requisito durante l'interpretazione: guasto gia presenti nel requisito e guasto nuovi gia accettati da questo file.</summary>
    private sealed record StatoRequisitoImport(HashSet<string> Esistenti, HashSet<string> Nuove);

    /// <summary>Soglia MORBIDA (nessun limite rigido): oltre questo numero di guasto per requisito il salvataggio diventa
    /// costoso, perche l'elenco vive nel JSON del Requisito e ogni salvataggio/journal riscrive l'intero requisito.</summary>
    public const int SogliaAvvisoDtcPerRequisito = 5000;

    private static bool EReq(string v) => v.Trim().ToLowerInvariant() is "requisito" or "requirement" or "req" or "id requisito";
    private static bool EDtc(string v) => v.Trim().ToLowerInvariant() is "guasto" or "codice" or "codice guasto" or "guasto code" or "code";
    private static bool EEcu(string v) => v.Trim().ToLowerInvariant() is "componente" or "centralina";

    private sealed record Colonne(int Req, int Guasto, int Componente, bool HaReq, bool PrimaEIntestazione, string? Errore, int Sv = -1);

    /// <summary>Deduce le colonne dalla prima riga. Con intestazione le colonne si cercano per nome e una colonna
    /// guasto/componente mancante e un ERRORE esplicito (mai un ripiego sulla colonna 0, che potrebbe essere il Requisito).
    /// Senza intestazione: ordine posizionale Requisito, guasto, componente (con sole 2 colonne: guasto, componente).</summary>
    private static Colonne Rileva(List<string>? primo)
    {
        if (primo != null && primo.Any(v => EReq(v) || EDtc(v) || EEcu(v) || DtcImportService.EVeicolo(v)))
        {
            var req = primo.FindIndex(EReq); var guasto = primo.FindIndex(EDtc); var componente = primo.FindIndex(EEcu);
            var sv = primo.FindIndex(DtcImportService.EVeicolo); // colonna OPZIONALE (6.5.1)
            if (guasto < 0) return new Colonne(req, guasto, componente, req >= 0, true, "colonna guasto mancante nell'intestazione", sv);
            if (componente < 0) return new Colonne(req, guasto, componente, req >= 0, true, "colonna componente mancante nell'intestazione", sv);
            return new Colonne(req, guasto, componente, req >= 0, true, null, sv);
        }
        if (primo != null && primo.Count < 3) return new Colonne(-1, 0, 1, false, false, null);
        return new Colonne(0, 1, 2, true, false, null);
    }

    private static int NumeroRiga(List<string> r, int progressivo) => r is RigaCsv c ? c.NumeroRiga : progressivo;

    private static IEnumerable<List<string>> ConPrimo(List<string>? primo, bool saltaPrimo, IEnumerator<List<string>> it)
    {
        if (primo != null && !saltaPrimo) yield return primo;
        while (it.MoveNext()) yield return it.Current;
    }

    private static bool Vuota(List<string> r) => r.Count == 0 || r.All(v => string.IsNullOrWhiteSpace(v));

    private static List<string>? PrimaNonVuota(IEnumerator<List<string>> it)
    {
        while (it.MoveNext()) if (!Vuota(it.Current)) return it.Current;
        return null;
    }

    /// <summary>Prima passata (streaming): id dei requisiti citati dal file (distinti), per risolverli sul catalogo PRIMA
    /// dell'interpretazione. Se la struttura e invalida ritorna l'errore.</summary>
    public static (HashSet<string> Ids, string? Errore) RaccogliRequisiti(
        IEnumerable<List<string>> righe, string? requisitoPredefinito, CancellationToken ct = default)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var it = righe.GetEnumerator();
        var primo = PrimaNonVuota(it);
        var col = Rileva(primo);
        if (col.Errore != null) return (ids, col.Errore);
        if (!col.HaReq)
        {
            if (string.IsNullOrWhiteSpace(requisitoPredefinito)) return (ids, "colonna Requisito mancante e nessun requisito in modifica");
            ids.Add(requisitoPredefinito.Trim());
            return (ids, null);
        }
        var n = 0;
        foreach (var r in ConPrimo(primo, col.PrimaEIntestazione, it))
        {
            if ((++n & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            var v = col.Req < r.Count ? r[col.Req].Trim() : "";
            if (v.Length > 0) ids.Add(v);
        }
        return (ids, null);
    }

    /// <summary>Interpreta righe (Requisito, guasto, componente). SEMANTICA: struttura non valida (colonna mancante) = import
    /// RIFIUTATO per intero con motivo; righe invalide (campo mancante, requisito inesistente/non Extra) sono SCARTATE e
    /// riportate con il numero di riga REALE del file; le valide sono importate, i duplicati contati. Nulla e
    /// persistito qui: l'editor applica le voci in modifica e il Salva resta esplicito (con conferma se ci sono
    /// scartate). Avviso morbido oltre <see cref="SogliaAvvisoDtcPerRequisito"/> guasto per requisito.</summary>
    /// <param name="esistentiPerRequisito">Voci gia presenti per id requisito; null = requisito inesistente/non Extra/non ammesso.</param>
    public static DtcImportReport Interpreta(
        IEnumerable<List<string>> righe,
        Func<string, IReadOnlyList<DtcRequisito>?> esistentiPerRequisito,
        string? requisitoPredefinito = null,
        IEnumerable<string>? ecuNote = null,
        IProgress<int>? progresso = null,
        CancellationToken ct = default,
        Func<string, IReadOnlyCollection<string>?>? veicoliDichiarati = null)
    {
        using var it = righe.GetEnumerator();
        var primo = PrimaNonVuota(it);
        var col = Rileva(primo);
        if (col.Errore != null)
            return new DtcImportReport(Array.Empty<DtcImportVoce>(), 0, new[] { col.Errore }, Array.Empty<string>()) { Rifiutato = true };
        if (!col.HaReq && string.IsNullOrWhiteSpace(requisitoPredefinito))
            return new DtcImportReport(Array.Empty<DtcImportVoce>(), 0, new[] { "colonna Requisito mancante e nessun requisito in modifica" }, Array.Empty<string>()) { Rifiutato = true };

        var note = ecuNote == null ? null : new HashSet<string>(ecuNote, StringComparer.OrdinalIgnoreCase);
        var viste = new Dictionary<string, StatoRequisitoImport>(StringComparer.OrdinalIgnoreCase);
        var nuovePerReq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var importate = new List<DtcImportVoce>();
        var scartate = new List<string>();
        var sconosciute = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicate = 0;
        var giaPresenti = 0;
        var progressivo = col.PrimaEIntestazione ? 1 : 0;
        foreach (var r in ConPrimo(primo, col.PrimaEIntestazione, it))
        {
            progressivo++;
            if ((progressivo & 0x3FF) == 0) { ct.ThrowIfCancellationRequested(); progresso?.Report(progressivo); }
            if (Vuota(r)) continue;
            var numero = NumeroRiga(r, progressivo);
            string Cella(int c) => c >= 0 && c < r.Count ? r[c].Trim() : "";
            var req = col.HaReq ? Cella(col.Req) : requisitoPredefinito!.Trim();
            var guasto = Cella(col.Guasto); var componente = Cella(col.Componente); var sv = Cella(col.Sv);
            if (req.Length == 0) { scartate.Add($"riga {numero}: Requisito mancante"); continue; }
            if (guasto.Length == 0) { scartate.Add($"riga {numero}: guasto mancante"); continue; }
            if (componente.Length == 0) { scartate.Add($"riga {numero}: componente mancante"); continue; }
            var esistenti = esistentiPerRequisito(req);
            if (esistenti == null) { scartate.Add($"riga {numero}: requisito '{req}' inesistente, non Extra o non ammesso qui"); continue; }
            if (sv.Length > 0 && veicoliDichiarati?.Invoke(req) is { } dichiarati)
            {
                var canonico = DtcAlberoRegole.Canonico(sv, dichiarati);
                if (canonico == null) { scartate.Add($"riga {numero}: veicolo '{sv}' non dichiarato dal requisito '{req}'"); continue; }
                sv = canonico;
            }
            if (!viste.TryGetValue(req, out var stato))
            {
                var gia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var x in esistenti) gia.Add(DtcRequisito.Chiave(x.Veicolo, x.Componente, x.Codice));
                viste[req] = stato = new StatoRequisitoImport(gia, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                nuovePerReq[req] = esistenti.Count;
            }
            var chiave = DtcRequisito.Chiave(sv, componente, guasto);
            if (stato.Esistenti.Contains(chiave)) { duplicate++; giaPresenti++; continue; }
            if (!stato.Nuove.Add(chiave)) { duplicate++; continue; }
            importate.Add(new DtcImportVoce(req, new DtcRequisito { Codice = guasto, Componente = componente, Veicolo = sv.Length == 0 ? null : sv }));
            nuovePerReq[req]++;
            if (note != null && !note.Contains(componente)) sconosciute.Add(componente);
        }
        progresso?.Report(progressivo);
        var avvisi = nuovePerReq.Where(kv => kv.Value > SogliaAvvisoDtcPerRequisito)
            .Select(kv => AvvisoVolume(kv.Key, kv.Value)).ToList();
        return new DtcImportReport(importate, duplicate, scartate, sconosciute.ToList(), avvisi) { GiaPresenti = giaPresenti };
    }

    /// <summary>Testo dell'avviso morbido sul volume guasto di un requisito (nessun limite rigido).</summary>
    public static string AvvisoVolume(string requisitoId, int n) =>
        $"Il requisito {requisitoId} ha {n} guasto (oltre {SogliaAvvisoDtcPerRequisito}): i guasti sono salvati nel JSON del requisito, quindi ogni salvataggio " +
        "(e la sincronizzazione tra PC) riscrive tutto l'elenco e diventa piu lento al crescere del numero. Nessun limite e imposto.";
}
