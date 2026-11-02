using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Cronologia "Indietro" (pulsante e Alt+←). Si registra come tornare alla posizione precedente — una sezione
/// in cache (che conserva filtri e selezione) oppure un Test/CR/NC già salvato — invece di tenere vive le pagine
/// abbandonate. Gli editor di un elemento nuovo non salvato non sono ripristinabili e vengono saltati. Il ritorno
/// passa sempre dalla conferma di abbandono delle modifiche non salvate.
/// </summary>
public partial class MainViewModel
{
    private const int MaxCronologia = 30;
    private readonly List<(string Descrizione, Action Ripristina)> _cronologia = new();
    private bool _ripristinoCronologiaInCorso;
    private bool _sostituisciPosizioneCorrente;

    /// <summary>Passaggio da sola lettura a modifica dello stesso elemento ("Modifica" in un editor aperto da "Apri"):
    /// la pagina in sola lettura che esce NON entra in cronologia, cosi' "Indietro" torna alla posizione di partenza
    /// (la lista) e non alla stessa scheda.</summary>
    private void SostituisciPosizione(Action naviga)
    {
        _sostituisciPosizioneCorrente = true;
        try { naviga(); }
        finally { _sostituisciPosizioneCorrente = false; }
    }

    public bool PuoTornareIndietro => _cronologia.Count > 0;
    public string DescrizioneIndietro => _cronologia.Count == 0 ? "" : "Torna a: " + _cronologia[^1].Descrizione;

    /// <summary>Chiamato da ImpostaCurrentPage prima di sostituire la pagina corrente.</summary>
    private void RegistraPosizionePrecedente(ViewModelBase? uscente, ViewModelBase? entrante)
    {
        if (_ripristinoCronologiaInCorso || _sostituisciPosizioneCorrente || uscente == null || ReferenceEquals(uscente, entrante)) return;
        var posizione = DescriviPosizione(uscente);
        if (posizione == null) return;
        if (_cronologia.Count > 0 && _cronologia[^1].Descrizione == posizione.Value.Descrizione) return;
        _cronologia.Add(posizione.Value);
        if (_cronologia.Count > MaxCronologia) _cronologia.RemoveAt(0);
        NotificaCronologia();
    }

    private (string Descrizione, Action Ripristina)? DescriviPosizione(ViewModelBase pagina)
    {
        switch (pagina)
        {
            case TestCaseEditorViewModel { IsNew: false } ed when !string.IsNullOrWhiteSpace(ed.TestCase.Id):
            {
                var id = ed.TestCase.Id;
                return ($"Test {id}", () => NavigaATestCaseCore(id));
            }
            case ChangeRequestEditorViewModel { IsNew: false } cr:
            {
                var id = cr.ChangeRequest.Id;
                var soloLettura = !cr.InModifica;
                return ($"Change Request {id}", () => NavigaAChangeRequestCore(id, soloLettura));
            }
            case SegnalazioneReteEditorViewModel { IsNew: false } nc:
            {
                var id = nc.Nc.Id;
                var soloLettura = !nc.InModifica;
                return ($"Segnalazione {id}", () => NavigaASegnalazioneReteCore(id, soloLettura));
            }
        }
        var navId = _pagineCache.FirstOrDefault(kv => ReferenceEquals(kv.Value, pagina)).Key;
        var item = navId == null ? null : NavItems.FirstOrDefault(n => n.Id == navId);
        if (item == null) return null;
        return (item.Titolo, () =>
        {
            ImpostaNavSenzaRicaricare(item);
            ImpostaCurrentPage(OttieniPagina(item));
        });
    }

    [RelayCommand]
    private async Task TornaIndietro()
    {
        if (_cronologia.Count == 0) return;
        if (!await ConfermaAbbandonoPaginaCorrenteSeNecessario()) return;
        var (_, ripristina) = _cronologia[^1];
        _cronologia.RemoveAt(_cronologia.Count - 1);
        _ripristinoCronologiaInCorso = true;
        try { ripristina(); }
        catch (Exception ex)
        {
            Services.CrashLogService.Registra("Navigazione indietro", ex);
            ErroreNavigazione = LocalizationService.Instance.Traduci("NavigazioneIndietroErrore");
        }
        finally
        {
            _ripristinoCronologiaInCorso = false;
            NotificaCronologia();
        }
    }

    private void NotificaCronologia()
    {
        OnPropertyChanged(nameof(PuoTornareIndietro));
        OnPropertyChanged(nameof(DescrizioneIndietro));
    }

    /// <summary>Registra in "Recenti" (Home) un Test, una CR o una Segnalazione di rete appena aperta. Un id vuoto o
    /// un elemento che non esiste non viene registrato; un errore di lettura non blocca mai l'apertura della pagina.</summary>
    private void RegistraRecente(string tipo, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        try
        {
            if (tipo == RecentItemsService.TipoTest)
            {
                if (_state.TestCases.GetById(id) is { } tc) RecentItemsService.Registra(_state, tipo, tc.Id, tc.DescrizioneBreve);
            }
            else if (tipo == RecentItemsService.TipoChangeRequest)
            {
                if (_state.ChangeRequests.GetById(id) is { } cr) RecentItemsService.Registra(_state, tipo, cr.Id, cr.Titolo);
            }
            else if (tipo == RecentItemsService.TipoSegnalazioneRete)
            {
                if (_state.NonConformita.GetById(id) is { } nc) RecentItemsService.Registra(_state, tipo, nc.Id, nc.Descrizione);
            }
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Registrazione Recenti Home", ex);
        }
    }
}
