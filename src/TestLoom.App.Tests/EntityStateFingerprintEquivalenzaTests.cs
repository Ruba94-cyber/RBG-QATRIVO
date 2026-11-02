using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Equivalenza dell'hash di contenuto: <see cref="EntityStateFingerprint.Compute{T}"/> e <see cref="EntityStateFingerprint.ComputeDaJson{T}"/>
/// devono dare lo stesso risultato del percorso storico (nodo JSON senza le proprieta' di transazione). Il baseline anti-overwrite
/// e il content_hash dell'indice dipendono da questo valore: una differenza romperebbe i confronti tra salvataggi e letture.
/// </summary>
public sealed class EntityStateFingerprintEquivalenzaTests
{
    [Fact]
    public void Compute_UgualeAlPercorsoStoricoSuCRVariate()
    {
        foreach (var cr in CrVariate(400))
        {
            Assert.Equal(HashStorico(cr), EntityStateFingerprint.Compute(cr));
        }
    }

    [Fact]
    public void ComputeDaJson_UgualeAlPercorsoStoricoESullaSerializzazioneCompatta()
    {
        foreach (var cr in CrVariate(400))
        {
            var json = JsonSerializer.Serialize(cr, JsonIo.CompactOptions);
            Assert.Equal(HashStorico(cr), EntityStateFingerprint.ComputeDaJson(cr, json));
        }
    }

    [Fact]
    public void ComputeDaJson_ConProprietaDiTransazione_RicadeSulPercorsoStorico()
    {
        // Un JSON scritto dal livello di transazione porta le proprieta' di transazione: il risultato resta quello storico.
        var cr = CrVariate(1).Single();
        var nodo = (JsonObject)JsonSerializer.SerializeToNode(cr, JsonIo.CompactOptions)!;
        nodo[WorkflowTransactionVisibilityService.TransactionProperty] = "0123456789abcdef";
        nodo[WorkflowTransactionVisibilityService.TransactionDeviceProperty] = "PC-TEST";
        var conTransazione = nodo.ToJsonString(JsonIo.CompactOptions);
        Assert.Equal(HashStorico(cr), EntityStateFingerprint.ComputeDaJson(cr, conTransazione));
    }

    /// <summary>Copia letterale del calcolo storico: nodo, rimozione delle proprieta' di transazione, hash.</summary>
    private static string HashStorico<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, JsonIo.CompactOptions) as JsonObject;
        if (node != null)
        {
            node.Remove(WorkflowTransactionVisibilityService.TransactionProperty);
            node.Remove(WorkflowTransactionVisibilityService.TransactionDeviceProperty);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(node, JsonIo.CompactOptions);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        var fallback = JsonSerializer.SerializeToUtf8Bytes(value, JsonIo.CompactOptions);
        return Convert.ToHexString(SHA256.HashData(fallback)).ToLowerInvariant();
    }

    /// <summary>Campi variati: accenti, a capo, liste vuote e piene, date, valori nulli, caratteri che il JSON escapa.</summary>
    private static IEnumerable<ChangeRequest> CrVariate(int quante)
    {
        string[] titoli = { "Freno", "Attuatore àèìòù", "Ruota \"ß\" test", "Titolo\ncon a capo", "Ispezione \\ barra", "Ξ unicode 🙂" };
        for (var n = 1; n <= quante; n++)
        {
            yield return new ChangeRequest
            {
                Id = $"CR-2026-{n:D4}",
                Titolo = titoli[n % titoli.Length] + n,
                Descrizione = n % 4 == 0 ? string.Empty : $"riga {n}\nseconda <riga> & \"quoted\" {n * 7}",
                Responsabile = n % 3 == 0 ? string.Empty : "Mario Rossi",
                Stato = (StatoChangeRequest)(n % 6),
                Archiviata = n % 9 == 0,
                CreatoDa = "Operatore Prova A",
                DataCreazione = new DateTime(2026, 1, 1).AddDays(n % 300),
                UltimaModifica = new DateTime(2026, 2, 1).AddMinutes(n * 13 % 5000),
                VeicoliImpattati = Enumerable.Range(0, n % 4).Select(i => $"Veicolo {i} {n}").ToList(),
                EcuImpattate = Enumerable.Range(0, (n + 1) % 3).Select(i => $"Componente-{(n + i) % 40}").ToList(),
                TicketCollegati = n % 5 == 0 ? new List<TicketRif> { new() { Riferimento = $"TK-{n}" } } : new(),
            };
        }
    }
}
