using System.Text;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Esito dell'interpretazione di un file di import guasto per un Requisito.</summary>
public sealed record DtcImportRisultato(
    IReadOnlyList<DtcRequisito> Nuove,
    int Duplicate,
    IReadOnlyList<string> Errori,
    IReadOnlyList<string> EcuSconosciute);

/// <summary>Import semplice dei guasti di UN Requisito (6.5.0, punto 7) da CSV o da righe gia lette da Excel:
/// colonne "Guasto" ed "Componente" (il Requisito e quello in editing, non e una colonna) e, dalla 6.5.1, una colonna OPZIONALE "Veicolo"
/// (rilevata solo da intestazione; assente o vuota = voce senza veicolo; con i sottoveicoli dichiarati del requisito il valore
/// deve essere tra quelli, altrimenti la riga e invalida). Puro, nessun I/O sul dato
/// master: l'editor applica il risultato. Intestazione rilevata automaticamente (prima riga con "Guasto"/"Codice");
/// senza intestazione le colonne sono posizionali (A = guasto, B = componente). Delimitatore CSV rilevato tra ';' ',' tab.
/// Una voce gia presente (stessa chiave Veicolo+componente+guasto, case-insensitive) o ripetuta nel file e saltata e contata, mai
/// sovrascritta; qualunque riga invalida (guasto o componente mancante) annulla l'intero import con l'elenco delle righe.</summary>
/// <summary>Riga CSV con il numero di riga reale (1-based) del file.</summary>
public sealed class RigaCsv : List<string>
{
    public int NumeroRiga { get; init; }
}

public static class DtcImportService
{
    public static List<List<string>> LeggiCsv(string contenuto)
    {
        if (string.IsNullOrEmpty(contenuto)) return new List<List<string>>();
        using var reader = new StringReader(contenuto);
        return LeggiCsvStreaming(reader).Where(r => r.Any(v => v.Length > 0)).ToList();
    }

    /// <summary>Delimitatore dalla prima riga, IGNORANDO i separatori dentro le virgolette.</summary>
    private static char RilevaDelimitatore(string primaRiga)
    {
        var conteggi = new Dictionary<char, int> { [';'] = 0, ['\t'] = 0, [','] = 0 };
        var tra = false;
        foreach (var ch in primaRiga)
        {
            if (ch == '"') { tra = !tra; continue; }
            if (!tra && conteggi.ContainsKey(ch)) conteggi[ch]++;
        }
        var migliore = conteggi.OrderByDescending(kv => kv.Value).First();
        return migliore.Value == 0 ? ';' : migliore.Key;
    }

    /// <summary>Lettura CSV in STREAMING (una riga alla volta, nessun caricamento dell'intero file): delimitatore
    /// rilevato dalla prima riga (fuori dalle virgolette) tra ';' ',' tab; virgolette con "" come escape. Le righe
    /// vuote sono restituite come <see cref="RigaCsv"/> vuote (cosi i numeri di riga nei messaggi restano quelli
    /// REALI del file); i consumatori le saltano.</summary>
    public static IEnumerable<List<string>> LeggiCsvStreaming(TextReader reader)
    {
        var primaRiga = reader.ReadLine();
        if (primaRiga == null) yield break;
        primaRiga = primaRiga.TrimStart('\uFEFF');
        var delimitatore = RilevaDelimitatore(primaRiga);

        var campo = new StringBuilder();
        var riga = new RigaCsv { NumeroRiga = 1 };
        var tra = false;
        IEnumerable<char> Caratteri()
        {
            foreach (var ch in primaRiga) yield return ch;
            yield return '\n';
            int x;
            while ((x = reader.Read()) >= 0) yield return (char)x;
        }
        char? precedente = null;
        var lineaFisica = 1; // riga fisica corrente (1-based)
        foreach (var ch in Caratteri())
        {
            if (ch == '\n') lineaFisica++;
            if (tra)
            {
                if (ch == '"') { tra = false; precedente = ch; continue; }
                campo.Append(ch); precedente = ch; continue;
            }
            if (ch == '"')
            {
                if (precedente == '"') campo.Append('"'); // "" dentro un campo: virgoletta letterale
                tra = true; precedente = ch; continue;
            }
            precedente = ch;
            if (ch == delimitatore) { riga.Add(campo.ToString().Trim()); campo.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n')
            {
                riga.Add(campo.ToString().Trim()); campo.Clear();
                if (riga.Any(v => v.Length > 0)) yield return riga;
                else yield return new RigaCsv { NumeroRiga = riga.NumeroRiga }; // vuota: mantiene il conteggio reale
                riga = new RigaCsv { NumeroRiga = lineaFisica };
            }
            else campo.Append(ch);
        }
        riga.Add(campo.ToString().Trim());
        if (riga.Any(v => v.Length > 0)) yield return riga;
    }

    private static bool EIntestazione(string v) =>
        v.Trim().ToLowerInvariant() is "guasto" or "codice" or "codice guasto" or "guasto code" or "code";

    /// <summary>Intestazione della colonna opzionale Veicolo (condivisa con l'import multi-requisito).</summary>
    internal static bool EVeicolo(string v) =>
        v.Trim().ToLowerInvariant() is "veicolo" or "sottoveicolo" or "sotto veicolo" or "veicoli" or "sottoveicoli" or "sub-vehicle" or "subvehicle" or "sub vehicle";

    /// <summary>Interpreta le righe (prima eventuale intestazione inclusa) contro le voci gia presenti.</summary>
    public static DtcImportRisultato Interpreta(
        IReadOnlyList<List<string>> righe, IEnumerable<DtcRequisito> esistenti, IEnumerable<string>? ecuNote = null,
        IReadOnlyCollection<string>? veicoliDichiarati = null)
    {
        var colDtc = 0; var colEcu = 1; var colSv = -1; var inizio = 0;
        if (righe.Count > 0 && righe[0].Any(EIntestazione))
        {
            inizio = 1;
            colDtc = righe[0].FindIndex(EIntestazione);
            var e = righe[0].FindIndex(v => v.Trim().ToLowerInvariant() is "componente" or "centralina");
            colEcu = e >= 0 ? e : (colDtc == 0 ? 1 : 0);
            colSv = righe[0].FindIndex(EVeicolo);
        }

        var viste = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in esistenti) viste.Add(DtcRequisito.Chiave(x.Veicolo, x.Componente, x.Codice));
        var note = ecuNote == null ? null : new HashSet<string>(ecuNote, StringComparer.OrdinalIgnoreCase);

        var nuove = new List<DtcRequisito>();
        var errori = new List<string>();
        var sconosciute = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicate = 0;
        for (var i = inizio; i < righe.Count; i++)
        {
            var r = righe[i];
            var guasto = colDtc < r.Count ? r[colDtc].Trim() : "";
            var componente = colEcu < r.Count ? r[colEcu].Trim() : "";
            var sv = colSv >= 0 && colSv < r.Count ? r[colSv].Trim() : "";
            if (guasto.Length == 0 || componente.Length == 0)
            {
                errori.Add($"Riga {i + 1}: " + (guasto.Length == 0 ? "Guasto mancante" : "Componente mancante"));
                continue;
            }
            if (sv.Length > 0 && veicoliDichiarati != null)
            {
                var canonico = DtcAlberoRegole.Canonico(sv, veicoliDichiarati);
                if (canonico == null) { errori.Add($"Riga {i + 1}: veicolo '{sv}' non dichiarato dal requisito"); continue; }
                sv = canonico;
            }
            if (!viste.Add(DtcRequisito.Chiave(sv, componente, guasto))) { duplicate++; continue; }
            nuove.Add(new DtcRequisito { Codice = guasto, Componente = componente, Veicolo = sv.Length == 0 ? null : sv });
            if (note != null && !note.Contains(componente)) sconosciute.Add(componente);
        }
        return new DtcImportRisultato(nuove, duplicate, errori, sconosciute.ToList());
    }
}
