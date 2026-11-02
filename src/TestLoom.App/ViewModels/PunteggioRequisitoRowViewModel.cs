using TestLoom.App.Models;

namespace TestLoom.App.ViewModels;

/// <summary>Riga bindabile per un punteggio di conformità nella scheda Esecuzione — wrapper leggero attorno
/// a un PunteggioRequisito, che è un POCO senza notifiche proprie.</summary>
public class PunteggioRequisitoRowViewModel : ViewModelBase
{
    private readonly PunteggioRequisito _modello;
    private readonly Action _onCambiato;

    public PunteggioRequisitoRowViewModel(PunteggioRequisito modello, Action onCambiato)
    {
        _modello = modello;
        _onCambiato = onCambiato;
    }

    public string RequisitoId => _modello.RequisitoId;

    public PunteggioConformita Valore
    {
        get => _modello.Valore;
        set
        {
            if (_modello.Valore == value) return;
            _modello.Valore = value;
            OnPropertyChanged();
            _onCambiato();
        }
    }

    public bool Sovrascritto
    {
        get => _modello.Sovrascritto;
        set
        {
            if (_modello.Sovrascritto == value) return;
            _modello.Sovrascritto = value;
            if (!value) _modello.MotivazioneOverride = "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(MotivazioneOverride));
            _onCambiato();
        }
    }

    public string MotivazioneOverride
    {
        get => _modello.MotivazioneOverride;
        set
        {
            if (_modello.MotivazioneOverride == value) return;
            _modello.MotivazioneOverride = value;
            OnPropertyChanged();
            _onCambiato();
        }
    }
}
