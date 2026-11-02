using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TestLoom.App.Services;

/// <summary>Perche' una segnalazione da classificare sembra gia' coperta da un'anomalia aperta. Dal piu' forte al
/// piu' debole (il valore numerico ne definisce l'ordine di preferenza).</summary>
public enum MotivoSuggerimentoAnomalia
{
    /// <summary>Stessa operazione (stessa radice) gia' collegata a questo Test.</summary>
    StessaOperazione = 0,
    /// <summary>Stessa componente e stessa Funzionalita' su questo stesso Test.</summary>
    StessaEcuFunzionalita = 1,
    /// <summary>Stessa componente e stessa Funzionalita' su un altro Test, con descrizione del problema simile.</summary>
    StessaEcuFunzionalitaDescrizioneSimile = 2
}

/// <summary>Operazione di una segnalazione o di un'anomalia: coppia componente x Funzionalita', con la radice
/// dell'operazione quando e' nota (solo per i collegamenti a un Test, vedi AnomaliaWorkflowService.Radice).</summary>
public sealed record OperazioneAnomalia(Guid? Radice, string Componente, string Funzionalita);

/// <summary>Anomalia aperta candidata a coprire una nuova segnalazione. <see cref="CollegataAlTest"/>: almeno una
/// sua prova appartiene al Test in cui si classifica. <see cref="Operazioni"/>: operazioni note (dai collegamenti al
/// Test corrente o dalle operazioni di una segnalazione di rete). <see cref="Descrizione"/>: titolo della NC, nel
/// formato di NonConformitaRulesService.TitoloDaOperazione ("Funzionalita (componente): problema").</summary>
public sealed record CandidataAnomalia(Guid Uid, string Id, string Descrizione, bool CollegataAlTest, IReadOnlyList<OperazioneAnomalia> Operazioni);

/// <summary>Anomalia suggerita per una segnalazione. E' solo una pre-selezione: il collegamento avviene con un clic.</summary>
public sealed record SuggerimentoAnomalia(Guid AnomaliaUid, string AnomaliaId, MotivoSuggerimentoAnomalia Motivo, double Similarita);

/// <summary>Sceglie, in modo conservativo, l'anomalia aperta che e' "chiaramente" la stessa di una nuova segnalazione.
/// Funzione pura: i chiamanti costruiscono le candidate una volta sola e le passano qui.
/// Regole: (1) stesso Test e stessa operazione; (2) stesso Test con stessa componente e Funzionalita'; (3) altro Test con
/// stessa componente e Funzionalita' e descrizione del problema simile (Jaccard sulle parole significative, soglia
/// <see cref="SogliaSimilaritaDescrizione"/>). Se il livello migliore e' occupato da piu' candidate indistinguibili
/// non si suggerisce nulla: meglio creare una NC nuova che collegare quella sbagliata.</summary>
public static class SuggerimentoAnomaliaService
{
    /// <summary>Soglia conservativa: sotto questo valore la descrizione non basta a dire che il problema e' lo stesso.</summary>
    public const double SogliaSimilaritaDescrizione = 0.6;

    /// <summary>Parole troppo comuni per distinguere due problemi (oltre alle parole corte, filtrate a parte).</summary>
    private static readonly HashSet<string> ParoleComuni = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "per", "non", "del", "della", "delle", "dei", "degli", "dell", "dalla", "dallo", "dal",
        "nel", "nella", "nello", "sul", "sulla", "che", "una", "uno", "dopo", "prima",
        "the", "and", "for", "not", "with"
    };

    private static readonly Regex Separatori = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>Anomalia piu' probabilmente uguale alla nuova operazione, oppure null se nessuna lo e' chiaramente.</summary>
    /// <param name="nuova">Operazione della segnalazione da classificare.</param>
    /// <param name="titoloNuovo">Titolo che la NC avrebbe alla creazione (stesso formato di TitoloDaOperazione).</param>
    /// <param name="candidate">Anomalie aperte candidate (gia' filtrate per stato).</param>
    public static SuggerimentoAnomalia? Migliore(OperazioneAnomalia nuova, string titoloNuovo, IEnumerable<CandidataAnomalia> candidate)
    {
        var problemaNuovo = ProblemaDaTitolo(titoloNuovo, nuova.Funzionalita, nuova.Componente);
        var valutate = new List<(CandidataAnomalia Candidata, MotivoSuggerimentoAnomalia Motivo, double Similarita)>();
        foreach (var c in candidate)
        {
            var stessaOperazione = c.CollegataAlTest && nuova.Radice is { } radice && c.Operazioni.Any(o => o.Radice == radice);
            var stessaEcuFunzionalita = !stessaOperazione && Corrisponde(c, nuova);
            if (!stessaOperazione && !stessaEcuFunzionalita) continue;

            var similarita = Similarita(problemaNuovo, ProblemaDaTitolo(c.Descrizione, nuova.Funzionalita, nuova.Componente));
            if (stessaOperazione)
                valutate.Add((c, MotivoSuggerimentoAnomalia.StessaOperazione, similarita));
            else if (c.CollegataAlTest)
                valutate.Add((c, MotivoSuggerimentoAnomalia.StessaEcuFunzionalita, similarita));
            else if (similarita >= SogliaSimilaritaDescrizione)
                valutate.Add((c, MotivoSuggerimentoAnomalia.StessaEcuFunzionalitaDescrizioneSimile, similarita));
        }
        if (valutate.Count == 0) return null;

        var migliore = valutate.OrderBy(v => (int)v.Motivo).ThenByDescending(v => v.Similarita).First();
        var indistinguibili = valutate.Count(v => v.Motivo == migliore.Motivo && v.Similarita == migliore.Similarita);
        if (indistinguibili > 1) return null;
        return new SuggerimentoAnomalia(migliore.Candidata.Uid, migliore.Candidata.Id, migliore.Motivo, migliore.Similarita);
    }

    /// <summary>Somiglianza di Jaccard fra le parole significative dei due testi (0 = nulla in comune, 1 = uguali).
    /// Maiuscole, accenti e punteggiatura non contano; le parole di meno di tre lettere e quelle comuni sono ignorate.
    /// Un testo vuoto non prova nulla: 0.</summary>
    public static double Similarita(string? a, string? b)
    {
        var paroleA = Parole(a);
        var paroleB = Parole(b);
        if (paroleA.Count == 0 || paroleB.Count == 0) return 0;
        var comuni = paroleA.Count(paroleB.Contains);
        var unione = paroleA.Count + paroleB.Count - comuni;
        return (double)comuni / unione;
    }

    /// <summary>Problema di un titolo NC senza il prefisso "Funzionalita' (componente)" (e i due punti che lo seguono). Se il
    /// titolo non inizia con quel prefisso lo restituisce intero: non si indovina un formato diverso.</summary>
    public static string ProblemaDaTitolo(string? titolo, string? funzionalita, string? componente)
    {
        var testo = (titolo ?? "").Trim();
        if (string.IsNullOrWhiteSpace(funzionalita) || string.IsNullOrWhiteSpace(componente)) return testo;
        var prefisso = Prefisso(funzionalita, componente);
        if (!testo.StartsWith(prefisso, StringComparison.OrdinalIgnoreCase)) return testo;
        var resto = testo[prefisso.Length..];
        if (resto.Length == 0) return "";
        return resto[0] == ':' ? resto[1..].Trim() : testo;
    }

    private static string Prefisso(string funzionalita, string componente) => $"{funzionalita.Trim()} ({componente.Trim()})";

    /// <summary>Stessa componente e stessa Funzionalita' della nuova operazione: per un'operazione nota alla candidata, oppure
    /// perche' il titolo della NC ha il prefisso "Funzionalita' (componente)" della nuova operazione.</summary>
    private static bool Corrisponde(CandidataAnomalia c, OperazioneAnomalia nuova)
    {
        if (string.IsNullOrWhiteSpace(nuova.Componente) || string.IsNullOrWhiteSpace(nuova.Funzionalita)) return false;
        if (c.Operazioni.Any(o => Uguale(o.Componente, nuova.Componente) && Uguale(o.Funzionalita, nuova.Funzionalita))) return true;
        var titolo = (c.Descrizione ?? "").Trim();
        var prefisso = Prefisso(nuova.Funzionalita, nuova.Componente);
        if (!titolo.StartsWith(prefisso, StringComparison.OrdinalIgnoreCase)) return false;
        return titolo.Length == prefisso.Length || titolo[prefisso.Length] == ':';
    }

    private static bool Uguale(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a);

    private static HashSet<string> Parole(string? testo)
    {
        var parole = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(testo)) return parole;
        foreach (var parola in Separatori.Split(SenzaAccenti(testo)))
            if (parola.Length >= 3 && !ParoleComuni.Contains(parola)) parole.Add(parola);
        return parole;
    }

    private static string SenzaAccenti(string testo)
    {
        var sb = new StringBuilder(testo.Length);
        foreach (var c in testo.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().ToLowerInvariant();
    }
}
