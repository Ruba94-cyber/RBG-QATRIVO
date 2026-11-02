namespace TestLoom.App.Services;

/// <summary>
/// L'indice SQLite locale non puo' essere usato in questa sessione: il file e' bloccato (altro processo, sola lettura,
/// ACL) e nemmeno la quarantena riesce a isolarlo. Non e' corruzione: il database resta dov'e' e la fonte autorevole
/// (i JSON) continua a essere valida. Deriva da IOException perche' le classificazioni esistenti la trattano come
/// guasto di I/O: chi legge l'indice degrada sul percorso lento, chi scrive segnala il read model obsoleto.
/// </summary>
public sealed class LocalIndexUnavailableException : IOException
{
    public LocalIndexUnavailableException(string motivo)
        : base($"Indice locale non disponibile in questa sessione: {motivo}") { }
}
