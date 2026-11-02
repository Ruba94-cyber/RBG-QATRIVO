using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

public partial class ChangeRequestEditorViewModel
{
    /// <summary>
    /// Unico percorso di uscita volontaria dall'editor CR. Prima il pulsante "Torna alla lista" chiamava
    /// direttamente _torna() e poteva perdere modifiche/allegati senza conferma. La conferma e' scoped a
    /// questo ViewModel: se l'utente conferma, IConfirmedAbandonmentParticipant riconcilia gli allegati di
    /// staging contro la CR autorevole corrente; se annulla, la pagina resta intatta.
    /// </summary>
    [RelayCommand]
    private async Task TornaAllaListaSicuro()
    {
        if (!ModificheNonSalvate)
        {
            _torna();
            return;
        }
        if (!await ConfirmService.ConfermaAbbandonoModifiche(this)) return;
        // Abbandono gia' confermato: l'uscita non deve riconfermarlo. Il flag vale solo durante il callback sincrono di uscita.
        AbbandonoConfermato = true;
        try { _torna(); }
        finally { AbbandonoConfermato = false; }
    }

    /// <summary>Vero solo durante l'uscita dopo la conferma di abbandono di TornaAllaListaSicuro. La pagina resta sporca fino
    /// all'uscita, quindi MainViewModel.TornaDallaChangeRequest la riconfermerebbe: senza questo flag l'utente vedeva due volte
    /// il dialogo "Modifiche non salvate" per una sola uscita.</summary>
    internal bool AbbandonoConfermato { get; private set; }

    /// <summary>
    /// Esc usa la stessa identica semantica del pulsante, ora che l'editor possiede una conferma scoped e
    /// una cleanup fail-safe degli allegati. Il metodo ritorna true appena l'azione e' stata presa in carico:
    /// l'esito del dialogo e' asincrono e non deve lasciare propagare Esc al controllo sottostante.
    /// </summary>
    bool IScorciatoiePagina.AnnullaDaTastiera()
    {
        _ = TornaAllaListaSicuro();
        return true;
    }
}
