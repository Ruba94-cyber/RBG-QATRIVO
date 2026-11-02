using System.Collections.ObjectModel;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Services;

/// <summary>Applica l'ordine di navigazione scelto dall'utente e salvato in AppConfig (Addendum: grill-me
/// 2026-08-18, follow-up — "tutti i pannelli sia nella barra Impostazioni che nella barra Home devono essere
/// riordinabili"). Pura: chi la chiama si occupa di leggere/scrivere la lista Id in AppConfig.</summary>
public static class NavOrderService
{
    public static List<NavItem> Ordina(IReadOnlyList<NavItem> items, IReadOnlyList<string> ordineSalvato)
    {
        var risultato = new List<NavItem>();
        foreach (var id in ordineSalvato)
        {
            var voce = items.FirstOrDefault(i => i.Id == id);
            if (voce != null && !risultato.Contains(voce)) risultato.Add(voce);
        }
        foreach (var voce in items)
            if (!risultato.Contains(voce)) risultato.Add(voce);
        return risultato;
    }

    /// <summary>Sposta l'elemento in posizione indice di delta posizioni (-1 = su, +1 = giù), senza effetto
    /// se già in cima/fondo. Separata dai comandi ImpostazioniViewModel così resta testabile senza toccare
    /// AppConfigService.Save (che scriverebbe nel config.json reale dello sviluppatore).</summary>
    public static void Muovi<T>(ObservableCollection<T> collezione, int indice, int delta)
    {
        var nuovoIndice = indice + delta;
        if (indice < 0 || indice >= collezione.Count || nuovoIndice < 0 || nuovoIndice >= collezione.Count) return;
        collezione.Move(indice, nuovoIndice);
    }
}
