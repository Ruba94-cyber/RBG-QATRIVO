using System.Collections.ObjectModel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Elenco compatto (sola lettura) delle anomalie collegate a un Test, usato dalla Lista Test
/// ("Verifica anomalie") senza entrare nell'editor. Nessuna logica di workflow: ogni riga apre lo STESSO pop-up
/// di dettaglio (<see cref="AnomaliaDettaglioViewModel"/>) della pagina Anomalie e dell'editor Test.</summary>
public sealed class AnomalieTestElencoViewModel : ViewModelBase
{
    private readonly AppState _state;

    public AnomalieTestElencoViewModel(AppState state, string testId)
    {
        _state = state;
        TestId = testId;
        Ricarica();
    }

    public string TestId { get; }
    public ObservableCollection<NcRiga> Righe { get; } = new();

    /// <summary>Vero se almeno un pop-up ha scritto qualcosa: il chiamante deve aggiornare la propria vista.</summary>
    public bool Modificato { get; set; }

    /// <summary>Anomalie collegate al test (aperte e chiuse), piu recenti per prime; ordine stabile.</summary>
    public static IReadOnlyList<NonConformita> AnomalieDelTest(AppState state, string testId) =>
        AnomaliaWorkflowService.PerTest(testId, state.NonConformita.GetByTestIds(new[] { testId }), state.TestCases.GetById)
            .OrderByDescending(n => n.DataApertura).ThenByDescending(n => n.Id).ToList();

    public void Ricarica()
    {
        Righe.Clear();
        foreach (var nc in AnomalieDelTest(_state, TestId)) Righe.Add(new NcRiga { Nc = nc });
    }

    public AnomaliaDettaglioViewModel CreaDettaglio(NcRiga riga) => new(_state, riga.Nc);
}
