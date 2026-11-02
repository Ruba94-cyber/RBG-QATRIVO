using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Layout del team nel pannello "Personalizza Home". Precedenza: layout personale del PC, poi layout del team
/// (<c>_ditm/ui/home-team-layout.json</c>), poi predefinito di sistema. Ogni operatore puo' applicare il layout del
/// team al proprio PC; pubblicarlo e' riservato ai Master, previa conferma.
/// </summary>
public partial class HomeViewModel
{
    /// <summary>Layout del team letto all'ultimo caricamento; null se il file manca o non e' leggibile.</summary>
    private HomeTeamLayoutDocumento? _layoutTeam;

    /// <summary>Revisione del layout del team alla lettura: la pubblicazione la confronta con quella nel file.</summary>
    private long _revisioneLayoutTeam;

    public bool TeamLayoutDisponibile => _layoutTeam != null;

    /// <summary>Solo Master su cartella condivisa: il pulsante di pubblicazione compare solo per loro.</summary>
    public bool PuoPubblicareLayoutTeam => _state.EMaster && _state.CartellaValida;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HaMessaggioLayoutTeam))]
    private string? messaggioLayoutTeam;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HaErroreLayoutTeam))]
    private string? messaggioErroreLayoutTeam;

    public bool HaMessaggioLayoutTeam => !string.IsNullOrEmpty(MessaggioLayoutTeam);

    public bool HaErroreLayoutTeam => !string.IsNullOrEmpty(MessaggioErroreLayoutTeam);

    /// <summary>Ricarica il layout del team dal file condiviso e lo applica al posto del layout personale di questo PC.</summary>
    [RelayCommand(CanExecute = nameof(TeamLayoutDisponibile))]
    private void UsaLayoutTeam()
    {
        try
        {
            // Lettura fresca: il file puo' essere cambiato da un altro PC dopo l'apertura della Home.
            var stato = CaricaLayoutTeam(_state);
            if (_layoutTeam == null)
            {
                MessaggioLayoutTeam = null;
                MessaggioErroreLayoutTeam = LocalizationService.Instance.Traduci(stato == StatoLayoutTeam.NonLeggibile
                    ? "HomeLayoutTeamNonLeggibile" : "HomeLayoutTeamNonDisponibile");
                return;
            }
            // Il layout personale si toglie solo dopo aver letto un layout del team valido.
            ViewStateService.ScriviLista(_state, ChiaveLayout, Array.Empty<string>());
            _layout = NormalizzaLayout(HomeTeamLayoutService.RigheLayout(_layoutTeam));
            RicostruisciVociLayout();
            NotificaLayout();
            MessaggioLayoutTeam = LocalizationService.Instance.Traduci("HomeLayoutTeamApplicato");
            MessaggioErroreLayoutTeam = null;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Applica layout Home del team", ex);
            MessaggioLayoutTeam = null;
            MessaggioErroreLayoutTeam = Errore(ex);
        }
    }

    /// <summary>Pubblica come default del team il layout che questo Master sta vedendo, previa conferma.</summary>
    [RelayCommand(CanExecute = nameof(PuoPubblicareLayoutTeam))]
    private Task PubblicaLayoutTeamAsync() =>
        ComandoAsincrono.EseguiAsync(PubblicaLayoutTeamCoreAsync, "Pubblicazione del layout di team", m => MessaggioErroreLayoutTeam = m);

    private async Task PubblicaLayoutTeamCoreAsync()
    {
        var confermato = await ConfirmService.Chiedi(
            LocalizationService.Instance.Traduci("HomeLayoutTeamConfermaTitolo"),
            LocalizationService.Instance.Traduci("HomeLayoutTeamConfermaMsg"),
            LocalizationService.Instance.Traduci("HomeLayoutTeamConfermaSi"),
            LocalizationService.Instance.Traduci("HomeLayoutTeamConfermaNo"));
        if (!confermato) return;

        try
        {
            var sezioni = _layout.Select(v => (Id: v.Id, Visibile: v.Visibile)).ToList();
            _layoutTeam = HomeTeamLayoutService.Pubblica(_state, sezioni, _revisioneLayoutTeam);
            _revisioneLayoutTeam = _layoutTeam.Revisione;
            MessaggioLayoutTeam = string.Format(LocalizationService.Instance.Traduci("HomeLayoutTeamPubblicatoFmt"),
                _layoutTeam.Revisione);
            MessaggioErroreLayoutTeam = null;
        }
        catch (ConflittoRevisioneException ex)
        {
            MessaggioLayoutTeam = null;
            MessaggioErroreLayoutTeam = string.Format(LocalizationService.Instance.Traduci("HomeLayoutTeamConflittoFmt"),
                ex.RevisioneCorrente, ex.RevisioneAttesa);
        }
        catch (UnauthorizedAccessException)
        {
            MessaggioLayoutTeam = null;
            MessaggioErroreLayoutTeam = LocalizationService.Instance.Traduci("HomeLayoutTeamSoloMaster");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Casi attesi, con un messaggio gia' esplicito dal servizio (sola lettura, lock occupato, file illeggibile).
            MessaggioLayoutTeam = null;
            MessaggioErroreLayoutTeam = Errore(ex);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Pubblica layout Home del team", ex);
            MessaggioLayoutTeam = null;
            MessaggioErroreLayoutTeam = Errore(ex);
        }
        finally
        {
            OnPropertyChanged(nameof(TeamLayoutDisponibile));
            UsaLayoutTeamCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Legge il layout del team e ne aggiorna lo stato. Assente o non leggibile: nessun layout del team.</summary>
    private StatoLayoutTeam CaricaLayoutTeam(AppState state)
    {
        var lettura = state.CartellaValida ? HomeTeamLayoutService.Leggi(state.Paths) : LetturaLayoutTeam.NessunFile;
        _layoutTeam = lettura.Stato == StatoLayoutTeam.Valido ? lettura.Documento : null;
        _revisioneLayoutTeam = lettura.Revisione;
        OnPropertyChanged(nameof(TeamLayoutDisponibile));
        UsaLayoutTeamCommand.NotifyCanExecuteChanged();
        return lettura.Stato;
    }

    /// <summary>Predefinito senza preferenza personale: il layout del team se pubblicato e leggibile, altrimenti tutte le
    /// sezioni visibili nell'ordine storico.</summary>
    private List<(string Id, bool Visibile)> LayoutPredefinito()
    {
        IEnumerable<string> righe = _layoutTeam != null ? HomeTeamLayoutService.RigheLayout(_layoutTeam) : Array.Empty<string>();
        return NormalizzaLayout(righe);
    }

    private static string Errore(Exception ex) =>
        string.Format(LocalizationService.Instance.Traduci("HomeLayoutTeamErroreFmt"), ex.Message);
}
