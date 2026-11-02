namespace TestLoom.App.ViewModels;

/// <summary>Riga "Mostra nel contatore" in Impostazioni: un tipo di notifica con il suo checkbox. La scelta viene scritta
/// nella configurazione in memoria tramite il callback; il salvataggio resta quello di "Salva preferenze".</summary>
public sealed class VoceNotificaContatore : ViewModelBase
{
    private readonly Action<string, bool> _onMostraCambiato;
    private bool _mostraNelContatore;

    public VoceNotificaContatore(string tipo, string etichetta, bool mostra, bool modificabile, Action<string, bool> onMostraCambiato)
    {
        Tipo = tipo;
        Etichetta = etichetta;
        Modificabile = modificabile;
        _mostraNelContatore = mostra;
        _onMostraCambiato = onMostraCambiato;
    }

    public string Tipo { get; }
    public string Etichetta { get; }

    /// <summary>False per il conflitto di sincronizzazione dati: il checkbox è disabilitato e il tipo resta sempre conteggiato.</summary>
    public bool Modificabile { get; }

    /// <summary>Identificativo per automazione UI e test: Preferenze_NotificaTipo_&lt;Tipo&gt;_CheckBox.</summary>
    public string AutomationId => $"Preferenze_NotificaTipo_{Tipo}_CheckBox";

    /// <summary>Checked = il tipo conta nel badge e nell'avviso Windows (non silenziato).</summary>
    public bool MostraNelContatore
    {
        get => _mostraNelContatore;
        set
        {
            if (!Modificabile || _mostraNelContatore == value) return;
            _mostraNelContatore = value;
            _onMostraCambiato(Tipo, value);
            OnPropertyChanged();
        }
    }
}
