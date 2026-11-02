using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Unica semantica TestLoom per il conteggio del lavoro operativo.
///
/// REGOLA: una operazione/tentativo vale una unita per ogni componente realmente coinvolta. Quindi X su Y+Z = 2,
/// K su Y = 1. Una revisione aggiunge soltanto le operazioni che contiene realmente. Le Condizioni storiche
/// ripetute restano tentativi distinti; per il conteggio "effettivo" si usano solo CondizioniEffettive.
///
/// Dati legacy: se una Condizione non dichiara componente ma il Test ne ha esattamente una, quella componente viene inferita.
/// Con zero o piu componente possibili e nessun tag sulla Condizione il dato e ambiguo e vale 0, mai 1 inventato.
///
/// Decisione utente 6.4.0, punto 1: un'operazione conta SOLO se e stata davvero eseguita (Esito.EEseguito(),
/// cioe Conforme/NonConforme/ConformeConDeviazioni). "Da eseguire" e N.A. non generano unita operative, ne
/// in CountAttemptUnits ne in CountEffectiveUnits: nasce cosi ogni nuova operazione, e resta cosi finche non
/// riceve un esito reale.
///
/// Decisione utente 6.4.8, punto 5: la PIANIFICAZIONE non e' un'operazione. Una REV (Esecuzione) vale come
/// "eseguita" solo se contiene almeno una Condizione realmente eseguita (EsecuzioneEseguita): una REV di sola
/// pianificazione (Esito Da eseguire) non conta come revisione/esecuzione/test eseguito ne per l'operatore che
/// l'ha creata. Una REV con Origine Rete (chiusura anomalia decisa dalla rete) conta come esecuzione sul
/// test/componente/funzionalita' ma NON per nessun operatore (EsecuzioneAttribuibileAOperatore). Lato SQL il
/// riflesso e': test_revision.raw_attempt_count &gt; 0 (= eseguita), test_revision.origine &lt;&gt; 1 (= non Rete),
/// test_summary.executed_revision_count (= numero di REV eseguite).
/// </summary>
public static class OperationalSemantics
{
    /// <summary>Unica definizione di "REV eseguita sullo strumento": almeno una Condizione con Esito.EEseguito().</summary>
    public static bool EsecuzioneEseguita(Esecuzione e) => e.Condizioni.Any(c => c.Esito.EEseguito());

    /// <summary>REV eseguita E attribuibile a un operatore (Origine != Rete): l'unica che entra nei conteggi per operatore.</summary>
    public static bool EsecuzioneAttribuibileAOperatore(Esecuzione e) =>
        e.EsecuzioneDaOperatore && EsecuzioneEseguita(e);

    public static int RevisioniEseguite(TestCase tc) => tc.Esecuzioni.Count(EsecuzioneEseguita);

    /// <summary>Un test e' "testato" solo se ha almeno una REV realmente eseguita (la sola pianificazione non basta).</summary>
    public static bool TestTestato(TestCase tc) => tc.Esecuzioni.Any(EsecuzioneEseguita);

    /// <summary>Esito corrente solo se il test e' stato davvero testato; null per il test solo pianificato.</summary>
    public static Esito? StatoCorrenteEseguito(TestCase tc) => TestTestato(tc) ? tc.StatoCorrente : null;

    /// <summary>Posizione (1-based, contando solo le REV eseguite) della prima REV Conforme; null se mai raggiunto.</summary>
    public static int? IterazioniEseguitePerConforme(TestCase tc)
    {
        var n = 0;
        foreach (var e in tc.Esecuzioni)
        {
            if (!EsecuzioneEseguita(e)) continue;
            n++;
            if (e.Esito == Esito.Conforme) return n;
        }
        return null;
    }

    public static IReadOnlyList<string> EcuUnits(TestCase testCase, Condizione condition)
    {
        var explicitUnits = UnitaDistinte(condition.Componente);
        if (explicitUnits.Count > 0) return explicitUnits;

        var testUnits = UnitaDistinte(testCase.Componente);
        return testUnits.Count == 1 ? testUnits : Array.Empty<string>();
    }

    /// <summary>Valori non vuoti, senza spazi ai bordi e senza duplicati (maiuscole ignorate), nell'ordine di primo incontro:
    /// stesso risultato di Where/Select/Distinct, senza iteratori LINQ. Chiamata per ogni operazione della matrice e della Home.</summary>
    private static List<string> UnitaDistinte(IEnumerable<string> valori)
    {
        var risultato = new List<string>();
        foreach (var valore in valori)
        {
            if (string.IsNullOrWhiteSpace(valore)) continue;
            var pulito = valore.Trim();
            var duplicato = false;
            for (var i = 0; i < risultato.Count && !duplicato; i++)
                duplicato = string.Equals(risultato[i], pulito, StringComparison.OrdinalIgnoreCase);
            if (duplicato) continue;
            risultato.Add(pulito);
        }
        return risultato;
    }

    public static int CountAttemptUnits(TestCase testCase, Esecuzione revision) =>
        revision.Condizioni.Where(c => c.Esito.EEseguito()).Sum(c => EcuUnits(testCase, c).Count);

    public static int CountEffectiveUnits(TestCase testCase, Esecuzione revision) =>
        revision.CondizioniEffettive.Where(c => c.Esito.EEseguito()).Sum(c => EcuUnits(testCase, c).Count);

    /// <summary>Unita di LAVORO dell'operatore (Home, 2026-10 punto 1): ogni operazione eseguita vale una unita
    /// per componente coinvolta, minimo 1 — un'operazione realmente svolta ma senza componente risolvibile (dato legacy) e'
    /// comunque lavoro fatto, a differenza di CountAttemptUnits che la lascia a 0 per le statistiche per componente.</summary>
    public static int CountWorkUnits(TestCase testCase, Esecuzione revision) =>
        revision.Condizioni.Where(c => c.Esito.EEseguito()).Sum(c => Math.Max(1, EcuUnits(testCase, c).Count));

    public static int CountAttemptUnits(TestCase testCase) =>
        testCase.Esecuzioni.Sum(e => CountAttemptUnits(testCase, e));

    public static int CountEffectiveUnits(TestCase testCase) =>
        testCase.Esecuzioni.Sum(e => CountEffectiveUnits(testCase, e));

    public static IEnumerable<OperationalUnit> EnumerateAttemptUnits(TestCase testCase)
    {
        for (var revisionIndex = 0; revisionIndex < testCase.Esecuzioni.Count; revisionIndex++)
        {
            var revision = testCase.Esecuzioni[revisionIndex];
            var effectiveUids = revision.CondizioniEffettive.Select(c => c.Uid).ToHashSet();
            for (var attemptIndex = 0; attemptIndex < revision.Condizioni.Count; attemptIndex++)
            {
                var condition = revision.Condizioni[attemptIndex];
                if (!condition.Esito.EEseguito()) continue;
                foreach (var componente in EcuUnits(testCase, condition))
                {
                    yield return new OperationalUnit(
                        testCase.Uid,
                        testCase.Id,
                        revisionIndex,
                        revision.Iterazione,
                        attemptIndex,
                        condition.Uid,
                        condition.OperazioneOrigineUid ?? condition.Uid,
                        effectiveUids.Contains(condition.Uid),
                        componente,
                        condition.Funzionalita ?? "",
                        condition.Esito,
                        revision.EsecuzioneDaOperatore ? revision.Operatore ?? "" : "",
                        revision.Data);
                }
            }
        }
    }
}

public sealed record OperationalUnit(
    Guid TestUid,
    string TestId,
    int RevisionIndex,
    int IterationNumber,
    int AttemptIndex,
    Guid ConditionUid,
    Guid OperationRootUid,
    bool IsEffective,
    string Componente,
    string Functionality,
    Esito Outcome,
    string Operator,
    DateTime Date);
