using System.Text.Json;
using System.Text.Json.Nodes;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Merge dei TestCase. Il three-way ordinario usa lo snapshot all'apertura come base. Per un fork OneDrive
/// rilevato in ritardo la base completa può non essere più disponibile: in quel caso PreparaFork marca
/// QUALUNQUE campo differente come conflitto da decidere esplicitamente, invece di assorbire silenziosamente
/// dati da uno dei rami.
/// </summary>
public static class TestCaseThreeWayMerge
{
    private static readonly HashSet<string> CampiTecnici = new(StringComparer.Ordinal)
    {
        nameof(TestCase.SchemaVersion), nameof(TestCase.Uid), nameof(TestCase.RevisioneDati),
        nameof(TestCase.UltimaModifica), nameof(TestCase.ModificatoDa), nameof(TestCase.Changelog),
        TestCaseFingerprint.StampKey, TestCaseForkResolution.StampKey
    };

    public static RisultatoMergeTestCase Prepara(TestCase baseVersion, TestCase miaVersione, TestCase loroVersione)
    {
        var baseJson = JsonSerializer.SerializeToNode(baseVersion, JsonIo.Options)!.AsObject();
        var mioJson = JsonSerializer.SerializeToNode(miaVersione, JsonIo.Options)!.AsObject();
        var loroJson = JsonSerializer.SerializeToNode(loroVersione, JsonIo.Options)!.AsObject();
        var campi = new List<CampoMergeTestCase>();

        foreach (var nome in baseJson.Select(x => x.Key).Union(mioJson.Select(x => x.Key)).Union(loroJson.Select(x => x.Key))
                     .Where(x => !CampiTecnici.Contains(x)).OrderBy(x => x, StringComparer.Ordinal))
        {
            baseJson.TryGetPropertyValue(nome, out var baseValore);
            mioJson.TryGetPropertyValue(nome, out var mioValore);
            loroJson.TryGetPropertyValue(nome, out var loroValore);
            var miaModifica = !JsonNode.DeepEquals(baseValore, mioValore);
            var loroModifica = !JsonNode.DeepEquals(baseValore, loroValore);
            var conflitto = miaModifica && loroModifica && !JsonNode.DeepEquals(mioValore, loroValore);
            SceltaMerge? sceltaAutomatica = conflitto ? null : miaModifica ? SceltaMerge.Mia : SceltaMerge.Loro;
            campi.Add(new CampoMergeTestCase(nome, baseValore?.DeepClone(), mioValore?.DeepClone(), loroValore?.DeepClone(), conflitto, sceltaAutomatica));
        }

        return new RisultatoMergeTestCase(loroVersione, campi, miaVersione.Changelog);
    }

    /// <summary>
    /// Merge conservativo di un fork tardivo. Senza una base comune affidabile NON deduciamo quale ramo abbia
    /// modificato cosa: ogni differenza reale richiede una decisione del Master. I campi identici sono invece
    /// automatici e non affollano il dialogo.
    /// </summary>
    public static RisultatoMergeTestCase PreparaFork(TestCase versioneCorrente, TestCase ramoRecovery)
    {
        var mioJson = JsonSerializer.SerializeToNode(versioneCorrente, JsonIo.Options)!.AsObject();
        var loroJson = JsonSerializer.SerializeToNode(ramoRecovery, JsonIo.Options)!.AsObject();
        var campi = new List<CampoMergeTestCase>();

        foreach (var nome in mioJson.Select(x => x.Key).Union(loroJson.Select(x => x.Key))
                     .Where(x => !CampiTecnici.Contains(x)).OrderBy(x => x, StringComparer.Ordinal))
        {
            mioJson.TryGetPropertyValue(nome, out var mioValore);
            loroJson.TryGetPropertyValue(nome, out var loroValore);
            var differente = !JsonNode.DeepEquals(mioValore, loroValore);
            campi.Add(new CampoMergeTestCase(
                nome,
                null,
                mioValore?.DeepClone(),
                loroValore?.DeepClone(),
                differente,
                differente ? null : SceltaMerge.Mia));
        }

        return new RisultatoMergeTestCase(ramoRecovery, campi, versioneCorrente.Changelog);
    }
}

public enum SceltaMerge { Mia, Loro }

public sealed record CampoMergeTestCase(string Nome, JsonNode? Base, JsonNode? Mia, JsonNode? Loro, bool InConflitto, SceltaMerge? SceltaAutomatica)
{
    public string BaseTesto => Testo(Base);
    public string MioTesto => Testo(Mia);
    public string LoroTesto => Testo(Loro);
    private static string Testo(JsonNode? valore)
    {
        var testo = valore?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "(vuoto)";
        return testo.Length <= 900 ? testo : testo[..900] + "\n…";
    }
}

public sealed class RisultatoMergeTestCase
{
    private readonly TestCase _loroVersione;
    private readonly IReadOnlyList<ChangelogEntry> _changelogMio;
    public IReadOnlyList<CampoMergeTestCase> Campi { get; }
    public IReadOnlyList<CampoMergeTestCase> Conflitti => Campi.Where(c => c.InConflitto).ToList();

    internal RisultatoMergeTestCase(TestCase loroVersione, IReadOnlyList<CampoMergeTestCase> campi, IReadOnlyList<ChangelogEntry> changelogMio)
    {
        _loroVersione = loroVersione;
        Campi = campi;
        _changelogMio = changelogMio;
    }

    public TestCase CreaVersioneRisolta(IReadOnlyDictionary<string, SceltaMerge> scelteMaster, string operatore)
    {
        if (Conflitti.Any(c => !scelteMaster.ContainsKey(c.Nome)))
            throw new InvalidOperationException("Ogni campo in conflitto deve essere deciso dal Master.");

        var json = JsonSerializer.SerializeToNode(_loroVersione, JsonIo.Options)!.AsObject();
        foreach (var campo in Campi)
        {
            var scelta = campo.InConflitto ? scelteMaster[campo.Nome] : campo.SceltaAutomatica!.Value;
            json[campo.Nome] = (scelta == SceltaMerge.Mia ? campo.Mia : campo.Loro)?.DeepClone();
        }

        var risolto = json.Deserialize<TestCase>(JsonIo.Options)!;
        // Il Changelog non è un campo mergeabile (è in CampiTecnici): la versione risolta deve conservare lo storico di
        // ENTRAMBI i rami, altrimenti le voci del ramo "mio" (es. modifiche locali non ancora pubblicate) andrebbero perse.
        risolto.Changelog = UnisciChangelog(risolto.Changelog, _changelogMio);
        foreach (var campo in Campi.Where(c => c.InConflitto || !JsonNode.DeepEquals(c.Base, c.Mia) || !JsonNode.DeepEquals(c.Base, c.Loro)))
        {
            var scelta = campo.InConflitto ? scelteMaster[campo.Nome] : campo.SceltaAutomatica!.Value;
            risolto.Changelog.Add(new ChangelogEntry
            {
                Data = DateTime.Now,
                Operatore = operatore,
                Campo = $"merge {(campo.InConflitto ? "Master" : "automatico")}: {campo.Nome}",
                ValorePrecedente = campo.LoroTesto,
                ValoreNuovo = scelta == SceltaMerge.Mia ? campo.MioTesto : campo.LoroTesto
            });
        }
        return risolto;
    }

    /// <summary>Storico unico dei due rami: le voci comuni (la base condivisa) compaiono una sola volta, nessuna voce
    /// di un ramo viene scartata, l'ordine è cronologico.</summary>
    private static List<ChangelogEntry> UnisciChangelog(IEnumerable<ChangelogEntry>? primo, IEnumerable<ChangelogEntry>? secondo)
    {
        var viste = new HashSet<string>(StringComparer.Ordinal);
        var unione = new List<ChangelogEntry>();
        foreach (var voce in (primo ?? Enumerable.Empty<ChangelogEntry>()).Concat(secondo ?? Enumerable.Empty<ChangelogEntry>()))
        {
            if (viste.Add(ChiaveChangelog(voce))) unione.Add(voce);
        }
        return unione.OrderBy(v => v.Data).ToList();
    }

    private static string ChiaveChangelog(ChangelogEntry voce) =>
        string.Join('\u001f', voce.Data.Ticks, voce.Operatore, voce.Campo, voce.ValorePrecedente ?? "", voce.ValoreNuovo ?? "");
}
