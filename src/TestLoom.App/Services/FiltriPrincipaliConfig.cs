namespace TestLoom.App.Services;

/// <summary>Elenco UNICO e facile da cambiare dei filtri "principali" (sempre visibili) per pagina; tutti gli altri
/// filtri della pagina vanno nel pannello collassato "Altri filtri (N attivi)". Decisione utente 6.5.0: il
/// pannello e sempre collassato all'apertura, i VALORI dei filtri sono mantenuti per PC (ViewStateService,
/// config locale, mai nel file condiviso). Per spostare un filtro tra principali e secondari basta
/// aggiungerlo/toglierlo qui (nessun altro cambio).</summary>
public static class FiltriPrincipaliConfig
{
    public const string PaginaAudit = "Audit";
    public const string PaginaAuditDtc = "AuditDtc";

    public const string TipoRequisito = "TipoRequisito";
    public const string Testo = "Testo";
    public const string SoloNonCompleti = "SoloNonCompleti";
    public const string Requisito = "Requisito";
    public const string Componente = "Componente";
    public const string StatoDtc = "StatoDtc";

    private static readonly Dictionary<string, HashSet<string>> Principali = new(StringComparer.Ordinal)
    {
        [PaginaAudit] = new(StringComparer.Ordinal) { TipoRequisito },
        [PaginaAuditDtc] = new(StringComparer.Ordinal) { Testo, SoloNonCompleti },
    };

    public static bool EPrincipale(string pagina, string filtro) =>
        Principali.TryGetValue(pagina, out var s) && s.Contains(filtro);
}
