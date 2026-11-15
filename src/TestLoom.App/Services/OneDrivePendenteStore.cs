namespace TestLoom.App.Services;

/// <summary>
/// Persistenza locale dei file rimasti da caricare alla chiusura. Vive FUORI dalla cartella condivisa, accanto al marker di
/// sola lettura (%LOCALAPPDATA%\TestLoom\onedrive-pendente\&lt;chiave workspace&gt;.json), così non si propaga ad altri PC né
/// viene toccata dalla pulizia degli indici. Scrittura atomica; ogni errore è non fatale per il chiamante (il servizio li assorbe).
/// </summary>
public sealed class OneDrivePendenteStore : IOneDrivePendenteStore
{
    private readonly string? _baseDirectory;

    public OneDrivePendenteStore(string? baseDirectory = null) => _baseDirectory = baseDirectory;

    private string Percorso(string radice) =>
        Path.Combine(_baseDirectory ?? Path.Combine(WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", "onedrive-pendente"),
            WorkspaceLocalPaths.ComputeWorkspaceKey(radice) + ".json");

    public OneDrivePendente? Leggi(string radice)
    {
        var path = Percorso(radice);
        return File.Exists(path) ? JsonIo.TryRead<OneDrivePendente>(path, out _) : null;
    }

    public void Salva(string radice, OneDrivePendente pendente)
    {
        var path = Percorso(radice);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonIo.WriteAtomic(path, pendente);
    }

    public void Cancella(string radice)
    {
        var path = Percorso(radice);
        if (File.Exists(path)) File.Delete(path);
    }
}
