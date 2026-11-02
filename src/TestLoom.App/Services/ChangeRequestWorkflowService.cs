using TestLoom.App.Models;

namespace TestLoom.App.Services;

public sealed record EsitoValidazioneCr(bool Completa, IReadOnlyList<string> Mancanze, IReadOnlyList<string> Avvisi);

public static class ChangeRequestWorkflowService
{
    public static EsitoValidazioneCr Valuta(ChangeRequest cr, IEnumerable<TestCase> testCases, bool aggiornaSnapshot = false)
    {
        var tests = testCases.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var mancanti = new List<string>();
        var avvisi = new List<string>();
        var links = cr.TestValidazione.Where(l => l.Obbligatorio).ToList();
        if (links.Count == 0) mancanti.Add("Nessun Test di validazione obbligatorio.");

        var veicoliCoperti = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ecuCoperte = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var strumentiCoperti = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in links)
        {
            if (!tests.TryGetValue(link.TestId, out var test) || test.Eliminato)
            {
                mancanti.Add($"Test {link.TestId} non disponibile.");
                continue;
            }
            var exec = test.Esecuzioni.Where(e => !cr.ImplementataIl.HasValue || e.Data >= cr.ImplementataIl.Value).OrderByDescending(e => e.Data).FirstOrDefault();
            if (exec == null)
            {
                mancanti.Add($"Test {test.Id}: nessuna esecuzione successiva all'implementazione.");
                continue;
            }
            var accettabile = exec.Esito == Esito.Conforme || exec.Esito == Esito.ConformeConDeviazioni && link.DeviazioneApprovata && !string.IsNullOrWhiteSpace(link.MotivazioneDeviazione);
            if (!accettabile)
            {
                mancanti.Add($"Test {test.Id}: esito {exec.Esito} non accettabile.");
                continue;
            }
            if (link.EccezioneCopertura)
            {
                if (string.IsNullOrWhiteSpace(link.MotivazioneEccezione) || string.IsNullOrWhiteSpace(link.ApprovataDa))
                    mancanti.Add($"Test {test.Id}: eccezione di copertura non approvata da un Master.");
                else avvisi.Add($"Test {test.Id}: copertura eccezionale approvata da {link.ApprovataDa}.");
            }
            veicoliCoperti.UnionWith(link.VeicoliCoperti);
            ecuCoperte.UnionWith(link.EcuCoperte);
            strumentiCoperti.UnionWith(link.StrumentiCoperti);
            if (aggiornaSnapshot)
            {
                link.EsecuzioneValidante = exec.Iterazione;
                link.SnapshotValidazione = new SnapshotValidazioneCr
                {
                    EseguitaIl = exec.Data,
                    Esito = exec.Esito,
                    Veicoli = new List<string>(test.VeicoliEffettivi),
                    Componente = test.Componente.ToList(),
                    Strumenti = new List<string>(test.StrumentiUsati)
                };
            }
        }

        foreach (var v in cr.VeicoliImpattati.Where(v => !veicoliCoperti.Contains(v))) mancanti.Add($"Veicolo non coperto: {v}");
        foreach (var e in cr.EcuImpattate.Where(e => !ecuCoperte.Contains(e))) mancanti.Add($"Componente non coperta: {e}");
        foreach (var s in cr.StrumentiImpattati.Where(s => !strumentiCoperti.Contains(s))) mancanti.Add($"Strumento non coperto: {s}");
        return new EsitoValidazioneCr(mancanti.Count == 0, mancanti, avvisi);
    }

    public static bool RicalcolaStato(ChangeRequest cr, IEnumerable<TestCase> tests)
    {
        if (cr.Stato is StatoChangeRequest.Chiusa or StatoChangeRequest.Annullata or StatoChangeRequest.Aperta) return false;
        // L'implementazione senza test obbligatori è una transizione terminale del ciclo di validazione:
        // non deve essere retrocessa a DaValidare solo perché non esistono test da valutare.
        if (cr.Stato == StatoChangeRequest.Implementata && !cr.TestValidazione.Any(l => l.Obbligatorio)) return false;
        var precedente = cr.Stato;
        var valutazione = Valuta(cr, tests, aggiornaSnapshot: true);
        cr.Stato = valutazione.Completa ? StatoChangeRequest.Validata : StatoChangeRequest.DaValidare;
        return precedente != cr.Stato;
    }

    /// <summary>Confronta il contenuto persistibile della CR ignorando i soli metadati generati dal Save.
    /// Copre l'intero record (incluse liste annidate, ticket, allegati, snapshot di validazione e campi importati),
    /// così i no-op non dipendono dai flag UI e nessun campo modificato viene scartato.</summary>
    public static bool StessoContenutoPersistibile(ChangeRequest prima, ChangeRequest dopo)
    {
        var snapshotPrima = EntityStateFingerprint.Clone(prima);
        var snapshotDopo = EntityStateFingerprint.Clone(dopo);
        snapshotPrima.RevisioneDati = snapshotDopo.RevisioneDati = 0;
        snapshotPrima.UltimaModifica = snapshotDopo.UltimaModifica = default;
        snapshotPrima.Revisioni.Clear();
        snapshotDopo.Revisioni.Clear();
        return EntityStateFingerprint.Compute(snapshotPrima)
            .Equals(EntityStateFingerprint.Compute(snapshotDopo), StringComparison.OrdinalIgnoreCase);
    }

    public static RevisioneChangeRequest CreaRevisione(ChangeRequest cr, string autore) => new()
    {
        Numero = cr.Revisioni.Count + 1,
        Data = DateTime.Now,
        Autore = autore,
        Titolo = cr.Titolo,
        DescrizioneBreve = cr.DescrizioneBreve,
        Descrizione = cr.Descrizione,
        MotivazioneDuplicato = cr.MotivazioneDuplicato,
        VeicoliImpattati = new(cr.VeicoliImpattati),
        EcuImpattate = new(cr.EcuImpattate),
        StrumentiImpattati = new(cr.StrumentiImpattati),
        CampiCustom = new(cr.CampiCustom),
        TestValidazioneId = cr.TestValidazione.Select(l => l.TestId).ToList()
    };
}
