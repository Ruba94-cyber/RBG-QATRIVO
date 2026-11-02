using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Esito di un rinnovo del lock di una sessione di modifica, interpretato dall'editor (P2.1).</summary>
internal enum EsitoRinnovoLock
{
    /// <summary>Lock rinnovato o riacquisito su disco: nulla da segnalare.</summary>
    Confermato,
    /// <summary>Rinnovo non riuscito senza un altro detentore: il lock può scadere se il problema persiste.</summary>
    NonConfermato,
    /// <summary>Un'altra sessione ha un lock vivo: il lock di questa modifica non è più nostro.</summary>
    NonPosseduto
}

/// <summary>
/// Conta i rinnovi del lock non confermati di seguito. Un rinnovo può fallire per I/O transitorio (file trattenuto dal
/// client di sincronizzazione) senza che il lock sia perso: finché il conto resta sotto la soglia è un fatto da
/// registrare. Dalla soglia in poi il lock può scadere e un altro PC entrare in modifica: l'editor avvisa l'utente di
/// salvare o chiudere. Non è thread-safe: va usato solo sul thread UI.
/// </summary>
internal sealed class MonitorRinnovoLock
{
    /// <summary>Rinnovi non confermati consecutivi dopo cui l'avviso diventa persistente (heartbeat ogni 2 minuti: circa 6 minuti).</summary>
    public const int SogliaAvviso = 3;

    public int NonConfermatiConsecutivi { get; private set; }

    /// <summary>Vero dalla soglia in poi, finché un rinnovo non torna confermato o il lock cambia detentore.</summary>
    public bool AvvisoAttivo => NonConfermatiConsecutivi >= SogliaAvviso;

    /// <summary>Registra l'esito di un rinnovo e restituisce la sua interpretazione.</summary>
    public EsitoRinnovoLock Registra(LockEnsureResult esito)
    {
        if (esito.Holder != null)
        {
            NonConfermatiConsecutivi = 0;
            return EsitoRinnovoLock.NonPosseduto;
        }
        if (esito.Posseduto && esito.Confermato)
        {
            NonConfermatiConsecutivi = 0;
            return EsitoRinnovoLock.Confermato;
        }
        NonConfermatiConsecutivi++;
        return EsitoRinnovoLock.NonConfermato;
    }

    /// <summary>Azzera il conto all'inizio e alla fine di una sessione di modifica.</summary>
    public void Azzera() => NonConfermatiConsecutivi = 0;
}
