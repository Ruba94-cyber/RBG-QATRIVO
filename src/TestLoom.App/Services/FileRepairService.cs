using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TestLoom.App.Services;

/// <summary>Riparazione automatica best-effort per un file JSON finito in "File con problemi" (§7.11):
/// copre solo i casi tipici di scrittura interrotta (BOM/whitespace spurio, virgola finale prima di una
/// chiusura, parentesi non bilanciate). Il file originale viene toccato soltanto dopo aver verificato che:
/// appartenga davvero allo storage TestLoom, il workspace sia scrivibile e senza transazioni concorrenti, il
/// contenuto non sia cambiato dal momento della lettura e una before-copy durevole sia stata creata.</summary>
public static class FileRepairService
{
    public sealed record RisultatoRiparazione(bool Riuscita, string Messaggio);

    public static RisultatoRiparazione ProvaRipara(string percorso)
    {
        if (string.IsNullOrWhiteSpace(percorso))
            return new RisultatoRiparazione(false, "Percorso file non valido.");

        string fullPath;
        try { fullPath = Path.GetFullPath(percorso); }
        catch (Exception ex) { return new RisultatoRiparazione(false, ex.Message); }

        // Il marcatore delle migrazioni del catalogo non si ripara mai: riscritto "a caso" potrebbe riportare un
        // registro diverso e far riapparire un tipo di test eliminato. Un marcatore corrotto resta fail-closed (Bloccata).
        if (string.Equals(Path.GetFileName(fullPath), "catalog-migrations.json", StringComparison.OrdinalIgnoreCase))
            return new RisultatoRiparazione(false,
                "Riparazione rifiutata: il marcatore delle migrazioni del catalogo non viene riparato automaticamente.");

        if (!TryResolveWorkspaceRoot(fullPath, out var root) || !TestLoomStorageOwnership.IsManagedJsonPath(root, fullPath))
            return new RisultatoRiparazione(false,
                "Riparazione rifiutata: il file non è riconosciuto con certezza come JSON autorevole TestLoom.");

        var paths = new DataPaths(root);
        try { WorkspaceWritePolicy.EnsureCanWrite(paths, "Riparazione JSON TestLoom"); }
        catch (Exception ex) { return new RisultatoRiparazione(false, ex.Message); }

        byte[] originalBytes;
        string originale;
        try
        {
            originalBytes = JsonIo.ReadAllBytesShared(fullPath);
            originale = DecodeUtf8(originalBytes);
        }
        catch (Exception ex) { return new RisultatoRiparazione(false, ex.Message); }

        var corretto = originale.Trim().TrimStart('﻿');
        corretto = RimuoviVirgoleFinali(corretto);
        corretto = BilanciaParentesi(corretto);

        if (corretto == originale.Trim())
            return new RisultatoRiparazione(false, "Nessuna correzione automatica applicabile: il file richiede una modifica manuale.");

        JsonElement repaired;
        try
        {
            using var document = JsonDocument.Parse(corretto);
            repaired = document.RootElement.Clone();
        }
        catch (Exception ex)
        {
            return new RisultatoRiparazione(false, $"Riparazione automatica non riuscita: {ex.Message}");
        }

        try
        {
            // Il gate viene ripetuto immediatamente prima delle operazioni mutanti: tra lettura/parsing e qui
            // può essere comparso un marker di un altro PC.
            WorkspaceWritePolicy.EnsureCanWrite(paths, "Riparazione JSON TestLoom");

            var currentBytes = JsonIo.ReadAllBytesShared(fullPath);
            if (!currentBytes.AsSpan().SequenceEqual(originalBytes))
                return new RisultatoRiparazione(false,
                    "Riparazione annullata: il file è cambiato dopo la lettura iniziale. Ricarica i dati e riprova sulla versione corrente.");

            var backup = fullPath + $".corrotto-{DateTime.Now:yyyyMMdd-HHmmssfff}.bak";
            CopyDurable(fullPath, backup);

            // JsonIo rende durevole il temporaneo e pubblica il nuovo JSON con rename atomico. Serializzare il
            // JsonElement ricostruisce un JSON canonico equivalente senza propagare whitespace/BOM difettosi.
            JsonIo.WriteAtomic(fullPath, repaired);
            return new RisultatoRiparazione(true, backup);
        }
        catch (Exception ex)
        {
            return new RisultatoRiparazione(false, $"Riparazione non applicata: {ex.Message}");
        }
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
    }

    private static void CopyDurable(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.WriteThrough | FileOptions.SequentialScan);
        input.CopyTo(output, 64 * 1024);
        output.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Risale al root del workspace solo tramite segnali strutturali forti. Il parent immediato non è
    /// sufficiente: un testcase dentro testcases/&lt;componente&gt;/... verrebbe altrimenti scambiato per un workspace
    /// autonomo e il controllo dei marker concorrenti guarderebbe nella directory sbagliata.
    /// </summary>
    private static bool TryResolveWorkspaceRoot(string fullPath, out string root)
    {
        root = "";
        try
        {
            var parent = Directory.GetParent(fullPath);
            for (var dir = parent; dir != null; dir = dir.Parent)
            {
                var candidate = dir.FullName;
                var hasTechnicalRoot = Directory.Exists(Path.Combine(candidate, "_ditm"));
                var hasVersionManifest = File.Exists(Path.Combine(candidate, "version-manifest.json"));
                if (!hasTechnicalRoot && !hasVersionManifest) continue;
                if (!TestLoomStorageOwnership.IsManagedJsonPath(candidate, fullPath)) continue;
                root = candidate;
                return true;
            }

            // Fallback conservativo per workspace legacy che non hanno ancora _ditm/version-manifest.
            var relativeFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "testcases", "cestino", "nonconformita", "change-requests", "segnalazioni", "notifiche"
            };
            var cursor = parent;
            while (cursor?.Parent != null)
            {
                if (relativeFolders.Contains(cursor.Name) && TestLoomStorageOwnership.IsManagedJsonPath(cursor.Parent.FullName, fullPath))
                {
                    root = cursor.Parent.FullName;
                    return true;
                }
                cursor = cursor.Parent;
            }

            // I soli master/config riconosciuti possono stare direttamente nel root. I TestCase autorevoli
            // appartengono a testcases/... o cestino/...; un generico testcase_*.json nella root non è più un
            // layout supportato e viene volutamente rifiutato dalla stessa ownership usata da repository/backup.
            var name = Path.GetFileName(fullPath);
            if (KnownRootFiles.Contains(name) && parent != null && TestLoomStorageOwnership.IsManagedJsonPath(parent.FullName, fullPath))
            {
                root = parent.FullName;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static readonly HashSet<string> KnownRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "requisiti.json", "funzionalita.json", "componente.json", "gamma.json", "categorie.json", "veicoli.json",
        "strumenti.json", "operatori.json", "test-template.json", "campi-sistema-pianificazione.json",
        "campi-sistema-dettagli.json", "audit-risoluzioni-conflitto.json", "revisioni-unitlogger.json",
        "version-manifest.json", "ticketing-config.json"
    };

    private static string RimuoviVirgoleFinali(string json) =>
        Regex.Replace(json, @",(\s*[}\]])", "$1");

    private static string BilanciaParentesi(string json)
    {
        var graffe = 0;
        var quadre = 0;
        var inStringa = false;
        var escape = false;
        foreach (var c in json)
        {
            if (escape) { escape = false; continue; }
            if (inStringa)
            {
                if (c == '\\') escape = true;
                else if (c == '"') inStringa = false;
                continue;
            }
            switch (c)
            {
                case '"': inStringa = true; break;
                case '{': graffe++; break;
                case '}': graffe--; break;
                case '[': quadre++; break;
                case ']': quadre--; break;
            }
        }
        if (graffe <= 0 && quadre <= 0) return json;

        var sb = new StringBuilder(json);
        for (var i = 0; i < quadre; i++) sb.Append(']');
        for (var i = 0; i < graffe; i++) sb.Append('}');
        return sb.ToString();
    }
}
