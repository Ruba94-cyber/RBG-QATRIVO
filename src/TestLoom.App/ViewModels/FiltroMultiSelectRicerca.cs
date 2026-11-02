using System;
using System.Collections.Generic;
using System.Linq;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Logica di ricerca testuale su un <see cref="FiltroMultiSelect"/>, estratta da
/// FiltroMultiSelectFlyoutHelper (Addendum performance 2026-09-25: virtualizzazione filtri) per essere testabile
/// senza UI. La ricerca lavora sempre su TUTTE le M opzioni del filtro (mai troncata, mai limitata a una
/// finestra), cosi' il controllo virtualizzato che la disegna puo' creare solo le decine di contenitori
/// effettivamente visibili senza perdere la possibilita' di trovare una qualunque delle M voci.
///
/// Le azioni "Seleziona tutto"/"Deseleziona tutto" agiscono sulle sole opzioni visibili con la ricerca corrente;
/// le selezioni nascoste restano intatte. Un'azione di massa non deve modificare valori che l'utente non vede.</summary>
public sealed class FiltroMultiSelectRicerca
{
    public FiltroMultiSelect Filtro { get; }

    private string _ricerca = "";
    public string Ricerca
    {
        get => _ricerca;
        set => _ricerca = value ?? "";
    }

    public FiltroMultiSelectRicerca(FiltroMultiSelect filtro) => Filtro = filtro;

    /// <summary>Tutte le opzioni che soddisfano la ricerca corrente (sottostringa, senza distinzione di maiuscole e
    /// di accenti, su Etichetta: stessa regola di <see cref="PickerTesto"/>), su TUTTE le opzioni del filtro.
    /// Ricerca vuota o solo spazi = tutte le opzioni, nello stesso ordine di Filtro.Opzioni.</summary>
    public IReadOnlyList<FiltroOpzione> VociVisibili()
    {
        if (string.IsNullOrWhiteSpace(_ricerca)) return Filtro.Opzioni.ToList();
        return new PickerMotore<FiltroOpzione>(Filtro.Opzioni, o => o.Etichetta ?? "").Cerca(_ricerca).Voci.ToList();
    }

    /// <summary>Testo del contatore mostrato sopra l'elenco: "M" se la ricerca e' vuota (o non nasconde
    /// nulla), "N di M" quando la ricerca restringe l'elenco a N delle M voci totali.</summary>
    public string TestoConteggio() => FormattaConteggio(VociVisibili().Count, Filtro.Opzioni.Count);

    /// <summary>Formattazione del contatore condivisa con il controllo asincrono del filtro.</summary>
    public static string FormattaConteggio(int visibili, int totale) =>
        visibili == totale
            ? totale.ToString()
            : string.Format(LocalizationService.Instance.Traduci("FiltroConteggioNDiMFmt"), visibili, totale);

    /// <summary>Seleziona le opzioni visibili, senza cambiare quelle nascoste dalla ricerca.</summary>
    public void SelezionaTutto()
        => SelezionaTutto(VociVisibili());

    /// <summary>Seleziona il risultato completo corrente già calcolato dal motore async, senza scandire il catalogo sul thread UI.</summary>
    public void SelezionaTutto(IEnumerable<FiltroOpzione> vociVisibili)
    {
        ArgumentNullException.ThrowIfNull(vociVisibili);
        foreach (var opzione in vociVisibili) opzione.Selezionato = true;
    }

    /// <summary>Deseleziona le opzioni visibili, senza cambiare quelle nascoste dalla ricerca.</summary>
    public void DeselezionaTutto()
        => DeselezionaTutto(VociVisibili());

    /// <summary>Deseleziona il risultato completo corrente già calcolato dal motore async, senza scandire il catalogo sul thread UI.</summary>
    public void DeselezionaTutto(IEnumerable<FiltroOpzione> vociVisibili)
    {
        ArgumentNullException.ThrowIfNull(vociVisibili);
        foreach (var opzione in vociVisibili) opzione.Selezionato = false;
    }
}
