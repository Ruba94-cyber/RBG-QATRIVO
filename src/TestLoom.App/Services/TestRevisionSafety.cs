using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Ultima linea di difesa dell'immutabilità REV. La UI crea normalmente la nuova revisione, ma il repository
/// richiama comunque questo servizio prima di ogni Save: una modifica tecnica dimenticata da un ViewModel non
/// può mai riscrivere in-place una REV già persistita e nessuna REV storica può essere alterata.
/// </summary>
public static class TestRevisionSafety
{
    /// <summary>
    /// Verifica che tutte le REV già storiche siano byte-semantically equivalenti a quelle persistite. Se il
    /// candidato ha lo stesso numero di revisioni ma il contenuto tecnico dell'ultima è cambiato, preserva
    /// l'ultima persistita e aggiunge automaticamente il candidato come nuova REV.
    /// </summary>
    private static readonly AsyncLocal<int> _riscritturaRiferimentiCatalogo = new();

    /// <summary>
    /// Finestra in cui il motore Unisci/Rinomina/Dividi dei cataloghi può riscrivere il NOME di un elemento di
    /// catalogo (componente, Veicolo, Funzionalità...) anche dentro le REV storiche: il nome è un'etichetta anagrafica
    /// della stessa entità, non un contenuto testato, quindi non nasce nessuna nuova REV. Restano vietati la
    /// rimozione o l'aggiunta di REV: cambia solo il contenuto dei riferimenti, mai il numero di revisioni.
    /// Da usare SOLO dal motore dei cataloghi (<see cref="MasterUnioneService"/>), mai da un percorso utente.
    /// </summary>
    internal static IDisposable AmmettiRiscritturaRiferimentiCatalogo()
    {
        _riscritturaRiferimentiCatalogo.Value++;
        return new ScopeRiscrittura();
    }

    private sealed class ScopeRiscrittura : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _riscritturaRiferimentiCatalogo.Value--;
        }
    }

    public static bool PreserveImmutableHistory(TestCase persisted, TestCase candidate, DateTime now, string operatore)
    {
        if (persisted.Esecuzioni.Count == 0) return false;
        if (candidate.Esecuzioni.Count < persisted.Esecuzioni.Count)
            throw new InvalidDataException($"Il Test {candidate.Id} tenta di rimuovere revisioni già persistite. Salvataggio bloccato.");

        // Riscrittura dei riferimenti di catalogo (Rinomina/Unisci/Dividi): stesso numero di REV, contenuto dei
        // soli riferimenti allineato al nuovo nome in tutte le REV, nessuna REV aggiunta.
        if (_riscritturaRiferimentiCatalogo.Value > 0)
        {
            if (candidate.Esecuzioni.Count != persisted.Esecuzioni.Count)
                throw new InvalidDataException($"Il Test {candidate.Id}: la riscrittura dei riferimenti di catalogo non può aggiungere o rimuovere revisioni. Salvataggio bloccato.");
            return false;
        }

        // Qualunque REV già congelata deve essere identica. Se il candidato ha già aggiunto una nuova REV,
        // anche la precedente REV corrente è ormai parte del prefisso immutabile.
        var prefixCount = candidate.Esecuzioni.Count > persisted.Esecuzioni.Count
            ? persisted.Esecuzioni.Count
            : Math.Max(0, persisted.Esecuzioni.Count - 1);
        for (var i = 0; i < prefixCount; i++)
        {
            if (!SamePersistedRevision(persisted.Esecuzioni[i], candidate.Esecuzioni[i]))
                throw new InvalidDataException($"La REV {persisted.Esecuzioni[i].Iterazione} del Test {candidate.Id} è storica e non può essere modificata. Salvataggio bloccato.");
        }

        // Il chiamante ha già creato una o più nuove REV: non duplicarle, ma il prefisso è stato verificato.
        if (candidate.Esecuzioni.Count > persisted.Esecuzioni.Count) return false;

        if (SameTechnicalState(persisted, candidate)) return false;

        // La REV corrente è stata modificata in-place da un percorso che non ha creato la nuova revisione.
        // Ripartiamo SEMPRE dalla storia autoritativa letta dal disco e appendiamo una copia della REV
        // candidata: nessun valore della vecchia REV viene sostituito.
        var history = persisted.Esecuzioni.Select(CloneRevision).ToList();
        var nuova = CloneRevision(candidate.Esecuzioni[^1]);
        nuova.Iterazione = history.Count + 1;
        nuova.Data = now;
        nuova.Operatore = operatore;
        history.Add(nuova);
        candidate.Esecuzioni = history;
        return true;
    }

    private static bool SamePersistedRevision(Esecuzione left, Esecuzione right) =>
        JsonSerializer.Serialize(left, JsonIo.CompactOptions) == JsonSerializer.Serialize(right, JsonIo.CompactOptions);

    /// <summary>
    /// Stato che rappresenta realmente ciò che è stato testato. Esclude soltanto metadati di workflow o
    /// proiezioni derivate; include componente dell'operazione, note, punteggi, campi strumento ed evidenziazioni,
    /// cioè i campi che in passato potevano sfuggire a DettagliCambiati().
    /// </summary>
    private static bool SameTechnicalState(TestCase persisted, TestCase candidate)
    {
        var a = TechnicalPayload(persisted);
        var b = TechnicalPayload(candidate);
        return JsonSerializer.Serialize(a, JsonIo.CompactOptions) == JsonSerializer.Serialize(b, JsonIo.CompactOptions);
    }

    private static object TechnicalPayload(TestCase tc)
    {
        var rev = tc.Esecuzioni.LastOrDefault();
        return new
        {
            Componente = tc.Componente.ToArray(),
            tc.Veicolo,
            Veicoli = tc.Veicoli.ToArray(),
            tc.TestTemplateUid,
            Requisiti = tc.RequisitoId.ToArray(),
            Funzionalita = tc.Funzionalita.ToArray(),
            Strumenti = tc.StrumentiUsati.ToArray(),
            tc.DescrizioneBreve,
            tc.Descrizione,
            tc.DescrizioneProblema,
            CampiCustom = Sorted(tc.CampiCustom),
            ValoriStrumentiTest = Sorted(tc.ValoriStrumentiTest),
            Revisione = rev == null ? null : new
            {
                rev.Origine,
                rev.NonConformitaOrigineId,
                rev.Esito,
                rev.EsitoSovrascritto,
                rev.MotivazioneOverride,
                rev.Deviazioni,
                rev.Note,
                rev.NonConformitaId,
                rev.Matricola,
                rev.BaselineSw,
                rev.BaselineHw,
                ValoriStrumenti = Sorted(rev.ValoriStrumenti),
                Punteggi = rev.PunteggiRequisiti.Select(p => new
                {
                    p.RequisitoId, p.Valore, p.Sovrascritto, p.MotivazioneOverride
                }).ToArray(),
                Condizioni = rev.Condizioni.Select(c => new
                {
                    c.Uid,
                    c.OperazioneOrigineUid,
                    Componente = c.Componente.ToArray(),
                    c.Funzionalita,
                    c.Guasto,
                    c.DtcRequisitoId,
                    c.DtcRequisitoVuotoScelto,
                    c.DtcPasso,
                    c.DtcVeicolo,
                    c.Strumento,
                    c.Esito,
                    c.Nota,
                    c.MotivazioneNonApplicabile,
                    c.DescrizioneDeviazione,
                    c.DescrizioneProblema,
                    c.ColoreNota,
                    c.ColoreMotivazioneNonApplicabile,
                    c.ColoreDescrizioneDeviazione,
                    c.ColoreDescrizioneProblema,
                    ValoriStrumenti = Sorted(c.ValoriStrumenti)
                    // SegnalazioneUid è metadato di workflow generato durante il salvataggio, non una prova
                    // inserita dall'operatore: non deve creare una REV soltanto perché è stata classificata.
                }).ToArray()
            }
        };
    }

    private static KeyValuePair<string, string>[] Sorted(Dictionary<string, string> values) =>
        values.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray();

    private static Esecuzione CloneRevision(Esecuzione value) =>
        JsonSerializer.Deserialize<Esecuzione>(JsonSerializer.Serialize(value, JsonIo.Options), JsonIo.Options)!;
}
