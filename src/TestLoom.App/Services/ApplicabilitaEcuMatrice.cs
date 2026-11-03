using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Applicabilita' requisito x componente nella matrice del Report. Un requisito Extra con coppie veicolo-componente dichiarate
/// (Requisito.VeicoliEcuD4D) e' relativo SOLO a quelle componente: nelle altre la cella e' "N.A." (stesso concetto e stessa
/// resa delle celle N.A. marcate a mano) e un componente non applicabile a nessun requisito presente nell'altro asse esce
/// dalla matrice. Solo logica di calcolo/visualizzazione: nessun dato persistito, nessuna migrazione.
/// </summary>
internal sealed class ApplicabilitaEcuMatrice
{
    private readonly IReadOnlyDictionary<string, HashSet<string>> _ecuPerRequisito;
    private readonly bool _requisitoSuX;
    private readonly int _livelloRequisito;
    private readonly int _livelloEcu;
    private readonly Dictionary<string, string> _cacheX = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _cacheY = new(StringComparer.OrdinalIgnoreCase);

    private ApplicabilitaEcuMatrice(IReadOnlyDictionary<string, HashSet<string>> mappa, bool requisitoSuX, int livelloRequisito, int livelloEcu)
    {
        _ecuPerRequisito = mappa;
        _requisitoSuX = requisitoSuX;
        _livelloRequisito = livelloRequisito;
        _livelloEcu = livelloEcu;
    }

    /// <summary>Vero se i componenti sono sull'asse X (colonne), falso se sono sull'asse Y (righe).</summary>
    public bool EcuSuX => !_requisitoSuX;

    /// <summary>Null se non serve: nessun requisito ristretto, oppure Requisito ed componente non stanno su assi opposti.</summary>
    public static ApplicabilitaEcuMatrice? Crea(ConfigurazioneMatriceReport config)
    {
        if (config.EcuRistrettePerRequisito.Count == 0) return null;
        var rx = config.CampiX.IndexOf(DimensioniMatrice.Requisito);
        var ry = config.CampiY.IndexOf(DimensioniMatrice.Requisito);
        var ex = config.CampiX.IndexOf(DimensioniMatrice.Componente);
        var ey = config.CampiY.IndexOf(DimensioniMatrice.Componente);
        if (rx >= 0 && ey >= 0) return new ApplicabilitaEcuMatrice(config.EcuRistrettePerRequisito, true, rx, ey);
        if (ry >= 0 && ex >= 0) return new ApplicabilitaEcuMatrice(config.EcuRistrettePerRequisito, false, ry, ex);
        return null;
    }

    private static string Componente(Dictionary<string, string> cache, string chiave, int livello)
    {
        if (cache.TryGetValue(chiave, out var valore)) return valore;
        var parti = ReportMatriceService.SpezzaPercorso(chiave);
        valore = livello < parti.Length ? parti[livello] : "";
        cache[chiave] = valore;
        return valore;
    }

    /// <summary>Vero se la cella (colonna x, riga y) incrocia un requisito ristretto ad altre componente.</summary>
    public bool NonApplicabile(string x, string y)
    {
        var requisito = _requisitoSuX ? Componente(_cacheX, x, _livelloRequisito) : Componente(_cacheY, y, _livelloRequisito);
        if (!_ecuPerRequisito.TryGetValue(requisito, out var ecuAmmesse)) return false;
        var componente = _requisitoSuX ? Componente(_cacheY, y, _livelloEcu) : Componente(_cacheX, x, _livelloEcu);
        return !ecuAmmesse.Contains(componente);
    }

    /// <summary>Come <see cref="NonApplicabile(string,string)"/> per una chiave cella "colonna\u0001riga".</summary>
    public bool NonApplicabileChiave(string cellaChiave)
    {
        var separatore = cellaChiave.IndexOf('\u0001');
        return separatore >= 0 && NonApplicabile(cellaChiave[..separatore], cellaChiave[(separatore + 1)..]);
    }
}
