using TestLoom.App.Models;
using System.Security.Cryptography;
using System.Text;

namespace TestLoom.App.Services;

/// <summary>Origine effettiva del valore mostrato in una cella (Requisito, Componente, Veicolo) della matrice Audit,
/// dopo la risoluzione completa (Piano 6.3.1 §1). A differenza di <see cref="OrigineValutazioneRequisito"/>
/// (che vive solo sulle righe salvate Manuale/Propagata) questo enum include anche il caso "Diretto", che non
/// ha una riga persistita: il valore segue sempre l'ultima esecuzione del/dei Test diretti su quel veicolo.</summary>
public enum OrigineCellaRequisitoEcuVeicolo
{
    Diretto,
    Manuale,
    Propagata,
}

/// <summary>Un altro veicolo della stessa componente da cui la cella (Requisito, Componente, veicolo richiesto) potrebbe
/// copiare un valore se un operatore lo confermasse esplicitamente (Piano 6.3.1 §1, "!"). TestIdSorgente resta
/// vuoto quando il candidato viene da una valutazione già salvata sull'altro veicolo (Manuale o Propagata a
/// sua volta), non da un Test diretto.</summary>
public sealed record CandidatoPropagazioneRequisito(
    string VeicoloSorgente,
    string TestIdSorgente,
    PunteggioConformita PunteggioSorgente,
    DateTime DataSorgente);

/// <summary>Una cella (Requisito, Componente) del veicolo V che la Propagazione massiva (Piano 6.4.0) propagherebbe,
/// con la sorgente scelta deterministicamente (candidati concordi: più recente per data, spareggio per nome
/// veicolo — vedi <see cref="ValutazioneRequisitoEcuVeicoloService.DecidiPropagazioneMassiva"/>).</summary>
public sealed record VocePropagazioneMassiva(string Componente, string RequisitoId, CandidatoPropagazioneRequisito Sorgente);

/// <summary>Una cella (Requisito, Componente) del veicolo V che la Propagazione massiva lascia "!" perché i candidati
/// disponibili non concordano sullo stesso punteggio: va decisa a mano con il dialogo "!" esistente.</summary>
public sealed record ConflittoPropagazioneMassiva(string Componente, string RequisitoId, IReadOnlyList<CandidatoPropagazioneRequisito> Candidati);

/// <summary>Anteprima (nessuna scrittura) di una Propagazione massiva per il veicolo V su un insieme di componente:
/// mostrata all'operatore prima della conferma (Piano 6.4.0, punto 4).</summary>
public sealed record AnteprimaPropagazioneMassiva(
    IReadOnlyList<VocePropagazioneMassiva> DaPropagare,
    IReadOnlyList<ConflittoPropagazioneMassiva> Conflitti);

/// <summary>Una valutazione veicolo-specifica Manuale/Propagata inclusa nella rimozione a livello requisito.</summary>
public sealed record VoceRimozioneValutazioneRigaAudit(
    Guid Uid,
    string Componente,
    PunteggioConformita Punteggio,
    OrigineValutazioneRequisito Origine,
    string Nota,
    string ImprontaEntita);

/// <summary>Anteprima senza scritture della rimozione a livello requisito sul solo veicolo selezionato.
/// Oltre alle valutazioni indica l'intero perimetro componente e le fonti esterne per ogni cella: così il commit può
/// rifiutare una fotografia incompleta o modificata dopo la conferma.</summary>
public sealed record AnteprimaRimozioneValutazioniRigaAudit(
    string RequisitoId,
    string Veicolo,
    IReadOnlyList<string> EcuAssociate,
    IReadOnlyDictionary<string, string> ImpronteFontiEsternePerEcu,
    IReadOnlyList<VoceRimozioneValutazioneRigaAudit> Valutazioni)
{
    public int NumeroValutazioni => Valutazioni.Count;
    public bool HaValutazioni => Valutazioni.Count > 0;
}

/// <summary>Fonti rilette subito prima del commit di una rimozione a livello requisito.</summary>
public sealed record FontiRimozioneValutazioniRigaAudit(
    IReadOnlyList<Componente> Componenti,
    IReadOnlyList<TestCase> TestCases,
    IReadOnlyList<RisoluzioneConflittoAudit> Risoluzioni);

/// <summary>Una valutazione veicolo-specifica Manuale/Propagata inclusa nella rimozione a livello componente.</summary>
public sealed record VoceRimozioneValutazioneColonnaAudit(
    Guid Uid,
    string RequisitoId,
    PunteggioConformita Punteggio,
    OrigineValutazioneRequisito Origine,
    string Nota,
    string ImprontaEntita);

/// <summary>Anteprima senza scritture della rimozione di tutte le valutazioni Manuali/Propagate di un componente e
/// del veicolo selezionato. Le impronte dei requisiti attivi e delle fonti esterne rendono il commit fail-closed
/// se cambia il perimetro o una fonte tra conferma e salvataggio.</summary>
public sealed record AnteprimaRimozioneValutazioniColonnaAudit(
    string Componente,
    string Veicolo,
    bool EcuAssociataAlVeicolo,
    IReadOnlyDictionary<string, string> ImpronteRequisitiAttivi,
    IReadOnlyDictionary<string, string> ImpronteFontiEsternePerRequisito,
    IReadOnlyList<VoceRimozioneValutazioneColonnaAudit> Valutazioni)
{
    public int NumeroValutazioni => Valutazioni.Count;
    public bool HaValutazioni => Valutazioni.Count > 0;
}

/// <summary>Fonti rilette subito prima del commit di una rimozione a livello componente.</summary>
public sealed record FontiRimozioneValutazioniColonnaAudit(
    IReadOnlyList<Componente> Componenti,
    IReadOnlyList<Requisito> Requisiti,
    IReadOnlyList<TestCase> TestCases,
    IReadOnlyList<RisoluzioneConflittoAudit> Risoluzioni);

/// <summary>Esito del salvataggio di una Propagazione massiva: <see cref="Propagate"/> sono le celle
/// effettivamente scritte, <see cref="ConflittiRimasti"/> quelle lasciate "!" perché il ricalcolo fresco al
/// momento del salvataggio le trova ancora (o di nuovo) discordanti, <see cref="SaltatePerchèCambiate"/> le
/// celle che l'anteprima proponeva ma che al momento del salvataggio non risolvono più ESATTAMENTE come
/// confermato dall'operatore (review indipendente 2026-09-26: non basta che la cella sia ancora "!" — deve
/// risolvere a un'unica sorgente con lo STESSO punteggio mostrato nell'anteprima; una sorgente diversa ma con
/// lo stesso punteggio è accettata e la riga scritta registra quella sorgente fresca, qualunque altra
/// differenza — punteggio cambiato, conflitto emerso o cella già risolta da un'altra postazione — finisce
/// qui, mai scritta). Nessun retry automatico: se <see cref="PropagaMassiva"/> fallisce per una scrittura
/// concorrente (MasterDataConflictException), NON viene scritto nulla e l'operatore deve ripetere l'intera
/// azione (anteprima + conferma) da capo.</summary>
public sealed record RisultatoPropagazioneMassiva(
    IReadOnlyList<VocePropagazioneMassiva> Propagate,
    IReadOnlyList<ConflittoPropagazioneMassiva> ConflittiRimasti,
    IReadOnlyList<(string Componente, string RequisitoId)> SaltatePerchèCambiate);

/// <summary>Una cella veicolo-specifica selezionata per l'applicazione manuale dalla matrice Audit.</summary>
public sealed record CellaApplicazioneManualeAudit(
    string Componente, string RequisitoId, string Veicolo, bool Sovrascrive, bool HaValutazioneVeicoloSalvata);

/// <summary>Fotografia di una cella selezionata prima dell'eventuale conferma di sovrascrittura.</summary>
public sealed record CellaApplicazioneManualeAuditAttesa(
    string Componente, string RequisitoId, string Veicolo, bool Sovrascrive, bool HaValutazioneVeicoloSalvata, string ImprontaAttesa)
{
    /// <summary>Fingerprint dei Test diretti e dell'override legacy visibili nella cella; null per i chiamanti
    /// compatibili che non forniscono una fotografia delle fonti esterne.</summary>
    public string? ImprontaFontiEsterneAttesa { get; init; }
}

public sealed record VocePropagazioneAuditAttesa(
    string Veicolo, CandidatoPropagazioneRequisito Candidato, string ImprontaAttesa);

/// <summary>Esito della risoluzione di UNA cella (Requisito, Componente, Veicolo), con la fonte a timestamp più recente
/// tra Test diretto, Manuale locale, Propagata e Manuale legacy globale; "!" si calcola solo se manca un valore.
/// Oggetto puramente di lettura, non persistito: chi lo consuma (AuditMatriceService
/// per la matrice/i KPI, oppure direttamente un editor cella) decide cosa mostrarne.</summary>
public sealed class RisoluzioneCellaRequisitoEcuVeicolo
{
    public required string RequisitoId { get; init; }
    public required string Componente { get; init; }
    public required string Veicolo { get; init; }

    /// <summary>True se la cella ha un Test diretto su questo veicolo, con Punteggio univoco tra i candidati
    /// diretti (nessun conflitto, o conflitto già risolto dalla RisoluzioneConflittoAudit esistente per la
    /// coppia Componente×Requisito — invariato rispetto a prima di questo piano).</summary>
    public bool Diretto { get; init; }

/// <summary>Conflitto tra fonti più recenti con timestamp pari: include il conflitto diretto tra Test e i
/// pareggi tra Test, valutazione Manuale locale, legacy globale e Propagata. Quando è true, Punteggio/Origine
/// restano null. Se il pareggio è solo fra Test, CandidatiConflittoDiretto permette la scelta esistente;
/// negli altri casi un nuovo inserimento manuale crea una decisione più recente.</summary>
    public bool ConflittoDiretto { get; init; }
    public IReadOnlyList<CandidatoConflitto> CandidatiConflittoDiretto { get; init; } = Array.Empty<CandidatoConflitto>();
    public string? DettaglioConflitto { get; init; }

    /// <summary>True quando esiste almeno un Test collegato a questo veicolo per la coppia Componente×Requisito,
    /// ma nessuno dei suoi Test ha ancora un'esecuzione con punteggio per questo Requisito (equivalente per
    /// veicolo di StatoCellaAudit.TestCreatoNonEseguito).</summary>
    public bool TestCreatoNonEseguito { get; init; }

    public PunteggioConformita? Punteggio { get; init; }
    public OrigineCellaRequisitoEcuVeicolo? Origine { get; init; }
    /// <summary>True quando il valore arriva dal campo manuale componente×requisito privo di dimensione veicolo.</summary>
    public bool InserimentoManuale { get; init; }
    public DateTime? Data { get; init; }
    public string? BaselineSw { get; init; }
    public string? Nota { get; init; }

    /// <summary>Testo pronto per il tooltip quando Origine è Manuale o Propagata (vedi
    /// <see cref="ValutazioneRequisitoEcuVeicoloService.TestoTooltip"/>); null per Diretto o per una cella
    /// senza valore.</summary>
    public string? Tooltip { get; init; }

    /// <summary>True se, in assenza di Diretto/valore salvato, esiste almeno un altro veicolo della stessa
    /// componente con un valore da cui questa cella potrebbe copiare (stato "!" — vedi CandidatiPropagazione). Quando
    /// true, Punteggio/Origine restano null: l'utente deve confermare esplicitamente la propagazione.</summary>
    public bool Propagabile { get; init; }
    public IReadOnlyDictionary<string, CandidatoPropagazioneRequisito> CandidatiPropagazione { get; init; } =
        new Dictionary<string, CandidatoPropagazioneRequisito>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True quando Origine è Manuale o Propagata E la cella ha una collisione di chiave business
    /// Phase D non ancora riparata (vedi PhaseDMasterKeyCollisionTolerance) con valori significativi
    /// discordanti tra le righe collise: Punteggio/Origine/Data restano quelli della riga vincente
    /// (deterministica, UltimaModifica più recente/Uid a parità), ma l'operatore deve vedere che un'altra
    /// postazione ha scritto un valore diverso quasi in contemporanea — la pagina Audit mostra questa cella
    /// come conflitto da rivedere invece che come un valore ordinario. False quando i duplicati hanno gli
    /// STESSI valori significativi (consolidamento silenzioso, nessun conflitto da mostrare) o quando non
    /// c'è alcuna collisione.</summary>
    public bool ConflittoCollisioneCella { get; init; }
}

/// <summary>
/// Servizio puro (nessuna dipendenza da AppState/ViewModel) per la risoluzione e la gestione delle celle
/// (Requisito × componente × Veicolo) della matrice Audit secondo il Piano 6.3.1 §1. Le sole due operazioni che
/// toccano disco (<see cref="Propaga"/> e <see cref="ImpostaManuale"/>) seguono lo stesso pattern
/// repo.Load() → muta → repo.Save(desired, baseline) con optimistic-concurrency già usato da
/// AuditViewModel.ProvaAggiornaRisoluzioni per RisoluzioneConflittoAudit — qui centralizzato cosi' l'editor
/// UI (in arrivo da un altro sviluppatore sopra questo lavoro) non deve reimplementare il retry-safe save.
///
/// AuditMatriceService resta il proprietario del calcolo "Diretto" (hot path, indici O(1) — vedi
/// <see cref="AuditMatriceService.CandidatiDiretti"/>, condiviso da questo servizio per non duplicare la
/// scansione Esecuzioni/PunteggiRequisiti): qui vive solo la logica propria della dimensione veicolo aggiunta
/// da questo piano (valutazioni salvate, propagabilità, persistenza, tooltip).
/// </summary>
public static class ValutazioneRequisitoEcuVeicoloService
{
    private static string ImprontaFontiEsterneCella(
        string componente, string requisitoId, string veicolo,
        IReadOnlyList<TestCase> testCases,
        IReadOnlyList<RisoluzioneConflittoAudit> risoluzioni)
    {
        var testDellaCoppia = testCases.Where(tc =>
            tc.Componente.Contains(componente, StringComparer.OrdinalIgnoreCase) &&
            tc.RequisitoId.Contains(requisitoId, StringComparer.OrdinalIgnoreCase));
        // Include anche i Test ancora senza punteggio: la loro creazione/assegnazione può cambiare la cella da
        // vuota a TestCreato, e un Test componente-generico può cambiare i candidati di propagazione. Il fingerprint
        // del contenuto rileva anche una REV/veicolo aggiornata tra anteprima e conferma.
        var fontiTest = testDellaCoppia
            .OrderBy(tc => tc.Id, StringComparer.OrdinalIgnoreCase)
            .Select(tc => new
            {
                tc.Id,
                Contenuto = TestCaseFingerprint.GetStored(tc) ?? TestCaseFingerprint.Compute(tc),
            }).ToList();
        var diretti = AuditMatriceService.CandidatiDiretti(requisitoId, testDellaCoppia, veicolo)
            .OrderBy(c => c.TestCaseId, StringComparer.OrdinalIgnoreCase)
            .Select(c => new { c.TestCaseId, c.Punteggio, c.Data }).ToList();
        var legacy = risoluzioni.Where(r => r.Componente.Equals(componente, StringComparison.OrdinalIgnoreCase) &&
                r.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => EntityStateFingerprint.Compute(r), StringComparer.Ordinal)
            .ToList();
        return EntityStateFingerprint.Compute(new { Test = fontiTest, Diretti = diretti, Legacy = legacy });
    }

    /// <summary>Unisce i punteggi Test ricostruibili dalle REV e gli snapshot Phase D della cella. Non usa i
    /// campi correnti del Test per attribuire una revisione storica: una REV senza Dettagli non è associabile
    /// con certezza a un veicolo/componente e quindi non entra nello storico della cella.</summary>
    public static IReadOnlyList<SnapshotValutazioneAudit> CostruisciStoricoCella(
        string requisitoId,
        string componente,
        string veicolo,
        IReadOnlyList<TestCase> testCases,
        IReadOnlyList<ValutazioneRequisitoEcuVeicolo> valutazioni,
        IReadOnlyList<RisoluzioneConflittoAudit> risoluzioni)
    {
        var eventi = new Dictionary<Guid, SnapshotValutazioneAudit>();
        foreach (var test in testCases)
        foreach (var revisione in test.Esecuzioni)
        {
            var dettagli = revisione.Dettagli;
            if (dettagli == null || !dettagli.Componente.Contains(componente, StringComparison.OrdinalIgnoreCase)) continue;
            IEnumerable<string> veicoliDellaRev = dettagli.Veicoli.Count > 0
                ? dettagli.Veicoli
                : string.IsNullOrWhiteSpace(dettagli.Veicolo) ? Array.Empty<string>() : new[] { dettagli.Veicolo };
            if (!veicoliDellaRev.Contains(veicolo, StringComparer.OrdinalIgnoreCase)) continue;

            foreach (var punteggio in revisione.PunteggiRequisiti.Where(p =>
                         p.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase)))
            {
                var evento = new SnapshotValutazioneAudit
                {
                    EventId = IdDeterministico($"test|{test.Id}|{revisione.Iterazione}|{requisitoId}|{componente}|{veicolo}|{revisione.Data:O}"),
                    Origine = OrigineStoricoValutazioneAudit.Test,
                    Punteggio = punteggio.Valore,
                    Data = revisione.Data,
                    Operatore = revisione.Operatore,
                    Nota = revisione.Note,
                    BaselineSw = revisione.BaselineSw,
                    TestCaseId = test.Id,
                    IterazioneTest = revisione.Iterazione,
                };
                AggiungiSnapshotUnico(eventi, evento);
            }
        }

        foreach (var valutazione in valutazioni.Where(v => StessaCella(v, componente, requisitoId, veicolo)))
        {
            foreach (var snapshot in valutazione.Storico ?? new List<SnapshotValutazioneAudit>())
                AggiungiSnapshotUnico(eventi, CopiaSnapshot(snapshot));
            if (!valutazione.IsArchiviata)
                AggiungiSnapshotUnico(eventi, SnapshotCorrente(valutazione));
        }

        foreach (var risoluzione in risoluzioni.Where(r =>
                     r.Componente.Equals(componente, StringComparison.OrdinalIgnoreCase) &&
                     r.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var snapshot in risoluzione.StoricoPunteggiManuali ?? new List<SnapshotValutazioneAudit>())
                AggiungiSnapshotUnico(eventi, CopiaSnapshot(snapshot));
            if (risoluzione.PunteggioManuale.HasValue)
            {
                AggiungiSnapshotUnico(eventi, new SnapshotValutazioneAudit
                {
                    EventId = risoluzione.EventoPunteggioManualeCorrenteId ?? IdDeterministico(
                        $"legacy|{risoluzione.Componente}|{risoluzione.RequisitoId}|{risoluzione.UltimaModifica:O}|{risoluzione.PunteggioManuale}|{risoluzione.Motivazione}"),
                    Origine = OrigineStoricoValutazioneAudit.Legacy,
                    Punteggio = risoluzione.PunteggioManuale.Value,
                    Data = risoluzione.UltimaModifica,
                    Operatore = risoluzione.ModificatoDa,
                    Nota = risoluzione.Motivazione,
                });
            }
        }

        return eventi.Values
            .OrderByDescending(e => e.Data)
            .ThenBy(e => e.Origine)
            .ThenBy(e => e.EventId)
            .ToList();
    }

    private static SnapshotValutazioneAudit SnapshotCorrente(ValutazioneRequisitoEcuVeicolo v) => new()
    {
        EventId = v.EventoCorrenteId ?? IdDeterministico(
            $"cella|{v.Uid}|{v.UltimaModifica:O}|{v.Origine}|{v.Punteggio}|{v.Operatore}|{v.Nota}|{v.BaselineSw}|{v.SorgenteVeicolo}|{v.SorgenteTestId}"),
        Origine = v.Origine == OrigineValutazioneRequisito.Propagata
            ? OrigineStoricoValutazioneAudit.Propagata
            : OrigineStoricoValutazioneAudit.Manuale,
        Punteggio = v.Punteggio,
        Data = v.UltimaModifica,
        Operatore = v.Operatore,
        Nota = v.Nota,
        BaselineSw = v.BaselineSw,
        VeicoloSorgente = v.SorgenteVeicolo,
        TestCaseId = v.SorgenteTestId,
    };

    private static SnapshotValutazioneAudit CopiaSnapshot(SnapshotValutazioneAudit s) => new()
    {
        EventId = s.EventId,
        Origine = s.Origine,
        Punteggio = s.Punteggio,
        Data = s.Data,
        Operatore = s.Operatore,
        Nota = s.Nota,
        BaselineSw = s.BaselineSw,
        VeicoloSorgente = s.VeicoloSorgente,
        TestCaseId = s.TestCaseId,
        IterazioneTest = s.IterazioneTest,
    };

    private static void AggiungiSnapshotUnico(
        IDictionary<Guid, SnapshotValutazioneAudit> eventi,
        SnapshotValutazioneAudit snapshot)
    {
        if (snapshot.EventId == Guid.Empty) snapshot.EventId = IdDeterministico(
            $"snapshot|{snapshot.Origine}|{snapshot.Data:O}|{snapshot.Operatore}|{snapshot.Punteggio}|{snapshot.Nota}|{snapshot.TestCaseId}|{snapshot.IterazioneTest}");
        eventi.TryAdd(snapshot.EventId, snapshot);
    }

    private static Guid IdDeterministico(string valore)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(valore));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void AccodaSnapshotCella(ValutazioneRequisitoEcuVeicolo valutazione)
    {
        valutazione.Storico ??= new List<SnapshotValutazioneAudit>();
        var snapshot = SnapshotCorrente(valutazione);
        if (valutazione.Storico.All(e => e.EventId != snapshot.EventId)) valutazione.Storico.Add(snapshot);
    }

    private static void AccodaSnapshotLegacy(RisoluzioneConflittoAudit risoluzione)
    {
        if (!risoluzione.PunteggioManuale.HasValue) return;
        risoluzione.StoricoPunteggiManuali ??= new List<SnapshotValutazioneAudit>();
        var eventoId = risoluzione.EventoPunteggioManualeCorrenteId ?? IdDeterministico(
            $"legacy|{risoluzione.Componente}|{risoluzione.RequisitoId}|{risoluzione.UltimaModifica:O}|{risoluzione.PunteggioManuale}|{risoluzione.Motivazione}");
        if (risoluzione.StoricoPunteggiManuali.All(e => e.EventId != eventoId))
        {
            risoluzione.StoricoPunteggiManuali.Add(new SnapshotValutazioneAudit
            {
                EventId = eventoId,
                Origine = OrigineStoricoValutazioneAudit.Legacy,
                Punteggio = risoluzione.PunteggioManuale.Value,
                Data = risoluzione.UltimaModifica,
                Operatore = risoluzione.ModificatoDa,
                Nota = risoluzione.Motivazione,
            });
        }
    }

    /// <summary>Archivia il valore manuale legacy corrente prima di sostituirlo o rimuoverlo. Non modifica
    /// TestCaseIdValido, che è una decisione separata e può coesistere nella stessa riga.</summary>
    public static void ArchiviaPunteggioManualeLegacy(RisoluzioneConflittoAudit risoluzione) =>
        AccodaSnapshotLegacy(risoluzione);

    private static void SalvaValoreCella(
        List<ValutazioneRequisitoEcuVeicolo> valutazioni,
        string requisitoId,
        string componente,
        string veicolo,
        PunteggioConformita punteggio,
        OrigineValutazioneRequisito origine,
        string nota,
        string operatore,
        string? baselineSw,
        string sorgenteVeicolo,
        string sorgenteTestId,
        PunteggioConformita? sorgentePunteggio,
        DateTime adesso)
    {
        var corrente = valutazioni.FirstOrDefault(v => StessaCella(v, componente, requisitoId, veicolo));
        var chiudeCollisionePrecedente = corrente != null && RiparazioneConValoriDiscordantiNonRisolta(corrente);
        if (corrente == null)
        {
            corrente = new ValutazioneRequisitoEcuVeicolo
            {
                RequisitoId = requisitoId,
                Componente = componente,
                Veicolo = veicolo,
                CreatoDa = operatore,
            };
            valutazioni.Add(corrente);
        }
        else if (!corrente.IsArchiviata)
        {
            AccodaSnapshotCella(corrente);
        }

        corrente.IsArchiviata = false;
        corrente.EventoCorrenteId = Guid.NewGuid();
        corrente.Punteggio = punteggio;
        corrente.Origine = origine;
        corrente.SorgenteVeicolo = sorgenteVeicolo;
        corrente.SorgenteTestId = sorgenteTestId;
        corrente.SorgentePunteggio = sorgentePunteggio;
        corrente.BaselineSw = baselineSw;
        corrente.Nota = nota;
        corrente.Operatore = operatore;
        corrente.UltimaModifica = adesso;
        corrente.ModificatoDa = operatore;

        // La riga viene riutilizzata per mantenere UID e storico append-only. Registra la chiusura esplicita
        // del conflitto nel Changelog invece di cancellare il marcatore della riparazione automatica.
        if (chiudeCollisionePrecedente)
        {
            corrente.Changelog.Add(new ChangelogEntry
            {
                Data = adesso,
                Operatore = operatore,
                Campo = CampoRisoluzioneCollisioneCella,
                ValorePrecedente = "collisione di cella da verificare",
                ValoreNuovo = $"cella rivalutata esplicitamente — {origine} {((int)punteggio)}",
            });
        }
    }

    /// <summary>Ultima valutazione salvata per ciascun veicolo, dato l'insieme di righe di UNA coppia
    /// Requisito×Componente (su tutti i veicoli): stesso schema "vince l'ultima per UltimaModifica" già usato da
    /// AuditMatriceService per RisoluzioneConflittoAudit. Nell'uso normale (Propaga/ImpostaManuale, RemoveAll
    /// prima di Add) c'è sempre al più una riga attiva per cella; questo raggruppamento resta comunque la
    /// difesa corretta anche nel caso limite in cui più righe la referenzino — PhaseDMasterRepositoryEngine.
    /// ValidateBusinessKeys impedisce a un Save di introdurre una VERA collisione sulla stessa chiave business
    /// Requisito|Componente|Veicolo (fail-closed: l'operazione che la causerebbe, es. un'unione componente su due righe che
    /// finirebbero sulla stessa cella, fallisce chiusa e non scrive nulla — vedi
    /// ValutazioneRequisitoEcuVeicoloUnisciDividiTests), ma un client legacy privo di quel controllo o un
    /// import diretto del JSON potrebbe comunque produrre un duplicato: qui si sceglie deliberatamente di non
    /// fidarsi ciecamente dell'unicità e di restare corretti anche in quel caso raro.</summary>
    public static Dictionary<string, ValutazioneRequisitoEcuVeicolo> UltimaPerVeicolo(
        IEnumerable<ValutazioneRequisitoEcuVeicolo> valutazioniDellaCoppiaRequisitoEcu) =>
        valutazioniDellaCoppiaRequisitoEcu
            .Where(v => !v.IsArchiviata)
            .GroupBy(v => v.Veicolo, StringComparer.OrdinalIgnoreCase)
            // ThenBy(Uid) (fix 6.4.0, collisione chiave business): a parita' di UltimaModifica (es. due righe
            // nate quasi nello stesso istante da una collisione Phase D non ancora riparata — vedi
            // PhaseDMasterKeyCollisionTolerance) la scelta deve essere deterministica su OGNI postazione che
            // legge lo stesso journal, non dipendere dall'ordine di iterazione/lettura.
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.UltimaModifica).ThenBy(v => v.Uid).First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Raggruppa TUTTE le righe di <paramref name="tutteLeValutazioni"/> per (Componente, RequisitoId), SENZA
    /// alcuna riduzione per veicolo: a differenza di <see cref="Indicizza"/> conserva quindi anche eventuali
    /// righe collise sulla stessa cella (stessa chiave RequisitoId|Componente|Veicolo, UID diverse — vedi
    /// PhaseDMasterKeyCollisionTolerance), necessarie a <see cref="RisolviCella"/> per decidere
    /// deterministicamente la vincente E segnalare il conflitto quando i valori differiscono
    /// (RisoluzioneCellaRequisitoEcuVeicolo.ConflittoCollisioneCella).</summary>
    public static Dictionary<(string Componente, string ReqId), List<ValutazioneRequisitoEcuVeicolo>> RaggruppaPerCoppia(
        IReadOnlyList<ValutazioneRequisitoEcuVeicolo> tutteLeValutazioni)
    {
        var perCoppia = new Dictionary<(string Componente, string ReqId), List<ValutazioneRequisitoEcuVeicolo>>();
        foreach (var v in tutteLeValutazioni)
        {
            if (v.IsArchiviata) continue;
            var chiave = (v.Componente, v.RequisitoId);
            if (!perCoppia.TryGetValue(chiave, out var lista)) perCoppia[chiave] = lista = new();
            lista.Add(v);
        }
        return perCoppia;
    }

    /// <summary>Indicizza le valutazioni per (RequisitoId, Componente) → per Veicolo → ultima riga, con lo stesso
    /// schema di indicizzazione O(1) di AuditMatriceService.IndicizzaTestPerEcuERequisito: evita di rifiltrare
    /// l'intero catalogo per ogni cella quando si scorre la matrice completa (hot path, gate 700 ms/100k).
    /// Riduce SEMPRE a una riga per veicolo (vedi UltimaPerVeicolo): un chiamante che deve anche rilevare/
    /// mostrare un conflitto di collisione per cella usa invece <see cref="RaggruppaPerCoppia"/> + RisolviCella
    /// direttamente (vedi AuditMatriceService.Matrice).</summary>
    public static Dictionary<(string Componente, string ReqId), Dictionary<string, ValutazioneRequisitoEcuVeicolo>> Indicizza(
        IReadOnlyList<ValutazioneRequisitoEcuVeicolo> tutteLeValutazioni) =>
        RaggruppaPerCoppia(tutteLeValutazioni).ToDictionary(kv => kv.Key, kv => UltimaPerVeicolo(kv.Value));

    /// <summary>Veicoli della stessa componente (Componente.VeicoliAssociati) da cui la cella (requisitoId, componente.Nome,
    /// veicoloNome) potrebbe copiare un valore, con il candidato trovato per ciascuno. Scelta esplicita
    /// (Piano 6.3.1 §1, "veicoli associati al componente e/o veicoli presenti nei test"): l'universo dei veicoli
    /// candidabili è Componente.VeicoliAssociati (i veicoli che montano davvero quell'componente secondo il master, incluso
    /// un veicolo mai ancora testato per questo requisito ma con una valutazione già salvata), NON i soli
    /// veicoli visti nei Test — che sarebbero un sottoinsieme già coperto da VeicoliAssociati in dati corretti,
    /// e mancherebbero un veicolo la cui unica prova è una valutazione salvata invece di un Test diretto.
    /// Per ciascun altro veicolo la priorità è: Test diretto più recente su quel veicolo (stessa logica di
    /// AuditMatriceService.CandidatiDiretti, punteggio dell'ultima esecuzione), altrimenti la sua valutazione
    /// salvata più recente (Manuale o Propagata a sua volta — una propagazione "a catena" resta comunque
    /// tracciabile fino alla sorgente originale attraverso SorgenteVeicolo/SorgenteTestId della riga copiata).</summary>
    public static Dictionary<string, CandidatoPropagazioneRequisito> CandidatiPropagazione(
        string requisitoId,
        Componente componente,
        string veicoloNome,
        IReadOnlyList<TestCase> testDellaCoppiaRequisitoEcu,
        IReadOnlyDictionary<string, ValutazioneRequisitoEcuVeicolo> ultimaValutazionePerVeicolo)
    {
        return CandidatiPropagazioneCore(componente, veicoloNome,
            altroVeicolo => AuditMatriceService.CandidatiDiretti(requisitoId, testDellaCoppiaRequisitoEcu, altroVeicolo),
            ultimaValutazionePerVeicolo,
            AuditMatriceService.CandidatiEcuGenerici(requisitoId, testDellaCoppiaRequisitoEcu));
    }

    /// <summary>Come sopra, ma richiede i candidati diretti già precalcolati UNA VOLTA per l'intera coppia
    /// Componente×Requisito (vedi AuditMatriceService.CandidatiDirettiPerVeicolo) invece di rifare una scansione
    /// completa di testDellaCoppiaRequisitoEcu per ciascun altro veicolo di Componente.VeicoliAssociati. Da preferire
    /// quando lo stesso caller deve risolvere la propagabilità per più veicoli della stessa coppia (review
    /// 2026-09-24, HIGH: il costo del calcolo per-cella diventava quadratico nel numero di veicoli condivisi
    /// da un componente — vedi AuditMatriceService.CalcolaKpiComplessivi, unico chiamante ad oggi).</summary>
    public static Dictionary<string, CandidatoPropagazioneRequisito> CandidatiPropagazione(
        Componente componente,
        string veicoloNome,
        IReadOnlyDictionary<string, List<CandidatoConflitto>> direttiPerVeicolo,
        IReadOnlyDictionary<string, ValutazioneRequisitoEcuVeicolo> ultimaValutazionePerVeicolo,
        IReadOnlyList<CandidatoConflitto>? candidatiEcuGenerici = null)
    {
        return CandidatiPropagazioneCore(componente, veicoloNome,
            altroVeicolo => direttiPerVeicolo.TryGetValue(altroVeicolo, out var lista) ? lista : (IReadOnlyList<CandidatoConflitto>)Array.Empty<CandidatoConflitto>(),
            ultimaValutazionePerVeicolo,
            candidatiEcuGenerici ?? Array.Empty<CandidatoConflitto>());
    }

    /// <summary>Chiave sentinella usata da <see cref="CandidatiPropagazioneCore"/> per un Test "Componente-generico"
    /// (nessun veicolo, dati storici — vedi AuditMatriceService.CandidatiEcuGenerici): non è il nome di un
    /// veicolo reale, quindi non collide con Componente.VeicoliAssociati in dati corretti.</summary>
    public const string SorgenteVeicoloEcuGenerica = "(componente)";

    private static Dictionary<string, CandidatoPropagazioneRequisito> CandidatiPropagazioneCore(
        Componente componente,
        string veicoloNome,
        Func<string, IReadOnlyList<CandidatoConflitto>> direttiAltroVeicoloDi,
        IReadOnlyDictionary<string, ValutazioneRequisitoEcuVeicolo> ultimaValutazionePerVeicolo,
        IReadOnlyList<CandidatoConflitto> candidatiEcuGenerici)
    {
        var risultato = new Dictionary<string, CandidatoPropagazioneRequisito>(StringComparer.OrdinalIgnoreCase);
        foreach (var altroVeicolo in componente.VeicoliAssociati)
        {
            if (string.IsNullOrWhiteSpace(altroVeicolo)) continue;
            if (string.Equals(altroVeicolo, veicoloNome, StringComparison.OrdinalIgnoreCase)) continue;
            if (risultato.ContainsKey(altroVeicolo)) continue;

            var direttiAltroVeicolo = direttiAltroVeicoloDi(altroVeicolo);
            if (direttiAltroVeicolo.Count > 0)
            {
                // Più Test discordanti sull'altro veicolo: nessuna sorgente univoca da proporre qui, la
                // risoluzione della SUA cella (non di questa) è quella che deve prima chiarirlo.
                var puntiDistinti = direttiAltroVeicolo.Select(c => c.Punteggio).Distinct().ToList();
                if (puntiDistinti.Count == 1)
                {
                    var migliore = direttiAltroVeicolo.OrderByDescending(c => c.Data).First();
                    risultato[altroVeicolo] = new CandidatoPropagazioneRequisito(altroVeicolo, migliore.TestCaseId, migliore.Punteggio, migliore.Data);
                }
                continue;
            }

            if (ultimaValutazionePerVeicolo.TryGetValue(altroVeicolo, out var salvata))
                risultato[altroVeicolo] = new CandidatoPropagazioneRequisito(altroVeicolo, salvata.SorgenteTestId, salvata.Punteggio, salvata.UltimaModifica);
        }

        // Test "Componente-generico" (Piano 6.3.1 §1, seconda consegna — semantica dati storici senza veicolo, es.
        // cartella legacy `_SENZA_GAMMA`): mai Diretto per nessun veicolo (VeicoliEffettivi vuoto), ma il suo
        // punteggio resta un valore propagabile a QUALSIASI veicolo del componente — aggiunto solo se non già
        // coperto da una sorgente più specifica (un altro veicolo con un valore proprio) trovata sopra, e solo
        // se i Test componente-generici concordano tra loro (stesso criterio "univoco altrimenti nessuna proposta"
        // degli altri veicoli).
        if (candidatiEcuGenerici.Count > 0)
        {
            var puntiDistinti = candidatiEcuGenerici.Select(c => c.Punteggio).Distinct().ToList();
            if (puntiDistinti.Count == 1)
            {
                var migliore = candidatiEcuGenerici.OrderByDescending(c => c.Data).First();
                risultato[SorgenteVeicoloEcuGenerica] = new CandidatoPropagazioneRequisito(SorgenteVeicoloEcuGenerica, migliore.TestCaseId, migliore.Punteggio, migliore.Data);
            }
        }
        return risultato;
    }

    /// <summary>Risolve una singola cella scegliendo il valore più recente tra Test diretti, Manuale veicolo,
    /// Propagata e inserimento manuale legacy componente×requisito. Un pareggio fra fonti con valori presenti resta
    /// conflittuale. Solo quando non c'è alcun valore la cella può risultare TestCreato o propagabile.
    /// Gli insiemi Test e valutazioni vanno filtrati a monte solo per (Componente, RequisitoId), non per veicolo.</summary>
    public static RisoluzioneCellaRequisitoEcuVeicolo RisolviCella(
        string requisitoId,
        Componente componente,
        string veicoloNome,
        IReadOnlyList<TestCase> testDellaCoppiaRequisitoEcu,
        IReadOnlyList<ValutazioneRequisitoEcuVeicolo> valutazioniDellaCoppiaRequisitoEcu,
        RisoluzioneConflittoAudit? risoluzioneConflittoDiretta = null)
    {
        var valoreRisolto = RisolviValoreCella(requisitoId, componente, veicoloNome,
            testDellaCoppiaRequisitoEcu, valutazioniDellaCoppiaRequisitoEcu, risoluzioneConflittoDiretta);
        if (valoreRisolto != null) return valoreRisolto;

        var testPerVeicolo = testDellaCoppiaRequisitoEcu.Any(tc => tc.VeicoliEffettivi.Contains(veicoloNome, StringComparer.OrdinalIgnoreCase));
        if (testPerVeicolo)
        {
            return new RisoluzioneCellaRequisitoEcuVeicolo
            {
                RequisitoId = requisitoId, Componente = componente.Nome, Veicolo = veicoloNome, TestCreatoNonEseguito = true,
            };
        }

        var ultimaValutazionePerVeicolo = UltimaPerVeicolo(valutazioniDellaCoppiaRequisitoEcu);
        var candidatiPropagazione = CandidatiPropagazione(requisitoId, componente, veicoloNome, testDellaCoppiaRequisitoEcu, ultimaValutazionePerVeicolo);
        return new RisoluzioneCellaRequisitoEcuVeicolo
        {
            RequisitoId = requisitoId, Componente = componente.Nome, Veicolo = veicoloNome,
            Propagabile = candidatiPropagazione.Count > 0, CandidatiPropagazione = candidatiPropagazione,
        };
    }

    /// <summary>Risolve solo i valori con punteggio, senza calcolare TestCreato/propagabilità; condivisa con
    /// i KPI per mantenere identica la regola senza introdurre una scansione di propagabilità per ogni cella.</summary>
    public static RisoluzioneCellaRequisitoEcuVeicolo? RisolviValoreCella(
        string requisitoId,
        Componente componente,
        string veicoloNome,
        IReadOnlyList<TestCase> testDellaCoppiaRequisitoEcu,
        IReadOnlyList<ValutazioneRequisitoEcuVeicolo> valutazioniDellaCoppiaRequisitoEcu,
        RisoluzioneConflittoAudit? risoluzioneConflittoDiretta = null)
    {
        var candidatiDiretti = AuditMatriceService.CandidatiDiretti(requisitoId, testDellaCoppiaRequisitoEcu, veicoloNome);
        var latestDirectDate = candidatiDiretti.Count == 0 ? (DateTime?)null : candidatiDiretti.Max(c => c.Data);
        var directLatest = latestDirectDate == null
            ? new List<CandidatoConflitto>()
            : candidatiDiretti.Where(c => c.Data == latestDirectDate.Value).ToList();

        var ultimaValutazionePerVeicolo = UltimaPerVeicolo(valutazioniDellaCoppiaRequisitoEcu);
        ultimaValutazionePerVeicolo.TryGetValue(veicoloNome, out var salvata);

        // La scelta esplicita del vecchio comando "valido" resta una decisione Master datata e compete
        // nella stessa graduatoria temporale con le altre fonti.
        CandidatoConflitto? testScelto = null;
        if (risoluzioneConflittoDiretta is { PunteggioManuale: null, TestCaseIdValido.Length: > 0 } manualeTest)
        {
            var testIdValido = manualeTest.TestCaseIdValido;
            testScelto = candidatiDiretti.FirstOrDefault(c => c.TestCaseId.Equals(testIdValido, StringComparison.OrdinalIgnoreCase));
        }
        if (testScelto != null)
        {
            var dataDecisione = risoluzioneConflittoDiretta!.UltimaModifica > testScelto.Data
                ? risoluzioneConflittoDiretta.UltimaModifica : testScelto.Data;
            directLatest = new List<CandidatoConflitto>
            {
                new() { TestCaseId = testScelto.TestCaseId, Punteggio = testScelto.Punteggio, Data = dataDecisione },
            };
        }
        else if (directLatest.Select(c => c.Punteggio).Distinct().Count() <= 1 && directLatest.Count > 1)
        {
            directLatest = new List<CandidatoConflitto> { directLatest[0] };
        }

        var candidates = new List<(PunteggioConformita Punteggio, DateTime Data, OrigineCellaRequisitoEcuVeicolo Origine,
            ValutazioneRequisitoEcuVeicolo? Valutazione, RisoluzioneConflittoAudit? Legacy, CandidatoConflitto? Test)>();
        if (salvata != null)
        {
            candidates.Add((salvata.Punteggio, salvata.UltimaModifica,
                salvata.Origine == OrigineValutazioneRequisito.Manuale ? OrigineCellaRequisitoEcuVeicolo.Manuale : OrigineCellaRequisitoEcuVeicolo.Propagata,
                salvata, null, null));
        }
        if (risoluzioneConflittoDiretta?.PunteggioManuale is { } legacyPunteggio)
            candidates.Add((legacyPunteggio, risoluzioneConflittoDiretta.UltimaModifica,
                OrigineCellaRequisitoEcuVeicolo.Manuale, null, risoluzioneConflittoDiretta, null));
        foreach (var test in directLatest)
            candidates.Add((test.Punteggio, test.Data, OrigineCellaRequisitoEcuVeicolo.Diretto, null, null, test));

        if (candidates.Count == 0) return null;
        var dataVincente = candidates.Max(c => c.Data);
        var finalisti = candidates.Where(c => c.Data == dataVincente).ToList();
        if (finalisti.Count > 1)
        {
            static string Score(PunteggioConformita p) => p == PunteggioConformita.NonApplicabile ? "N/A" : ((int)p).ToString();
            var fonti = string.Join("; ", finalisti.Select(c => c.Origine switch
            {
                OrigineCellaRequisitoEcuVeicolo.Diretto =>
                    $"Test {c.Test?.TestCaseId} = {Score(c.Punteggio)} ({c.Data:dd/MM/yyyy HH:mm:ss})",
                OrigineCellaRequisitoEcuVeicolo.Propagata when c.Valutazione is { } prop =>
                    $"Propagata = {Score(c.Punteggio)} ({c.Data:dd/MM/yyyy HH:mm:ss}, da {prop.SorgenteVeicolo}" +
                    (string.IsNullOrWhiteSpace(prop.SorgenteTestId) ? "" : $", Test {prop.SorgenteTestId}") +
                    $", operatore {prop.Operatore}, nota: {prop.Nota})",
                _ when c.Legacy is { } legacy =>
                    $"Manuale legacy globale = {Score(c.Punteggio)} ({c.Data:dd/MM/yyyy HH:mm:ss}, operatore {legacy.ModificatoDa}, nota: {legacy.Motivazione})",
                _ when c.Valutazione is { } manuale =>
                    $"Manuale veicolo = {Score(c.Punteggio)} ({c.Data:dd/MM/yyyy HH:mm:ss}, operatore {manuale.Operatore}, nota: {manuale.Nota}, baseline: {manuale.BaselineSw})",
                _ => $"{c.Origine} = {Score(c.Punteggio)} ({c.Data:dd/MM/yyyy HH:mm:ss})",
            }));
            return new RisoluzioneCellaRequisitoEcuVeicolo
            {
                RequisitoId = requisitoId, Componente = componente.Nome, Veicolo = veicoloNome,
                ConflittoDiretto = true,
                CandidatiConflittoDiretto = finalisti.Where(c => c.Test != null).Select(c => c.Test!).ToList(),
                DettaglioConflitto = $"Fonti con timestamp pari ({dataVincente:dd/MM/yyyy HH:mm:ss}): {fonti}.",
            };
        }

        var vincente = finalisti[0];
        if (vincente.Test is { } vincitoreTest)
            return new RisoluzioneCellaRequisitoEcuVeicolo
            {
                RequisitoId = requisitoId, Componente = componente.Nome, Veicolo = veicoloNome,
                Diretto = true, Punteggio = vincitoreTest.Punteggio, Origine = OrigineCellaRequisitoEcuVeicolo.Diretto,
                Data = vincitoreTest.Data,
            };
        if (vincente.Legacy is { } manualeLegacy)
            return new RisoluzioneCellaRequisitoEcuVeicolo
            {
                RequisitoId = requisitoId, Componente = componente.Nome, Veicolo = veicoloNome,
                Punteggio = manualeLegacy.PunteggioManuale, Origine = OrigineCellaRequisitoEcuVeicolo.Manuale,
                Data = manualeLegacy.UltimaModifica, Nota = manualeLegacy.Motivazione, InserimentoManuale = true,
            };

        var valoreSalvato = vincente.Valutazione!;
        return new RisoluzioneCellaRequisitoEcuVeicolo
        {
            RequisitoId = requisitoId, Componente = componente.Nome, Veicolo = veicoloNome,
            Punteggio = valoreSalvato.Punteggio, Origine = vincente.Origine,
            Data = valoreSalvato.UltimaModifica, BaselineSw = valoreSalvato.BaselineSw, Nota = valoreSalvato.Nota,
            Tooltip = TestoTooltip(valoreSalvato),
            ConflittoCollisioneCella = HaCollisioneValoriDiscordanti(valutazioniDellaCoppiaRequisitoEcu, veicoloNome),
        };
    }

    /// <summary>Testo del tooltip che dichiara l'origine di un valore non diretto (requisito utente: "ogni
    /// valore non diretto porta nota + testo tooltip che dichiara l'origine").</summary>
    public static string TestoTooltip(ValutazioneRequisitoEcuVeicolo v)
    {
        var quando = v.UltimaModifica == default ? "" : v.UltimaModifica.ToString("dd/MM/yyyy HH:mm");
        var chi = string.IsNullOrWhiteSpace(v.Operatore) ? "operatore non registrato" : v.Operatore;
        var nota = string.IsNullOrWhiteSpace(v.Nota) ? "" : $": {v.Nota}";
        if (v.Origine == OrigineValutazioneRequisito.Propagata)
        {
            var punteggioSorgente = v.SorgentePunteggio.HasValue
                ? (v.SorgentePunteggio.Value == PunteggioConformita.NonApplicabile ? "N/A" : ((int)v.SorgentePunteggio.Value).ToString())
                : "?";
            var testSorgente = string.IsNullOrWhiteSpace(v.SorgenteTestId) ? "" : $" (test {v.SorgenteTestId}, punteggio {punteggioSorgente})";
            return $"Valore derivato da propagazione dal veicolo '{v.SorgenteVeicolo}'{testSorgente} il {quando} da {chi}{nota}";
        }
        return $"Valore manuale inserito il {quando} da {chi}{nota}";
    }

    private static string ChiaveCella(ValutazioneRequisitoEcuVeicolo v) => $"{v.RequisitoId}|{v.Componente}|{v.Veicolo}";

    /// <summary>Valori significativi di una riga per il confronto di collisione di cella: due righe con la
    /// STESSA chiave di cella (RequisitoId|Componente|Veicolo) ma questa tupla diversa sono una collisione con
    /// valori DISCORDANTI (conflitto da segnalare), non un semplice duplicato silenzioso.</summary>
    private static (PunteggioConformita Punteggio, OrigineValutazioneRequisito Origine, string? SorgenteVeicolo,
        string? SorgenteTestId, PunteggioConformita? SorgentePunteggio) ValoriSignificativi(ValutazioneRequisitoEcuVeicolo v) =>
        (v.Punteggio, v.Origine, v.SorgenteVeicolo, v.SorgenteTestId, v.SorgentePunteggio);

    /// <summary>Marcatore testuale (NON un nuovo campo persistito — nessun cambio di formato dati, fix 6.4.0
    /// R2) inserito in ChangelogEntry.ValoreNuovo da <see cref="ConsolidaCollisioni"/> quando la collisione
    /// riparata aveva valori discordanti, per poterlo riconoscere in lettura dopo che la riparazione ha già
    /// ridotto la cella a una sola riga (vedi <see cref="RiparazioneConValoriDiscordantiNonRisolta"/>).</summary>
    private const string CampoConsolidamentoCollisioneCella = "(consolidamento collisione cella)";
    private const string CampoRisoluzioneCollisioneCella = "(risoluzione collisione cella)";
    private const string MarcatoreValoriDiscordanti = "valori discordanti";

    /// <summary>True se la riga vincente porta ancora traccia, nel proprio Changelog, di una riparazione di
    /// collisione (<see cref="ConsolidaCollisioni"/>) con valori discordanti mai chiusa da un successivo
    /// ImpostaManuale/Propaga riusano la riga per mantenere UID e storico append-only, aggiungendo un evento di
    /// risoluzione. Il conflitto resta visibile solo se un marcatore di consolidamento discordante è più recente
    /// nella sequenza del Changelog rispetto all'ultima risoluzione esplicita.</summary>
    private static bool RiparazioneConValoriDiscordantiNonRisolta(ValutazioneRequisitoEcuVeicolo riga)
    {
        var nonRisolta = false;
        foreach (var voce in riga.Changelog)
        {
            if (voce.Campo == CampoRisoluzioneCollisioneCella)
                nonRisolta = false;
            else if (voce.Campo == CampoConsolidamentoCollisioneCella &&
                     voce.ValoreNuovo?.Contains(MarcatoreValoriDiscordanti, StringComparison.OrdinalIgnoreCase) == true)
                nonRisolta = true;
        }
        return nonRisolta;
    }

    /// <summary>True se, per la cella (coppia già filtrata × <paramref name="veicoloNome"/>), esistono più
    /// righe con valori significativi discordanti (Punteggio, Origine o sorgente) — cioè una collisione di
    /// chiave business Phase D non ancora riparata (vedi PhaseDMasterKeyCollisionTolerance) dove due
    /// postazioni hanno scritto valori DIVERSI per la stessa cella quasi in contemporanea — OPPURE, quando la
    /// cella ha già una sola riga, che quella riga porti ancora il marcatore di una riparazione con valori
    /// discordanti non ancora chiusa da un successivo ImpostaManuale/Propaga (fix 6.4.0 R2: il conflitto deve
    /// restare visibile in Audit dopo la riparazione automatica al Save, non solo prima). Il valore mostrato
    /// da RisolviCella resta comunque deterministico (UltimaPerVeicolo: UltimaModifica più recente, a parità
    /// Uid), ma l'operatore deve poter vedere che si tratta di un valore in conflitto, non l'unico mai
    /// esistito — a differenza del caso in cui i duplicati abbiano gli STESSI valori significativi (nessun
    /// conflitto da segnalare, solo un consolidamento silenzioso).</summary>
    private static bool HaCollisioneValoriDiscordanti(
        IEnumerable<ValutazioneRequisitoEcuVeicolo> valutazioniDellaCoppiaRequisitoEcu,
        string veicoloNome)
    {
        var righeCella = valutazioniDellaCoppiaRequisitoEcu
            .Where(v => !v.IsArchiviata && v.Veicolo.Equals(veicoloNome, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (righeCella.Count > 1)
            return righeCella.Select(ValoriSignificativi).Distinct().Count() > 1;
        return righeCella.Count == 1 && RiparazioneConValoriDiscordantiNonRisolta(righeCella[0]);
    }

    /// <summary>Consolida deterministicamente, IN PLACE su <paramref name="valutazioni"/>, le righe che sono
    /// finite sulla stessa chiave di cella (RequisitoId|Componente|Veicolo) — tipicamente dopo che
    /// MasterRiferimentiService.ApplicaSuCataloghiIncrociati ha riscritto Componente/Veicolo/RequisitoId per
    /// un'unione/rinomina sul catalogo referenziato (review indipendente 2026-09-24: il guard generico
    /// PhaseDMasterRepositoryEngine.ValidateBusinessKeys rifiuterebbe altrimenti l'intero Save, bloccando
    /// un'unione dell'utente altrimenti legittima) — oppure dopo una collisione di chiave business Phase D fra
    /// due postazioni concorrenti sul catalogo audit-valutazioni-requisito-componente-veicolo (fix 6.4.0, vedi
    /// PhaseDMasterKeyCollisionTolerance). Vince la riga con UltimaModifica più recente (a parità, Uid come
    /// criterio deterministico stabile); le altre vengono rimosse dalla lista, ma la loro informazione non si
    /// perde: punteggio/origine/nota confluiscono nella Nota della vincente e un ChangelogEntry ne conserva
    /// traccia. Non tocca il disco: il chiamante decide quando salvare (stesso principio "muta poi salva" di
    /// MasterUnioneService.FondiProprietaSpecifiche). Ritorna il numero di righe rimosse per consolidamento
    /// (0 = nessuna collisione).
    ///
    /// IMPORTANTE (fix 6.4.0, review concorrenza — vedi ValutazioneRequisitoEcuVeicoloPropagazioneMassivaConcorrenzaMultiPcTests.
    /// RiparazioneConcorrenteDiDuePostazioni_EIdempotenteNessunaDuplicazione): la mutazione applicata alla riga
    /// vincente deve essere INTERAMENTE derivata dai dati delle righe stesse, MAI da DateTime.Now o
    /// dall'<paramref name="operatore"/> chiamante. Questo metodo viene chiamato all'inizio di OGNI
    /// Propaga/ImpostaManuale/PropagaMassiva (anche su una cella diversa da quella collisa): se due postazioni
    /// riparano CONCORRENTEMENTE la STESSA collisione preesistente, entrambe mutano la STESSA riga vincente
    /// (un'entità Phase D già esistente, non una nuova) — se quella mutazione non fosse byte-per-byte identica
    /// sulle due postazioni (es. per un timestamp "adesso" o l'operatore locale che ha innescato la riparazione),
    /// i due Save concorrenti produrrebbero due "teste" Phase D con lo stesso vettore ma payload diverso, un
    /// VERO conflitto Phase D (PhaseDMasterConflictException, "teste concorrenti non risolte") che bloccherebbe
    /// la voce per chiunque (prima della lettura non bloccante per voce, anche l'INTERO catalogo) — esattamente il difetto che questo fix doveva eliminare,
    /// solo spostato dalla collisione di chiave business a una collisione di revisione sulla stessa UID.
    /// Rendendo la mutazione deterministica sui soli dati (nessun wall-clock, nessuna identità dell'operatore
    /// chiamante), due riparazioni indipendenti della stessa collisione producono lo STESSO payload: Phase D le
    /// tratta come "stesso valore" (nessun conflitto) anche se arrivano da mutation-id diverse.</summary>
    public static int ConsolidaCollisioni(List<ValutazioneRequisitoEcuVeicolo> valutazioni, string operatore)
    {
        var gruppi = valutazioni.GroupBy(ChiaveCella, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        var rimosse = 0;
        foreach (var gruppo in gruppi)
        {
            var ordinate = gruppo.OrderBy(v => v.IsArchiviata)
                .ThenByDescending(v => v.UltimaModifica).ThenBy(v => v.Uid).ToList();
            var vincitrice = ordinate[0];
            var valoriVincitrice = ValoriSignificativi(vincitrice);
            foreach (var scartata in ordinate.Skip(1))
            {
                vincitrice.Storico ??= new List<SnapshotValutazioneAudit>();
                var idsStorico = new HashSet<Guid>(vincitrice.Storico.Select(e => e.EventId));
                foreach (var evento in scartata.Storico ?? new List<SnapshotValutazioneAudit>())
                {
                    var copia = CopiaSnapshot(evento);
                    if (copia.EventId == Guid.Empty)
                        copia.EventId = IdDeterministico($"snapshot|{copia.Origine}|{copia.Data:O}|{copia.Operatore}|{copia.Punteggio}|{copia.Nota}|{copia.TestCaseId}|{copia.IterazioneTest}");
                    if (idsStorico.Add(copia.EventId)) vincitrice.Storico.Add(copia);
                }
                if (!scartata.IsArchiviata)
                {
                    var snapshotCorrenteScartato = SnapshotCorrente(scartata);
                    if (idsStorico.Add(snapshotCorrenteScartato.EventId)) vincitrice.Storico.Add(snapshotCorrenteScartato);
                }
                vincitrice.Storico = vincitrice.Storico
                    .OrderBy(e => e.Data).ThenBy(e => e.EventId).ToList();

                var punteggioTesto = scartata.Punteggio == PunteggioConformita.NonApplicabile ? "N/A" : ((int)scartata.Punteggio).ToString();
                var operatoreScartata = string.IsNullOrWhiteSpace(scartata.Operatore) ? "operatore non registrato" : scartata.Operatore;
                var notaScartata = string.IsNullOrWhiteSpace(scartata.Nota) ? "" : $": {scartata.Nota}";
                var pezzo = $"Unita da valutazione di {operatoreScartata} del {scartata.UltimaModifica:dd/MM/yyyy} — {scartata.Origine} {punteggioTesto}{notaScartata}";
                vincitrice.Nota = string.IsNullOrWhiteSpace(vincitrice.Nota) ? pezzo : $"{vincitrice.Nota} | {pezzo}";
                // Fix 6.4.0 R2: se questa riga scartata aveva valori significativi DIVERSI dalla vincitrice, il
                // marcatore testuale (nessun campo nuovo persistito) permette a HaCollisioneValoriDiscordanti/
                // RiparazioneConValoriDiscordantiNonRisolta di continuare a mostrare il conflitto in Audit anche
                // dopo che questa riparazione ha già ridotto la cella a una sola riga — finché un operatore non
                // reimposta la cella con ImpostaManuale/Propaga (che sostituiscono l'intera riga e ne azzerano
                // il Changelog, chiudendo così il conflitto).
                var discordanti = !vincitrice.IsArchiviata && !scartata.IsArchiviata &&
                    !ValoriSignificativi(scartata).Equals(valoriVincitrice);
                vincitrice.Changelog.Add(new ChangelogEntry
                {
                    // Deterministico (vedi commento di metodo): derivato dalla riga scartata stessa, non da
                    // DateTime.Now — due postazioni che riparano la stessa collisione producono lo stesso valore.
                    Data = scartata.UltimaModifica,
                    // Costante, non dall'operatore chiamante: la riparazione è un effetto collaterale automatico
                    // di Propaga/ImpostaManuale/PropagaMassiva, non un'azione esplicita di QUELL'operatore sulla
                    // cella scartata — e deve restare identica indipendentemente da quale postazione la esegue.
                    Operatore = "(riparazione automatica)",
                    Campo = CampoConsolidamentoCollisioneCella,
                    ValorePrecedente = $"riga di {operatoreScartata} del {scartata.UltimaModifica:dd/MM/yyyy}",
                    ValoreNuovo = discordanti
                        ? $"consolidata in questa voce (UltimaModifica più recente) — {MarcatoreValoriDiscordanti}, verificare"
                        : "consolidata in questa voce (UltimaModifica più recente)",
                });
                valutazioni.Remove(scartata);
                rimosse++;
            }
            vincitrice.IsArchiviata = ordinate.All(v => v.IsArchiviata);
            // Deterministico (vedi commento di metodo): NON stampigliare UltimaModifica/ModificatoDa con
            // DateTime.Now/operatore chiamante — la vincente mantiene i propri valori originali, la riparazione
            // resta visibile solo tramite Nota/Changelog (già data-derivati sopra).
        }
        return rimosse;
    }

    /// <summary>Conteggio "a secco" (nessuna mutazione) di quante righe collidrebbero sulla stessa cella se
    /// <paramref name="vecchio"/> venisse sostituito con <paramref name="nuovo"/> sulle proprietà indicate —
    /// per l'anteprima di conferma prima di un'unione/rinomina (review 2026-09-24). Considera solo le
    /// proprietà che compongono davvero la chiave di cella (RequisitoId/Componente/Veicolo): SorgenteVeicolo, pur
    /// referenziando anch'esso il catalogo Veicolo, non fa parte della chiave e non genera collisioni.</summary>
    public static int ContaCollisioniSeSostituito(
        IReadOnlyList<ValutazioneRequisitoEcuVeicolo> valutazioni,
        IEnumerable<System.Reflection.PropertyInfo> proprieta,
        string vecchio,
        string nuovo)
    {
        var nomiChiave = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(ValutazioneRequisitoEcuVeicolo.RequisitoId), nameof(ValutazioneRequisitoEcuVeicolo.Componente), nameof(ValutazioneRequisitoEcuVeicolo.Veicolo),
        };
        var proprietaChiave = proprieta.Select(p => p.Name).Where(nomiChiave.Contains).ToHashSet(StringComparer.Ordinal);
        if (proprietaChiave.Count == 0) return 0;

        string ChiaveSimulata(ValutazioneRequisitoEcuVeicolo v)
        {
            // Sostituisce SOLO il/i campo/i realmente registrati per questo catalogo — un valore che per puro
            // caso coincidesse con "vecchio" su un campo diverso (es. un Componente chiamato come un RequisitoId) non
            // deve essere toccato dalla simulazione, esattamente come non lo tocca SostituisciInProprieta reale.
            string SimSe(string nomeProprieta, string valoreCorrente) =>
                proprietaChiave.Contains(nomeProprieta) && valoreCorrente.Equals(vecchio, StringComparison.OrdinalIgnoreCase) ? nuovo : valoreCorrente;
            return $"{SimSe(nameof(ValutazioneRequisitoEcuVeicolo.RequisitoId), v.RequisitoId)}|{SimSe(nameof(ValutazioneRequisitoEcuVeicolo.Componente), v.Componente)}|{SimSe(nameof(ValutazioneRequisitoEcuVeicolo.Veicolo), v.Veicolo)}";
        }

        return valutazioni
            .GroupBy(ChiaveSimulata, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Sum(g => g.Count() - 1);
    }

    /// <summary>Applica una modifica al catalogo condiviso delle valutazioni con optimistic-concurrency sullo
    /// snapshot appena letto (stesso pattern di AuditViewModel.ProvaAggiornaRisoluzioni per
    /// RisoluzioneConflittoAudit): se un'altra postazione pubblica tra Load e Save, la modifica fallisce
    /// chiusa invece di sovrascrivere righe concorrenti.</summary>
    private static bool ProvaAggiorna(
        MasterDataRepository<ValutazioneRequisitoEcuVeicolo> repo,
        Func<List<ValutazioneRequisitoEcuVeicolo>, bool> mutazione,
        out string? errore,
        Func<string?>? verificaImmediatamentePrimaDelCommit = null,
        bool allowSilentRemoteMerge = true)
    {
        var valutazioni = repo.Load();
        var atteso = EntityStateFingerprint.Clone(valutazioni);
        if (!mutazione(valutazioni)) { errore = null; return false; }

        // Il callback può rileggere le fonti esterne all'archivio Phase D (in particolare i Test) dopo aver
        // preparato la mutazione ma immediatamente prima del Save. Se la fotografia non è completa o è
        // cambiata, fallire chiusi: il batch locale non è mai stato scritto.
        if (verificaImmediatamentePrimaDelCommit != null)
        {
            try
            {
                errore = verificaImmediatamentePrimaDelCommit();
                if (!string.IsNullOrWhiteSpace(errore)) return false;
            }
            catch (Exception ex)
            {
                errore = ex.Message;
                return false;
            }
        }

        try
        {
            repo.Save(valutazioni, atteso, allowSilentRemoteMerge: allowSilentRemoteMerge);
            errore = null;
            return true;
        }
        catch (Exception ex)
        {
            errore = ex.Message;
            return false;
        }
    }

    /// <summary>Conferma la propagazione a uno o più veicoli scelti dall'operatore (requisito utente: "dal '!'
    /// l'utente decide se propagare o no; se propaga, il valore diventa una valutazione 'Propagata'
    /// persistita con sorgente, nota, operatore, data"). Ogni voce sostituisce (non accumula) l'eventuale riga
    /// precedente della stessa cella — stesso comportamento di AuditViewModel.RisolviConflitto/
    /// SalvaValutazioneManuale su RisoluzioneConflittoAudit: RemoveAll + Add, una sola riga attiva per cella,
    /// lo storico resta comunque nel Changelog/journal Phase D sottostante, non in righe duplicate qui.</summary>
    public static bool Propaga(
        MasterDataRepository<ValutazioneRequisitoEcuVeicolo> repo,
        string requisitoId,
        string ecuNome,
        IEnumerable<(string Veicolo, CandidatoPropagazioneRequisito Sorgente)> voci,
        string nota,
        string operatore,
        out string? errore)
    {
        var listaVoci = voci.ToList();
        if (listaVoci.Count == 0 || string.IsNullOrWhiteSpace(nota))
        {
            errore = "Nessun veicolo scelto o nota mancante.";
            return false;
        }

        return ProvaAggiorna(repo, valutazioni =>
        {
            // Ripara eventuali collisioni di chiave business Phase D preesistenti e visibili in questo
            // catalogo (vedi PhaseDMasterKeyCollisionTolerance/ConsolidaCollisioni), non solo sulla cella che
            // questa chiamata sta per scrivere: il Save che segue è così il "primo salvataggio successivo"
            // che ripara il journal condiviso, rimuovendo la riga perdente con una mutation di delete.
            ConsolidaCollisioni(valutazioni, operatore);

            var adesso = DateTime.Now;
            foreach (var (veicolo, sorgente) in listaVoci)
            {
                SalvaValoreCella(valutazioni, requisitoId, ecuNome, veicolo, sorgente.PunteggioSorgente,
                    OrigineValutazioneRequisito.Propagata, nota.Trim(), operatore, null,
                    sorgente.VeicoloSorgente, sorgente.TestIdSorgente, sorgente.PunteggioSorgente, adesso);
            }
            return true;
        }, out errore);
    }

    /// <summary>Fotografia fail-closed del target di una propagazione singola. Comprende i candidati Test per
    /// ogni veicolo sorgente, le valutazioni Phase D, la risoluzione legacy e lo stato effettivo della cella:
    /// una sorgente cambiata o un target diventato valorizzato invalida la conferma.</summary>
    public static string ImprontaPropagazioneCella(
        string requisitoId, Componente componente, string veicolo,
        IReadOnlyList<TestCase> testCases,
        IReadOnlyList<ValutazioneRequisitoEcuVeicolo> valutazioni,
        IReadOnlyList<RisoluzioneConflittoAudit> risoluzioni)
    {
        var testDellaCoppia = testCases.Where(tc =>
            tc.Componente.Contains(componente.Nome, StringComparer.OrdinalIgnoreCase) &&
            tc.RequisitoId.Contains(requisitoId, StringComparer.OrdinalIgnoreCase)).ToList();
        var valutazioniCoppia = valutazioni.Where(v => v.Componente.Equals(componente.Nome, StringComparison.OrdinalIgnoreCase) &&
            v.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase)).OrderBy(v => v.Uid).ToList();
        var risoluzioniCoppia = risoluzioni.Where(r => r.Componente.Equals(componente.Nome, StringComparison.OrdinalIgnoreCase) &&
            r.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => EntityStateFingerprint.Compute(r), StringComparer.Ordinal).ToList();
        var direttiPerVeicolo = AuditMatriceService.CandidatiDirettiPerVeicolo(requisitoId, testDellaCoppia)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new
            {
                Veicolo = kv.Key,
                Candidati = kv.Value.OrderBy(c => c.TestCaseId, StringComparer.OrdinalIgnoreCase)
                    .Select(c => new { c.TestCaseId, c.Punteggio, c.Data }).ToList(),
            }).ToList();
        var generici = AuditMatriceService.CandidatiEcuGenerici(requisitoId, testDellaCoppia)
            .OrderBy(c => c.TestCaseId, StringComparer.OrdinalIgnoreCase)
            .Select(c => new { c.TestCaseId, c.Punteggio, c.Data }).ToList();
        var risoluzioneTarget = risoluzioniCoppia.FirstOrDefault(r => r.Componente.Equals(componente.Nome, StringComparison.OrdinalIgnoreCase));
        var effettiva = RisolviCella(requisitoId, componente, veicolo, testDellaCoppia, valutazioniCoppia, risoluzioneTarget);
        var statoEffettivo = new
        {
            effettiva.Punteggio, effettiva.Origine, effettiva.Data, effettiva.Diretto,
            effettiva.ConflittoDiretto, effettiva.TestCreatoNonEseguito, effettiva.Propagabile,
            Candidati = effettiva.CandidatiPropagazione.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new { kv.Key, kv.Value.VeicoloSorgente, kv.Value.TestIdSorgente, kv.Value.PunteggioSorgente, kv.Value.DataSorgente }).ToList(),
        };
        return EntityStateFingerprint.Compute(new { Diretti = direttiPerVeicolo, Generici = generici,
            Valutazioni = valutazioniCoppia, Legacy = risoluzioniCoppia, StatoEffettivo = statoEffettivo });
    }

    /// <summary>Propagazione singola con verifica atomica del catalogo Phase D e ricontrollo immediatamente
    /// prima della mutazione di target e sorgenti rispetto alla fotografia confermata dalla UI.</summary>
    public static bool PropagaVerificando(
        MasterDataRepository<ValutazioneRequisitoEcuVeicolo> repo,
        string requisitoId,
        Componente componente,
        IReadOnlyList<VocePropagazioneAuditAttesa> voci,
        IReadOnlyList<TestCase> testCases,
        IReadOnlyList<RisoluzioneConflittoAudit> risoluzioni,
        string nota,
        string operatore,
        out string? errore,
        DateTime? dataConferma = null,
        Func<IReadOnlyList<TestCase>>? ricaricaTestCasePrimaDelCommit = null)
    {
        if (voci.Count == 0 || string.IsNullOrWhiteSpace(nota))
        {
            errore = "Nessun veicolo scelto o nota mancante.";
            return false;
        }
        if (voci.Select(v => v.Veicolo).Distinct(StringComparer.OrdinalIgnoreCase).Count() != voci.Count)
        {
            errore = "Il batch di propagazione contiene veicoli duplicati.";
            return false;
        }

        string? erroreConvalida = null;
        List<ValutazioneRequisitoEcuVeicolo>? valutazioniPrimaDellaMutazione = null;
        var ok = ProvaAggiorna(repo, valutazioni =>
        {
            valutazioniPrimaDellaMutazione = EntityStateFingerprint.Clone(valutazioni);
            foreach (var attesa in voci)
            {
                var testDellaCoppia = testCases.Where(tc =>
                    tc.Componente.Contains(componente.Nome, StringComparer.OrdinalIgnoreCase) &&
                    tc.RequisitoId.Contains(requisitoId, StringComparer.OrdinalIgnoreCase)).ToList();
                var valutazioniDellaCoppia = valutazioni.Where(v => v.Componente.Equals(componente.Nome, StringComparison.OrdinalIgnoreCase) &&
                    v.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase)).ToList();
                var risoluzioneTarget = risoluzioni.FirstOrDefault(r => r.Componente.Equals(componente.Nome, StringComparison.OrdinalIgnoreCase) &&
                    r.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase));
                var cella = RisolviCella(requisitoId, componente, attesa.Veicolo, testDellaCoppia, valutazioniDellaCoppia, risoluzioneTarget);
                if (!cella.Propagabile || !cella.CandidatiPropagazione.TryGetValue(attesa.Candidato.VeicoloSorgente, out var candidatoCorrente) ||
                    candidatoCorrente != attesa.Candidato ||
                    !string.Equals(attesa.ImprontaAttesa,
                        ImprontaPropagazioneCella(requisitoId, componente, attesa.Veicolo, testCases, valutazioni, risoluzioni),
                        StringComparison.Ordinal))
                {
                    erroreConvalida = $"La sorgente o il target {componente.Nome} × {requisitoId} × {attesa.Veicolo} è cambiato dopo l'anteprima; nessuna cella è stata scritta.";
                    return false;
                }
            }

            ConsolidaCollisioni(valutazioni, operatore);
            var adesso = dataConferma ?? DateTime.Now;
            foreach (var attesa in voci)
            {
                var sorgente = attesa.Candidato;
                SalvaValoreCella(valutazioni, requisitoId, componente.Nome, attesa.Veicolo, sorgente.PunteggioSorgente,
                    OrigineValutazioneRequisito.Propagata, nota.Trim(), operatore, null,
                    sorgente.VeicoloSorgente, sorgente.TestIdSorgente, sorgente.PunteggioSorgente, adesso);
            }
            return true;
        }, out errore, ricaricaTestCasePrimaDelCommit == null ? null : () =>
        {
            var testCorrenti = ricaricaTestCasePrimaDelCommit();
            var valutazioniFotografate = valutazioniPrimaDellaMutazione ?? throw new InvalidOperationException("Fotografia Phase D non disponibile.");
            foreach (var attesa in voci)
            {
                var testDellaCoppia = testCorrenti.Where(tc =>
                    tc.Componente.Contains(componente.Nome, StringComparer.OrdinalIgnoreCase) &&
                    tc.RequisitoId.Contains(requisitoId, StringComparer.OrdinalIgnoreCase)).ToList();
                var valutazioniDellaCoppia = valutazioniFotografate.Where(v => v.Componente.Equals(componente.Nome, StringComparison.OrdinalIgnoreCase) &&
                    v.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase)).ToList();
                var risoluzioneTarget = risoluzioni.FirstOrDefault(r => r.Componente.Equals(componente.Nome, StringComparison.OrdinalIgnoreCase) &&
                    r.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase));
                var cella = RisolviCella(requisitoId, componente, attesa.Veicolo, testDellaCoppia, valutazioniDellaCoppia, risoluzioneTarget);
                if (!cella.Propagabile || !cella.CandidatiPropagazione.TryGetValue(attesa.Candidato.VeicoloSorgente, out var candidatoCorrente) ||
                    candidatoCorrente != attesa.Candidato ||
                    !string.Equals(attesa.ImprontaAttesa,
                        ImprontaPropagazioneCella(requisitoId, componente, attesa.Veicolo, testCorrenti, valutazioniFotografate, risoluzioni),
                        StringComparison.Ordinal))
                    return $"La sorgente o il target {componente.Nome} × {requisitoId} × {attesa.Veicolo} è cambiato prima del commit; nessuna cella è stata scritta. Ripeti anteprima e conferma.";
            }
            return null;
        });
        errore ??= erroreConvalida;
        return ok;
    }

    /// <summary>Inserimento/correzione manuale per una singola coppia componente×Veicolo, o per un sottoinsieme di
    /// veicoli scelti dall'operatore su una stessa componente (requisito utente, due livelli di inserimento: "per
    /// singola coppia componente×veicolo" e "per componente applicandoli a un sottoinsieme scelto di veicoli propagabili").
    /// Nota obbligatoria (requisito utente). <paramref name="baselineSw"/> è facoltativo (per coppia).</summary>
    public static bool ImpostaManuale(
        MasterDataRepository<ValutazioneRequisitoEcuVeicolo> repo,
        string requisitoId,
        string ecuNome,
        IEnumerable<string> veicoli,
        PunteggioConformita punteggio,
        string nota,
        string operatore,
        string? baselineSw,
        out string? errore)
    {
        var listaVeicoli = veicoli.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (listaVeicoli.Count == 0 || string.IsNullOrWhiteSpace(nota))
        {
            errore = "Nessun veicolo scelto o nota mancante.";
            return false;
        }

        return ProvaAggiorna(repo, valutazioni =>
        {
            // Vedi commento analogo in Propaga: ripara eventuali collisioni preesistenti visibili PRIMA di
            // applicare la mutazione richiesta da questa chiamata.
            ConsolidaCollisioni(valutazioni, operatore);

            var adesso = DateTime.Now;
            foreach (var veicolo in listaVeicoli)
            {
                SalvaValoreCella(valutazioni, requisitoId, ecuNome, veicolo, punteggio,
                    OrigineValutazioneRequisito.Manuale, nota.Trim(), operatore, baselineSw,
                    "", "", null, adesso);
            }
            return true;
        }, out errore);
    }

    /// <summary>Cattura in una sola Load le righe Phase D delle celle batch interessate. L'impronta per cella
    /// viene verificata nuovamente prima della mutazione così una modifica concorrente fra anteprima e conferma
    /// non viene sovrascritta silenziosamente.</summary>
    public static IReadOnlyList<CellaApplicazioneManualeAuditAttesa> CatturaApplicazioneManualeAudit(
        MasterDataRepository<ValutazioneRequisitoEcuVeicolo> repo,
        IReadOnlyList<CellaApplicazioneManualeAudit> celle) =>
        CatturaApplicazioneManualeAudit(repo, celle, null, null);

    public static IReadOnlyList<CellaApplicazioneManualeAuditAttesa> CatturaApplicazioneManualeAudit(
        MasterDataRepository<ValutazioneRequisitoEcuVeicolo> repo,
        IReadOnlyList<CellaApplicazioneManualeAudit> celle,
        IReadOnlyList<TestCase>? testCases,
        IReadOnlyList<RisoluzioneConflittoAudit>? risoluzioni)
    {
        var distinte = celle
            .GroupBy(c => $"{c.RequisitoId}\u001f{c.Componente}\u001f{c.Veicolo}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).ToList();
        var correnti = repo.Load();
        return distinte.Select(c =>
        {
            var righeCella = correnti.Where(v => StessaCella(v, c.Componente, c.RequisitoId, c.Veicolo)).ToList();
            return new CellaApplicazioneManualeAuditAttesa(
                c.Componente, c.RequisitoId, c.Veicolo, c.Sovrascrive, righeCella.Any(v => !v.IsArchiviata),
                ImprontaCella(righeCella))
            {
                ImprontaFontiEsterneAttesa = testCases != null && risoluzioni != null
                    ? ImprontaFontiEsterneCella(c.Componente, c.RequisitoId, c.Veicolo, testCases, risoluzioni)
                    : null,
            };
        }).ToList();
    }

    /// <summary>Applica un valore manuale a più celle in unica mutazione e un solo Save atomico. Le celle di input
    /// rappresentano esclusivamente la riga/colonna visibile del veicolo selezionato; i Test diretti e gli
    /// override legacy componente×requisito restano esclusi come dati Phase D ma possono essere sovrascritti da una
    /// nuova riga locale con timestamp successivo. Le
    /// valutazioni manuali/propagate veicolo-specifiche richiedono conferma esplicita. Su qualunque differenza
    /// Anche un valore diretto o legacy può essere sostituito tramite un Manuale veicolo-specifico, senza
    /// cancellare i Test o l'override globale. Su qualunque differenza dalla fotografia Phase D precedente
    /// non scrive nulla e richiede di ricaricare/ripetere l'operazione.</summary>
    public static bool ImpostaManualeMassivaAudit(
        MasterDataRepository<ValutazioneRequisitoEcuVeicolo> repo,
        IReadOnlyList<CellaApplicazioneManualeAuditAttesa> celleAttese,
        PunteggioConformita punteggio,
        string nota,
        string operatore,
        string? baselineSw,
        bool confermaSovrascritture,
        out int celleApplicate,
        out string? errore) =>
        ImpostaManualeMassivaAudit(repo, celleAttese, punteggio, nota, operatore, baselineSw,
            confermaSovrascritture, null, null, out celleApplicate, out errore);

    public static bool ImpostaManualeMassivaAudit(
        MasterDataRepository<ValutazioneRequisitoEcuVeicolo> repo,
        IReadOnlyList<CellaApplicazioneManualeAuditAttesa> celleAttese,
        PunteggioConformita punteggio,
        string nota,
        string operatore,
        string? baselineSw,
        bool confermaSovrascritture,
        IReadOnlyList<TestCase>? testCases,
        IReadOnlyList<RisoluzioneConflittoAudit>? risoluzioni,
        out int celleApplicate,
        out string? errore,
        Func<(IReadOnlyList<TestCase> TestCases, IReadOnlyList<RisoluzioneConflittoAudit> Risoluzioni)>? ricaricaFontiPrimaDelCommit = null,
        DateTime? dataConferma = null)
    {
        celleApplicate = 0;
        if (celleAttese.Count == 0 || string.IsNullOrWhiteSpace(nota))
        {
            errore = "Nessuna cella modificabile o nota mancante.";
            return false;
        }
        if (!Enum.IsDefined(punteggio))
        {
            errore = "Il punteggio selezionato non è valido.";
            return false;
        }
        if (celleAttese.Any(c => c.Sovrascrive) && !confermaSovrascritture)
        {
            errore = "Confermare esplicitamente la sovrascrittura delle valutazioni esistenti.";
            return false;
        }

        var chiavi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in celleAttese)
            if (!chiavi.Add($"{c.RequisitoId}\u001f{c.Componente}\u001f{c.Veicolo}"))
            {
                errore = "Il batch contiene celle duplicate.";
                return false;
            }

        List<ValutazioneRequisitoEcuVeicolo> valutazioni;
        try { valutazioni = repo.Load(); }
        catch (Exception ex) { errore = ex.Message; return false; }
        var atteso = EntityStateFingerprint.Clone(valutazioni);

        foreach (var c in celleAttese)
        {
            if (c.ImprontaFontiEsterneAttesa != null)
            {
                if (testCases == null || risoluzioni == null ||
                    !string.Equals(c.ImprontaFontiEsterneAttesa,
                        ImprontaFontiEsterneCella(c.Componente, c.RequisitoId, c.Veicolo, testCases, risoluzioni),
                        StringComparison.Ordinal))
                {
                    errore = $"Le fonti della cella {c.Componente} × {c.RequisitoId} × {c.Veicolo} sono cambiate dopo l'anteprima; ricarica e conferma di nuovo.";
                    return false;
                }
            }
            var correnti = valutazioni.Where(v => StessaCella(v, c.Componente, c.RequisitoId, c.Veicolo)).ToList();
            var correntiAttive = correnti.Where(v => !v.IsArchiviata).ToList();
            if (!string.Equals(ImprontaCella(correnti), c.ImprontaAttesa, StringComparison.Ordinal))
            {
                errore = $"La cella {c.Componente} × {c.RequisitoId} × {c.Veicolo} è cambiata dopo l'anteprima.";
                return false;
            }
            if (c.HaValutazioneVeicoloSalvata != (correntiAttive.Count > 0) ||
                correntiAttive.Any(v => v.Origine is not (OrigineValutazioneRequisito.Manuale or OrigineValutazioneRequisito.Propagata)))
            {
                errore = $"La cella {c.Componente} × {c.RequisitoId} × {c.Veicolo} non è più modificabile nel perimetro confermato.";
                return false;
            }
            if (correntiAttive.Count > 0 && !confermaSovrascritture)
            {
                errore = $"Confermare esplicitamente la sovrascrittura della valutazione salvata in {c.Componente} × {c.RequisitoId} × {c.Veicolo}.";
                return false;
            }
        }

        try
        {
            // Una sola mutazione della lista, poi un solo Save con la fotografia attesa del catalogo.
            ConsolidaCollisioni(valutazioni, operatore);
            var adesso = dataConferma ?? DateTime.Now;
            foreach (var c in celleAttese)
            {
                SalvaValoreCella(valutazioni, c.RequisitoId, c.Componente, c.Veicolo, punteggio,
                    OrigineValutazioneRequisito.Manuale, nota.Trim(), operatore,
                    string.IsNullOrWhiteSpace(baselineSw) ? null : baselineSw.Trim(),
                    "", "", null, adesso);
            }

            if (ricaricaFontiPrimaDelCommit != null)
            {
                var fontiCorrenti = ricaricaFontiPrimaDelCommit();
                foreach (var c in celleAttese)
                {
                    if (string.IsNullOrWhiteSpace(c.ImprontaFontiEsterneAttesa) ||
                        !string.Equals(c.ImprontaFontiEsterneAttesa,
                            ImprontaFontiEsterneCella(c.Componente, c.RequisitoId, c.Veicolo,
                                fontiCorrenti.TestCases, fontiCorrenti.Risoluzioni),
                            StringComparison.Ordinal))
                    {
                        errore = $"Le fonti Test della cella {c.Componente} × {c.RequisitoId} × {c.Veicolo} sono cambiate o non verificabili prima del commit; nessuna cella è stata scritta. Ricarica e ripeti la conferma.";
                        return false;
                    }
                }
            }

            repo.Save(valutazioni, atteso);
            celleApplicate = celleAttese.Count;
            errore = null;
            return true;
        }
        catch (Exception ex)
        {
            errore = ex.Message;
            return false;
        }
    }

    private static bool StessaCella(ValutazioneRequisitoEcuVeicolo v, string componente, string requisitoId, string veicolo) =>
        v.Componente.Equals(componente, StringComparison.OrdinalIgnoreCase) &&
        v.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase) &&
        v.Veicolo.Equals(veicolo, StringComparison.OrdinalIgnoreCase);

    private static string ImprontaCella(IEnumerable<ValutazioneRequisitoEcuVeicolo> righe) =>
        EntityStateFingerprint.Compute(righe.OrderBy(v => v.Uid).ToList());

    /// <summary>Rimuove la valutazione salvata (Manuale o Propagata) di UNA cella (Piano 6.3.1 §1 UI, punto 4:
    /// "permetti di rimuovere/modificare una valutazione salvata"): la cella torna a seguire il calcolo
    /// automatico (Diretto se esiste un Test sul veicolo, altrimenti "!" se ancora propagabile, altrimenti
    /// vuoto). La riga viene soft-archiviata e il valore corrente aggiunto allo storico nella stessa mutazione;
    /// gli inserimenti successivi riutilizzano la stessa riga/UID.</summary>
    public static bool RimuoviValutazione(
        MasterDataRepository<ValutazioneRequisitoEcuVeicolo> repo,
        string requisitoId,
        string ecuNome,
        string veicolo,
        out string? errore) =>
        ProvaAggiorna(repo, valutazioni =>
        {
            ConsolidaCollisioni(valutazioni, "");
            var correnti = valutazioni.Where(v => !v.IsArchiviata &&
                v.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase) &&
                v.Componente.Equals(ecuNome, StringComparison.OrdinalIgnoreCase) &&
                v.Veicolo.Equals(veicolo, StringComparison.OrdinalIgnoreCase)).ToList();
            if (correnti.Count == 0) return false;
            foreach (var corrente in correnti)
            {
                AccodaSnapshotCella(corrente);
                corrente.IsArchiviata = true;
                corrente.EventoCorrenteId = null;
                corrente.UltimaModifica = DateTime.Now;
            }
            return true;
        }, out errore);

    /// <summary>Fotografa le sole valutazioni Manuali/Propagate attive per un requisito e un veicolo,
    /// attraversando i componenti associate al veicolo. Le valutazioni legacy globali e i Test non sono target di
    /// questa azione; i Test e le risoluzioni legacy sono inclusi solo come fonti da rivalidare al commit.</summary>
    public static AnteprimaRimozioneValutazioniRigaAudit CatturaRimozioneValutazioniRigaAudit(
        string requisitoId,
        string veicolo,
        IReadOnlyList<Componente> tutteLeEcu,
        IReadOnlyList<TestCase> testCases,
        IReadOnlyList<RisoluzioneConflittoAudit> risoluzioni,
        IReadOnlyList<ValutazioneRequisitoEcuVeicolo> valutazioni)
    {
        var ecuAssociate = AuditMatriceService.EcuDelSottoveicolo(tutteLeEcu, veicolo);
        var impronteFonti = ecuAssociate.ToDictionary(
            componente => componente,
            componente => ImprontaFontiEsterneCella(componente, requisitoId, veicolo, testCases, risoluzioni),
            StringComparer.OrdinalIgnoreCase);
        var nomiEcu = new HashSet<string>(ecuAssociate, StringComparer.OrdinalIgnoreCase);
        var daRimuovere = valutazioni.Where(v =>
                !v.IsArchiviata &&
                v.RequisitoId.Equals(requisitoId, StringComparison.OrdinalIgnoreCase) &&
                v.Veicolo.Equals(veicolo, StringComparison.OrdinalIgnoreCase) &&
                nomiEcu.Contains(v.Componente) &&
                v.Origine is OrigineValutazioneRequisito.Manuale or OrigineValutazioneRequisito.Propagata)
            .OrderBy(v => v.Componente, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Uid)
            .Select(v => new VoceRimozioneValutazioneRigaAudit(
                v.Uid, v.Componente, v.Punteggio, v.Origine, v.Nota, EntityStateFingerprint.Compute(v)))
            .ToList();

        return new AnteprimaRimozioneValutazioniRigaAudit(
            requisitoId, veicolo, ecuAssociate, impronteFonti, daRimuovere);
    }

    /// <summary>Archivia in un unico Save tutte le valutazioni Manuali/Propagate per il requisito e il solo
    /// veicolo mostrati nell'anteprima. Ogni riga mantiene UID, origine e valore e riceve lo snapshot corrente
    /// nella cronologia. Non modifica Test, risoluzioni legacy o valutazioni di altri veicoli. Fallisce
    /// chiusa se il perimetro componente, le celle, le fonti esterne o il catalogo Phase D cambiano prima del commit.</summary>
    public static bool RimuoviValutazioniRigaAudit(
        MasterDataRepository<ValutazioneRequisitoEcuVeicolo> repo,
        AnteprimaRimozioneValutazioniRigaAudit anteprima,
        IReadOnlyList<Componente> tutteLeEcu,
        IReadOnlyList<TestCase> testCases,
        IReadOnlyList<RisoluzioneConflittoAudit> risoluzioni,
        string operatore,
        out int valutazioniRimosse,
        out string? errore,
        Func<FontiRimozioneValutazioniRigaAudit>? ricaricaFontiPrimaDelCommit = null,
        DateTime? dataConferma = null)
    {
        valutazioniRimosse = 0;
        if (!anteprima.HaValutazioni)
        {
            errore = null;
            return false;
        }

        string? erroreRevalidazione = null;
        var candidateRimosse = 0;
        List<ValutazioneRequisitoEcuVeicolo>? fotografiaValutazioni = null;
        var salvata = ProvaAggiorna(repo, valutazioni =>
        {
            fotografiaValutazioni = EntityStateFingerprint.Clone(valutazioni);
            var corrente = CatturaRimozioneValutazioniRigaAudit(
                anteprima.RequisitoId, anteprima.Veicolo, tutteLeEcu, testCases, risoluzioni, valutazioni);
            if (!RimozioneRigaCorrisponde(anteprima, corrente))
            {
                erroreRevalidazione = MessaggioAnteprimaRimozioneCambiata(anteprima);
                return false;
            }

            var uidAttesi = new HashSet<Guid>(anteprima.Valutazioni.Select(v => v.Uid));
            var ecusAttese = new HashSet<string>(anteprima.EcuAssociate, StringComparer.OrdinalIgnoreCase);
            var correnti = valutazioni.Where(v => uidAttesi.Contains(v.Uid) &&
                !v.IsArchiviata &&
                v.RequisitoId.Equals(anteprima.RequisitoId, StringComparison.OrdinalIgnoreCase) &&
                v.Veicolo.Equals(anteprima.Veicolo, StringComparison.OrdinalIgnoreCase) &&
                ecusAttese.Contains(v.Componente) &&
                v.Origine is OrigineValutazioneRequisito.Manuale or OrigineValutazioneRequisito.Propagata).ToList();
            if (correnti.Count != anteprima.NumeroValutazioni)
            {
                erroreRevalidazione = MessaggioAnteprimaRimozioneCambiata(anteprima);
                return false;
            }

            var adesso = dataConferma ?? DateTime.Now;
            foreach (var correnteValutazione in correnti)
            {
                AccodaSnapshotCella(correnteValutazione);
                correnteValutazione.IsArchiviata = true;
                correnteValutazione.EventoCorrenteId = null;
                correnteValutazione.UltimaModifica = adesso;
                correnteValutazione.ModificatoDa = operatore;
            }
            candidateRimosse = correnti.Count;
            return candidateRimosse > 0;
        }, out errore, ricaricaFontiPrimaDelCommit == null ? null : () =>
        {
            var fontiFresche = ricaricaFontiPrimaDelCommit();
            var fotografie = fotografiaValutazioni ?? throw new InvalidOperationException("Fotografia Phase D non disponibile.");
            var ricalcolata = CatturaRimozioneValutazioniRigaAudit(
                anteprima.RequisitoId, anteprima.Veicolo, fontiFresche.Componenti,
                fontiFresche.TestCases, fontiFresche.Risoluzioni, fotografie);
            if (!RimozioneRigaCorrisponde(anteprima, ricalcolata))
                return MessaggioAnteprimaRimozioneCambiata(anteprima);
            return null;
        }, allowSilentRemoteMerge: false);

        if (!salvata)
        {
            valutazioniRimosse = 0;
            errore ??= erroreRevalidazione;
            return false;
        }

        valutazioniRimosse = candidateRimosse;
        errore = null;
        return true;
    }

    /// <summary>Fotografa le sole valutazioni Manuali/Propagate attive del componente selezionata per ogni requisito
    /// attivo e per il veicolo corrente. Le REV/Test e le risoluzioni legacy sono solo fonti da rivalidare.</summary>
    public static AnteprimaRimozioneValutazioniColonnaAudit CatturaRimozioneValutazioniColonnaAudit(
        string componente,
        string veicolo,
        IReadOnlyList<Componente> tutteLeEcu,
        IReadOnlyList<Requisito> requisiti,
        IReadOnlyList<TestCase> testCases,
        IReadOnlyList<RisoluzioneConflittoAudit> risoluzioni,
        IReadOnlyList<ValutazioneRequisitoEcuVeicolo> valutazioni)
    {
        var ecuAssociata = AuditMatriceService.EcuDelSottoveicolo(tutteLeEcu, veicolo)
            .Contains(componente, StringComparer.OrdinalIgnoreCase);
        var requisitiAttivi = requisiti.Where(r => !r.Archiviata)
            .OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var impronteRequisiti = requisitiAttivi.ToDictionary(
            r => r.Id,
            EntityStateFingerprint.Compute,
            StringComparer.OrdinalIgnoreCase);
        var requisitiPerId = new HashSet<string>(impronteRequisiti.Keys, StringComparer.OrdinalIgnoreCase);
        var impronteFonti = requisitiAttivi.ToDictionary(
            r => r.Id,
            r => ImprontaFontiEsterneCella(componente, r.Id, veicolo, testCases, risoluzioni),
            StringComparer.OrdinalIgnoreCase);
        var daRimuovere = valutazioni.Where(v =>
                !v.IsArchiviata &&
                ecuAssociata &&
                v.Componente.Equals(componente, StringComparison.OrdinalIgnoreCase) &&
                v.Veicolo.Equals(veicolo, StringComparison.OrdinalIgnoreCase) &&
                requisitiPerId.Contains(v.RequisitoId) &&
                v.Origine is OrigineValutazioneRequisito.Manuale or OrigineValutazioneRequisito.Propagata)
            .OrderBy(v => v.RequisitoId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Uid)
            .Select(v => new VoceRimozioneValutazioneColonnaAudit(
                v.Uid, v.RequisitoId, v.Punteggio, v.Origine, v.Nota, EntityStateFingerprint.Compute(v)))
            .ToList();

        return new AnteprimaRimozioneValutazioniColonnaAudit(
            componente, veicolo, ecuAssociata, impronteRequisiti, impronteFonti, daRimuovere);
    }

    /// <summary>Archivia in un unico Save tutte le valutazioni Manuali/Propagate del componente selezionata e del
    /// solo veicolo corrente. Ogni valore passa nello storico prima dell'archiviazione. Il catalogo Test,
    /// le risoluzioni legacy e le valutazioni per requisiti archiviati non vengono scritti.</summary>
    public static bool RimuoviValutazioniColonnaAudit(
        MasterDataRepository<ValutazioneRequisitoEcuVeicolo> repo,
        AnteprimaRimozioneValutazioniColonnaAudit anteprima,
        IReadOnlyList<Componente> tutteLeEcu,
        IReadOnlyList<Requisito> requisiti,
        IReadOnlyList<TestCase> testCases,
        IReadOnlyList<RisoluzioneConflittoAudit> risoluzioni,
        string operatore,
        out int valutazioniRimosse,
        out string? errore,
        Func<FontiRimozioneValutazioniColonnaAudit>? ricaricaFontiPrimaDelCommit = null,
        DateTime? dataConferma = null)
    {
        valutazioniRimosse = 0;
        if (!anteprima.HaValutazioni)
        {
            errore = null;
            return false;
        }

        string? erroreRevalidazione = null;
        var candidateRimosse = 0;
        List<ValutazioneRequisitoEcuVeicolo>? fotografiaValutazioni = null;
        var salvata = ProvaAggiorna(repo, valutazioniCorrenti =>
        {
            fotografiaValutazioni = EntityStateFingerprint.Clone(valutazioniCorrenti);
            var corrente = CatturaRimozioneValutazioniColonnaAudit(
                anteprima.Componente, anteprima.Veicolo, tutteLeEcu, requisiti, testCases, risoluzioni, valutazioniCorrenti);
            if (!RimozioneColonnaCorrisponde(anteprima, corrente))
            {
                erroreRevalidazione = MessaggioAnteprimaRimozioneColonnaCambiata(anteprima);
                return false;
            }

            var uidAttesi = new HashSet<Guid>(anteprima.Valutazioni.Select(v => v.Uid));
            var requisitiAttesi = new HashSet<string>(anteprima.ImpronteRequisitiAttivi.Keys, StringComparer.OrdinalIgnoreCase);
            var correnti = valutazioniCorrenti.Where(v => uidAttesi.Contains(v.Uid) &&
                !v.IsArchiviata &&
                v.Componente.Equals(anteprima.Componente, StringComparison.OrdinalIgnoreCase) &&
                v.Veicolo.Equals(anteprima.Veicolo, StringComparison.OrdinalIgnoreCase) &&
                requisitiAttesi.Contains(v.RequisitoId) &&
                v.Origine is OrigineValutazioneRequisito.Manuale or OrigineValutazioneRequisito.Propagata).ToList();
            if (correnti.Count != anteprima.NumeroValutazioni)
            {
                erroreRevalidazione = MessaggioAnteprimaRimozioneColonnaCambiata(anteprima);
                return false;
            }

            var adesso = dataConferma ?? DateTime.Now;
            foreach (var correnteValutazione in correnti)
            {
                AccodaSnapshotCella(correnteValutazione);
                correnteValutazione.IsArchiviata = true;
                correnteValutazione.EventoCorrenteId = null;
                correnteValutazione.UltimaModifica = adesso;
                correnteValutazione.ModificatoDa = operatore;
            }
            candidateRimosse = correnti.Count;
            return candidateRimosse > 0;
        }, out errore, ricaricaFontiPrimaDelCommit == null ? null : () =>
        {
            var fontiFresche = ricaricaFontiPrimaDelCommit();
            var fotografie = fotografiaValutazioni ?? throw new InvalidOperationException("Fotografia Phase D non disponibile.");
            var ricalcolata = CatturaRimozioneValutazioniColonnaAudit(
                anteprima.Componente, anteprima.Veicolo, fontiFresche.Componenti, fontiFresche.Requisiti,
                fontiFresche.TestCases, fontiFresche.Risoluzioni, fotografie);
            if (!RimozioneColonnaCorrisponde(anteprima, ricalcolata))
                return MessaggioAnteprimaRimozioneColonnaCambiata(anteprima);
            return null;
        }, allowSilentRemoteMerge: false);

        if (!salvata)
        {
            valutazioniRimosse = 0;
            errore ??= erroreRevalidazione;
            return false;
        }

        valutazioniRimosse = candidateRimosse;
        errore = null;
        return true;
    }

    private static bool RimozioneRigaCorrisponde(
        AnteprimaRimozioneValutazioniRigaAudit attesa,
        AnteprimaRimozioneValutazioniRigaAudit corrente)
    {
        if (!attesa.RequisitoId.Equals(corrente.RequisitoId, StringComparison.OrdinalIgnoreCase) ||
            !attesa.Veicolo.Equals(corrente.Veicolo, StringComparison.OrdinalIgnoreCase) ||
            !attesa.EcuAssociate.OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(corrente.EcuAssociate.OrderBy(e => e, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase) ||
            attesa.ImpronteFontiEsternePerEcu.Count != corrente.ImpronteFontiEsternePerEcu.Count ||
            attesa.Valutazioni.Count != corrente.Valutazioni.Count)
            return false;

        foreach (var (componente, impronta) in attesa.ImpronteFontiEsternePerEcu)
            if (!corrente.ImpronteFontiEsternePerEcu.TryGetValue(componente, out var correnteImpronta) ||
                !correnteImpronta.Equals(impronta, StringComparison.Ordinal))
                return false;

        var attesePerUid = attesa.Valutazioni.GroupBy(v => v.Uid).ToDictionary(g => g.Key, g => g
            .OrderBy(v => v.Componente, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.ImprontaEntita, StringComparer.Ordinal).ToList());
        var correntiPerUid = corrente.Valutazioni.GroupBy(v => v.Uid).ToDictionary(g => g.Key, g => g
            .OrderBy(v => v.Componente, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.ImprontaEntita, StringComparer.Ordinal).ToList());
        return attesePerUid.Count == correntiPerUid.Count && attesePerUid.All(kv =>
            correntiPerUid.TryGetValue(kv.Key, out var voci) && kv.Value.SequenceEqual(voci));
    }

    private static bool RimozioneColonnaCorrisponde(
        AnteprimaRimozioneValutazioniColonnaAudit attesa,
        AnteprimaRimozioneValutazioniColonnaAudit corrente)
    {
        if (!attesa.Componente.Equals(corrente.Componente, StringComparison.OrdinalIgnoreCase) ||
            !attesa.Veicolo.Equals(corrente.Veicolo, StringComparison.OrdinalIgnoreCase) ||
            !attesa.EcuAssociataAlVeicolo || !corrente.EcuAssociataAlVeicolo ||
            !ImpronteCorrispondono(attesa.ImpronteRequisitiAttivi, corrente.ImpronteRequisitiAttivi) ||
            !ImpronteCorrispondono(attesa.ImpronteFontiEsternePerRequisito, corrente.ImpronteFontiEsternePerRequisito) ||
            attesa.Valutazioni.Count != corrente.Valutazioni.Count)
            return false;

        var attesePerUid = attesa.Valutazioni.GroupBy(v => v.Uid).ToDictionary(g => g.Key, g => g
            .OrderBy(v => v.RequisitoId, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.ImprontaEntita, StringComparer.Ordinal).ToList());
        var correntiPerUid = corrente.Valutazioni.GroupBy(v => v.Uid).ToDictionary(g => g.Key, g => g
            .OrderBy(v => v.RequisitoId, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.ImprontaEntita, StringComparer.Ordinal).ToList());
        return attesePerUid.Count == correntiPerUid.Count && attesePerUid.All(kv =>
            correntiPerUid.TryGetValue(kv.Key, out var voci) && kv.Value.SequenceEqual(voci));
    }

    private static bool ImpronteCorrispondono(
        IReadOnlyDictionary<string, string> attese,
        IReadOnlyDictionary<string, string> correnti) =>
        attese.Count == correnti.Count && attese.All(kv =>
            correnti.TryGetValue(kv.Key, out var impronta) && impronta.Equals(kv.Value, StringComparison.Ordinal));

    private static string MessaggioAnteprimaRimozioneCambiata(AnteprimaRimozioneValutazioniRigaAudit anteprima) =>
        $"La riga {anteprima.RequisitoId} del veicolo {anteprima.Veicolo} è cambiata dopo l'anteprima; nessuna valutazione è stata rimossa. Ricarica e ripeti l'operazione.";

    private static string MessaggioAnteprimaRimozioneColonnaCambiata(AnteprimaRimozioneValutazioniColonnaAudit anteprima) =>
        $"La colonna componente {anteprima.Componente} del veicolo {anteprima.Veicolo} è cambiata dopo l'anteprima; nessuna valutazione è stata rimossa. Ricarica e ripeti l'operazione.";

    /// <summary>Decide se una cella "!" già risolta (<see cref="RisolviCella"/>, <c>Propagabile == true</c>) è
    /// propagabile in massa (Piano 6.4.0, punto 3): SOLO se tutti i candidati proposti hanno lo stesso
    /// punteggio (N/A conta come valore, quindi entra nel confronto come qualsiasi altro). Se concordano, la
    /// sorgente registrata è il candidato con la Data più recente; a parità di data lo spareggio è il nome del
    /// veicolo sorgente in ordine alfabetico (deterministico — la sentinella "(componente)" ordina prima di qualunque
    /// nome di veicolo reale, ma è irrilevante: a quel punto i punteggi concordano già). Ritorna null se la
    /// cella non è propagabile in massa (candidati discordanti): il chiamante la classifica come conflitto.</summary>
    public static VocePropagazioneMassiva? DecidiPropagazioneMassiva(RisoluzioneCellaRequisitoEcuVeicolo cella)
    {
        if (!cella.Propagabile || cella.CandidatiPropagazione.Count == 0) return null;

        var puntiDistinti = cella.CandidatiPropagazione.Values.Select(c => c.PunteggioSorgente).Distinct().ToList();
        if (puntiDistinti.Count != 1) return null;

        var scelto = cella.CandidatiPropagazione.Values
            .OrderByDescending(c => c.DataSorgente)
            .ThenBy(c => c.VeicoloSorgente, StringComparer.OrdinalIgnoreCase)
            .First();
        return new VocePropagazioneMassiva(cella.Componente, cella.RequisitoId, scelto);
    }

    /// <summary>Calcola, SENZA scrivere nulla, l'anteprima di Propagazione massiva per il veicolo
    /// <paramref name="veicoloNome"/> sull'insieme di componente indicato (una sola componente per "Propaga tutti i '!' di
    /// questa componente", tutti i componenti del sottoveicolo per "Propaga tutti i '!' di tutti i componenti" — Piano 6.4.0,
    /// punto 1). Perimetro: solo le coppie (Componente, Requisito) la cui cella per QUESTO veicolo risolve a "!"
    /// (<see cref="RisolviCella"/>.Propagabile) — le celle Dirette, Manuali, Propagate o già risolte da una
    /// RisoluzioneConflittoAudit manuale (Componente×Requisito, senza dimensione veicolo — stesso short-circuit già
    /// applicato da AuditMatriceService.Matrice) non vengono nemmeno considerate, indipendentemente dagli
    /// altri veicoli della stessa componente (mai toccati da questa funzione).</summary>
    public static AnteprimaPropagazioneMassiva AnteprimaPropagazioneMassiva(
        string veicoloNome,
        IReadOnlyList<Componente> ecuDaConsiderare,
        IReadOnlyList<Requisito> requisitiAttivi,
        IReadOnlyList<TestCase> testCases,
        IReadOnlyList<ValutazioneRequisitoEcuVeicolo> valutazioni,
        IReadOnlyList<RisoluzioneConflittoAudit>? risoluzioni = null)
    {
        risoluzioni ??= Array.Empty<RisoluzioneConflittoAudit>();
        var perCoppia = AuditMatriceService.IndicizzaTestPerEcuERequisito(testCases);
        var risoluzioniPerCoppia = risoluzioni
            .GroupBy(r => (r.Componente, r.RequisitoId))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.UltimaModifica).First());
        var valutazioniPerCoppia = Indicizza(valutazioni);

        var daPropagare = new List<VocePropagazioneMassiva>();
        var conflitti = new List<ConflittoPropagazioneMassiva>();

        foreach (var componente in ecuDaConsiderare)
        foreach (var req in requisitiAttivi)
        {
            if (!RequisitoApplicabilita.SiApplicaAlSottoveicolo(req, veicoloNome)) continue;

            // Stesso short-circuit di AuditMatriceService.Matrice: una RisoluzioneConflittoAudit manuale già
            // valorizzata per la coppia rende la cella "Valutato" a monte, mai "!" — RisolviCella non viene
            // nemmeno chiamata, come per il resto della matrice.
            if (risoluzioniPerCoppia.TryGetValue((componente.Nome, req.Id), out var risManuale) && risManuale.PunteggioManuale.HasValue) continue;

            var testDellaCoppia = perCoppia.TryGetValue((componente.Nome, req.Id), out var listaTest) ? listaTest : (IReadOnlyList<TestCase>)Array.Empty<TestCase>();
            var valutazioniDellaCoppia = valutazioniPerCoppia.TryGetValue((componente.Nome, req.Id), out var valMap)
                ? valMap.Values.ToList()
                : new List<ValutazioneRequisitoEcuVeicolo>();
            risoluzioniPerCoppia.TryGetValue((componente.Nome, req.Id), out var risoluzioneConflittoDiretta);

            var cella = RisolviCella(req.Id, componente, veicoloNome, testDellaCoppia, valutazioniDellaCoppia, risoluzioneConflittoDiretta);
            if (!cella.Propagabile) continue;

            var voce = DecidiPropagazioneMassiva(cella);
            if (voce != null) daPropagare.Add(voce);
            else conflitti.Add(new ConflittoPropagazioneMassiva(componente.Nome, req.Id, cella.CandidatiPropagazione.Values.ToList()));
        }

        return new AnteprimaPropagazioneMassiva(daPropagare, conflitti);
    }

    /// <summary>Conferma ed esegue la Propagazione massiva (Piano 6.4.0, punti 1-6): stessa persistenza di
    /// <see cref="Propaga"/> (stesso formato riga, stessa Origine=Propagata, stesso pattern
    /// Load→muta→Save con optimistic-concurrency di <see cref="ProvaAggiorna"/>), ma per più celle in
    /// un'unica scrittura atomica.
    ///
    /// <paramref name="celleAttese"/> è esattamente <see cref="AnteprimaPropagazioneMassiva"/>.DaPropagare
    /// (o un sottoinsieme) — il tipo <see cref="VocePropagazioneMassiva"/> stesso impedisce a un chiamante di
    /// farci passare una cella che l'anteprima aveva mostrato come conflitto (quelle non hanno un'unica
    /// sorgente/punteggio da rappresentare in una VocePropagazioneMassiva). La garanzia vive QUI, non nel
    /// chiamante (review indipendente 2026-09-26, CRITICO): per ciascuna voce attesa, lo stato viene
    /// ricalcolato sui dati appena letti da <paramref name="repo"/> (non sullo snapshot con cui è stata
    /// costruita l'anteprima, che può risalire a prima che l'operatore finisse di leggere il dialogo), e la
    /// riga viene scritta SOLO se il ricalcolo fresco produce ancora un'unica sorgente concordante con lo
    /// STESSO <see cref="CandidatoPropagazioneRequisito.PunteggioSorgente"/> mostrato nell'anteprima — una
    /// sorgente diversa ma con lo stesso punteggio è accettata e viene registrata con i dati FRESCHI (veicolo/
    /// Test/data), mai con quelli (potenzialmente stantii) dell'anteprima. Qualunque altra differenza —
    /// punteggio cambiato, conflitto emerso o cella già risolta da un'altra postazione (Diretto, Manuale,
    /// Propagata da un'altra sorgente) — finisce in <see cref="RisultatoPropagazioneMassiva.SaltatePerchèCambiate"/>
    /// o <see cref="RisultatoPropagazioneMassiva.ConflittiRimasti"/>, mai scritta. Se TUTTE le celle attese
    /// sono state saltate/diventate conflitto non c'è nulla da scrivere: nessun Save viene effettuato (no-op,
    /// non un errore). Fail-closed SENZA retry automatico: se repo.Save lancia (scrittura concorrente durante
    /// la finestra Load→Save di questa stessa chiamata, rara e distinta dal caso normale sopra), non viene
    /// scritto nulla e il chiamante deve far ripetere all'operatore l'intera azione (anteprima + conferma) da
    /// capo — questo metodo non ritenta da solo.</summary>
    public static RisultatoPropagazioneMassiva PropagaMassiva(
        MasterDataRepository<ValutazioneRequisitoEcuVeicolo> repo,
        string veicoloNome,
        IReadOnlyList<Componente> ecuDaConsiderare,
        IReadOnlyList<Requisito> requisitiAttivi,
        IReadOnlyList<TestCase> testCases,
        IReadOnlyList<RisoluzioneConflittoAudit>? risoluzioni,
        IReadOnlyList<VocePropagazioneMassiva> celleAttese,
        string nota,
        string operatore,
        out string? errore,
        DateTime? dataConferma = null,
        Func<IReadOnlyList<TestCase>>? ricaricaTestCasePrimaDelCommit = null)
    {
        if (celleAttese.Count == 0 || string.IsNullOrWhiteSpace(nota))
        {
            errore = "Nessuna cella da propagare o nota mancante.";
            return new RisultatoPropagazioneMassiva(Array.Empty<VocePropagazioneMassiva>(), Array.Empty<ConflittoPropagazioneMassiva>(), Array.Empty<(string, string)>());
        }

        var attesePerChiave = celleAttese.ToDictionary(v => (v.Componente, v.RequisitoId));
        var propagate = new List<VocePropagazioneMassiva>();
        var conflittiRimasti = new List<ConflittoPropagazioneMassiva>();
        var saltate = new List<(string Componente, string RequisitoId)>();
        List<ValutazioneRequisitoEcuVeicolo>? valutazioniPrimaDellaMutazione = null;

        var salvata = ProvaAggiorna(repo, valutazioni =>
        {
            valutazioniPrimaDellaMutazione = EntityStateFingerprint.Clone(valutazioni);
            // Vedi commento analogo in Propaga: ripara eventuali collisioni preesistenti visibili PRIMA di
            // ricalcolare l'anteprima fresca e applicare la mutazione.
            ConsolidaCollisioni(valutazioni, operatore);

            propagate.Clear();
            conflittiRimasti.Clear();
            saltate.Clear();

            // Ricalcolo sui dati appena caricati (fresh), NON sullo snapshot dell'anteprima: qui vive la
            // difesa dal caso "un'altra postazione scrive fra anteprima e conferma" (Piano 6.4.0, punto 5).
            var fresca = AnteprimaPropagazioneMassiva(veicoloNome, ecuDaConsiderare, requisitiAttivi, testCases, valutazioni, risoluzioni);
            var daPropagarePerChiave = fresca.DaPropagare.ToDictionary(v => (v.Componente, v.RequisitoId));
            var conflittiPerChiave = fresca.Conflitti.ToDictionary(c => (c.Componente, c.RequisitoId));

            foreach (var (chiave, attesa) in attesePerChiave)
            {
                // Scrive SOLO se il ricalcolo fresco risolve ancora questa cella a un'unica sorgente
                // concordante con LO STESSO punteggio confermato dall'operatore: il confronto è sul
                // punteggio, non sull'identità della sorgente, così un veicolo sorgente diverso ma con lo
                // stesso valore non blocca la propagazione — ma qualunque punteggio diverso (unanime o no)
                // la salta, mai la scrive con un valore che l'operatore non ha visto.
                if (daPropagarePerChiave.TryGetValue(chiave, out var fresh) && fresh.Sorgente.PunteggioSorgente == attesa.Sorgente.PunteggioSorgente)
                {
                    propagate.Add(fresh); // sorgente FRESCA (veicolo/Test/data), mai quella dell'anteprima
                }
                else if (conflittiPerChiave.TryGetValue(chiave, out var conflitto))
                {
                    conflittiRimasti.Add(conflitto);
                }
                else
                {
                    saltate.Add(chiave); // punteggio cambiato o cella già risolta diversamente nel frattempo
                }
            }

            if (propagate.Count == 0) return false; // nulla da scrivere: no-op, nessun Save

            var adesso = dataConferma ?? DateTime.Now;
            foreach (var voce in propagate)
            {
                SalvaValoreCella(valutazioni, voce.RequisitoId, voce.Componente, veicoloNome,
                    voce.Sorgente.PunteggioSorgente, OrigineValutazioneRequisito.Propagata,
                    nota.Trim(), operatore, null, voce.Sorgente.VeicoloSorgente,
                    voce.Sorgente.TestIdSorgente, voce.Sorgente.PunteggioSorgente, adesso);
            }
            return true;
        }, out errore, ricaricaTestCasePrimaDelCommit == null ? null : () =>
        {
            var testCorrenti = ricaricaTestCasePrimaDelCommit();
            var valutazioniFotografate = valutazioniPrimaDellaMutazione ?? throw new InvalidOperationException("Fotografia Phase D non disponibile.");
            var fresca = AnteprimaPropagazioneMassiva(veicoloNome, ecuDaConsiderare, requisitiAttivi,
                testCorrenti, valutazioniFotografate, risoluzioni);
            var daPropagarePerChiave = fresca.DaPropagare.ToDictionary(v => (v.Componente, v.RequisitoId));
            foreach (var attesa in celleAttese)
            {
                if (!daPropagarePerChiave.TryGetValue((attesa.Componente, attesa.RequisitoId), out var corrente) ||
                    corrente.Sorgente != attesa.Sorgente)
                    return $"La sorgente o il target {attesa.Componente} × {attesa.RequisitoId} × {veicoloNome} è cambiato prima del commit; nessuna cella è stata scritta. Ripeti anteprima e conferma.";
            }
            return null;
        });

        if (!salvata && errore != null)
        {
            // Il riepilogo deve riflettere che il batch fail-closed non ha scritto nessuna riga.
            propagate.Clear();
            conflittiRimasti.Clear();
            saltate.Clear();
        }

        return new RisultatoPropagazioneMassiva(propagate, conflittiRimasti, saltate);
    }
}
