using TestLoom.App.Models;
using System.Text.RegularExpressions;

namespace TestLoom.App.Services;

/// <summary>Regola condivisa per segnalare Change Request attive con titolo/descrizione simili e ambito sovrapposto.</summary>
public static class ChangeRequestDuplicatiService
{
    private static readonly HashSet<string> ParoleGeneriche = new(StringComparer.OrdinalIgnoreCase)
    {
        "alla", "alle", "agli", "anche", "come", "con", "della", "delle", "dello", "dell", "dalla", "dalle",
        "dopo", "dove", "dati", "delle", "della", "dello", "dell", "dentro", "dalla", "dalle", "dello", "dopo",
        "il", "in", "nel", "nella", "nelle", "nello", "per", "presso", "questa", "questo", "richiesta", "richieste",
        "modifica", "modifiche", "change", "request", "requests", "cr", "possibile", "sistema", "sistemi", "funzione",
        "funzionalita", "funzionalità", "test", "implementazione", "implementare", "analisi", "verifica", "supporto",
        "the", "this", "that", "with", "from", "into", "when", "where", "change", "request", "possible", "system",
        "systems", "feature", "functionality", "implementation", "analysis", "check", "support"
    };

    public static List<string> TrovaPossibiliDuplicati(IEnumerable<ChangeRequest> richieste, ChangeRequest candidata)
    {
        static HashSet<string> Token(string value) => Regex.Matches(value ?? "", @"[\p{L}\p{N}]{4,}")
            .Select(m => m.Value.ToLowerInvariant())
            .Where(x => !ParoleGeneriche.Contains(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var attive = richieste
            .Where(c => c.Id != candidata.Id && c.Stato is not (StatoChangeRequest.Chiusa or StatoChangeRequest.Annullata))
            .ToList();
        var tokenPerId = attive.Select(c => (Richiesta: c, Tokens: Token(c.Titolo + " " + c.DescrizioneBreve + " " + c.Descrizione))).ToList();
        var tokenCandidata = Token(candidata.Titolo + " " + candidata.DescrizioneBreve + " " + candidata.Descrizione);
        if (tokenCandidata.Count < 2) return new();

        // Le importazioni spesso ripetono boilerplate identico in molte descrizioni. Le parole presenti in gran
        // parte dell'archivio non sono evidenza di duplicato: il conteggio documentale le esclude dal confronto.
        var frequenze = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokenCandidata) frequenze[token] = 1;
        foreach (var (_, tokens) in tokenPerId)
            foreach (var token in tokens) frequenze[token] = frequenze.GetValueOrDefault(token) + 1;
        var documenti = attive.Count + 1;
        var frequenzaMassima = Math.Max(3, (int)Math.Ceiling(documenti * 0.35));
        tokenCandidata.RemoveWhere(token => frequenze[token] > frequenzaMassima);
        if (tokenCandidata.Count < 2) return new();

        return tokenPerId.Where(voce =>
            {
                var c = voce.Richiesta;
                var tokenAltra = voce.Tokens;
                tokenAltra.RemoveWhere(token => frequenze[token] > frequenzaMassima);
                if (tokenAltra.Count < 2) return false;
                var sovrapposizione = tokenCandidata.Intersect(tokenAltra, StringComparer.OrdinalIgnoreCase).Count();
                if (sovrapposizione < 2) return false;
                var coefficienteContenimento = sovrapposizione / (double)Math.Min(tokenCandidata.Count, tokenAltra.Count);
                var indiceJaccard = sovrapposizione / (double)(tokenCandidata.Count + tokenAltra.Count - sovrapposizione);
                if (coefficienteContenimento < 0.6 || indiceJaccard < 0.25) return false;

                return c.VeicoliImpattati.Intersect(candidata.VeicoliImpattati, StringComparer.OrdinalIgnoreCase).Any()
                    || c.EcuImpattate.Intersect(candidata.EcuImpattate, StringComparer.OrdinalIgnoreCase).Any()
                    || c.StrumentiImpattati.Intersect(candidata.StrumentiImpattati, StringComparer.OrdinalIgnoreCase).Any();
            }).Select(voce => voce.Richiesta.Id).Take(5).ToList();
    }
}
