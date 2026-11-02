using System.Collections.Concurrent;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Marcatore condiviso delle migrazioni idempotenti del catalogo (catalog-migrations.json nella radice della
/// cartella dati). Contiene gli id già applicati e una revisione: ogni scrittura riparte da una rilettura, verifica che
/// revisione e contenuto non siano cambiati e riunisce gli id scritti da altre postazioni (stesso schema di AccessModeConfig).</summary>
public sealed class CatalogMigrationsStato
{
    public long RevisioneDati { get; set; }
    public List<string> Applicate { get; set; } = new();
}

public enum EsitoMigrazioneCatalogo
{
    /// <summary>Id già registrato: nessuna lettura del catalogo, nessuna scrittura.</summary>
    GiaApplicata,

    /// <summary>Template aggiunto e pubblicato sul catalogo condiviso.</summary>
    Aggiunto,

    /// <summary>Template già presente per Uid o per nome (anche archiviato): nessun append.</summary>
    GiaPresente,

    /// <summary>Catalogo dei template vuoto su un file legacy esistente, letto senza errori e con array vuoto (il Master lo
    /// ha svuotato): nessun append, marcatore registrato, così la migrazione non riaggiunge RAS UnitLogger. Un file mancante o non
    /// leggibile non è uno svuotamento (esito <see cref="EsitoMigrazioneCatalogo.Rinviata"/>, nessun marcatore). Eccezione Phase D:
    /// uno zero residuo non è mai uno svuotamento, perché il seed di avvio ripopola i default (RAS UnitLogger incluso) prima della
    /// migrazione; per questo lo zero in Phase D resta <see cref="EsitoMigrazioneCatalogo.Rinviata"/>.</summary>
    CatalogoVuoto,

    /// <summary>Catalogo in modifica su un'altra sessione, conflitto persistente o errore di lettura/scrittura del catalogo:
    /// nessuna riga aggiunta e nessun marcatore. Il prossimo avvio ripete la verifica.</summary>
    Rinviata,

    /// <summary>Marcatore illeggibile: fail-closed, nessuna scrittura del catalogo.</summary>
    Bloccata,
}

/// <summary>Esito della migrazione. <see cref="MarcatorePendente"/> = il catalogo è già nello stato voluto (template
/// aggiunto o già presente) ma il marcatore non è ancora scritto: il chiamante riprova con
/// <see cref="CatalogMigrationService.RiprovaMarcatore"/>, che non riaggiunge mai il template.</summary>
public sealed record RisultatoMigrazioneCatalogo(EsitoMigrazioneCatalogo Esito, bool MarcatorePendente);

/// <summary>Parametri di esecuzione: i valori predefiniti sono quelli di produzione. I seam (<c>Dopo...</c>, <c>Prima...</c>)
/// servono ai test per simulare le altre postazioni nelle finestre di concorrenza.</summary>
public sealed class OpzioniMigrazioneCatalogo
{
    /// <summary>Tentativi di pubblicazione del catalogo per chiamata, in caso di conflitto di concorrenza.</summary>
    public int TentativiPubblicazione { get; init; } = 3;

    /// <summary>Tentativi di scrittura del marcatore per chiamata (guasti di I/O e conflitti di revisione).</summary>
    public int TentativiMarcatore { get; init; } = 3;

    /// <summary>Attesa base fra i tentativi del marcatore; cresce linearmente (1x, 2x, ...).</summary>
    public TimeSpan AttesaMarcatore { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Seam di test: dopo la prima lettura del catalogo.</summary>
    public Action? DopoCaricamento { get; init; }

    /// <summary>Seam di test: dopo la rilettura che precede la pubblicazione, cioè fra la verifica dei nomi e il salvataggio.</summary>
    public Action? PrimaDellaPubblicazione { get; init; }

    /// <summary>Seam di test: dopo la lettura del marcatore e prima della sua scrittura.</summary>
    public Action? PrimaDiScrivereMarcatore { get; init; }
}

/// <summary>Migrazione one-shot e idempotente: aggiunge il template "RAS UnitLogger" a un catalogo esistente.
/// Garanzie e limiti:
/// <list type="bullet">
/// <item>La pubblicazione è serializzata con il lock del catalogo dei template (stessa chiave dell'editor Master,
/// il file del catalogo). Se un'altra sessione lo tiene, la migrazione rinvia senza scrivere. L'editor però salva senza
/// verificare il lock (revisione e hash restano la garanzia): una scrittura di un nuovo template «RAS UnitLogger» da un'altra
/// postazione nella stessa frazione di secondo può ancora sovrapporsi. È un rischio residuo documentato in
/// RELEASE_NOTES 6.6.1.</item>
/// <item>Il nome e l'Uid si verificano su una rilettura fatta subito prima della pubblicazione: con Uid o nome già
/// presenti (anche archiviati, nome senza distinzione di maiuscole) non si aggiunge nulla.</item>
/// <item>Il marcatore è scritto solo dopo il salvataggio, o quando il template risulta già presente, con controllo di
/// revisione prima della scrittura e verifica dopo. Se la scrittura fallisce, il marcatore resta in sospeso e viene
/// riprovato nel corso della sessione (giro periodico, <see cref="RiprovaMarcatore"/>) e, al più tardi, all'avvio.
/// Finestra residua: se il marcatore non si riesce a scrivere per tutta la sessione e il Master elimina «RAS UnitLogger» in
/// quella stessa sessione, il template ricompare al primo avvio successivo in cui la migrazione può girare.</item>
/// <item>Un marcatore illeggibile blocca ogni scrittura (fail-closed). Il marcatore non viene mai riparato
/// automaticamente: la riparazione dei JSON (FileRepairService) lo rifiuta.</item>
/// </list>
/// Chiamare solo da sessioni che possono scrivere (vedi AppState.SessionePuoMigrareCatalogo).</summary>
public static class CatalogMigrationService
{
    public const string IdRasUdtTemplateV1 = "ras-unitlogger-template-v1";

    /// <summary>Esegue la migrazione RAS UnitLogger. Non lancia per conflitti di concorrenza, per un catalogo in modifica su
    /// un'altra sessione o per errori di I/O: rinvia, e il risultato dice se il marcatore è in sospeso.</summary>
    public static RisultatoMigrazioneCatalogo EseguiRasUdt(DataPaths paths, MasterDataRepository<TestTemplate> templates,
        LockService locks, string operatore, OpzioniMigrazioneCatalogo? opzioni = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatore);
        opzioni ??= new OpzioniMigrazioneCatalogo();

        var stato = Leggi(paths, out var errore);
        if (stato == null)
        {
            CrashLogService.Registra($"Migrazione catalogo {IdRasUdtTemplateV1}: marcatore non leggibile, nessuna scrittura.",
                new InvalidDataException(errore ?? "marcatore non leggibile"));
            return new RisultatoMigrazioneCatalogo(EsitoMigrazioneCatalogo.Bloccata, MarcatorePendente: false);
        }
        if (stato.Applicate.Contains(IdRasUdtTemplateV1, StringComparer.Ordinal))
            return new RisultatoMigrazioneCatalogo(EsitoMigrazioneCatalogo.GiaApplicata, MarcatorePendente: false);

        // Stesso lock dell'editor Master dei template (chiave: il file del catalogo). Un lock già vivo, anche di questa
        // stessa istanza (per esempio un editor aperto), non viene né acquisito né rilasciato da qui: si rinvia.
        var catalogo = paths.TestTemplateFile;
        if (locks.GetActiveLock(catalogo) != null)
            return Rinvia("catalogo dei template in modifica su un'altra sessione");
        if (!locks.TryAcquire(catalogo, operatore).Acquisito)
            return Rinvia("lock del catalogo dei template non acquisito");
        try
        {
            return PubblicaRasUdt(paths, templates, opzioni);
        }
        finally
        {
            locks.Release(catalogo);
        }
    }

    /// <summary>Riprova nel corso della sessione la sola scrittura del marcatore rimasta in sospeso. Non legge né modifica
    /// il catalogo: da questo tentativo un template eliminato dal Master non può rinascere.</summary>
    public static bool RiprovaMarcatore(DataPaths paths, OpzioniMigrazioneCatalogo? opzioni = null) =>
        RegistraApplicata(paths, IdRasUdtTemplateV1, opzioni ?? new OpzioniMigrazioneCatalogo());

    /// <summary>Registra il marcatore subito dopo il seed di un catalogo nuovo, che contiene già RAS UnitLogger, così che una futura
    /// eliminazione del Master non venga annullata da una migrazione successiva. Non blocca l'avvio.</summary>
    public static void RegistraDopoSeed(DataPaths paths)
    {
        if (!RegistraApplicata(paths, IdRasUdtTemplateV1, new OpzioniMigrazioneCatalogo()))
            DiagnosticTraceService.Registra("Catalogo.Migrazione",
                $"{IdRasUdtTemplateV1}: marcatore non registrato dopo il seed; riprovo nel corso della sessione o all'avvio.");
    }

    /// <summary>Lettura tollerante del marcatore: file assente = nessun id applicato; file presente ma non valido = null,
    /// così il chiamante non scrive nulla (fail-closed).</summary>
    internal static CatalogMigrationsStato? Leggi(DataPaths paths, out string? errore)
    {
        errore = null;
        var path = paths.CatalogMigrationsFile;
        if (!File.Exists(path)) return new CatalogMigrationsStato();

        var stato = JsonIo.TryRead<CatalogMigrationsStato>(path, out var messaggio);
        if (stato == null)
        {
            errore = messaggio ?? "file non leggibile";
            return null;
        }
        if (stato.Applicate == null) stato.Applicate = new List<string>();
        return stato;
    }

    /// <summary>Registra un id nel marcatore condiviso con controllo di revisione e riunione degli id altrui. Restituisce true
    /// se l'id risulta scritto e verificato (o già presente), false se dopo i tentativi non è stato possibile scriverlo.</summary>
    internal static bool RegistraApplicata(DataPaths paths, string id, OpzioniMigrazioneCatalogo opzioni)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(opzioni);

        for (var tentativo = 1; tentativo <= opzioni.TentativiMarcatore; tentativo++)
        {
            try
            {
                if (TentaScritturaMarcatore(paths, id, opzioni))
                {
                    _erroriMarcatore.TryRemove(paths.CatalogMigrationsFile, out _);
                    return true;
                }
                DiagnosticTraceService.Registra("Catalogo.Migrazione",
                    $"marcatore {id}: revisione cambiata durante la scrittura (tentativo {tentativo}); rileggo e riunisco gli id.");
            }
            catch (Exception ex)
            {
                if (tentativo == opzioni.TentativiMarcatore)
                {
                    SegnalaErroreMarcatore(paths, id, opzioni, ex);
                    return false;
                }
                DiagnosticTraceService.Registra("Catalogo.Migrazione", $"marcatore {id}: tentativo {tentativo} fallito ({ex.GetType().Name}).");
            }
            if (tentativo < opzioni.TentativiMarcatore && opzioni.AttesaMarcatore > TimeSpan.Zero)
                Thread.Sleep(TimeSpan.FromTicks(opzioni.AttesaMarcatore.Ticks * tentativo));
        }
        SegnalaErroreMarcatore(paths, id, opzioni, null);
        return false;
    }

    /// <summary>Serie di esiti falliti consecutivi del marcatore per cartella, nel processo. Una serie produce una sola voce
    /// nel crash log (la prima); un successo chiude la serie, così un nuovo guasto torna a essere registrato.</summary>
    private static readonly ConcurrentDictionary<string, (int Serie, int VociLog)> _erroriMarcatore =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stato degli errori del marcatore per la cartella (serie corrente e voci scritte), per test e diagnostica.</summary>
    internal static (int Serie, int VociLog) ErroriMarcatore(DataPaths paths) =>
        _erroriMarcatore.TryGetValue(paths.CatalogMigrationsFile, out var stato) ? stato : (0, 0);

    private static void SegnalaErroreMarcatore(DataPaths paths, string id, OpzioniMigrazioneCatalogo opzioni, Exception? ultimoErrore)
    {
        var stato = _erroriMarcatore.AddOrUpdate(paths.CatalogMigrationsFile,
            _ => (Serie: 1, VociLog: 1),
            (_, precedente) => (Serie: precedente.Serie + 1, VociLog: precedente.VociLog));
        if (stato.Serie == 1)
            CrashLogService.Registra(
                $"Marcatore migrazione {id}: non registrato dopo {opzioni.TentativiMarcatore} tentativi " +
                $"({ultimoErrore?.GetType().Name ?? "conflitto di revisione"}); riprovo nel corso della sessione.",
                ultimoErrore);
        else
            DiagnosticTraceService.Registra("Catalogo.Migrazione",
                $"marcatore {id}: ancora in sospeso (tentativo {stato.Serie} della serie, nessuna nuova voce di log).");
    }

    /// <returns>true se l'id è presente nel marcatore dopo la scrittura verificata; false se revisione o contenuto sono
    /// cambiati durante il tentativo (conflitto: il chiamante rilegge e riunisce).</returns>
    private static bool TentaScritturaMarcatore(DataPaths paths, string id, OpzioniMigrazioneCatalogo opzioni)
    {
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Registrazione migrazione catalogo");
        var letto = Leggi(paths, out var errore)
            ?? throw new InvalidDataException($"Marcatore migrazioni non leggibile ({errore}): nessuna scrittura.");
        if (letto.Applicate.Contains(id, StringComparer.Ordinal)) return true;

        var impronta = EntityStateFingerprint.Compute(letto);
        opzioni.PrimaDiScrivereMarcatore?.Invoke();

        // Controllo prima della scrittura, come AccessModeConfigService: se revisione o impronta sono cambiate,
        // un'altra postazione ha scritto e questo tentativo non deve sovrascriverla.
        var immediatamentePrima = Leggi(paths, out _);
        if (immediatamentePrima == null || immediatamentePrima.RevisioneDati != letto.RevisioneDati
            || EntityStateFingerprint.Compute(immediatamentePrima) != impronta)
            return false;

        var nuovo = new CatalogMigrationsStato
        {
            RevisioneDati = letto.RevisioneDati + 1,
            Applicate = letto.Applicate.Append(id).ToList(),
        };
        JsonIo.WriteAtomic(paths.CatalogMigrationsFile, nuovo);

        // Verifica dopo la scrittura: l'id deve risultare presente (una scrittura concorrente lo avrebbe cancellato).
        var verifica = Leggi(paths, out _);
        return verifica != null && verifica.Applicate.Contains(id, StringComparer.Ordinal);
    }

    private static RisultatoMigrazioneCatalogo PubblicaRasUdt(DataPaths paths, MasterDataRepository<TestTemplate> templates,
        OpzioniMigrazioneCatalogo opzioni)
    {
        for (var tentativo = 1; tentativo <= opzioni.TentativiPubblicazione; tentativo++)
        {
            try
            {
                var primaLettura = templates.Load();
                opzioni.DopoCaricamento?.Invoke();

                // Rilettura immediatamente prima della decisione e della pubblicazione: nome e Uid si verificano su questo stato.
                var items = templates.Load();
                if (items.Count != primaLettura.Count)
                    DiagnosticTraceService.Registra("Catalogo.Migrazione",
                        $"{IdRasUdtTemplateV1}: catalogo cambiato durante la verifica ({primaLettura.Count} -> {items.Count} template).");
                if (items.Count == 0) return ZeroRighe(paths, opzioni);
                if (items.Any(IsRasUdt)) return RegistraEsito(paths, opzioni, EsitoMigrazioneCatalogo.GiaPresente);

                var ammessi = TemplateAmmessiPerNome(items);
                var ordine = items.Max(t => t.Ordine) + 1;
                items.Add(TestTemplateDefaults.CreaRasUdt(ordine, ammessi));
                opzioni.PrimaDellaPubblicazione?.Invoke();
                templates.Save(items);
                return RegistraEsito(paths, opzioni, EsitoMigrazioneCatalogo.Aggiunto);
            }
            catch (Exception ex) when (EConflittoConcorrente(ex) && tentativo < opzioni.TentativiPubblicazione)
            {
                DiagnosticTraceService.Registra("Catalogo.Migrazione",
                    $"{IdRasUdtTemplateV1} conflitto di pubblicazione al tentativo {tentativo} ({ex.GetType().Name}): rileggo e ricontrollo.");
            }
            catch (Exception ex)
            {
                // Conflitto persistente o errore transitorio (workspace bloccato, I/O, catalogo non leggibile): nessuna riga
                // duplicata e nessun marcatore. Il prossimo avvio, o un nuovo giro, ripete la verifica.
                CrashLogService.Registra($"Migrazione catalogo {IdRasUdtTemplateV1} rinviata (tentativo {tentativo}).", ex);
                return new RisultatoMigrazioneCatalogo(EsitoMigrazioneCatalogo.Rinviata, MarcatorePendente: false);
            }
        }
        return new RisultatoMigrazioneCatalogo(EsitoMigrazioneCatalogo.Rinviata, MarcatorePendente: false);
    }

    private static RisultatoMigrazioneCatalogo RegistraEsito(DataPaths paths, OpzioniMigrazioneCatalogo opzioni, EsitoMigrazioneCatalogo esito)
    {
        var registrato = RegistraApplicata(paths, IdRasUdtTemplateV1, opzioni);
        return new RisultatoMigrazioneCatalogo(esito, MarcatorePendente: !registrato);
    }

    /// <summary>Catalogo senza righe. È uno svuotamento del Master solo se il file legacy esiste, si legge senza errori e
    /// contiene un array vuoto. Un file mancante o non leggibile non prova nulla (MasterDataRepository lo vede come catalogo
    /// vuoto anche su un errore di condivisione): si rinvia senza marcatore. In Phase D lo zero non è mai uno svuotamento
    /// legittimo, perché il bootstrap ripopola i default prima della migrazione.</summary>
    private static RisultatoMigrazioneCatalogo ZeroRighe(DataPaths paths, OpzioniMigrazioneCatalogo opzioni)
    {
        if (PhaseDMasterJournalService.IsInitialized(paths.Root))
            return Rinvia("catalogo dei template vuoto in Phase D (anomalia: il bootstrap lo ripopola)");
        if (!LegacyVuotoLetto(paths))
            return Rinvia("file dei template mancante o non leggibile");
        return RegistraEsito(paths, opzioni, EsitoMigrazioneCatalogo.CatalogoVuoto);
    }

    /// <summary>Vero solo se il file legacy esiste, si legge per intero e contiene un array JSON vuoto.</summary>
    private static bool LegacyVuotoLetto(DataPaths paths)
    {
        if (!File.Exists(paths.TestTemplateFile)) return false;
        var lista = JsonIo.TryRead<List<TestTemplate>>(paths.TestTemplateFile, out _);
        return lista is { Count: 0 };
    }

    private static RisultatoMigrazioneCatalogo Rinvia(string motivo)
    {
        DiagnosticTraceService.Registra("Catalogo.Migrazione", $"{IdRasUdtTemplateV1} rinviata: {motivo}.");
        return new RisultatoMigrazioneCatalogo(EsitoMigrazioneCatalogo.Rinviata, MarcatorePendente: false);
    }

    private static bool IsRasUdt(TestTemplate t) =>
        t.Uid == TestTemplateDefaults.RasUdtUid || NomeUguale(t.Nome, TestTemplateDefaults.NomeRasUdt);

    private static bool NomeUguale(string? nome, string atteso) =>
        string.Equals((nome ?? string.Empty).Trim(), atteso, StringComparison.OrdinalIgnoreCase);

    /// <summary>Uid dei template "Validazione Banco" e "Validazione Veicolo" del catalogo corrente, nell'ordine del seed.
    /// Una riga archiviata resta ammessa se è l'unica con quel nome (il riferimento per Uid non si perde). Lista vuota: il
    /// vincolo ammette qualunque template (come <see cref="VincoloCollegamentoTemplate"/>), e lo si registra nel log.</summary>
    private static List<Guid> TemplateAmmessiPerNome(IReadOnlyList<TestTemplate> items)
    {
        var ammessi = new List<Guid>();
        foreach (var nome in new[] { TestTemplateDefaults.NomeTemplateBanco, TestTemplateDefaults.NomeTemplateVeicolo })
        {
            var trovato = items.Where(t => NomeUguale(t.Nome, nome)).OrderBy(t => t.Archiviata).FirstOrDefault();
            if (trovato != null && !ammessi.Contains(trovato.Uid)) ammessi.Add(trovato.Uid);
        }
        if (ammessi.Count == 0)
            DiagnosticTraceService.Registra("Catalogo.Migrazione",
                $"{IdRasUdtTemplateV1}: nessun template banco/veicolo nel catalogo, vincolo collegamento senza template ammessi (qualunque).");
        return ammessi;
    }

    private static bool EConflittoConcorrente(Exception ex) =>
        ex is MasterDataConflictException or PhaseDMasterConflictException or PhaseDMasterBusinessKeyConflictException;
}
