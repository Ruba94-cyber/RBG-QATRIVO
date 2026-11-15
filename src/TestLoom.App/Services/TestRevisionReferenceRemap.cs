using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Esito del riallineamento dei riferimenti a numeri di REV dopo l'unione di un Test.</summary>
public sealed record EsitoRiallineamentoRev(int NcAggiornate, int SegnalazioniAggiornate, int CrAggiornate, IReadOnlyList<string> Errori);

/// <summary>
/// Dopo l'unione/rinumerazione delle REV di un Test, riallinea i riferimenti "per numero" (NC: prove collegate e iterazione di
/// origine; segnalazioni; CR: REV validante) al Test unito usando l'IDENTITA' stabile (CondizioneUid; per le CR la data della REV
/// validante), mai il vecchio numero. Idempotente e senza distruzione: i riferimenti gia' corretti non vengono riscritti e, in
/// ogni caso, <see cref="AnomaliaWorkflowService.RisolviProva"/> risale per identita' anche prima di questo passaggio, quindi un
/// fallimento parziale qui (record per record, errori elencati) non rende nulla irrisolvibile. Chiamare dopo il salvataggio
/// riuscito del Test unito; i record storici gia' salvati con i vecchi numeri vengono corretti senza migrazioni.
/// </summary>
public static class TestRevisionReferenceRemap
{
    public static EsitoRiallineamentoRev Riallinea(AppState state, TestCase unito)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(unito);
        var errori = new List<string>();
        int nc = 0, seg = 0, cr = 0;

        bool StessoTest(Guid? uid, string id) =>
            uid.HasValue ? uid == unito.Uid : string.Equals(id, unito.Id, StringComparison.OrdinalIgnoreCase);

        int? NumeroPer(Guid condizione, int suggerito)
        {
            if (condizione == Guid.Empty) return null;
            var r = AnomaliaWorkflowService.RisolviProva(unito,
                new RiferimentoProva { TestId = unito.Id, TestUid = unito.Uid, EsecuzioneIterazione = suggerito, CondizioneUid = condizione });
            return r?.Esecuzione.Iterazione;
        }

        foreach (var originale in state.NonConformita.GetAll())
        {
            try
            {
                var modificata = false;
                var copia = originale.Clona();
                foreach (var p in copia.ProveCollegate.Where(p => StessoTest(p.TestUid, p.TestId)))
                    if (NumeroPer(p.CondizioneUid, p.EsecuzioneIterazione) is { } n && n != p.EsecuzioneIterazione)
                    { p.EsecuzioneIterazione = n; modificata = true; }
                if (copia.CondizioneOrigineUid is { } origine && StessoTest(copia.TestUid, copia.TestCaseId) &&
                    NumeroPer(origine, copia.EsecuzioneIterazione) is { } no && no != copia.EsecuzioneIterazione)
                { copia.EsecuzioneIterazione = no; modificata = true; }
                if (!modificata) continue;
                state.NonConformita.Save(copia, originale.RevisioneDati);
                nc++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            { errori.Add($"NC {originale.Id}: {ex.Message}"); }
        }

        foreach (var originale in state.Segnalazioni.GetAll())
        {
            try
            {
                if (!StessoTest(originale.TestUid, originale.TestId)) continue;
                if (NumeroPer(originale.CondizioneUid, originale.EsecuzioneIterazione) is not { } n || n == originale.EsecuzioneIterazione) continue;
                var copia = JsonSerializer.Deserialize<SegnalazioneDaClassificare>(JsonSerializer.Serialize(originale, JsonIo.Options), JsonIo.Options)!;
                copia.EsecuzioneIterazione = n;
                state.Segnalazioni.Save(copia, originale.RevisioneDati);
                seg++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            { errori.Add($"Segnalazione {originale.Uid}: {ex.Message}"); }
        }

        foreach (var originale in state.ChangeRequests.GetAll(includeArchiviate: true))
        {
            try
            {
                if (!originale.TestValidazione.Any(l => string.Equals(l.TestId, unito.Id, StringComparison.OrdinalIgnoreCase) && l.EsecuzioneValidante.HasValue)) continue;
                var copia = JsonSerializer.Deserialize<ChangeRequest>(JsonSerializer.Serialize(originale, JsonIo.Options), JsonIo.Options)!;
                var modificata = false;
                foreach (var l in copia.TestValidazione.Where(l => string.Equals(l.TestId, unito.Id, StringComparison.OrdinalIgnoreCase) && l.EsecuzioneValidante.HasValue))
                {
                    // Identita' della REV validante = data di esecuzione registrata nello snapshot (il numero e' solo un'etichetta).
                    if (l.SnapshotValidazione is not { } snap) continue;
                    var rev = unito.Esecuzioni.FirstOrDefault(e => e.Data == snap.EseguitaIl);
                    if (rev != null && rev.Iterazione != l.EsecuzioneValidante) { l.EsecuzioneValidante = rev.Iterazione; modificata = true; }
                }
                if (!modificata) continue;
                state.ChangeRequests.Save(copia, originale.RevisioneDati);
                cr++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            { errori.Add($"CR {originale.Id}: {ex.Message}"); }
        }
        return new EsitoRiallineamentoRev(nc, seg, cr, errori);
    }
}
