using System.Security.Cryptography;
using System.Text;

namespace TestLoom.App.Services;

/// <summary>Hash del PIN operatore. Nota esplicita (Addendum §17.2): questo NON è un confine di sicurezza
/// reale — chiunque abbia accesso alla cartella condivisa può comunque leggere/modificare operatori.json.
/// L'hashing serve solo a non tenere il PIN leggibile a colpo d'occhio (igiene minima), non a difendersi da
/// un utente malintenzionato con accesso al file.</summary>
public static class PinService
{
    public static string Hash(string pin)
    {
        var bytes = Encoding.UTF8.GetBytes(pin);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public static bool Verifica(string pin, string? hashSalvato) =>
        !string.IsNullOrEmpty(hashSalvato) && Hash(pin) == hashSalvato;

    /// <summary>Codice di recupero leggibile (4 gruppi di 4 caratteri alfanumerici, senza caratteri ambigui
    /// come 0/O o 1/I): generato ad ogni creazione/reset del PIN, mostrato una tantum, salvato solo come hash
    /// — permette a un operatore di reimpostare il proprio PIN senza un Master (Addendum redesign 2026-08).</summary>
    private const string AlfabetoRecupero = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string GeneraCodiceRecupero()
    {
        var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        var buffer = new byte[16];
        rng.GetBytes(buffer);
        var gruppi = new string[4];
        for (int g = 0; g < 4; g++)
        {
            var caratteri = new char[4];
            for (int i = 0; i < 4; i++) caratteri[i] = AlfabetoRecupero[buffer[g * 4 + i] % AlfabetoRecupero.Length];
            gruppi[g] = new string(caratteri);
        }
        return string.Join("-", gruppi);
    }

    /// <summary>PIN numerico casuale a 4 cifre (Addendum §5, feedback QA): usato da "Rigenera", che non
    /// richiede più di digitare un nuovo PIN a mano prima di poter generare un nuovo codice di recupero.</summary>
    public static string GeneraPin()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(2);
        var n = (bytes[0] << 8 | bytes[1]) % 10000;
        return n.ToString("D4");
    }

    /// <summary>Chiave fissa incorporata nell'app per la cifratura reversibile di PIN/chiave di recupero
    /// (feedback utente 2026-08-20, punto 2): NON è un vero segreto — è nel binario, chiunque decompili l'app
    /// la trova. Serve solo a evitare che il PIN sia leggibile a colpo d'occhio aprendo operatori.json a mano,
    /// stesso livello di "igiene minima" già dichiarato per l'hash, non una barriera contro un attaccante
    /// determinato. Il Master lo vede comunque sempre in chiaro dentro l'app (questo è l'obiettivo esplicito),
    /// la cifratura protegge solo la lettura diretta del file.</summary>
    private static readonly byte[] ChiaveCifratura = Convert.FromHexString("ad026455e64e7896179cd9ceffbe69553d2eb05d7e850c4a2b0f2d458fe88e56");

    /// <summary>Cifra un valore in chiaro (PIN o chiave di recupero) per la persistenza su disco. IV casuale
    /// per ogni chiamata, concatenato in testa al blob cifrato (Base64) — nessun bisogno di segretezza sull'IV.</summary>
    public static string? Cifra(string? testoInChiaro)
    {
        if (string.IsNullOrEmpty(testoInChiaro)) return null;
        using var aes = Aes.Create();
        aes.Key = ChiaveCifratura;
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(testoInChiaro);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        return Convert.ToBase64String(aes.IV.Concat(cipherBytes).ToArray());
    }

    /// <summary>Decifra un valore prodotto da Cifra(). Ritorna null se il blob è vuoto o non valido (es. dati
    /// residui da prima di questa funzionalità, o file corrotto) invece di lanciare — un valore non
    /// decifrabile deve apparire come "PIN non impostato", non far esplodere la UI.</summary>
    public static string? Decifra(string? cifrato)
    {
        if (string.IsNullOrEmpty(cifrato)) return null;
        try
        {
            var tutti = Convert.FromBase64String(cifrato);
            if (tutti.Length <= 16) return null;
            using var aes = Aes.Create();
            aes.Key = ChiaveCifratura;
            aes.IV = tutti[..16];
            using var decryptor = aes.CreateDecryptor();
            var plainBytes = decryptor.TransformFinalBlock(tutti, 16, tutti.Length - 16);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null;
        }
    }
}
