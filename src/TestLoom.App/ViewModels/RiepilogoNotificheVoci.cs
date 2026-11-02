using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Una voce del menu "Riepilogo notifiche" in Impostazioni (Disattivato, Giornaliero, All'avvio).</summary>
public sealed record OpzioneModoRiepilogo(NotificaRiepilogoService.ModoRiepilogo Modo, string Etichetta);

/// <summary>
/// Un tipo di notifica nelle Impostazioni del riepilogo: checkbox "Include nel riepilogo". I tipi che non entrano mai nel
/// riepilogo (conflitto di sincronizzazione, Pacchetto da decidere) restano visibili ma disabilitati.
/// Scrive nella configurazione in memoria: il salvataggio è quello di "Salva preferenze".
/// </summary>
public sealed class VoceRiepilogoTipo : ViewModelBase
{
    private readonly Action<string, bool> _onInclusoCambiato;
    private bool _incluso;

    public VoceRiepilogoTipo(string tipo, string etichetta, bool incluso, bool modificabile, Action<string, bool> onInclusoCambiato)
    {
        Tipo = tipo;
        Etichetta = etichetta;
        Modificabile = modificabile;
        _incluso = incluso;
        _onInclusoCambiato = onInclusoCambiato;
    }

    public string Tipo { get; }
    public string Etichetta { get; }

    /// <summary>False per i tipi sempre immediati: il checkbox è disabilitato e il tipo non entra mai nel riepilogo.</summary>
    public bool Modificabile { get; }

    /// <summary>Identificativo per automazione UI e test: Preferenze_Riepilogo_Tipo_&lt;Tipo&gt;_CheckBox.</summary>
    public string AutomationId => $"Preferenze_Riepilogo_Tipo_{Tipo}_CheckBox";

    /// <summary>Checked = il tipo entra nel riepilogo (purché non sia silenziato nel contatore).</summary>
    public bool IncludiNelRiepilogo
    {
        get => _incluso;
        set
        {
            if (!Modificabile || _incluso == value) return;
            _incluso = value;
            _onInclusoCambiato(Tipo, value);
            OnPropertyChanged();
        }
    }
}
