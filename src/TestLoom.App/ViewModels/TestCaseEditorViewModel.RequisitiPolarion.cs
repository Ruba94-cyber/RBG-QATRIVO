using CommunityToolkit.Mvvm.ComponentModel;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Badge dello stato Ticketing accanto ai requisiti collegati (sola lettura dalla cache in memoria; nessuna rete).</summary>
public partial class TestCaseEditorViewModel
{
    public DataPaths? PolarionPaths => _state.CartellaValida ? _state.Paths : null;

    /// <summary>Cambia a ogni aggiornamento della cache: fa rivalutare i badge (MultiBinding).</summary>
    [ObservableProperty] private int polarionRequisitiVersione;

    /// <summary>La view si sottoscrive solo mentre e visibile (nessuna sottoscrizione statica orfana).</summary>
    public void CollegaCachePolarion()
    {
        RequisitoPolarionCache.Aggiornato -= OnCachePolarionAggiornata;
        RequisitoPolarionCache.Aggiornato += OnCachePolarionAggiornata;
        RequisitoPolarionCache.Richiedi(_state);
    }

    public void ScollegaCachePolarion() => RequisitoPolarionCache.Aggiornato -= OnCachePolarionAggiornata;

    private void OnCachePolarionAggiornata() { if (!_disposto) PolarionRequisitiVersione++; }
}
