namespace TestLoom.App.Services;

/// <summary>
/// Precaricamento della pagina Anomalie/CR per lo splash: costruisce la proiezione di elenco delle Change Request (lettura parallela dei
/// soli record cambiati, ordinamenti per colonna) e scalda le strutture delle Non Conformita' (mappe per Id/Uid, indice SQLite, assegnatari).
/// Dopo il precarico la pagina non deserializza piu' le CR a ogni apertura/filtro. Chiamabile dallo splash dopo il bootstrap di AppState;
/// non lancia per guasti dell'indice (la pagina ricade sulla lettura storica), propaga solo l'annullamento.
/// </summary>
public static class AnomalieCrPrecarico
{
    private const int PassiNc = 3;

    /// <param name="progresso">(fatti, totale): unita' di lavoro cumulative (righe CR lette + passi NC); il totale puo' crescere quando il numero di righe CR da leggere e' noto.</param>
    public static async Task PrecaricaAsync(AppState state, IProgress<(int fatti, int totale)>? progresso, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.CartellaValida) { progresso?.Report((1, 1)); return; }
        var crFatti = 0; var crTotale = 0; var ncFatti = 0;
        void Riporta() => progresso?.Report((Volatile.Read(ref crFatti) + Volatile.Read(ref ncFatti), Volatile.Read(ref crTotale) + PassiNc));
        Riporta();
        var nc = state.NonConformita.PrecaricaAsync(new SincronoProgress<(int fatti, int totale)>(p => { Volatile.Write(ref ncFatti, p.fatti); Riporta(); }), ct);
        var cr = state.ChangeRequests.PrecaricaAsync(new SincronoProgress<(int fatti, int totale)>(p =>
        {
            Volatile.Write(ref crTotale, p.totale); Volatile.Write(ref crFatti, p.fatti); Riporta();
        }), ct);
        await Task.WhenAll(nc, cr).ConfigureAwait(false);
        progresso?.Report((Volatile.Read(ref crTotale) + PassiNc, Volatile.Read(ref crTotale) + PassiNc));
    }

    /// <summary>Progresso senza marshalling su un contesto di sincronizzazione: il chiamante (splash) inoltra al thread UI come preferisce.</summary>
    private sealed class SincronoProgress<T> : IProgress<T>
    {
        private readonly Action<T> _azione;
        public SincronoProgress(Action<T> azione) => _azione = azione;
        public void Report(T value) => _azione(value);
    }
}
