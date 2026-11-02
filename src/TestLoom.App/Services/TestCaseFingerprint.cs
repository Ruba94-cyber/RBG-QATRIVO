using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Fingerprint canonico del contenuto logico di un TestCase.
///
/// Il version stamp viene conservato nello stesso JSON usando TestCase.JsonExtensionData: non serve alcun
/// sidecar e quindi non aumenta il numero di file OneDrive/SharePoint. Il campo riservato viene escluso dal
/// proprio hash, evitando la ricorsione hash-di-se-stesso. Poiché ExtensionData viene già serializzata dal
/// clone dell'editor, la versione aperta porta con sé automaticamente il proprio stamp senza modificare il
/// ViewModel da centinaia di KB.
/// </summary>
public static class TestCaseFingerprint
{
    public const string StampKey = "_ditmContentHash";

    /// <summary>Versione dell'algoritmo di <see cref="Compute"/>: va incrementata ogni volta che cambia cio' che entra
    /// nell'hash (JsonIo.Options, forma del modello TestCase, chiavi escluse). L'indice SQLite locale la memorizza come
    /// marcatore (vedi <see cref="LocalTestIndexStore.HashIndicizzatiRiusabili"/>): hash prodotti da un'altra versione non
    /// vengono riusati dal warm start.</summary>
    public const string VersioneAlgoritmo = "sha256-v1";

    /// <summary>Vero solo per la forma di un hash SHA-256 esadecimale (64 caratteri): unico valore riusabile dall'indice.</summary>
    public static bool IsWellFormed(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);

    /// <summary>Guardrail unico per riusare lo hash memorizzato di una riga dell'indice Test come stamp: il marcatore
    /// di algoritmo (meta content_hash_algo) deve coincidere con <see cref="VersioneAlgoritmo"/> e la forma deve essere
    /// valida. Stesso criterio del costruttore di TestCaseRepository; in ogni altro caso lo stamp si ricalcola.</summary>
    public static bool RiusabileDaIndice(string? marcatoreAlgoritmo, string? hashMemorizzato) =>
        string.Equals(marcatoreAlgoritmo, VersioneAlgoritmo, StringComparison.Ordinal) && IsWellFormed(hashMemorizzato);

    /// <summary>Assegna lo stamp a un Test letto dalla riga dell'indice: riusa lo hash memorizzato solo se
    /// <see cref="RiusabileDaIndice"/> lo consente, altrimenti ricalcola. Il JSON deve essere gia' normalizzato
    /// (NormalizzaVeicoli) dal chiamante, come nel percorso di scrittura che ha prodotto la riga.</summary>
    public static string StampDaIndice(TestCase testCase, string? hashMemorizzato, string? marcatoreAlgoritmo)
    {
        if (hashMemorizzato != null && RiusabileDaIndice(marcatoreAlgoritmo, hashMemorizzato))
        {
            SetStored(testCase, hashMemorizzato);
            PerfContatori.IncrementaFingerprintRiusato();
            return hashMemorizzato;
        }
        return Stamp(testCase);
    }

    /// <summary>Hash SHA-256 del payload logico, compresi eventuali campi forward-compatible sconosciuti,
    /// esclusi il campo riservato StampKey e i marcatori di visibilità transazionale (WorkflowTransactionVisibilityService
    /// li scrive sul file DOPO il salvataggio: un oggetto in memoria stampato prima del commit non li porta mai,
    /// quindi includerli nell'hash renderebbe qualunque validazione successiva un falso conflitto di contenuto
    /// nonostante la revisione coincida — visto con SoftDelete + EliminaDefinitivamente/Restore sullo stesso Test).</summary>
    public static string Compute(TestCase testCase)
    {
        PerfContatori.IncrementaFingerprintCalcolato();
        var node = JsonSerializer.SerializeToNode(testCase, JsonIo.Options) as JsonObject
            ?? throw new InvalidOperationException("Impossibile serializzare il TestCase per il fingerprint.");
        node.Remove(StampKey);
        node.Remove(WorkflowTransactionVisibilityService.TransactionProperty);
        node.Remove(WorkflowTransactionVisibilityService.TransactionDeviceProperty);
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(node, JsonIo.Options);
        return Convert.ToHexString(SHA256.HashData(utf8));
    }

    public static string? GetStored(TestCase testCase)
    {
        if (testCase.ExtensionData == null || !testCase.ExtensionData.TryGetValue(StampKey, out var element))
            return null;
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    public static void SetStored(TestCase testCase, string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            testCase.ExtensionData?.Remove(StampKey);
            return;
        }
        testCase.ExtensionData ??= new Dictionary<string, JsonElement>();
        testCase.ExtensionData[StampKey] = JsonSerializer.SerializeToElement(hash);
    }

    /// <summary>Imposta/aggiorna lo stamp in memoria e ritorna l'hash calcolato.</summary>
    public static string Stamp(TestCase testCase)
    {
        var hash = Compute(testCase);
        SetStored(testCase, hash);
        return hash;
    }

    /// <summary>
    /// Per un JSON legacy privo di stamp lo aggiunge soltanto all'oggetto in memoria. Il file condiviso non
    /// viene riscritto durante la lettura; riceverà lo stamp al successivo salvataggio ordinario.
    /// </summary>
    public static string EnsureStamped(TestCase testCase)
    {
        var stored = GetStored(testCase);
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        return Stamp(testCase);
    }

    public static bool MatchesStored(TestCase testCase)
    {
        var stored = GetStored(testCase);
        return !string.IsNullOrWhiteSpace(stored) && stored.Equals(Compute(testCase), StringComparison.OrdinalIgnoreCase);
    }
}