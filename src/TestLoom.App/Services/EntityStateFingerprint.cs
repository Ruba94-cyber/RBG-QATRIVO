using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TestLoom.App.Services;

/// <summary>
/// Fingerprint canonico per le entità workflow JSON che non hanno ancora il journal specializzato dei Test.
/// Il repository conserva il fingerprint calcolato quando ha letto la propria fotografia autoritativa: così
/// può rilevare anche un fork "stessa RevisioneDati, contenuto diverso" creato da due PC offline.
/// </summary>
public static class EntityStateFingerprint
{
    /// <summary>
    /// Esclude i marcatori di visibilità transazionale (bug segnalato dall'utente 2026-09-18: "non ho la
    /// possibilita di archiviare le CR, quando creo un test collegato alla CR poi lo archivio... mi da che c'è
    /// il file della CR non leggibile"). Tutte le entità con [JsonExtensionData] (ChangeRequest, NonConformita,
    /// Segnalazione, i master) catturano _ditmTx/_ditmTxDevice quando vengono deserializzate da un file scritto
    /// dentro una WorkflowTransaction — WorkflowTransactionVisibilityService li stampa PERMANENTEMENTE nel JSON
    /// per la visibilità cross-PC (vedi StampJson). Un oggetto ancora in memoria, mai riletto dal file dopo il
    /// commit, non li porta: includerli nell'hash rende falso qualunque confronto "stessa revisione, stesso
    /// contenuto" fatto subito dopo — esattamente il caso creaTestValidazione (salva in transazione) seguito da
    /// Archivia (salva fuori transazione, sull'oggetto in memoria mai ristampato). Stessa correzione già
    /// applicata a TestCaseFingerprint.Compute per lo stesso problema sui Test.
    /// </summary>
    public static string Compute<T>(T value)
    {
        // Caso normale: il JSON compatto non contiene le proprieta' di transazione, e l'hash del nodo senza di esse coincide con
        // quello della serializzazione diretta. Una sola serializzazione, niente albero intermedio.
        var diretto = JsonSerializer.SerializeToUtf8Bytes(value, JsonIo.CompactOptions);
        if (!ContieneProprietaTransazione(diretto)) return Esadecimale(diretto);
        // Con le proprieta' di transazione: percorso storico (rimozione dal nodo, poi hash).
        var node = JsonSerializer.SerializeToNode(value, JsonIo.CompactOptions) as JsonObject;
        if (node != null)
        {
            node.Remove(WorkflowTransactionVisibilityService.TransactionProperty);
            node.Remove(WorkflowTransactionVisibilityService.TransactionDeviceProperty);
            return Esadecimale(JsonSerializer.SerializeToUtf8Bytes(node, JsonIo.CompactOptions));
        }
        return Esadecimale(diretto);
    }

    /// <summary>
    /// Come <see cref="Compute{T}"/>, quando il JSON compatto dell'entita' e' gia' disponibile (prodotto con
    /// <see cref="JsonIo.CompactOptions"/>): evita di serializzare di nuovo. Risultato identico a <see cref="Compute{T}"/>.
    /// </summary>
    public static string ComputeDaJson<T>(T value, string jsonCompatto)
    {
        var bytes = Encoding.UTF8.GetBytes(jsonCompatto);
        if (!ContieneProprietaTransazione(bytes)) return Esadecimale(bytes);
        return Compute(value);
    }

    private static readonly byte[] ProprietaTransazioneUtf8 =
        Encoding.UTF8.GetBytes("\"" + WorkflowTransactionVisibilityService.TransactionProperty + "\"");
    private static readonly byte[] ProprietaDeviceUtf8 =
        Encoding.UTF8.GetBytes("\"" + WorkflowTransactionVisibilityService.TransactionDeviceProperty + "\"");

    /// <summary>Ricerca conservativa: un testo che contiene il nome tra virgolette fa scegliere il percorso storico, che resta corretto.</summary>
    private static bool ContieneProprietaTransazione(ReadOnlySpan<byte> bytes) =>
        bytes.IndexOf(ProprietaTransazioneUtf8) >= 0 || bytes.IndexOf(ProprietaDeviceUtf8) >= 0;

    private static string Esadecimale(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static T Clone<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonIo.Options), JsonIo.Options)
        ?? throw new InvalidDataException($"Impossibile clonare {typeof(T).Name} per la fotografia di sicurezza.");
}
