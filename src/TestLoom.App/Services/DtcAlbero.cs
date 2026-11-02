using TestLoom.App.Models;

namespace TestLoom.App.Services;

public enum LivelloDtcAlbero { Veicolo = 0, Componente = 1, Guasto = 2 }

/// <summary>Riga APPIATTITA dell'albero Veicolo -> componente -> guasto di un requisito Extra (6.5.1). La vista e una ListBox virtualizzata
/// di queste righe (un vero TreeView non virtualizza e non regge 100k guasto); l'indentazione e il livello danno l'aspetto ad albero.
/// Lo storage resta la lista piatta di <see cref="DtcRequisito"/>: l'albero e SOLO una vista.</summary>
public sealed class DtcAlberoNodo
{
    public LivelloDtcAlbero Livello { get; init; }
    /// <summary>Veicolo normalizzato; "" = voci senza veicolo (gruppo "(senza veicolo)").</summary>
    public string Veicolo { get; init; } = "";
    public string Componente { get; init; } = "";
    /// <summary>Solo per le foglie: la voce reale (da rimuovere).</summary>
    public DtcRequisito? Voce { get; init; }
    /// <summary>Chiave di espansione: "sv" per il nodo veicolo, "sv|componente" per il nodo componente.</summary>
    public string ChiaveNodo { get; init; } = "";
    public string Testo { get; init; } = "";
    /// <summary>guasto contenuti (nodi veicolo/componente); 0 per una foglia o per un nodo vuoto creato a mano.</summary>
    public int Conteggio { get; init; }
    public bool Espanso { get; init; }
    /// <summary>Il veicolo non e (piu) tra quelli dichiarati dal requisito: da riallineare (non blocca nulla).</summary>
    public bool NonAllineata { get; init; }
    public string AutomationId { get; init; } = "";
    public string AutomationIdRimuovi { get; init; } = "";

    public bool EIntestazione => Livello != LivelloDtcAlbero.Guasto;
    public bool EFoglia => Livello == LivelloDtcAlbero.Guasto;
    /// <summary>Indentazione in pixel (stesso significato in tutti i temi).</summary>
    public double Rientro => (int)Livello * 20;
    public Avalonia.Thickness Margine => new(Rientro, 0, 0, 0);
    public Avalonia.Media.FontWeight Peso => EIntestazione ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal;
    public string Freccia => EFoglia ? "" : Espanso ? "▾" : "▸";
    public string ConteggioTesto => EFoglia ? "" : $"({Conteggio})";
    public string AvvisoTesto => NonAllineata ? LocalizationService.Instance.Traduci("DtcNonAllineata") : "";
    public bool HaAvviso => NonAllineata;
    /// <summary>Nome accessibile della riga (UI Automation / lettori di schermo) invece del nome del tipo.</summary>
    public override string ToString() => EFoglia ? Testo : $"{Testo} {ConteggioTesto}";
}

/// <summary>Regole PURE dell'albero guasto del requisito (6.5.1): costruzione della vista raggruppata e validazione in scrittura di una
/// nuova voce. Nessun I/O.</summary>
public static class DtcAlberoRegole
{
    private const string Prefisso = "Requis_Guasto_";

    private static string Id(string s) => s.Replace(' ', '_');

    /// <summary>Veicoli ammessi per le voci guasto: quelli dichiarati dal requisito Extra oppure, se non ne dichiara nessuno (coerente con
    /// <see cref="RequisitoApplicabilita.SiApplicaAlSottoveicolo"/>: lista vuota = tutti i veicoli), tutti quelli dell'anagrafica.
    /// Senza anagrafica (null) valgono i soli dichiarati (comportamento precedente).</summary>
    public static IReadOnlyCollection<string> Ammessi(IReadOnlyCollection<string> dichiarati, IReadOnlyCollection<string>? veicoliAnagrafica) =>
        dichiarati.Count > 0 || veicoliAnagrafica == null ? dichiarati : veicoliAnagrafica;

    /// <summary>Costruisce le righe appiattite. Gruppi: veicolo (le voci senza veicolo in "(senza veicolo)", per prime),
    /// poi componente, poi codice. <paramref name="nodiVuoti"/> = nodi creati a mano senza ancora guasto (veicolo, componente; componente "" = solo il
    /// veicolo). <paramref name="espanso"/> decide per chiave nodo se mostrare i figli.</summary>
    public static List<DtcAlberoNodo> Costruisci(IReadOnlyList<DtcRequisito> voci, IReadOnlyCollection<string> dichiarati,
        IEnumerable<(string Veicolo, string Componente)>? nodiVuoti, Func<string, bool> espanso, IReadOnlyCollection<string>? veicoliAnagrafica = null)
    {
        dichiarati = Ammessi(dichiarati, veicoliAnagrafica);
        var gruppi = new Dictionary<string, Dictionary<string, List<DtcRequisito>>>(StringComparer.OrdinalIgnoreCase);
        List<DtcRequisito>? Assicura(string sv, string componente, bool conEcu)
        {
            if (!gruppi.TryGetValue(sv, out var perEcu)) gruppi[sv] = perEcu = new(StringComparer.OrdinalIgnoreCase);
            if (!conEcu) return null;
            if (!perEcu.TryGetValue(componente, out var lista)) perEcu[componente] = lista = new List<DtcRequisito>();
            return lista;
        }
        foreach (var v in voci) Assicura(v.VeicoloNorm, (v.Componente ?? "").Trim(), true)!.Add(v);
        if (nodiVuoti != null)
            foreach (var (sv, componente) in nodiVuoti) Assicura((sv ?? "").Trim(), (componente ?? "").Trim(), !string.IsNullOrWhiteSpace(componente));

        var righe = new List<DtcAlberoNodo>(voci.Count + gruppi.Count * 2);
        var cmp = NaturalStringComparer.Instance;
        var svOrdinati = gruppi.Keys.OrderBy(k => k.Length == 0 ? 0 : 1).ThenBy(k => k, cmp).ToList();
        foreach (var sv in svOrdinati)
        {
            var perEcu = gruppi[sv];
            var totSv = perEcu.Values.Sum(l => l.Count);
            var nonAllineata = sv.Length > 0 && !dichiarati.Contains(sv, StringComparer.OrdinalIgnoreCase);
            var apertoSv = espanso(sv);
            righe.Add(new DtcAlberoNodo
            {
                Livello = LivelloDtcAlbero.Veicolo, Veicolo = sv, ChiaveNodo = sv, Conteggio = totSv, Espanso = apertoSv,
                Testo = sv.Length == 0 ? DtcRequisito.SenzaVeicolo : sv, NonAllineata = nonAllineata,
                AutomationId = Prefisso + "Veicolo_" + (sv.Length == 0 ? "senza" : Id(sv))
            });
            if (!apertoSv) continue;
            foreach (var componente in perEcu.Keys.OrderBy(e => e, cmp))
            {
                var lista = perEcu[componente];
                var chiaveEcu = sv + "|" + componente;
                var apertoEcu = espanso(chiaveEcu);
                righe.Add(new DtcAlberoNodo
                {
                    Livello = LivelloDtcAlbero.Componente, Veicolo = sv, Componente = componente, ChiaveNodo = chiaveEcu, Conteggio = lista.Count, Espanso = apertoEcu,
                    Testo = componente.Length == 0 ? "(senza componente)" : componente, NonAllineata = nonAllineata,
                    AutomationId = Prefisso + "Componente_" + (sv.Length == 0 ? "senza" : Id(sv)) + "_" + Id(componente)
                });
                if (!apertoEcu) continue;
                lista.Sort((a, b) => cmp.Compare(a.Codice, b.Codice));
                foreach (var v in lista)
                {
                    var suffisso = sv.Length == 0 ? Id(v.Codice) : Id(sv) + "_" + Id(componente) + "_" + Id(v.Codice);
                    righe.Add(new DtcAlberoNodo
                    {
                        Livello = LivelloDtcAlbero.Guasto, Veicolo = sv, Componente = componente, Voce = v, Testo = v.Codice, NonAllineata = nonAllineata,
                        ChiaveNodo = chiaveEcu + "|" + v.Codice,
                        AutomationId = Prefisso + "Voce_" + suffisso, AutomationIdRimuovi = Prefisso + "Rimuovi_" + suffisso
                    });
                }
            }
        }
        return righe;
    }

    /// <summary>Esito della validazione in scrittura di una nuova voce: null = valida, altrimenti la chiave di localizzazione dell'errore.</summary>
    /// <param name="veicolo">"" = voce senza veicolo (sempre ammessa: dati 6.5.0); altrimenti DEVE essere tra i dichiarati dal requisito.</param>
    /// <param name="ecuAnagrafica">componente dell'anagrafica (non archiviate); se non vuota il componente deve appartenervi (scelta da menu, non digitata).</param>
    /// <param name="chiaviEsistenti">Chiavi <see cref="DtcRequisito.Chiave(string?, string?, string?)"/> gia presenti (confronto OrdinalIgnoreCase).</param>
    public static string? ValidaNuovaVoce(string? veicolo, string? componente, string? codice, IReadOnlyCollection<string> dichiarati,
        IReadOnlyCollection<string>? ecuAnagrafica, ISet<string> chiaviEsistenti, IReadOnlyCollection<string>? veicoliAnagrafica = null)
    {
        dichiarati = Ammessi(dichiarati, veicoliAnagrafica);
        var sv = (veicolo ?? "").Trim(); var e = (componente ?? "").Trim(); var c = (codice ?? "").Trim();
        if (!DtcOperazioneRegole.CodiceValido(c)) return "DtcCodiceNonValido";
        if (e.Length == 0) return "DtcEcuMancante";
        if (ecuAnagrafica is { Count: > 0 } && !ecuAnagrafica.Contains(e, StringComparer.OrdinalIgnoreCase)) return "DtcEcuNonInAnagrafica";
        if (sv.Length > 0 && !dichiarati.Contains(sv, StringComparer.OrdinalIgnoreCase)) return "DtcVeicoloNonDichiarato";
        if (chiaviEsistenti.Contains(DtcRequisito.Chiave(sv, e, c))) return "DtcDuplicato";
        return null;
    }

    /// <summary>Esito dell'aggiunta in blocco di codici a un nodo (Veicolo, componente).</summary>
    /// <param name="ErroreNodo">Chiave di localizzazione se il NODO e invalido (veicolo non dichiarato, componente mancante/fuori anagrafica, nessun codice): nulla e aggiunto.</param>
    public sealed record RisultatoBlocco(IReadOnlyList<DtcRequisito> Nuove, IReadOnlyList<string> Duplicati, IReadOnlyList<string> NonValidi, string? ErroreNodo, int Letti);

    private static readonly System.Text.RegularExpressions.Regex Separatori = new(@"[\s,;]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Spezza il testo incollato in codici: separatori = a capo, virgola, punto e virgola, spazio/tab. Vuoti scartati, ordine preservato.</summary>
    public static List<string> Tokenizza(string? testo) =>
        string.IsNullOrWhiteSpace(testo) ? new List<string>() : Separatori.Split(testo.Trim()).Where(t => t.Length > 0).ToList();

    /// <summary>Validazione del NODO (veicolo + componente): null = valido, altrimenti la chiave di localizzazione dell'errore.</summary>
    public static string? ValidaNodo(string? veicolo, string? componente, IReadOnlyCollection<string> dichiarati, IReadOnlyCollection<string>? ecuAnagrafica,
        IReadOnlyCollection<string>? veicoliAnagrafica = null)
    {
        dichiarati = Ammessi(dichiarati, veicoliAnagrafica);
        var v = (veicolo ?? "").Trim(); var e = (componente ?? "").Trim();
        if (e.Length == 0) return "DtcEcuMancante";
        if (ecuAnagrafica is { Count: > 0 } && !ecuAnagrafica.Contains(e, StringComparer.OrdinalIgnoreCase)) return "DtcEcuNonInAnagrafica";
        if (v.Length > 0 && !dichiarati.Contains(v, StringComparer.OrdinalIgnoreCase)) return "DtcVeicoloNonDichiarato";
        return null;
    }

    /// <summary>Aggiunta in blocco al nodo (Veicolo, componente): ogni codice e validato con <see cref="DtcOperazioneRegole.CodiceValido"/> e deduplicato sulla
    /// chiave (Veicolo, componente, Codice) rispetto alle voci esistenti E ai codici gia letti nello stesso blocco. Non muta nulla: il chiamante applica <c>Nuove</c>.</summary>
    public static RisultatoBlocco ValidaBlocco(string? veicolo, string? componente, string? testo, IReadOnlyCollection<string> dichiarati,
        IReadOnlyCollection<string>? ecuAnagrafica, ISet<string> chiaviEsistenti, IReadOnlyCollection<string>? veicoliAnagrafica = null)
    {
        dichiarati = Ammessi(dichiarati, veicoliAnagrafica);
        var codici = Tokenizza(testo);
        if (codici.Count == 0) return new RisultatoBlocco(Array.Empty<DtcRequisito>(), Array.Empty<string>(), Array.Empty<string>(), "DtcNessunCodice", 0);
        var errore = ValidaNodo(veicolo, componente, dichiarati, ecuAnagrafica, null);
        if (errore != null) return new RisultatoBlocco(Array.Empty<DtcRequisito>(), Array.Empty<string>(), Array.Empty<string>(), errore, codici.Count);
        var v = Canonico(veicolo, dichiarati) ?? (veicolo ?? "").Trim(); var e = (componente ?? "").Trim();
        var viste = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nuove = new List<DtcRequisito>(); var dup = new List<string>(); var nonValidi = new List<string>();
        foreach (var c in codici)
        {
            if (!DtcOperazioneRegole.CodiceValido(c)) { nonValidi.Add(c); continue; }
            var k = DtcRequisito.Chiave(v, e, c);
            if (chiaviEsistenti.Contains(k) || !viste.Add(k)) { dup.Add(c); continue; }
            nuove.Add(new DtcRequisito { Codice = c, Componente = e, Veicolo = v.Length == 0 ? null : v });
        }
        return new RisultatoBlocco(nuove, dup, nonValidi, null, codici.Count);
    }

    /// <summary>Nome del veicolo come scritto nel requisito (maiuscole/minuscole canoniche), o null se non dichiarato.</summary>
    public static string? Canonico(string? veicolo, IReadOnlyCollection<string> dichiarati)
    {
        var sv = (veicolo ?? "").Trim();
        if (sv.Length == 0) return "";
        return dichiarati.FirstOrDefault(d => d.Equals(sv, StringComparison.OrdinalIgnoreCase));
    }
}
