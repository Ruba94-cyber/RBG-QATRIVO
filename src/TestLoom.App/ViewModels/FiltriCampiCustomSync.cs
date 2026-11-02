using System.Collections.Generic;
using System.Linq;
using TestLoom.App.Models;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Allineamento dei filtri dei campi di sistema (Home e Report) al catalogo corrente, per <see cref="FiltroCampoCustom.Chiave"/>.
/// Il catalogo sorgente e' <c>CampoSistemaCatalogoService.CampiUtilizzabiliComeFiltro</c>: campi Ruolo=Nessuno, non archiviati e con
/// <c>UsabileComeFiltro</c>. Un filtro ancora eleggibile resta lo stesso oggetto (selezione e posizione conservate); un campo nuovo
/// viene aggiunto in fondo, nell'ordine del catalogo; un campo non piu' eleggibile viene rimosso dalla collezione, e quindi anche dal
/// predicato effettivo (nessun filtro invisibile). I valori dei test (TestCase.CampiCustom) non vengono toccati.
/// </summary>
internal static class FiltriCampiCustomSync
{
    /// <summary>Allinea <paramref name="filtri"/> a <paramref name="eleggibili"/>. Ritorna i filtri aggiunti (in ordine di catalogo)
    /// e quelli rimossi; chi chiama deve collegare/scollegare gli eventi <see cref="FiltroMultiSelect.Cambiato"/> dei due insiemi.</summary>
    internal static (List<FiltroCampoCustom> Aggiunti, List<FiltroCampoCustom> Rimossi) Allinea(
        IList<FiltroCampoCustom> filtri, IReadOnlyList<CampoSistemaDefinizione> eleggibili)
    {
        var catalogo = new Dictionary<string, CampoSistemaDefinizione>(StringComparer.OrdinalIgnoreCase);
        foreach (var campo in eleggibili) catalogo.TryAdd(campo.Chiave, campo);

        var rimossi = new List<FiltroCampoCustom>();
        for (var i = filtri.Count - 1; i >= 0; i--)
        {
            if (catalogo.ContainsKey(filtri[i].Chiave)) continue;
            rimossi.Add(filtri[i]);
            filtri.RemoveAt(i);
        }
        rimossi.Reverse(); // ordine della collezione originale (la rimozione procede dall'ultimo al primo)

        // Etichetta, tipo e opzioni sempre allineati al catalogo: un Master puo' rinominare il campo, o cambiarne le opzioni, senza
        // cambiarne la Chiave.
        foreach (var filtro in filtri)
            if (catalogo.TryGetValue(filtro.Chiave, out var campo))
            {
                if (filtro.Etichetta != campo.Etichetta) filtro.Etichetta = campo.Etichetta;
                AllineaDefinizione(filtro, campo);
            }

        var presenti = new HashSet<string>(filtri.Select(f => f.Chiave), StringComparer.OrdinalIgnoreCase);
        var aggiunti = new List<FiltroCampoCustom>();
        foreach (var campo in eleggibili)
        {
            if (!presenti.Add(campo.Chiave)) continue;
            var nuovo = new FiltroCampoCustom { Chiave = campo.Chiave, Etichetta = campo.Etichetta };
            AllineaDefinizione(nuovo, campo);
            filtri.Add(nuovo);
            aggiunti.Add(nuovo);
        }
        return (aggiunti, rimossi);
    }

    /// <summary>Copia tipo e opzioni del catalogo nel filtro: servono a offrire le opzioni di un campo Lista anche senza Test che le usino.</summary>
    private static void AllineaDefinizione(FiltroCampoCustom filtro, CampoSistemaDefinizione campo)
    {
        filtro.Tipo = campo.Tipo;
        filtro.OpzioniLista = campo.OpzioniLista.ToList();
    }
}
