using System.Security.Cryptography;
using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

public sealed record TestUidMigrationDiagnostic(string Entity, string Code, string State, string Detail);
public sealed record TestUidMigrationChange(string Path, string Kind, Guid Uid, long Revision, string Hash,
    Guid? TestUid, Dictionary<int, Guid> ProofUids);
public sealed class TestUidMigrationPlan
{
    public int SchemaVersion { get; set; } = 1;
    public string Root { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public int NonConformitaCount { get; set; }
    public int SegnalazioniCount { get; set; }
    public Dictionary<string, string> TestHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TestUidMigrationChange> Changes { get; set; } = new();
    public List<TestUidMigrationDiagnostic> Diagnostics { get; set; } = new();
}

/// <summary>Migrazione esplicita. Analyze legge soltanto byte; Apply usa revision/hash guard e la recovery
/// workflow esistente. Non viene chiamata da startup, refresh o apertura dell'editor.</summary>
public static class TestUidMigrationService
{
    internal static Action<int>? AfterWriteForTests { get; set; }

    public static TestUidMigrationPlan Analyze(DataPaths paths)
    {
        var plan = new TestUidMigrationPlan { Root = Path.GetFullPath(paths.Root) };
        var tests = new List<TestCase>();
        foreach (var folder in new[] { paths.TestCasesDir, Path.Combine(paths.Root, "cestino") })
        foreach (var file in Files(folder, "testcase_*.json"))
        {
            var bytes = JsonIo.ReadAllBytesShared(file);
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("_ditmDeleted", out var deleted) && deleted.ValueKind == JsonValueKind.True) continue;
            if (!doc.RootElement.TryGetProperty("Uid", out var uid) || !uid.TryGetGuid(out var testUid) || testUid == Guid.Empty)
                throw new InvalidDataException($"Test senza identità persistita: {file}");
            var test = JsonIo.TryParse<TestCase>(bytes, out var error) ?? throw new InvalidDataException($"{file}: {error}");
            tests.Add(test);
            plan.TestHashes[file] = Hash(bytes);
        }
        var byCode = tests.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var uidCounts = tests.GroupBy(t => t.Uid).ToDictionary(g => g.Key, g => g.Count());

        Guid? Resolve(string entity, string code, Guid? existing, Guid condition, int iteration)
        {
            var candidates = byCode.GetValueOrDefault(code) ?? new List<TestCase>();
            if (existing.HasValue)
            {
                plan.Diagnostics.Add(new(entity, code, uidCounts.GetValueOrDefault(existing.Value) == 1 ? "already-linked" : "orphan",
                    "TestUid esistente conservato senza override"));
                return null;
            }
            if (candidates.Count == 0)
            {
                plan.Diagnostics.Add(new(entity, code, "orphan", "Test non disponibile"));
                return null;
            }
            var certain = candidates.Count == 1 && uidCounts[candidates[0].Uid] == 1 && condition != Guid.Empty &&
                AnomaliaWorkflowService.RisolviProva(candidates[0], new RiferimentoProva
                    { TestId = code, CondizioneUid = condition, EsecuzioneIterazione = iteration }) != null;
            plan.Diagnostics.Add(new(entity, code, certain ? "linked" : "ambiguous",
                certain ? "Prova risolta nel Test attuale" : "Codice senza prova univoca risolvibile; nessuna modifica"));
            return certain ? candidates[0].Uid : null;
        }

        foreach (var file in Files(paths.NonConformitaDir, "nc_*.json", recursive: false))
        {
            var bytes = JsonIo.ReadAllBytesShared(file);
            if (IsTombstone(bytes)) continue;
            RequirePersistedUid(bytes, file);
            var nc = JsonIo.TryParse<NonConformita>(bytes, out var error);
            if (nc == null) throw new InvalidDataException($"{file}: {error}");
            if (!Path.GetFullPath(file).Equals(Path.GetFullPath(paths.NonConformitaFile(nc.Id)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"NC in percorso non canonico o copia di conflitto: {file}. Risolvere il file prima della migrazione.");
            plan.NonConformitaCount++;
            if (nc.TestUid.HasValue) Resolve(nc.Id, nc.TestCaseId, nc.TestUid, Guid.Empty, 0);
            var proofUids = new Dictionary<int, Guid>();
            for (var i = 0; i < nc.ProveCollegate.Count; i++)
            {
                var p = nc.ProveCollegate[i];
                // Una identità primaria già nota vieta di inferire un UID differente dal codice riusato.
                var primaryMismatch = nc.TestUid.HasValue && !p.TestUid.HasValue &&
                    string.Equals(nc.TestCaseId, p.TestId, StringComparison.OrdinalIgnoreCase);
                var resolved = Resolve($"{nc.Id}/proof/{i}", p.TestId, p.TestUid ?? (primaryMismatch ? nc.TestUid : null), p.CondizioneUid, p.EsecuzioneIterazione);
                if (resolved.HasValue) proofUids[i] = resolved.Value;
            }
            Guid? primary = null;
            if (!nc.TestUid.HasValue && !string.IsNullOrWhiteSpace(nc.TestCaseId))
            {
                var certain = nc.ProveCollegate.Select((p, i) => (p, i))
                    .Where(x => string.Equals(x.p.TestId, nc.TestCaseId, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.p.TestUid ?? proofUids.GetValueOrDefault(x.i)).Where(u => u != Guid.Empty).Distinct().ToList();
                // Un UID già presente su una prova orfana non autorizza a inferire l'identità primaria.
                if (certain.Count == 1 && byCode.GetValueOrDefault(nc.TestCaseId) is { Count: 1 } current &&
                    current[0].Uid == certain[0] && nc.ProveCollegate.Any(p =>
                        string.Equals(p.TestId, nc.TestCaseId, StringComparison.OrdinalIgnoreCase) &&
                        AnomaliaWorkflowService.RisolviProva(current[0], p) != null)) primary = certain[0];
                if (nc.ProveCollegate.Count == 0) Resolve(nc.Id, nc.TestCaseId, null, Guid.Empty, 0);
            }
            if (primary.HasValue || proofUids.Count > 0)
                plan.Changes.Add(new(file, "nc", nc.Uid, nc.RevisioneDati, Hash(bytes), primary, proofUids));
        }
        foreach (var file in Files(paths.SegnalazioniDir, "segnalazione_*.json", recursive: false))
        {
            var bytes = JsonIo.ReadAllBytesShared(file);
            if (IsTombstone(bytes)) continue;
            RequirePersistedUid(bytes, file);
            var signal = JsonIo.TryParse<SegnalazioneDaClassificare>(bytes, out var error) ?? throw new InvalidDataException($"{file}: {error}");
            if (!Path.GetFullPath(file).Equals(Path.GetFullPath(paths.SegnalazioneFile(signal.Uid)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Segnalazione in percorso non canonico o copia di conflitto: {file}. Risolvere il file prima della migrazione.");
            plan.SegnalazioniCount++;
            var uid = Resolve(signal.Uid.ToString(), signal.TestId, signal.TestUid, signal.CondizioneUid, signal.EsecuzioneIterazione);
            if (uid.HasValue) plan.Changes.Add(new(file, "signal", signal.Uid, signal.RevisioneDati, Hash(bytes), uid, new()));
        }
        return plan;
    }

    public static string? Apply(DataPaths paths, TestUidMigrationPlan plan, string localIndexDirectory)
    {
        if (plan.SchemaVersion != 1 || !Path.GetFullPath(paths.Root).Equals(plan.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Piano di migrazione non valido per questa cartella.");
        foreach (var change in plan.Changes)
        {
            if (change.Kind is not ("nc" or "signal")) throw new InvalidDataException("Famiglia record non valida nel piano.");
            var family = change.Kind == "nc" ? paths.NonConformitaDir : paths.SegnalazioniDir;
            var relative = Path.GetRelativePath(family, Path.GetFullPath(change.Path));
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new InvalidDataException("Percorso non valido per la famiglia di record.");
        }
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Migrazione esplicita TestUid");
        if (plan.Changes.Count == 0) return null;
        using var tx = WorkflowTransactionService.Begin(paths, "Migrazione esplicita TestUid");
        if (AlreadyApplied(plan)) return null;
        CheckSnapshot(plan);
        // Alzare il gate PRIMA di qualunque record UID. Un errore lascia tutti i record immutati.
        CompatibilitaVersioneService.AllineaSchemaDatiManifest(paths, new Version(6, 5, 2));
        CompatibilitaVersioneService.EnsureTestUidSchema(paths);
        var fresh = Analyze(paths);
        if (JsonSerializer.Serialize(fresh.Changes, JsonIo.CompactOptions) != JsonSerializer.Serialize(plan.Changes, JsonIo.CompactOptions))
            throw new IOException("Il piano non corrisponde più alle prove attuali. Ripetere dry-run.");
        var backup = Path.Combine(paths.Root, "_ditm", "test-uid-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        foreach (var change in plan.Changes)
        {
            var relative = Path.GetRelativePath(paths.Root, change.Path);
            var target = Path.Combine(backup, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(change.Path, target, overwrite: false);
        }
        JsonIo.WriteAtomic(Path.Combine(backup, "plan.json"), plan, overwrite: false);
        var ncRepo = new NonConformitaRepository(paths, localIndex: new LocalNonConformitaIndexStore(paths.Root, localIndexDirectory));
        var signalRepo = new SegnalazioneRepository(paths);
        ncRepo.RefreshIndex();
        signalRepo.RefreshIndex();
        var writes = 0;
        foreach (var change in plan.Changes)
        {
            if (Hash(JsonIo.ReadAllBytesShared(change.Path)) != change.Hash) throw new IOException("Modifica concorrente durante la migrazione.");
            if (change.Kind == "nc")
            {
                var nc = ncRepo.PeekAllReadOnly().Single(n => n.Uid == change.Uid).Clona();
                if (change.TestUid.HasValue) nc.TestUid ??= change.TestUid;
                foreach (var pair in change.ProofUids) nc.ProveCollegate[pair.Key].TestUid ??= pair.Value;
                ncRepo.Save(nc, change.Revision);
            }
            else
            {
                var signal = signalRepo.Get(change.Uid) ?? throw new IOException("Segnalazione non disponibile.");
                signal.TestUid ??= change.TestUid;
                signalRepo.Save(signal, change.Revision);
            }
            AfterWriteForTests?.Invoke(++writes);
        }
        foreach (var pair in plan.TestHashes)
            if (!File.Exists(pair.Key) || Hash(JsonIo.ReadAllBytesShared(pair.Key)) != pair.Value)
                throw new IOException("Test modificato durante la migrazione. Migrazione annullata.");
        var finalTests = Analyze(paths).TestHashes;
        if (finalTests.Count != plan.TestHashes.Count || finalTests.Any(p => !plan.TestHashes.TryGetValue(p.Key, out var hash) || hash != p.Value))
            throw new IOException("Catalogo Test modificato durante la migrazione. Migrazione annullata.");
        tx.Commit();
        return backup;
    }

    private static void CheckSnapshot(TestUidMigrationPlan plan)
    {
        foreach (var pair in plan.TestHashes)
            if (!File.Exists(pair.Key) || Hash(JsonIo.ReadAllBytesShared(pair.Key)) != pair.Value) throw new IOException("Test modificati dopo dry-run.");
        foreach (var change in plan.Changes)
        {
            var relative = Path.GetRelativePath(plan.Root, Path.GetFullPath(change.Path));
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new InvalidDataException("Percorso del piano fuori dal workspace.");
            if (!File.Exists(change.Path) || Hash(JsonIo.ReadAllBytesShared(change.Path)) != change.Hash) throw new IOException("Record modificati dopo dry-run.");
        }
    }

    private static bool AlreadyApplied(TestUidMigrationPlan plan)
    {
        foreach (var pair in plan.TestHashes)
            if (!File.Exists(pair.Key) || Hash(JsonIo.ReadAllBytesShared(pair.Key)) != pair.Value) return false;
        foreach (var change in plan.Changes)
        {
            var relative = Path.GetRelativePath(plan.Root, Path.GetFullPath(change.Path));
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new InvalidDataException("Percorso del piano fuori dal workspace.");
            if (change.Kind == "nc")
            {
                var nc = JsonIo.TryRead<NonConformita>(change.Path, out _);
                if (nc == null || nc.Uid != change.Uid || (change.TestUid.HasValue && nc.TestUid != change.TestUid) ||
                    change.ProofUids.Any(p => p.Key < 0 || p.Key >= nc.ProveCollegate.Count || nc.ProveCollegate[p.Key].TestUid != p.Value)) return false;
            }
            else
            {
                var signal = JsonIo.TryRead<SegnalazioneDaClassificare>(change.Path, out _);
                if (signal == null || signal.Uid != change.Uid || signal.TestUid != change.TestUid) return false;
            }
        }
        return true;
    }

    private static IEnumerable<string> Files(string folder, string pattern, bool recursive = true) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        : Array.Empty<string>();
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsTombstone(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.TryGetProperty("_ditmDeleted", out var deleted) && deleted.ValueKind == JsonValueKind.True;
    }
    private static void RequirePersistedUid(byte[] bytes, string path)
    {
        using var doc = JsonDocument.Parse(bytes);
        if (!doc.RootElement.TryGetProperty("Uid", out var uid) || !uid.TryGetGuid(out var value) || value == Guid.Empty)
            throw new InvalidDataException($"Record senza identità persistita: {path}");
    }
}
