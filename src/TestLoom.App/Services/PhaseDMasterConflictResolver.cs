using System.Text.Json;

namespace TestLoom.App.Services;

public sealed record PhaseDMasterConflittoVersione(
    string VersionId,
    PhaseDMasterMutationOperation Operazione,
    DateTime TimestampUtc,
    bool Visualizzata,
    IReadOnlyList<string> CampiDiversi);

public sealed record PhaseDMasterConflitto(
    string Catalogo,
    Guid EntityUid,
    string BusinessKey,
    IReadOnlyList<PhaseDMasterConflittoVersione> Versioni)
{
    /// <summary>Firma stabile dell'insieme delle versioni: cambia quando il conflitto cambia (serve a non rinotificare).</summary>
    public string Firma => string.Join(",", Versioni.Select(v => v.VersionId).OrderBy(x => x, StringComparer.Ordinal));
}

public sealed record PhaseDMasterRisoluzione(
    string Catalogo,
    Guid EntityUid,
    Guid MutationId,
    PhaseDMasterMutationOperation Operazione,
    string Operatore);

/// <summary>
/// Risoluzione Master delle teste concorrenti di un catalogo master Phase D.
///
/// Lettura provvisoria: finche' non c'e' una scelta la voce mostra la TESTA DETERMINISTICA di
/// <see cref="PhaseDMasterMaterializer.ResolveHeads"/> = versione con TimestampUtc massimo (a parita' MutationId massimo).
/// La regola dipende solo dal contenuto dei journal, quindi ogni PC mostra la stessa versione.
///
/// Risoluzione: il Master sceglie una delle versioni concorrenti. Si pubblica UNA nuova mutation (Upsert con il payload
/// scelto, oppure Delete se la versione scelta e' una eliminazione) il cui version vector DOMINA tutte le teste
/// concorrenti: unione dei vettori in conflitto + incremento del contatore del proprio device. Tutte le versioni restano
/// nei journal (nulla viene cancellato); la nuova mutation le rende superate. Dopo la sincronizzazione ogni PC vede una
/// sola testa, senza conflitto, uguale alla versione scelta.
///
/// Non c'e' un ruolo nel journal: il controllo Master e' in questo servizio (caller), la scrittura passa dai normali
/// controlli di workspace (WorkspaceWritePolicy) e dal batch condiviso (PhaseDMasterBatchWriter).
/// </summary>
public static class PhaseDMasterConflictResolver
{
    /// <summary>Identificatore stabile di una versione: hash del payload per un Upsert, costante per una eliminazione.
    /// Due mutation con lo stesso contenuto condividono l'identificatore (una sola scelta nella UI).</summary>
    public static string VersionIdOf(PhaseDMasterMutationOperation operazione, string payloadSha256) =>
        operazione == PhaseDMasterMutationOperation.Delete
            ? PhaseDMasterMutation.HashPayload("DELETE")
            : payloadSha256.ToUpperInvariant();

    /// <summary>Elenco dei conflitti aperti su tutti i cataloghi master del workspace. Vuoto se Phase D non e' pronto.</summary>
    public static IReadOnlyList<PhaseDMasterConflitto> Elenca(string root, string? localBaseDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!PhaseDMasterJournalService.IsInitialized(root)) return Array.Empty<PhaseDMasterConflitto>();

        var teste = new PhaseDMasterMaterializer(root, localBaseDirectory).LoadConflicts(root);
        var risultato = new List<PhaseDMasterConflitto>(teste.Count);
        foreach (var testa in teste)
        {
            var visualizzataId = VersionIdOf(testa.Operation, testa.PayloadSha256);
            var versioni = testa.ConcurrentHeads
                .GroupBy(m => VersionIdOf(m.Operation, m.PayloadSha256), StringComparer.Ordinal)
                .Select(g => g.OrderBy(m => m.TimestampUtc).ThenBy(m => m.MutationId).Last())
                .Select(m =>
                {
                    var id = VersionIdOf(m.Operation, m.PayloadSha256);
                    return new PhaseDMasterConflittoVersione(
                        id, m.Operation, m.TimestampUtc, id == visualizzataId, CampiDiversi(m, testa));
                })
                .OrderBy(v => v.TimestampUtc).ThenBy(v => v.VersionId, StringComparer.Ordinal)
                .ToList();

            var businessKey = !string.IsNullOrWhiteSpace(testa.BusinessKey)
                ? testa.BusinessKey
                : testa.ConcurrentHeads.FirstOrDefault(m => m.Operation == PhaseDMasterMutationOperation.Upsert)?.BusinessKey ?? "";
            risultato.Add(new PhaseDMasterConflitto(testa.Catalog, testa.EntityUid, businessKey, versioni));
        }

        return risultato
            .OrderBy(c => c.Catalogo, StringComparer.Ordinal)
            .ThenBy(c => c.BusinessKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.EntityUid)
            .ToList();
    }

    /// <summary>
    /// Risolve un conflitto scegliendo la versione <paramref name="versionId"/> (vedi <see cref="VersionIdOf"/>).
    /// Lancia UnauthorizedAccessException se il chiamante non e' Master, IOException se il workspace non e' scrivibile,
    /// InvalidOperationException se il conflitto non c'e' piu' o la versione non ne fa piu' parte.
    /// </summary>
    public static PhaseDMasterRisoluzione Risolvi(
        DataPaths paths,
        string catalogo,
        Guid entityUid,
        string versionId,
        string operatore,
        bool isMaster,
        string? localBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogo);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        if (entityUid == Guid.Empty) throw new ArgumentException("Uid vuoto.", nameof(entityUid));
        if (!isMaster)
            throw new UnauthorizedAccessException("Solo un Master può risolvere un conflitto fra postazioni nei cataloghi master.");
        WorkspaceWritePolicy.EnsureCanWrite(paths, $"Risoluzione conflitto master Phase D {catalogo}");

        var lettore = new PhaseDMasterScalableReader(paths.Root, localBaseDirectory);
        var stato = lettore.LoadPatchState(catalogo, new[] { entityUid }, Array.Empty<string>());
        if (!stato.Heads.TryGetValue(entityUid, out var testa) || !testa.HasConflict)
            throw new InvalidOperationException(
                "Nessun conflitto aperto per questa voce: forse un'altra postazione l'ha già risolto. Aggiorna l'elenco.");

        var scelta = testa.ConcurrentHeads
            .Where(m => VersionIdOf(m.Operation, m.PayloadSha256).Equals(versionId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.TimestampUtc).ThenBy(m => m.MutationId)
            .LastOrDefault()
            ?? throw new InvalidOperationException(
                "La versione scelta non fa più parte del conflitto. Aggiorna l'elenco e riprova.");

        if (scelta.Operation == PhaseDMasterMutationOperation.Upsert && !PhaseDMasterKeyCollisionTolerance.IsTolerant(catalogo))
        {
            var proprietari = lettore.LoadPatchState(catalogo, Array.Empty<Guid>(), new[] { scelta.BusinessKey }).BusinessKeyOwners;
            if (proprietari.TryGetValue(scelta.BusinessKey, out var proprietario) && proprietario != entityUid)
                throw new PhaseDMasterBusinessKeyConflictException(catalogo, scelta.BusinessKey, proprietario, entityUid);
        }

        var isUpsert = scelta.Operation == PhaseDMasterMutationOperation.Upsert;
        var nuova = new PhaseDMasterMutation
        {
            MutationId = Guid.NewGuid(),
            Catalog = catalogo,
            EntityUid = entityUid,
            Operation = scelta.Operation,
            // Unione dei vettori in conflitto + incremento del proprio contatore: domina ogni testa concorrente.
            VersionVector = PhaseDVersionVector.Increment(testa.VersionVector, DeviceIdentityService.GetOrCreate()),
            PayloadJson = isUpsert ? scelta.PayloadJson : "",
            PayloadSha256 = isUpsert ? scelta.PayloadSha256 : "",
            BusinessKey = isUpsert ? scelta.BusinessKey : "",
            TimestampUtc = DateTime.UtcNow
        };
        PhaseDMasterBatchWriter.Pubblica(paths, localBaseDirectory, new[] { nuova });

        var dopo = lettore.LoadPatchState(catalogo, new[] { entityUid }, Array.Empty<string>());
        if (!dopo.Heads.TryGetValue(entityUid, out var testaDopo) || testaDopo.HasConflict)
            throw new PhaseDMasterConflictException(catalogo, new[] { entityUid });

        return new PhaseDMasterRisoluzione(catalogo, entityUid, nuova.MutationId, scelta.Operation, operatore);
    }

    /// <summary>Nomi dei campi di primo livello che differiscono dalla versione mostrata. Mai i valori.</summary>
    private static IReadOnlyList<string> CampiDiversi(PhaseDMasterMutation versione, PhaseDMaterializedHead testaVisibile)
    {
        if (versione.Operation != PhaseDMasterMutationOperation.Upsert ||
            testaVisibile.Operation != PhaseDMasterMutationOperation.Upsert ||
            versione.PayloadSha256.Equals(testaVisibile.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();
        try
        {
            using var sinistra = JsonDocument.Parse(versione.PayloadJson);
            using var destra = JsonDocument.Parse(testaVisibile.PayloadJson);
            var report = JsonStructuralDiffService.Compare(sinistra.RootElement, destra.RootElement, maxDifferences: 200);
            return report.Items
                .Select(d => PrimoCampo(d.Path))
                .Distinct(StringComparer.Ordinal)
                .Take(12)
                .ToList();
        }
        catch (JsonException)
        {
            return new[] { "contenuto non confrontabile" };
        }
    }

    private static string PrimoCampo(string path)
    {
        var segmenti = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segmenti.Length == 0 ? "(voce)" : segmenti[0];
    }
}
