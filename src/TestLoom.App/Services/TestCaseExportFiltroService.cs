using TestLoom.App.Models;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Services;

/// <summary>
/// Snapshot IMMUTABILE dei filtri confermati dal dialogo Test. I valori vengono
/// catturati una sola volta sul thread UI e applicati in background a tutti i
/// Test autorizzati, non soltanto alla pagina corrente.
/// I criteri sono coerenti con il fallback in memoria della Lista Test.
/// </summary>
public sealed class TestCaseExportFiltro
{
    public string Ricerca { get; init; } = "";
    public DateTime? Dal { get; init; }
    public DateTime? Al { get; init; }
    public bool SoloSenzaEcu { get; init; }
    public bool SoloImportatoUdt { get; init; }
    public FiltroPolarioneOpzione Ticketing { get; init; }
    public IReadOnlySet<Guid> UidTestConNcPolarion { get; init; } = new HashSet<Guid>();
    public IReadOnlySet<Guid> UidTestConNcStato { get; init; } = new HashSet<Guid>();
    public IReadOnlyDictionary<string, IReadOnlySet<string>> Multi { get; init; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>>? CampiCustom { get; init; }

    public IEnumerable<TestCase> Applica(IEnumerable<TestCase> sorgente, CancellationToken ct = default)
    {
        foreach (var tc in sorgente.ConAnnullamento(ct))
            if (Corrisponde(tc)) yield return tc;
    }

    public bool Corrisponde(TestCase tc)
    {
        if (!string.IsNullOrWhiteSpace(Ricerca))
        {
            var q = Ricerca.Trim();
            if (!(tc.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                || tc.DescrizioneBreve.Contains(q, StringComparison.OrdinalIgnoreCase)
                || tc.Descrizione.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (tc.Assegnatario?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || tc.CreatoDa.Contains(q, StringComparison.OrdinalIgnoreCase)
                || tc.RequisitoId.Any(r => r.Contains(q, StringComparison.OrdinalIgnoreCase))
                || tc.Funzionalita.Any(f => f.Contains(q, StringComparison.OrdinalIgnoreCase))))
                return false;
        }

        if (!MatchAny("Componente", tc.Componente.ToList())) return false;
        if (!MatchAny("Veicolo", tc.VeicoliEffettivi)) return false;
        if (!MatchAny("TemplateUid", [tc.TestTemplateUid.ToString()])) return false;
        if (!MatchAny("Esito", tc.StatoCorrente?.ToString())) return false;
        if (!MatchAny("Assegnatario", tc.Assegnatario)) return false;
        if (!MatchAny("CreatoDa", tc.CreatoDa)) return false;
        if (!MatchAny("StatoLavoro", tc.StatoLavoro.ToString())) return false;
        if (!MatchAny("Requisito", tc.RequisitoId)) return false;
        if (!MatchAny("Funzionalita", tc.Funzionalita)) return false;
        if (!MatchAny("Severita", tc.Severita?.ToString())) return false;
        if (!MatchAny("Priorita", tc.Priorita.ToString())) return false;
        if (Multi.TryGetValue("StatoAnomalia", out var stati) && stati.Count > 0
            && !UidTestConNcStato.Contains(tc.Uid)) return false;
        if (Ticketing != FiltroPolarioneOpzione.Tutti)
        {
            var ha = tc.TicketCollegati.Count > 0 || UidTestConNcPolarion.Contains(tc.Uid);
            if (ha != (Ticketing == FiltroPolarioneOpzione.ConPolarion)) return false;
        }
        if (CampiCustom != null
            && !CampoSistemaCatalogoService.CorrispondeCampiCustom(CampiCustom, tc.CampiCustom))
            return false;
        if (SoloSenzaEcu && tc.Componente.Count != 0) return false;
        if (SoloImportatoUdt && tc.Origine != OrigineTest.ImportStatoUdt) return false;
        if (Dal.HasValue && tc.DataCreazione.Date < Dal.Value.Date) return false;
        if (Al.HasValue && tc.DataCreazione.Date > Al.Value.Date) return false;
        return true;
    }

    private bool MatchAny(string key, string? valore)
    {
        if (!Multi.TryGetValue(key, out var valori) || valori.Count == 0) return true;
        return valore != null && valori.Contains(valore);
    }

    private bool MatchAny(string key, IEnumerable<string> valoriTest)
    {
        if (!Multi.TryGetValue(key, out var valori) || valori.Count == 0) return true;
        return valoriTest.Any(valori.Contains);
    }
}
