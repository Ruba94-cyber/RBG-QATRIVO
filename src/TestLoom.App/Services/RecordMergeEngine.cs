using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

public enum RecordMergeKind { TestCase, NonConformita, ChangeRequest, Segnalazione, Generico }

public enum RecordFieldChoice { Mia, Loro, Manuale }

public enum RecordEditKind { Nessuno, Testo, Numero, Booleano, ElencoTesto }

/// <summary>Un campo del record con le due versioni gia' in forma leggibile: l'utente non vede mai JSON.</summary>
public sealed class RecordMergeField
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public JsonNode? Mia { get; init; }
    public JsonNode? Loro { get; init; }
    public bool Differs { get; init; }
    public RecordEditKind EditKind { get; init; }
    public string MiaTesto { get; init; } = "";
    public string LoroTesto { get; init; } = "";
    /// <summary>Valore grezzo (non decorato) da cui parte la modifica manuale.</summary>
    public string MiaModifica { get; init; } = "";
    public string LoroModifica { get; init; } = "";
}

/// <summary>Scelte dell'utente: per ogni campo diverso, la versione tenuta oppure un valore scritto a mano.</summary>
public sealed class RecordMergeChoices
{
    public Dictionary<string, RecordFieldChoice> Scelte { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ValoriManuali { get; } = new(StringComparer.Ordinal);

    public static RecordMergeChoices Tutti(RecordFieldChoice scelta, RecordMergePlan plan)
    {
        var c = new RecordMergeChoices();
        foreach (var f in plan.Differences) c.Scelte[f.Name] = scelta;
        return c;
    }
}

public sealed record RecordMergePreviewLine(string Name, string Label, string Testo, string Origine, bool Valida, string? Errore, bool Cambiata);

public sealed record RecordMergeResult(JsonObject Json, object? Model, byte[] Bytes);

public sealed class RecordMergeException : InvalidOperationException
{
    public RecordMergeException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record RecordMergePrepared(RecordMergePlan? Plan, string? MotivoNonConfrontabile)
{
    public bool Confrontabile => Plan != null;
}

/// <summary>
/// Motore di confronto/unione a DUE versioni (mia = canonica corrente, altra = copia/ramo in conflitto) su record JSON.
/// Opera sul record reale (deserializzato nel suo modello): il risultato e' validato come NC/CR/Segnalazione/Test prima di
/// essere restituito. I campi tecnici (Uid, revisione, ultima modifica, schema, stampi _ditm*) non sono mai oggetto di scelta:
/// restano quelli della versione corrente; il Changelog e' l'unione di entrambe le versioni (nessuna voce persa).
/// Nessuna decisione implicita: ogni campo diverso richiede una scelta esplicita (mia / altra / valore manuale).
/// </summary>
public static class RecordMergeEngine
{
    private static readonly HashSet<string> CampiTecnici = new(StringComparer.Ordinal)
    {
        "SchemaVersion", "Uid", "RevisioneDati", "UltimaModifica", "ModificatoDa", "Changelog"
    };

    private static bool Tecnico(string name) => CampiTecnici.Contains(name) || name.StartsWith("_ditm", StringComparison.OrdinalIgnoreCase);

    public static Type? ModelType(RecordMergeKind kind) => kind switch
    {
        RecordMergeKind.TestCase => typeof(TestCase),
        RecordMergeKind.NonConformita => typeof(NonConformita),
        RecordMergeKind.ChangeRequest => typeof(ChangeRequest),
        RecordMergeKind.Segnalazione => typeof(SegnalazioneDaClassificare),
        _ => null
    };

    public static RecordMergePrepared Prepare(RecordMergeKind kind, byte[] mine, byte[] theirs)
    {
        ArgumentNullException.ThrowIfNull(mine);
        ArgumentNullException.ThrowIfNull(theirs);
        if (!TryParseObject(mine, out var mio, out var erroreMio))
            return new(null, "La mia versione non e' leggibile (" + erroreMio + ").");
        if (!TryParseObject(theirs, out var loro, out var erroreLoro))
            return new(null, "L'altra versione non e' leggibile (" + erroreLoro + ").");
        if (IsTombstone(mio!) || IsTombstone(loro!))
            return new(null, "Una delle due versioni e' marcata come eliminata: non confrontabile campo per campo.");

        var uidMio = mio!["Uid"]?.ToString();
        var uidLoro = loro!["Uid"]?.ToString();
        if (!string.IsNullOrEmpty(uidMio) && !string.IsNullOrEmpty(uidLoro) &&
            !string.Equals(uidMio, uidLoro, StringComparison.OrdinalIgnoreCase))
            return new(null, "Le due versioni appartengono a record diversi.");

        var type = ModelType(kind);
        if (type != null)
        {
            foreach (var (node, etichetta) in new[] { (mio, "mia"), (loro, "altra") })
            {
                var problema = ProblemaModello(type, node!);
                if (problema != null) return new(null, $"Nella versione {etichetta}: {problema}");
            }
        }

        // Test: le REV (Esecuzioni) si UNISCONO invece di scegliere l'intera lista di una parte (nessuna operazione persa).
        string? nota = null;
        TestRevisionMergePlan? pianoRev = null;
        if (kind == RecordMergeKind.TestCase)
        {
            pianoRev = TestRevisionMerge.Pianifica(mio!, loro!);
            if (pianoRev is { Unibile: true })
            {
                var unite = pianoRev.ToJsonArray();
                mio!["Esecuzioni"] = unite.DeepClone();
                loro!["Esecuzioni"] = unite.DeepClone();
                nota = pianoRev.Nota;
            }
            else nota = TestRevisionMerge.DescriviConflitto(mio!, loro!); // non ordinabili: resta la scelta, ben spiegata
        }
        var campi = new List<RecordMergeField>();
        foreach (var name in mio!.Select(x => x.Key).Union(loro!.Select(x => x.Key)).Where(n => !Tecnico(n)))
        {
            // Proprieta' calcolate (es. NumeroRevisione = numero di REV): non si scelgono, si ricalcolano dal contenuto unito.
            if (type?.GetProperty(name) is { CanWrite: false }) continue;
            mio.TryGetPropertyValue(name, out var vMio);
            loro.TryGetPropertyValue(name, out var vLoro);
            var differs = !JsonNode.DeepEquals(vMio, vLoro);
            var kindEdit = EditKindOf(type, name, vMio, vLoro);
            campi.Add(new RecordMergeField
            {
                Name = name,
                Label = RecordValueFormatter.Label(name),
                Mia = vMio?.DeepClone(),
                Loro = vLoro?.DeepClone(),
                Differs = differs,
                EditKind = kindEdit,
                MiaTesto = RecordValueFormatter.Format(vMio),
                LoroTesto = RecordValueFormatter.Format(vLoro),
                MiaModifica = RecordValueFormatter.EditText(vMio, kindEdit),
                LoroModifica = RecordValueFormatter.EditText(vLoro, kindEdit)
            });
        }
        return new(new RecordMergePlan(kind, mio, loro, campi) { Nota = nota, RevisioniUnite = pianoRev is { Unibile: true } ? pianoRev : null }, null);
    }

    /// <summary>Null se il contenuto e' un record valido del tipo; altrimenti una frase in italiano chiaro che indica il campo
    /// e il valore problematico (es. "Il campo Priorità interna ha un valore non valido: High"). Per i kind senza modello
    /// controlla solo che il contenuto sia un oggetto leggibile.</summary>
    public static string? Problema(RecordMergeKind kind, byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return "il contenuto è vuoto o mancante.";
        if (!TryParseObject(bytes, out var obj, out var errore)) return $"il file non è leggibile ({errore}).";
        if (IsTombstone(obj!)) return "la versione è marcata come eliminata.";
        var type = ModelType(kind);
        return type == null ? null : ProblemaModello(type, obj!);
    }

    private static string? ProblemaModello(Type type, JsonObject node)
    {
        try
        {
            _ = node.Deserialize(type, JsonIo.Options);
            return null;
        }
        catch (JsonException ex)
        {
            var campo = (ex.Path ?? "").TrimStart('$', '.').Split('.', '[')[0];
            if (campo.Length > 0 && node.TryGetPropertyValue(campo, out var valore))
            {
                var testo = valore?.ToString() ?? "(vuoto)";
                if (testo.Length > 80) testo = testo[..80] + "…";
                return $"il campo {RecordValueFormatter.Label(campo)} ha un valore non valido: {testo}.";
            }
            return "il contenuto non corrisponde al tipo di record (un campo ha un valore non valido).";
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return "il contenuto non corrisponde al tipo di record.";
        }
    }

    private static bool TryParseObject(byte[] bytes, out JsonObject? obj, out string error)
    {
        obj = null;
        error = "";
        if (bytes.Length == 0) { error = "file vuoto"; return false; }
        try
        {
            obj = JsonNode.Parse(bytes) as JsonObject;
            if (obj == null) { error = "il contenuto non e' un record"; return false; }
            return true;
        }
        catch (JsonException) { error = "contenuto danneggiato"; return false; }
    }

    private static bool IsTombstone(JsonObject o) =>
        o.TryGetPropertyValue(WorkflowTransactionVisibilityService.DeletedProperty, out var d) &&
        d is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static RecordEditKind EditKindOf(Type? modelType, string name, JsonNode? a, JsonNode? b)
    {
        var prop = modelType?.GetProperty(name);
        if (prop != null)
        {
            var t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (t.IsEnum) return RecordEditKind.Nessuno; // valori ammessi solo da elenco: scelta fra le due versioni
            if (t == typeof(string)) return RecordEditKind.Testo;
            if (t == typeof(bool)) return RecordEditKind.Booleano;
            if (t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(decimal)) return RecordEditKind.Numero;
            if (t == typeof(List<string>)) return RecordEditKind.ElencoTesto;
            if (t == typeof(DateTime) || t == typeof(DateTime?) || t == typeof(Guid)) return RecordEditKind.Nessuno;
            return RecordEditKind.Nessuno;
        }
        // Senza modello (cataloghi master): deduzione dal tipo del valore.
        var sample = a ?? b;
        if (sample is JsonValue jv)
        {
            if (jv.TryGetValue<bool>(out _)) return RecordEditKind.Booleano;
            if (jv.TryGetValue<string>(out var s)) return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) && s!.Contains('T')
                ? RecordEditKind.Nessuno : RecordEditKind.Testo;
            return RecordEditKind.Numero;
        }
        if (sample is JsonArray arr && arr.All(x => x is JsonValue v && v.TryGetValue<string>(out _))) return RecordEditKind.ElencoTesto;
        return RecordEditKind.Nessuno;
    }

    internal static bool TryParseManual(RecordEditKind kind, string text, JsonNode? riferimento, out JsonNode? node, out string? errore)
    {
        node = null;
        errore = null;
        text ??= "";
        switch (kind)
        {
            case RecordEditKind.Testo:
                node = text.Length == 0 && riferimento == null ? null : JsonValue.Create(text);
                return true;
            case RecordEditKind.Numero:
                var norm = text.Trim().Replace(',', '.');
                if (norm.Length == 0) { node = null; return true; }
                if (long.TryParse(norm, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) { node = JsonValue.Create(l); return true; }
                if (double.TryParse(norm, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { node = JsonValue.Create(d); return true; }
                errore = "Inserire un numero valido.";
                return false;
            case RecordEditKind.Booleano:
                var t = text.Trim().ToLowerInvariant();
                if (t is "true" or "si" or "sì" or "yes") { node = JsonValue.Create(true); return true; }
                if (t is "false" or "no") { node = JsonValue.Create(false); return true; }
                errore = "Inserire Si oppure No.";
                return false;
            case RecordEditKind.ElencoTesto:
                var arr = new JsonArray();
                foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    arr.Add(JsonValue.Create(line));
                node = arr;
                return true;
            default:
                errore = "Questo campo non e' modificabile a mano: scegliere una delle due versioni.";
                return false;
        }
    }
}

public sealed class RecordMergePlan
{
    private readonly JsonObject _mine;
    private readonly JsonObject _theirs;

    internal RecordMergePlan(RecordMergeKind kind, JsonObject mine, JsonObject theirs, IReadOnlyList<RecordMergeField> campi)
    {
        Kind = kind;
        _mine = mine;
        _theirs = theirs;
        Fields = campi;
    }

    public RecordMergeKind Kind { get; }
    /// <summary>Informazione sulle unioni automatiche gia' applicate (es. REV unite e rinumerate), o null.</summary>
    public string? Nota { get; init; }
    /// <summary>Test: piano di unione delle REV gia' applicato (REV risultanti, mappe vecchio->nuovo numero per copia). Null se non
    /// applicabile o se le REV richiedono una decisione manuale (allora compaiono fra le <see cref="DecisioniManuali"/>).</summary>
    public TestRevisionMergePlan? RevisioniUnite { get; init; }
    /// <summary>Elenco minimale dei soli punti che l'utente deve decidere (campi diversi fra le due copie), con valore, autore e data
    /// di ciascuna copia. Tutto il resto e' gia' risolto in automatico.</summary>
    public IReadOnlyList<DecisioneManuale> DecisioniManuali => Differences.Select(f => new DecisioneManuale(
        f.Name, f.Label, f.MiaTesto, f.LoroTesto,
        _mine["ModificatoDa"]?.ToString() ?? "", DataDi(_mine), _theirs["ModificatoDa"]?.ToString() ?? "", DataDi(_theirs))).ToList();

    private static DateTime? DataDi(JsonObject o) =>
        DateTime.TryParse(o["UltimaModifica"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
    public IReadOnlyList<RecordMergeField> Fields { get; }
    public IReadOnlyList<RecordMergeField> Differences => Fields.Where(f => f.Differs).ToList();
    public bool HasDifferences => Fields.Any(f => f.Differs);

    private (JsonNode? Node, string Origine, string? Errore) Resolve(RecordMergeField f, RecordMergeChoices c)
    {
        if (!f.Differs) return (f.Mia, "Uguale", null);
        if (!c.Scelte.TryGetValue(f.Name, out var scelta)) return (f.Mia, "Da scegliere", "Scegliere la versione da tenere.");
        switch (scelta)
        {
            case RecordFieldChoice.Mia: return (f.Mia, "Mia", null);
            case RecordFieldChoice.Loro: return (f.Loro, "Altra", null);
            default:
                if (!c.ValoriManuali.TryGetValue(f.Name, out var testo)) testo = f.MiaModifica;
                if (!RecordMergeEngine.TryParseManual(f.EditKind, testo, f.Mia ?? f.Loro, out var node, out var errore))
                    return (f.Mia, "Manuale", errore);
                return (node, "Manuale", null);
        }
    }

    /// <summary>Anteprima del risultato campo per campo, sempre in forma leggibile.</summary>
    public IReadOnlyList<RecordMergePreviewLine> Preview(RecordMergeChoices choices)
    {
        var righe = new List<RecordMergePreviewLine>();
        foreach (var f in Fields)
        {
            var (node, origine, errore) = Resolve(f, choices);
            righe.Add(new RecordMergePreviewLine(f.Name, f.Label, RecordValueFormatter.Format(node), origine,
                errore == null, errore, f.Differs));
        }
        return righe;
    }

    public bool IsComplete(RecordMergeChoices choices) => Preview(choices).All(r => r.Valida);

    /// <summary>Costruisce il record risultante e lo valida con il suo modello. Lancia <see cref="RecordMergeException"/> se
    /// manca una scelta, un valore manuale non e' valido o il risultato non e' un record valido.</summary>
    public RecordMergeResult Build(RecordMergeChoices choices, string operatore)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var merged = (JsonObject)_mine.DeepClone();
        var cambiati = new List<string>();
        foreach (var f in Fields)
        {
            var (node, _, errore) = Resolve(f, choices);
            if (errore != null) throw new RecordMergeException($"{f.Label}: {errore}");
            if (!f.Differs) continue;
            if (node == null) merged.Remove(f.Name); else merged[f.Name] = node.DeepClone();
            cambiati.Add(f.Label);
        }

        if (_mine.ContainsKey("Changelog") || _theirs.ContainsKey("Changelog"))
        {
            var unione = UnisciChangelog(_mine["Changelog"] as JsonArray, _theirs["Changelog"] as JsonArray);
            if (cambiati.Count > 0 || Nota != null)
            {
                var voce = new ChangelogEntry
                {
                    Data = DateTime.Now,
                    Operatore = operatore,
                    Campo = "risoluzione conflitto sincronizzazione",
                    ValorePrecedente = "campi in conflitto: " + string.Join(", ", Differences.Select(d => d.Label)),
                    ValoreNuovo = "scelte dal Master" +
                        (Differences.Count == 0 ? "" : " (" + string.Join(", ", Differences.Select(d =>
                            d.Label + "=" + (choices.Scelte.TryGetValue(d.Name, out var sc) ? sc switch { RecordFieldChoice.Mia => "questa copia", RecordFieldChoice.Loro => "altra copia", _ => "manuale" } : "?"))) + "; il valore non scelto e' nel recovery-bin)") +
                        (Nota == null ? "" : "; " + Nota)
                };
                unione.Add(JsonSerializer.SerializeToNode(voce, JsonIo.Options));
            }
            merged["Changelog"] = unione;
        }

        object? model = null;
        var type = RecordMergeEngine.ModelType(Kind);
        if (type != null)
        {
            try { model = merged.Deserialize(type, JsonIo.Options); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                throw new RecordMergeException("Il risultato non e' un record valido: controllare i valori scritti a mano.", ex);
            }
            if (model == null) throw new RecordMergeException("Il risultato non e' un record valido.");
        }
        return new RecordMergeResult(merged, model, JsonSerializer.SerializeToUtf8Bytes(merged, JsonIo.Options));
    }

    private static JsonArray UnisciChangelog(JsonArray? a, JsonArray? b)
    {
        var viste = new HashSet<string>(StringComparer.Ordinal);
        var elementi = new List<JsonNode>();
        foreach (var n in (a ?? new JsonArray()).Concat(b ?? new JsonArray()))
        {
            if (n == null) continue;
            if (viste.Add(n.ToJsonString())) elementi.Add(n.DeepClone());
        }
        // Ordine cronologico stabile (Data ISO ordinabile come testo); voci senza data in coda mantenendo l'ordine.
        var ordinati = elementi
            .Select((n, i) => (n, i, data: (n as JsonObject)?["Data"]?.ToString() ?? "￿"))
            .OrderBy(x => x.data, StringComparer.Ordinal).ThenBy(x => x.i)
            .Select(x => x.n);
        var arr = new JsonArray();
        foreach (var n in ordinati) arr.Add(n);
        return arr;
    }
}

/// <summary>Rende i valori dei record in testo leggibile (mai JSON) e ricava le etichette dei campi dal nome.</summary>
public static class RecordValueFormatter
{
    private const int MaxChars = 3000;
    private const int MaxItems = 40;

    private static readonly Dictionary<string, string> EtichetteItaliane = new(StringComparer.Ordinal)
    {
        ["Id"] = "Identificativo", ["DataApertura"] = "Data di apertura", ["DataChiusura"] = "Data di chiusura",
        ["Archiviata"] = "Archiviata", ["DataArchiviazione"] = "Data di archiviazione", ["StatoWorkflow"] = "Stato",
        ["Stato"] = "Stato", ["Severita"] = "Severità", ["Priorita"] = "Priorità interna", ["MotivoChiusura"] = "Motivo di chiusura",
        ["ProveCollegate"] = "Prove collegate", ["CollegamentiPolarion"] = "Collegamenti Ticketing", ["ChangeRequestIds"] = "Change Request collegate",
        ["StatoDettaglio"] = "Dettaglio dello stato", ["DataFix"] = "Data del fix", ["CausaProbabileAltro"] = "Causa probabile (altro)",
        ["EcuSegnalate"] = "Componente segnalate", ["VeicoliSegnalati"] = "Veicoli segnalati", ["FunzionalitaSegnalate"] = "Funzionalità segnalate",
        ["StrumentoSegnalato"] = "Strumento segnalato", ["OperazioniSegnalate"] = "Operazioni segnalate",
        ["DescrizioneBreveSegnalata"] = "Descrizione breve", ["DescrizioneBreve"] = "Descrizione breve", ["NumeriPraticaRete"] = "Numeri pratica rete",
        ["VinCliente"] = "Matricola cliente", ["ConcessionariaOfficina"] = "Concessionaria/officina", ["Componente"] = "Componente", ["Funzionalita"] = "Funzionalità",
        ["TestId"] = "Test", ["Url"] = "Indirizzo web", ["Veicolo"] = "Veicolo", ["CollegatoIl"] = "Collegato il", ["CollegatoDa"] = "Collegato da",
        ["CreatoDa"] = "Creato da", ["Scadenza"] = "Scadenza", ["Titolo"] = "Titolo"
    };

    /// <summary>Etichetta del campo come nelle schermate dell'applicazione: prima la chiave di localizzazione dell'editor
    /// (<c>&lt;Campo&gt;Label</c>, es. PrioritaLabel), poi una tabella di etichette italiane con accenti, poi il nome del campo
    /// scritto in modo leggibile. Fuori dall'italiano si usa il nome leggibile.</summary>
    public static string Label(string name)
    {
        if (LocalizationService.Instance.TryTraduci(name + "Label", out var testo))
        {
            testo = testo.Trim().TrimEnd('*', ':', ' ');
            if (testo.Length > 0 && testo.Length <= 40) return testo;
        }
        if (LocalizationService.Instance.Lingua == "it" && EtichetteItaliane.TryGetValue(name, out var it)) return it;
        return Humanize(name);
    }

    /// <summary>"StatoWorkflow" -> "Stato workflow".</summary>
    public static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is '_' or '-') { sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]))) sb.Append(' ').Append(char.ToLowerInvariant(c));
            else sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }

    public static string Format(JsonNode? node) => Truncate(FormatCore(node, 0));

    private static string Truncate(string s) => s.Length <= MaxChars ? s : s[..MaxChars] + "\n…";

    private static string FormatCore(JsonNode? node, int depth)
    {
        switch (node)
        {
            case null: return "(vuoto)";
            case JsonValue v: return FormatScalar(v);
            case JsonArray arr:
                if (arr.Count == 0) return "(nessuno)";
                var righe = new List<string>();
                foreach (var item in arr.Take(MaxItems))
                    righe.Add("• " + Inline(item, depth + 1));
                if (arr.Count > MaxItems) righe.Add($"… altre {arr.Count - MaxItems} voci");
                return string.Join("\n", righe);
            case JsonObject obj:
                var campi = obj.Where(p => p.Value != null && !IsEmpty(p.Value) && !p.Key.StartsWith("_ditm", StringComparison.OrdinalIgnoreCase))
                    .Select(p => $"{Label(p.Key)}: {Inline(p.Value, depth + 1)}").ToList();
                return campi.Count == 0 ? "(vuoto)" : string.Join("\n", campi);
            default: return node.ToString();
        }
    }

    private static string Inline(JsonNode? node, int depth)
    {
        string testo;
        switch (node)
        {
            case JsonObject obj:
                testo = string.Join("; ", obj.Where(p => p.Value != null && !IsEmpty(p.Value) && !p.Key.StartsWith("_ditm", StringComparison.OrdinalIgnoreCase))
                    .Take(8).Select(p => $"{Label(p.Key)}: {(depth > 3 ? "…" : Inline(p.Value, depth + 1))}"));
                if (testo.Length == 0) testo = "(vuoto)";
                break;
            case JsonArray arr:
                testo = arr.Count == 0 ? "(nessuno)" : string.Join(", ", arr.Take(6).Select(x => Inline(x, depth + 1))) + (arr.Count > 6 ? $" … (+{arr.Count - 6})" : "");
                break;
            default:
                testo = FormatCore(node, depth);
                break;
        }
        return testo.Length <= 240 ? testo.Replace('\n', ' ') : testo[..240].Replace('\n', ' ') + "…";
    }

    private static bool IsEmpty(JsonNode n) => n switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrEmpty(s),
        JsonArray a => a.Count == 0,
        JsonObject o => o.Count == 0,
        _ => false
    };

    private static string FormatScalar(JsonValue v)
    {
        if (v.TryGetValue<bool>(out var b)) return b ? "Sì" : "No";
        if (v.TryGetValue<string>(out var s))
        {
            if (string.IsNullOrEmpty(s)) return "(vuoto)";
            if (s.Length >= 19 && s[4] == '-' && s[10] == 'T' &&
                DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                return dt.Year <= 1 ? "(vuoto)" : (dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt).ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
            return s;
        }
        return v.ToString();
    }

    /// <summary>Testo di partenza per la modifica manuale (valore grezzo, senza decorazioni).</summary>
    public static string EditText(JsonNode? node, RecordEditKind kind)
    {
        if (node == null) return "";
        return kind switch
        {
            RecordEditKind.Booleano => node is JsonValue v && v.TryGetValue<bool>(out var b) ? (b ? "Sì" : "No") : "",
            RecordEditKind.ElencoTesto => node is JsonArray a ? string.Join("\n", a.Select(x => x?.ToString() ?? "")) : "",
            RecordEditKind.Testo or RecordEditKind.Numero => node is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : node.ToString(),
            _ => ""
        };
    }
}
