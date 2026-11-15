namespace TestLoom.App.Services;

/// <summary>
/// Un altro PC o processo ha sostituito il file di destinazione tra l'inizio della scrittura e il rename finale.
/// Il file resta quello dell'altro operatore: la scrittura locale NON è stata applicata.
/// </summary>
public sealed class DestinazioneModificataDuranteScritturaException : IOException
{
    public string Percorso { get; }

    public DestinazioneModificataDuranteScritturaException(string percorso)
        : base($"Il file '{Path.GetFileName(percorso)}' è stato modificato da un altro operatore mentre veniva salvato. Le modifiche locali non sono state sovrascritte: ricarica e riprova.")
    {
        Percorso = percorso;
    }
}
