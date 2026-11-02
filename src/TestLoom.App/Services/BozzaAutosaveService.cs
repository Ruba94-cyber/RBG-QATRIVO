using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Contenitore serializzato della bozza di un Test nuovo non ancora autorevole.</summary>
public sealed class BozzaAutosalvata
{
    public DateTime SalvataIl { get; set; }
    public string Operatore { get; set; } = "";
    public TestCase TestCase { get; set; } = new();
}

/// <summary>
/// Bozza locale di modifica di un Test GIA esistente. Conserva anche la fotografia base letta all'apertura:
/// se nel frattempo OneDrive porta una versione remota diversa, TestLoom può fare un vero three-way merge
/// Base / Bozza locale / Versione corrente invece di sovrascrivere o scartare il lavoro dell'operatore.
/// </summary>
public sealed class BozzaModificaAutosalvata
{
    public DateTime SalvataIl { get; set; }
    public string Operatore { get; set; } = "";
    public Guid TestUid { get; set; }
    public string TestId { get; set; } = "";
    public long RevisioneBase { get; set; }
    public string HashBase { get; set; } = "";
    public TestCase BaseTestCase { get; set; } = new();
    public TestCase TestCase { get; set; } = new();
}

/// <summary>
/// Autosave locale, mai nella cartella condivisa. Le bozze nuove restano una per operatore; le modifiche a
/// Test esistenti sono una per UID, così aprire/modificare più Test non elimina la recovery di un altro.
/// </summary>
public class BozzaAutosaveService
{
    private readonly string _cartella;
    public BozzaAutosaveService(string cartellaLocale) => _cartella = cartellaLocale;

    private static string NomeOperatoreSicuro(string operatore)
    {
        var nome = string.Concat(operatore.Where(char.IsLetterOrDigit));
        return string.IsNullOrWhiteSpace(nome) ? "utente" : nome;
    }

    private string PercorsoPer(string operatore) =>
        Path.Combine(_cartella, $"bozza_nuovo_test_{NomeOperatoreSicuro(operatore)}.json");

    private string PercorsoModifica(string operatore, Guid testUid) =>
        Path.Combine(_cartella, $"bozza_modifica_test_{NomeOperatoreSicuro(operatore)}_{testUid:N}.json");

    public void Salva(string operatore, TestCase testCase)
    {
        if (string.IsNullOrWhiteSpace(operatore)) return;
        try
        {
            JsonIo.WriteAtomic(PercorsoPer(operatore), new BozzaAutosalvata
            {
                SalvataIl = DateTime.Now,
                Operatore = operatore,
                TestCase = EntityStateFingerprint.Clone(testCase)
            });
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Autosave bozza nuovo test", ex);
        }
    }

    public BozzaAutosalvata? Carica(string operatore)
    {
        if (string.IsNullOrWhiteSpace(operatore)) return null;
        var path = PercorsoPer(operatore);
        return File.Exists(path) ? JsonIo.TryRead<BozzaAutosalvata>(path, out _) : null;
    }

    public void Elimina(string operatore)
    {
        if (string.IsNullOrWhiteSpace(operatore)) return;
        TryDelete(PercorsoPer(operatore));
    }

    public void SalvaModifica(string operatore, TestCase baseTestCase, TestCase testCase)
    {
        if (string.IsNullOrWhiteSpace(operatore) || testCase.Uid == Guid.Empty || baseTestCase.Uid != testCase.Uid) return;
        try
        {
            var baseClone = EntityStateFingerprint.Clone(baseTestCase);
            JsonIo.WriteAtomic(PercorsoModifica(operatore, testCase.Uid), new BozzaModificaAutosalvata
            {
                SalvataIl = DateTime.Now,
                Operatore = operatore,
                TestUid = testCase.Uid,
                TestId = testCase.Id,
                RevisioneBase = baseClone.RevisioneDati,
                HashBase = TestCaseFingerprint.Compute(baseClone),
                BaseTestCase = baseClone,
                TestCase = EntityStateFingerprint.Clone(testCase)
            });
        }
        catch (Exception ex)
        {
            CrashLogService.Registra($"Autosave modifica Test {testCase.Id}", ex);
        }
    }

    public BozzaModificaAutosalvata? CaricaModifica(string operatore, Guid testUid)
    {
        if (string.IsNullOrWhiteSpace(operatore) || testUid == Guid.Empty) return null;
        var path = PercorsoModifica(operatore, testUid);
        if (!File.Exists(path)) return null;
        var draft = JsonIo.TryRead<BozzaModificaAutosalvata>(path, out var error);
        if (draft == null)
        {
            CrashLogService.Registra("Lettura bozza modifica Test", new InvalidDataException(error ?? "Bozza non leggibile."));
            return null;
        }
        if (draft.TestUid != testUid || draft.TestCase.Uid != testUid || draft.BaseTestCase.Uid != testUid)
        {
            CrashLogService.Registra("Lettura bozza modifica Test", new InvalidDataException("UID incoerente nella bozza locale."));
            return null;
        }
        return draft;
    }

    public void EliminaModifica(string operatore, Guid testUid)
    {
        if (string.IsNullOrWhiteSpace(operatore) || testUid == Guid.Empty) return;
        TryDelete(PercorsoModifica(operatore, testUid));
    }

    public IReadOnlyList<BozzaModificaAutosalvata> ElencaModifiche(string operatore)
    {
        if (string.IsNullOrWhiteSpace(operatore) || !Directory.Exists(_cartella)) return Array.Empty<BozzaModificaAutosalvata>();
        var prefix = $"bozza_modifica_test_{NomeOperatoreSicuro(operatore)}_";
        var result = new List<BozzaModificaAutosalvata>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(_cartella, prefix + "*.json", SearchOption.TopDirectoryOnly))
            {
                var draft = JsonIo.TryRead<BozzaModificaAutosalvata>(path, out _);
                if (draft != null && draft.Operatore.Equals(operatore, StringComparison.OrdinalIgnoreCase)) result.Add(draft);
            }
        }
        catch (Exception ex) { CrashLogService.Registra("Elenco bozze modifica Test", ex); }
        return result.OrderByDescending(x => x.SalvataIl).ToList();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* recovery locale best-effort; il file può già essere stato rimosso */ }
    }
}
