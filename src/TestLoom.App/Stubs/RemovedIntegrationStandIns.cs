// Neutral stand-ins for the few types of removed, tool-specific integrations that shared code still names.
// They only keep the shared code compiling; the removed features themselves are not part of this edition.
using System;
using System.Collections.Generic;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

public enum ModoLetturaUdt { PerChiave, ChiaveReale, WorkingUnitCombinata, RevisioneWorkingUnit }

public sealed record LetturaUdt(ModoLetturaUdt Modo, string? ChiaveReale = null)
{
    public static readonly LetturaUdt Invariata = new(ModoLetturaUdt.PerChiave);
}

public static class RisolutoreCampiUdt
{
    public const string NomeStrumento = "UnitLogger";

    public static LetturaUdt LetturaColonna(string chiaveColonna, IEnumerable<DefinizioneCampoStrumento>? campi) => LetturaUdt.Invariata;
}

public static class RevisioneUdtService
{
    // Keys of the instrument fields the scale-test data generators still write; the values are the generic spelling.
    public const string ChiaveCampoNome = "UnitLogger::Pacchetto";
    public const string ChiaveCampoVersione = "UnitLogger::Versione Pacchetto";

    public static string ValoreCombinato(IReadOnlyDictionary<string, string> valori) => "";

    public static string VersioneDelValoreCombinato(string valore) => "";

    public static IReadOnlyList<(string WorkingUnitRev, string Firma)> WuDaDecidere(IEnumerable<RevisioneUdt> revisioni, IEnumerable<TestCase> test) =>
        Array.Empty<(string WorkingUnitRev, string Firma)>();

    public static void Sincronizza(MasterDataRepository<RevisioneUdt> repo, TestCase testCase, string operatore) { }
}

public static class UdtLogImportService
{
    // The log import itself is not part of this edition; these two helpers are plain text utilities that shared code uses.
    public static string NormalizzaPerConfronto(string s)
    {
        // Same result as s.ToUpperInvariant().Where(char.IsLetterOrDigit), without LINQ (called for every entry).
        var upper = s.ToUpperInvariant();
        var result = new System.Text.StringBuilder(upper.Length);
        foreach (var c in upper)
            if (char.IsLetterOrDigit(c)) result.Append(c);
        return result.ToString();
    }

    public static List<TCoppia> CoppieDoppioni<TCoppia>(IEnumerable<string> valori, Func<string, string> chiave,
        Func<string, string, TCoppia> crea)
    {
        var gruppi = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var ordineGruppi = new List<string>();
        foreach (var valore in valori)
        {
            var k = chiave(valore);
            if (!gruppi.TryGetValue(k, out var voci))
            {
                voci = new List<string>(1);
                gruppi.Add(k, voci);
                ordineGruppi.Add(k);
            }
            voci.Add(valore);
        }
        var coppie = new List<TCoppia>();
        foreach (var k in ordineGruppi)
        {
            var voci = gruppi[k];
            for (var i = 0; i < voci.Count; i++)
                for (var j = i + 1; j < voci.Count; j++)
                    if (!voci[i].Equals(voci[j], StringComparison.Ordinal)) coppie.Add(crea(voci[i], voci[j]));
        }
        return coppie;
    }

    public static string? TrovaOpzioneSimile(string testo, IEnumerable<string> opzioni)
    {
        var normTesto = NormalizzaPerConfronto(testo);
        if (normTesto.Length == 0) return null;
        foreach (var opzione in opzioni)
        {
            var normOpzione = NormalizzaPerConfronto(opzione);
            if (normOpzione.Length == 0) continue;
            if (normOpzione.Contains(normTesto) || normTesto.Contains(normOpzione)) return opzione;
        }
        return null;
    }
}
