using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using TestLoom.App.ViewModels;

namespace TestLoom.App;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// </summary>
/// <remarks>
/// Riuso delle View (correzione prestazioni 2026-09-25, misurato con tools/perf-ui-6.4.0: la rivisita di una
/// pagina di navigazione già in cache in MainViewModel._pagineCache ricostruiva comunque da zero l'intero
/// albero Avalonia della View con Activator.CreateInstance, anche se il ViewModel era la stessa istanza di
/// prima). Cache tenuta con ConditionalWeakTable&lt;ViewModel, View&gt;: chiave debole, nessuna vita innaturale
/// data ai ViewModel gia' rimossi da MainViewModel._pagineCache (es. dopo Dispose/reset).
///
/// Scope: applicato a TUTTI i ViewModelBase, non solo alle pagine di navigazione, perche' la guardia sotto
/// (riuso SOLO se la View cache non ha gia' un genitore visuale o logico) rende la scelta sicura in ogni caso:
/// - Pagine di navigazione (OttieniPagina): stessa istanza di ViewModel ad ogni rivisita -> cache hit reale,
///   e' il caso che il piano vuole ottimizzare (Anomalie/CR, Home, WU, Audit, Master, Test).
/// - "impostazioni" e i ViewModel di editor (TestCaseEditorViewModel, ChangeRequestEditorViewModel, ...) sono
///   sempre ricreati con `new` da MainViewModel: ogni volta e' una chiave diversa nella ConditionalWeakTable,
///   quindi nessun cache hit e nessun comportamento diverso da prima (Build crea comunque una View nuova).
/// - Se in futuro lo stesso ViewModel venisse mai mostrato in due punti contemporaneamente (es. due finestre),
///   la seconda richiesta troverebbe la View cache ancora agganciata alla prima (Parent/GetVisualParent non
///   nulli) e ne creerebbe una nuova, esattamente come il comportamento storico: nessuna regressione possibile.
/// Estendere a tutti i ViewModelBase invece di limitare alle sole pagine di navigazione evita di dover
/// distinguere i due casi qui (che richiederebbe conoscere MainViewModel, un accoppiamento che ViewLocator non
/// ha oggi) senza introdurre rischio aggiuntivo.
/// </remarks>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    private readonly ConditionalWeakTable<object, Control> _viewCache = new();

    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        if (_viewCache.TryGetValue(param, out var cached) && PuoEssereRiusata(cached))
            return cached;

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            var control = (Control)Activator.CreateInstance(type)!;
            _viewCache.AddOrUpdate(param, control);
            return control;
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    /// <summary>
    /// Precaricamento NON invasivo dopo la prima finestra: una View gia' associata
    /// al ViewModel, anche se montata nella UI, non va mai ricreata o sostituita.
    /// Build() resta invariato per la navigazione reale e per la multi-window.
    /// Chiamare esclusivamente dal thread UI Avalonia.
    /// </summary>
    public void PreparaInCacheSeAssente(ViewModelBase vm)
    {
        if (_viewCache.TryGetValue(vm, out _)) return;
        _ = Build(vm);
    }

    /// <summary>Riusabile solo se non e' attualmente agganciata da nessuna parte (ne' genitore logico ne'
    /// visuale): altrimenti staccarla da dove si trova per rimetterla altrove romperebbe quella visualizzazione.
    /// </summary>
    private static bool PuoEssereRiusata(Control cached) =>
        cached.Parent is null && cached.GetVisualParent() is null;

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
