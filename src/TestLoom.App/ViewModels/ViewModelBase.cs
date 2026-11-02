using CommunityToolkit.Mvvm.ComponentModel;

namespace TestLoom.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
}

/// <summary>Pagina di primo livello che MainViewModel tiene viva per l'intera sessione invece di ricreare ad
/// ogni cambio scheda (Addendum: audit prestazioni 2026-08-25): Aggiorna() riesegue solo il lavoro che
/// dipende dai dati correnti (le stesse query già fatte dal costruttore), non la sottoscrizione di eventi o
/// la creazione delle collezioni — quella resta un lavoro "una volta sola" per istanza. Vedi
/// MainViewModel.OttieniPagina.</summary>
public interface IPaginaAggiornabile
{
    void Aggiorna();
}

/// <summary>
/// ObservableCollection che sostituisce migliaia di righe con una sola notifica Reset. Con archivi grandi,
/// Clear + Add ripetuto costringeva Avalonia a ricalcolare la tabella una volta per ogni TestCase.
/// </summary>
public sealed class BulkObservableCollection<T> : System.Collections.ObjectModel.ObservableCollection<T>
{
    public void ReplaceWith(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
