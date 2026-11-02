namespace TestLoom.App.ViewModels;

/// <summary>Serializzazione delle selezioni multiple dentro una vista salvata (VistaSalvataFiltri.Valori), condivisa
/// fra la lista Test e WU Status.</summary>
internal static class FiltroMultiSelectSalvataggio
{
    // Separatore di unita' (0x1F): non compare in nomi/ID reali. Un valore SENZA separatore (formato delle viste
    // salvate e dello snapshot persistito prima della multi-selezione: una sola stringa/enum/Uid) si legge come
    // selezione di un solo elemento, quindi le viste vecchie restano caricabili senza migrazione.
    internal const char SeparatoreValori = '\u001F';

    public static string? Serializza(FiltroMultiSelect filtro)
    {
        var sel = filtro.Selezionati;
        return sel.Count == 0 ? null : string.Join(SeparatoreValori, sel);
    }

    public static HashSet<string> DeserializzaValori(string? grezzo) =>
        string.IsNullOrEmpty(grezzo)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : grezzo.Split(SeparatoreValori, StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Applica una selezione salvata alle Opzioni correnti (i valori non piu' presenti fra le opzioni
    /// vengono scartati: non c'e' modo di selezionarli). Chiamata sotto la sospensione dei filtri del ViewModel.</summary>
    public static void ApplicaSelezione(FiltroMultiSelect filtro, string? grezzo, bool conservaMancanti = false)
    {
        var valori = DeserializzaValori(grezzo);
        if (conservaMancanti && valori.Count > 0)
        {
            // Valori testuali non piu' nel catalogo (componente rinominata, ecc.): restano come opzioni selezionate
            // invece di essere scartati, cosi' il filtro non si allarga in silenzio (stesso criterio di
            // ConSelezionatiMancanti usato da Aggiorna).
            var noti = new HashSet<string>(filtro.Opzioni.Select(o => o.Valore), StringComparer.OrdinalIgnoreCase);
            var mancanti = valori.Where(x => !noti.Contains(x)).ToList();
            if (mancanti.Count > 0)
                filtro.Popola(filtro.Opzioni.Select(o => (o.Valore, o.Etichetta)).Concat(mancanti.Select(m => (m, m))).ToList());
        }
        foreach (var o in filtro.Opzioni.ToList())
        {
            var voluto = valori.Contains(o.Valore);
            if (o.Selezionato != voluto) o.Selezionato = voluto;
        }
    }
}
