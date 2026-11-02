using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Quick editor outcome shortcuts. They use exactly the existing outcome workflow,
/// not a second anomaly/NC or persistence path. Full editor still uses the original ComboBox.
/// </summary>
public partial class TestCaseEditorViewModel
{
    // Avalonia synchronizes the original ComboBox after a shortcut updates the model.
    // Its SelectionChanged bubbles to the view: ignore that echo while the shortcut is
    // performing the same workflow, otherwise "Conforme" could show two confirmation dialogs.
    private readonly HashSet<Condizione> _esitiRapidiInCorso = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Condizione, Esito> _ecoEsitiRapidi = new(ReferenceEqualityComparer.Instance);

    public bool ECambioEsitoRapidoInCorso(Condizione condizione) =>
        _esitiRapidiInCorso.Contains(condizione);

    /// <summary>
    /// Consume exactly one UI ComboBox echo of a Quick command. The binding may be
    /// dispatched after the async command finishes; a short-lived in-progress flag
    /// alone would miss it. Never suppress a user's selection of a different outcome.
    /// </summary>
    public bool ConsumaEcoEsitoRapido(Condizione condizione)
    {
        if (!_ecoEsitiRapidi.Remove(condizione, out var expected)) return false;
        return condizione.Esito == expected;
    }

    [RelayCommand] private Task EsitoRapidoConforme(Condizione? condizione) =>
        ImpostaEsitoRapidoAsync(condizione, Esito.Conforme);

    [RelayCommand] private Task EsitoRapidoNonConforme(Condizione? condizione) =>
        ImpostaEsitoRapidoAsync(condizione, Esito.NonConforme);

    [RelayCommand] private Task EsitoRapidoDeviazione(Condizione? condizione) =>
        ImpostaEsitoRapidoAsync(condizione, Esito.ConformeConDeviazioni);

    [RelayCommand] private Task EsitoRapidoNonApplicabile(Condizione? condizione) =>
        ImpostaEsitoRapidoAsync(condizione, Esito.NonApplicabile);

    /// <summary>Riporta l'operazione a "Da eseguire" (annulla un esito scelto per errore). Stesso workflow degli altri esiti.</summary>
    [RelayCommand] private Task EsitoRapidoDaEseguire(Condizione? condizione) =>
        ImpostaEsitoRapidoAsync(condizione, Esito.DaEseguire);

    private async Task ImpostaEsitoRapidoAsync(Condizione? condizione, Esito nuovoEsito)
    {
        // A stale UI row, a historical revision, a read-only editor and Full mode
        // must not modify any test data even if the generated command is invoked.
        if (condizione is null || !ModoEditorRapido || !PuoModificareContenuto ||
            RevisioneCorrente?.Condizioni.Any(c => ReferenceEquals(c, condizione)) != true ||
            condizione.Esito == nuovoEsito || !_esitiRapidiInCorso.Add(condizione))
            return;

        try
        {
            // Register before model notification: ComboBox can echo synchronously.
            _ecoEsitiRapidi[condizione] = nuovoEsito;
            condizione.Esito = nuovoEsito;
            ModificheNonSalvate = true;
            // Reuse native NC/CR suggestions, outcome reconciliation and the existing
            // confirmation when changing to Conforme while keeping older problem notes.
            await GestisciCambioEsitoCondizione(condizione);
        }
        finally
        {
            _esitiRapidiInCorso.Remove(condizione);
        }
    }
}
