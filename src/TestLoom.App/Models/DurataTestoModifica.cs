using System.ComponentModel;
using System.Globalization;

namespace TestLoom.App.Models;

/// <summary>Testo modificabile della durata di un'operazione (TIME, 6.6.1). Un testo valido (intero 0..limite, o vuoto = non nota)
/// aggiorna il modello; un testo non valido resta nel campo, segnalato con <see cref="HaErrore"/>, e NON cambia il modello.
/// Il salvataggio dell'editor blocca finche' il testo non e' valido (vedi TestCaseEditorViewModel). Il messaggio e' localizzato dalla vista.</summary>
public sealed class DurataTestoModifica : INotifyPropertyChanged, IDisposable
{
    private readonly Condizione _condizione;
    private string _testo;
    private bool _haErrore;
    private bool _applicando;
    private bool _scollegato;

    internal DurataTestoModifica(Condizione condizione)
    {
        _condizione = condizione;
        _testo = Formatta(condizione.DurataMinuti);
        condizione.PropertyChanged += OnModelloCambiato;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stacca la sottoscrizione al modello (operazione rimossa dalla revisione). Idempotente.</summary>
    public void Dispose()
    {
        if (_scollegato) return;
        _scollegato = true;
        _condizione.PropertyChanged -= OnModelloCambiato;
    }

    /// <summary>Testo mostrato nel campo (modificabile dall'operatore).</summary>
    public string Testo
    {
        get => _testo;
        set
        {
            _testo = value ?? "";
            _condizione.SegnaDurataModificataDallOperatore();   // anche un testo non valido o vuoto e' un intervento dell'operatore
            if (DurataOperazione.TryParse(_testo, out var minuti))
            {
                _applicando = true;
                try
                {
                    _condizione.DurataMinuti = minuti;
                }
                finally
                {
                    _applicando = false;
                }
                ImpostaErrore(false);
            }
            else
            {
                // Testo non valido: il modello resta com'e'. Il salvataggio viene bloccato dall'editor.
                ImpostaErrore(true);
            }
            OnPropertyChanged(nameof(Testo));
        }
    }

    /// <summary>Vero quando il testo digitato non e' una durata valida (negativo, decimale, testo, oltre il limite).</summary>
    public bool HaErrore => _haErrore;

    private void OnModelloCambiato(object? sender, PropertyChangedEventArgs e)
    {
        if (_applicando || e.PropertyName != nameof(Condizione.DurataMinuti)) return;
        _testo = Formatta(_condizione.DurataMinuti);
        ImpostaErrore(false);
        OnPropertyChanged(nameof(Testo));
    }

    private void ImpostaErrore(bool valore)
    {
        if (_haErrore == valore) return;
        _haErrore = valore;
        OnPropertyChanged(nameof(HaErrore));
    }

    private static string Formatta(int? minuti) => minuti is { } valore ? valore.ToString(CultureInfo.InvariantCulture) : "";

    private void OnPropertyChanged(string nome) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nome));
}
