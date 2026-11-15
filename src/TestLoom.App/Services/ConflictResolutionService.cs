using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

public sealed record ConflictResolveOutcome(bool Risolto, string Messaggio);

/// <summary>Sorgente di un conflitto "copia": envelope in quarantena oppure file ancora in loco (illeggibile/non stabile).</summary>
public sealed class ConflictCopySource
{
    public DomainRecoveryCategory Category { get; init; }
    public Guid Uid { get; init; }
    public string Titolo { get; init; } = "";
    public byte[] OtherBytes { get; init; } = Array.Empty<byte>();
    public ConflictCopyEntry? Entry { get; init; }
    public string? InPlacePath { get; init; }
    public string? CanonicalPathHint { get; init; }
    public string ExemptKey => Entry?.Id ?? Path.GetFullPath(InPlacePath ?? "");

    public static ConflictCopySource FromEntry(ConflictCopyEntry e) => new()
    {
        Category = e.Envelope.Category,
        Uid = e.Envelope.EntityUid,
        Titolo = ConflictResolutionService.TitoloDi(e.Envelope.Category, e.Payload, e.Envelope.EntityUid),
        OtherBytes = e.Payload,
        Entry = e
    };

    /// <summary>Copia ancora nella cartella del record. Null se non e' una copia esplicita con canonico accanto.</summary>
    public static ConflictCopySource? FromInPlace(DataPaths paths, string copyPath)
    {
        DomainRecoveryCategory? cat = null;
        foreach (var c in new[] { DomainRecoveryCategory.TestCase, DomainRecoveryCategory.NonConformita,
                     DomainRecoveryCategory.ChangeRequest, DomainRecoveryCategory.Segnalazione })
            if (TestLoomStorageOwnership.IsRecoverableDomainJsonPath(paths.Root, copyPath, c)) { cat = c; break; }
        if (cat == null || !ConflictCopyClassifier.TryGetCanonicalPath(copyPath, out var canonical)) return null;
        byte[] other;
        try { other = JsonIo.ReadAllBytesShared(copyPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { other = Array.Empty<byte>(); }
        Guid uid = Guid.Empty;
        try
        {
            if (JsonNode.Parse(JsonIo.ReadAllBytesShared(canonical)) is JsonObject o && Guid.TryParse(o["Uid"]?.ToString(), out var g)) uid = g;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new ConflictCopySource
        {
            Category = cat.Value, Uid = uid, Titolo = ConflictResolutionService.TitoloDi(cat.Value, other, uid),
            OtherBytes = other, InPlacePath = copyPath, CanonicalPathHint = canonical
        };
    }
}

/// <summary>Esito del percorso di confronto/scelta (indipendente dal tipo di record).</summary>
internal sealed record ConflictPipelineResult(byte[]? FinalBytes, bool Changed, object? Model);

/// <summary>
/// Risoluzione dei conflitti sul RECORD REALE (mai sul JSON): confronto campo per campo con anteprima, oppure, se il confronto non
/// e' possibile (file danneggiato, campi non confrontabili) o l'utente lo preferisce, scelta della versione intera in sola
/// lettura. Regole di sicurezza dati:
/// - nessuna scrittura prima della conferma dell'utente; annullare non modifica nulla;
/// - la versione scartata (o i rami incorporati in un'unione) va nel recovery-bin PRIMA di toccare il canonico, e se l'archivio
///   non e' scrivibile la risoluzione si ferma;
/// - il conflitto viene chiuso solo DOPO il salvataggio riuscito della versione risolta: un errore intermedio lascia il
///   conflitto aperto e nulla di perso (al piu' copie in piu' nel recovery-bin);
/// - il salvataggio passa dai normali controlli di revisione dei repository.
/// </summary>
public static class ConflictResolutionService
{
    public static RecordMergeKind KindOf(DomainRecoveryCategory c) => c switch
    {
        DomainRecoveryCategory.TestCase => RecordMergeKind.TestCase,
        DomainRecoveryCategory.NonConformita => RecordMergeKind.NonConformita,
        DomainRecoveryCategory.ChangeRequest => RecordMergeKind.ChangeRequest,
        DomainRecoveryCategory.Segnalazione => RecordMergeKind.Segnalazione,
        _ => RecordMergeKind.Generico
    };

    public static string EtichettaTipo(DomainRecoveryCategory c) => c switch
    {
        DomainRecoveryCategory.TestCase => "Test",
        DomainRecoveryCategory.NonConformita => "Non Conformità",
        DomainRecoveryCategory.ChangeRequest => "Change Request",
        DomainRecoveryCategory.Segnalazione => "Segnalazione",
        _ => "Voce di catalogo"
    };

    public static string TitoloDi(DomainRecoveryCategory c, byte[] bytes, Guid uid)
    {
        string? id = null;
        try { id = (JsonNode.Parse(bytes) as JsonObject)?["Id"]?.ToString(); }
        catch (JsonException) { }
        return $"{EtichettaTipo(c)} {(string.IsNullOrWhiteSpace(id) ? (uid == Guid.Empty ? "(non identificabile)" : uid.ToString("N")[..8]) : id)}";
    }

    // ---------------------------------------------------------------- percorso comune

    internal static string Anteprima(byte[] bytes)
    {
        if (bytes.Length == 0) return "Contenuto non disponibile.";
        try
        {
            if (JsonNode.Parse(bytes) is JsonObject o)
            {
                var copia = (JsonObject)o.DeepClone();
                foreach (var k in copia.Select(p => p.Key).Where(k => k.StartsWith("_ditm", StringComparison.OrdinalIgnoreCase)).ToList()) copia.Remove(k);
                return RecordValueFormatter.Format(copia);
            }
        }
        catch (JsonException) { }
        return $"Contenuto non leggibile ({bytes.Length} byte, impronta {Convert.ToHexString(SHA256.HashData(bytes))[..8]}).";
    }

    private static bool Scegliibile(RecordMergeKind kind, byte[]? bytes) => RecordMergeEngine.Problema(kind, bytes) == null;

    /// <summary>Righe per campo di <paramref name="bytes"/>, con Diverso = il valore differisce dall'altra versione.
    /// Null se il contenuto non e' un record leggibile (resta solo l'anteprima testuale).</summary>
    internal static IReadOnlyList<WholeVersionField>? CampiPerVista(byte[]? bytes, byte[]? altri)
    {
        if (bytes == null || bytes.Length == 0) return null;
        JsonObject? a, b = null;
        try
        {
            a = JsonNode.Parse(bytes) as JsonObject;
            if (altri is { Length: > 0 }) b = JsonNode.Parse(altri) as JsonObject;
        }
        catch (JsonException) { return null; }
        if (a == null) return null;
        bool Escluso(string k) => k.StartsWith("_ditm", StringComparison.OrdinalIgnoreCase);
        var righe = new List<WholeVersionField>();
        foreach (var (k, v) in a.Where(p => !Escluso(p.Key)))
        {
            JsonNode? ov = null;
            var inB = b != null && b.TryGetPropertyValue(k, out ov);
            righe.Add(new WholeVersionField(RecordValueFormatter.Label(k), RecordValueFormatter.Format(v),
                b != null && (!inB || !JsonNode.DeepEquals(v, ov))));
        }
        if (b != null)
            foreach (var (k, _) in b.Where(p => !Escluso(p.Key) && !a.ContainsKey(p.Key)))
                righe.Add(new WholeVersionField(RecordValueFormatter.Label(k), RecordValueFormatter.Format(null), true));
        return righe;
    }

    /// <summary>Confronta la versione corrente con una o piu' versioni in conflitto, una alla volta, guidando l'utente.</summary>
    internal static async Task<ConflictPipelineResult?> EseguiPercorsoAsync(
        RecordMergeKind kind, string titolo, byte[]? mine, IReadOnlyList<(byte[] Bytes, string Etichetta, string Descrizione)> altre,
        string operatore, string etichettaMia = "Versione corrente")
    {
        var working = mine;
        var changed = false;
        foreach (var (bytes, etichetta, descrizione) in altre)
        {
            var idDiversi = IdentificativiDiversi(kind, working, bytes);
            var prep = idDiversi
                ? new RecordMergePrepared(null, "Le due versioni hanno identificativi diversi: non si possono unire. L'altra versione può essere solo scartata.")
                : working != null
                    ? RecordMergeEngine.Prepare(kind, working, bytes)
                    : new RecordMergePrepared(null, "La versione corrente non è leggibile.");
            var problemaMia = RecordMergeEngine.Problema(kind, working);
            var problemaAltra = idDiversi ? "ha un identificativo diverso da quello del record corrente." : RecordMergeEngine.Problema(kind, bytes);
            var risolto = false;
            string motivo = prep.MotivoNonConfrontabile ?? "Scelta manuale della versione intera.";

            if (prep.Confrontabile)
            {
                // Contenuto uguale: nulla da decidere. Ma se il motore ha gia' applicato un'unione automatica (Nota: es. REV
                // unite e rinumerate) il risultato differisce dalla mia versione e va mostrato e applicato, mai scartato.
                if (!prep.Plan!.HasDifferences && prep.Plan.Nota == null) continue;
                var ui = await ConflictResolverUi.Merge(new MergeUiRequest(titolo, descrizione, prep.Plan, etichettaMia, etichetta));
                switch (ui.Esito)
                {
                    case MergeUiEsito.Annulla:
                        return null;
                    case MergeUiEsito.Unisci:
                        var res = prep.Plan.Build(ui.Scelte ?? new RecordMergeChoices(), operatore);
                        working = res.Bytes;
                        changed = true;
                        risolto = true;
                        break;
                    default:
                        motivo = "Hai scelto di tenere una delle due versioni intere.";
                        break;
                }
            }
            if (risolto) continue;

            var richiesta = new WholeVersionRequest(titolo, motivo,
                new WholeVersionSide(etichettaMia, "Quella oggi salvata", Anteprima(working ?? Array.Empty<byte>()), problemaMia == null,
                    problemaMia == null ? null : "Questa versione non si può tenere: " + problemaMia, CampiPerVista(working, bytes)),
                new WholeVersionSide(etichetta, descrizione, Anteprima(bytes), problemaAltra == null,
                    problemaAltra == null ? null : "Questa versione non si può tenere: " + problemaAltra, CampiPerVista(bytes, working)));
            var scelta = await ConflictResolverUi.WholeVersion(richiesta);
            if (scelta == null) return null;
            if (scelta == WholeVersionChoice.TieniAltra)
            {
                if (!richiesta.Altra.Scegliibile)
                    throw new InvalidOperationException("Questa versione non è leggibile: puo' essere scartata ma non scelta.");
                working = bytes;
                changed = true;
            }
        }

        object? model = null;
        var type = RecordMergeEngine.ModelType(kind);
        if (type != null && working != null)
        {
            try { model = JsonNode.Parse(working)!.Deserialize(type, JsonIo.Options); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { if (changed) throw; }
        }
        return new ConflictPipelineResult(working, changed, model);
    }

    /// <summary>Revisione dati scritta nel documento (null se assente/illeggibile).</summary>
    internal static long? RevisioneDi(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try { return (JsonNode.Parse(bytes) as JsonObject)?["RevisioneDati"]?.GetValue<long>(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    private static string? IdDi(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try { return (JsonNode.Parse(bytes) as JsonObject)?["Id"]?.ToString(); }
        catch (JsonException) { return null; }
    }

    /// <summary>Due versioni dello stesso record con identificativo diverso: scegliere l'altra scriverebbe il record in un nuovo
    /// percorso lasciando il canonico (UID duplicato). Non si fa: l'altra puo' solo essere scartata.</summary>
    internal static bool IdentificativiDiversi(RecordMergeKind kind, byte[]? a, byte[]? b)
    {
        if (kind == RecordMergeKind.Generico) return false;
        var x = IdDi(a);
        var y = IdDi(b);
        return x != null && y != null && !string.Equals(x, y, StringComparison.Ordinal);
    }

    private static void Tocca(object model, string operatore)
    {
        var t = model.GetType();
        var m = t.GetProperty("ModificatoDa");
        if (m is { CanWrite: true } && m.PropertyType == typeof(string)) m.SetValue(model, operatore);
        var u = t.GetProperty("UltimaModifica");
        if (u is { CanWrite: true } && u.PropertyType == typeof(DateTime)) u.SetValue(model, DateTime.Now);
    }

    // ---------------------------------------------------------------- copie (qualunque tipo)

    /// <summary>Percorso del record canonico per categoria/UID, oppure null.</summary>
    public static string? PercorsoCanonico(AppState state, DomainRecoveryCategory c, Guid uid)
    {
        if (uid == Guid.Empty) return null;
        switch (c)
        {
            case DomainRecoveryCategory.TestCase:
                return state.TestCases.GetByUid(uid) is { } tc ? state.TestCases.FilePathOf(tc) : null;
            case DomainRecoveryCategory.NonConformita:
                return state.NonConformita.GetByUid(uid) is { } nc ? state.NonConformita.FilePathOf(nc) : null;
            case DomainRecoveryCategory.ChangeRequest:
                return state.ChangeRequests.GetAll(includeArchiviate: true).FirstOrDefault(x => x.Uid == uid) is { } cr
                    ? state.Paths.ChangeRequestFile(cr.Id) : null;
            case DomainRecoveryCategory.Segnalazione:
                var p = state.Paths.SegnalazioneFile(uid);
                return File.Exists(p) ? p : null;
            default:
                return null;
        }
    }

    /// <summary>Salva la versione risolta. Restituisce un avviso da mostrare all'utente (riallineamento dei riferimenti REV non
    /// completato) oppure null; gli stessi errori sono scritti nel log.</summary>
    internal static string? SalvaVersioneRisolta(AppState state, DomainRecoveryCategory c, Guid uid, string canonicalPath,
        byte[] finalBytes, object? model, bool canonicoLeggibile, long? revisioneMia, string operatore)
    {
        if (!canonicoLeggibile || model == null)
        {
            // Canonico illeggibile: sostituzione grezza e atomica con la versione scelta (leggibile), dentro una transazione.
            WorkflowTransactionService.Execute(state.Paths, "conflict-replace:" + Path.GetFileName(canonicalPath),
                () => JsonIo.WriteBytesAtomic(canonicalPath, finalBytes, overwrite: true));
            return null;
        }
        Tocca(model, operatore);
        // La revisione attesa e' quella della versione che l'utente ha CONFRONTATO: se nel frattempo il record e' cambiato
        // (altra postazione, altra sessione) il repository rifiuta con ConflittoRevisioneException e nulla viene sovrascritto.
        var rev = revisioneMia ?? throw new IOException("La revisione della versione corrente non è nota: risoluzione annullata, nessuna modifica.");
        string? avviso = null;
        switch (c)
        {
            case DomainRecoveryCategory.TestCase:
            {
                var tc = (TestCase)model;
                tc.Uid = uid;
                tc.RevisioneDati = rev;
                // Risoluzione esplicita: le REV possono essere rinumerate/sostituite (versioni gia' nel recovery-bin).
                using (TestRevisionSafety.AmmettiRisoluzioneConflitto())
                    state.TestCases.Save(tc, isNew: false, revisioneAttesa: rev);
                // REV rinumerate dall'unione: i riferimenti per numero (NC, segnalazioni, CR) si riallineano per identita'.
                avviso = RiallineaRiferimentiRev(state, tc);
                break;
            }
            case DomainRecoveryCategory.NonConformita:
            {
                var nc = (NonConformita)model;
                nc.RevisioneDati = rev;
                state.NonConformita.Save(nc, rev);
                break;
            }
            case DomainRecoveryCategory.ChangeRequest:
            {
                var cr = (ChangeRequest)model;
                cr.RevisioneDati = rev;
                state.ChangeRequests.Save(cr, rev);
                break;
            }
            case DomainRecoveryCategory.Segnalazione:
            {
                var seg = (SegnalazioneDaClassificare)model;
                seg.RevisioneDati = rev;
                state.Segnalazioni.Save(seg, rev);
                break;
            }
            default:
                throw new InvalidOperationException("Tipo di record non supportato.");
        }
        return avviso;
    }

    /// <summary>Riallinea i riferimenti per numero di REV al Test unito. Errori: log + testo per l'utente (mai ingoiati). Null se tutto ok.</summary>
    public static string? RiallineaRiferimentiRev(AppState state, TestCase unito)
    {
        try
        {
            var esito = TestRevisionReferenceRemap.Riallinea(state, unito);
            foreach (var e in esito.Errori) CrashLogService.Registra("Riallineamento riferimenti REV: " + e, new InvalidOperationException(e));
            if (esito.Errori.Count == 0) return null;
            return "Attenzione: " + esito.Errori.Count + " riferimenti alle REV (NC, segnalazioni o CR) non sono stati riallineati e restano validi per identità: " +
                   string.Join("; ", esito.Errori.Take(3)) + (esito.Errori.Count > 3 ? "; …" : "") + ".";
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Riallineamento riferimenti REV", ex);
            return "Attenzione: riallineamento dei riferimenti alle REV non completato (" + ex.Message + "). I riferimenti restano validi per identità.";
        }
    }

    /// <summary>Risolve una copia di conflitto (Test, NC, CR, Segnalazione) con confronto grafico o scelta della versione intera.</summary>
    public static async Task<ConflictResolveOutcome> RisolviCopiaAsync(AppState state, ConflictCopySource src)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(src);
        if (!state.EMaster) throw new UnauthorizedAccessException("Solo un Master può risolvere un conflitto.");
        var operatore = state.Config.OperatoreAttivo;
        var canonical = PercorsoCanonico(state, src.Category, src.Uid) ?? src.CanonicalPathHint;
        if (canonical == null || !File.Exists(canonical))
            return new(false, "Il record corrente non è stato trovato: aggiornare l'elenco e riprovare. Nessuna modifica.");
        WorkspaceWritePolicy.EnsureCanWrite(state.Paths, "Risoluzione di un conflitto");

        byte[]? mine = null;
        try { mine = JsonIo.ReadAllBytesShared(canonical); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var kind = KindOf(src.Category);
        var canonicoLeggibile = Scegliibile(kind, mine);

        var descrizione = src.Entry != null
            ? $"Copia rilevata il {src.Entry.Envelope.DetectedUtc.ToLocalTime():dd/MM/yyyy HH:mm} su {src.Entry.Envelope.Device}"
            : "Copia lasciata dalla sincronizzazione";
        var percorso = await EseguiPercorsoAsync(kind, src.Titolo, mine,
            new[] { (src.OtherBytes, "Versione in conflitto", descrizione) }, operatore);
        if (percorso == null) return new(false, "Risoluzione annullata: nessuna modifica.");

        string? avvisoRiallineamento = null;
        await Task.Run(() =>
        {
            using var scope = VisibleConflictWriteGuard.BeginResolution(new[] { src.ExemptKey });
            // 1) Archivio durevole PRIMA di toccare il canonico.
            if (src.OtherBytes.Length > 0)
                DomainRecoveryService.ArchivePayload(state.Paths, canonical, src.Category, src.OtherBytes, operatore,
                    percorso.Changed ? "versione in conflitto: scartata o incorporata nella risoluzione" : "versione in conflitto scartata (tenuta la versione corrente)");
            if (percorso.Changed && mine is { Length: > 0 })
                DomainRecoveryService.ArchivePayload(state.Paths, canonical, src.Category, mine, operatore,
                    "versione corrente prima della risoluzione del conflitto");
            // 2) Salvataggio della versione risolta (solo se cambia qualcosa).
            if (percorso.Changed)
                avvisoRiallineamento = SalvaVersioneRisolta(state, src.Category, src.Uid, canonical, percorso.FinalBytes!, percorso.Model, canonicoLeggibile, RevisioneDi(mine), operatore);
            // 3) Chiusura SOLO dopo il salvataggio riuscito.
            if (src.Entry != null) ConflictCopyQuarantineService.Close(state.Paths.Root, src.Entry);
            else if (src.InPlacePath != null && File.Exists(src.InPlacePath)) WorkflowTransactionService.DeleteFile(src.InPlacePath);
            state.RegistraAudit("RisoluzioneConflittoCopia", src.Titolo,
                $"categoria={src.Category}; uid={src.Uid:N}; unione={percorso.Changed}");
        });
        return new(true, (percorso.Changed
            ? "Conflitto risolto: la versione scelta è stata salvata. Le versioni originali sono nel cestino di recupero."
            : "Conflitto chiuso mantenendo la versione corrente. L'altra versione è nel cestino di recupero.") +
            (avvisoRiallineamento == null ? "" : " " + avvisoRiallineamento));
    }

    // ---------------------------------------------------------------- cataloghi master

    /// <summary>Risolve un conflitto di una voce di catalogo master: confronto grafico (unione) oppure versione intera. Le
    /// versioni scartate vanno nel recovery-bin (categoria CatalogoMaster) prima della pubblicazione.</summary>
    public static async Task<ConflictResolveOutcome> RisolviMasterAsync(AppState state, PhaseDMasterConflitto conflitto)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(conflitto);
        if (!state.EMaster) throw new UnauthorizedAccessException("Solo un Master può risolvere un conflitto.");
        var operatore = state.Config.OperatoreAttivo;
        var paths = state.Paths;
        var versioni = await Task.Run(() => PhaseDMasterConflictResolver.CaricaVersioni(paths.Root, conflitto.Catalogo, conflitto.EntityUid));
        if (versioni.Count < 2)
            return new(false, "Nessun conflitto aperto per questa voce: forse un'altra postazione l'ha già risolto. Aggiorna l'elenco.");
        var titolo = $"{conflitto.Catalogo} · " + (string.IsNullOrWhiteSpace(conflitto.BusinessKey) ? conflitto.EntityUid.ToString("N")[..8] : conflitto.BusinessKey);
        var attuale = versioni.FirstOrDefault(v => v.Visualizzata) ?? versioni[0];
        var altre = versioni.Where(v => v.VersionId != attuale.VersionId).ToList();
        string Desc(PhaseDMasterConflictResolver.VersionePerConfronto v) => $"Versione del {v.TimestampUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}";

        string sceltaId;
        byte[]? unione = null;
        if (versioni.Any(v => v.Operazione != PhaseDMasterMutationOperation.Upsert))
        {
            WholeVersionSide Lato(PhaseDMasterConflictResolver.VersionePerConfronto v) => new(Desc(v),
                v.Operazione == PhaseDMasterMutationOperation.Delete ? "Elimina la voce dal catalogo" : "Modifica della voce",
                v.Operazione == PhaseDMasterMutationOperation.Delete ? "Questa versione ELIMINA la voce dal catalogo." : Anteprima(v.PayloadUtf8), true);
            var corrente = attuale;
            foreach (var o in altre)
            {
                var scelta = await ConflictResolverUi.WholeVersion(new WholeVersionRequest(titolo,
                    "Una delle versioni elimina la voce: non si possono unire campo per campo.", Lato(corrente), Lato(o)));
                if (scelta == null) return new(false, "Risoluzione annullata: nessuna modifica.");
                if (scelta == WholeVersionChoice.TieniAltra) corrente = o;
            }
            sceltaId = corrente.VersionId;
        }
        else
        {
            var esito = await EseguiPercorsoAsync(RecordMergeKind.Generico, titolo, attuale.PayloadUtf8,
                altre.Select(v => (v.PayloadUtf8, "Versione concorrente", Desc(v))).ToList(), operatore, "Versione mostrata ora");
            if (esito == null) return new(false, "Risoluzione annullata: nessuna modifica.");
            sceltaId = attuale.VersionId;
            if (esito.Changed)
            {
                var uguale = versioni.FirstOrDefault(v => v.PayloadUtf8.AsSpan().SequenceEqual(esito.FinalBytes));
                if (uguale != null) sceltaId = uguale.VersionId; else unione = esito.FinalBytes;
            }
        }

        var risoluzione = await Task.Run(() =>
        {
            var r = unione != null
                ? PhaseDMasterConflictResolver.RisolviConUnione(paths, conflitto.Catalogo, conflitto.EntityUid, unione, operatore, state.EMaster)
                : PhaseDMasterConflictResolver.Risolvi(paths, conflitto.Catalogo, conflitto.EntityUid, sceltaId, operatore, state.EMaster);
            state.RegistraAudit("RisoluzioneConflittoMaster", titolo,
                $"uid={conflitto.EntityUid:N}; unione={unione != null}; scelta={sceltaId[..8]}; mutation={r.MutationId:N}");
            return r;
        });
        return new(true, risoluzione.Operazione == PhaseDMasterMutationOperation.Delete
            ? "Conflitto risolto: la voce è stata eliminata. Le altre versioni sono nel cestino di recupero."
            : "Conflitto risolto: la versione scelta è stata pubblicata. Le altre versioni sono nel cestino di recupero.");
    }
}
