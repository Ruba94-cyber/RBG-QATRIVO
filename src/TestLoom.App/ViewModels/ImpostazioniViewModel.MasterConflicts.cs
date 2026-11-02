using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Una voce di catalogo master con piu' versioni concorrenti (card "Conflitti nei cataloghi master").</summary>
public sealed class MasterConflittoRiga
{
    public MasterConflittoRiga(PhaseDMasterConflitto conflitto)
    {
        Conflitto = conflitto;
        Versioni = conflitto.Versioni.Select(v => new MasterConflittoVersioneRiga(conflitto, v)).ToList();
    }

    public PhaseDMasterConflitto Conflitto { get; }
    public IReadOnlyList<MasterConflittoVersioneRiga> Versioni { get; }

    public string Titolo => $"{Conflitto.Catalogo} · " + (string.IsNullOrWhiteSpace(Conflitto.BusinessKey)
        ? "voce " + Conflitto.EntityUid.ToString("N")[..8]
        : Conflitto.BusinessKey);

    public string Riepilogo => $"{Conflitto.Versioni.Count} versioni concorrenti. Finché un Master non sceglie, " +
        "la voce mostra provvisoriamente la versione più recente; le altre voci del catalogo restano modificabili.";
}

/// <summary>Una versione concorrente di una voce: data, campi diversi e pulsante "Usa questa versione".</summary>
public sealed class MasterConflittoVersioneRiga
{
    public MasterConflittoVersioneRiga(PhaseDMasterConflitto conflitto, PhaseDMasterConflittoVersione versione)
    {
        Conflitto = conflitto;
        Versione = versione;
    }

    public PhaseDMasterConflitto Conflitto { get; }
    public PhaseDMasterConflittoVersione Versione { get; }

    public string Sha8 => Versione.VersionId.Length >= 8 ? Versione.VersionId[..8] : Versione.VersionId;
    public string AutomationId => $"MasterConflict_Usa_{Sha8}_Button";

    public string Titolo => Versione.Operazione == PhaseDMasterMutationOperation.Delete ? LocalizationService.Instance.Traduci("MasterConflittoEliminazioneTitolo") : LocalizationService.Instance.Traduci("MasterConflittoModificaTitolo");

    public string Dettaglio
    {
        get
        {
            var parti = new List<string> { Versione.TimestampUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") };
            if (Versione.CampiDiversi.Count > 0) parti.Add(LocalizationService.Instance.Traduci("MasterConflittoCampiDiversiPrefisso") + string.Join(", ", Versione.CampiDiversi));
            if (Versione.Visualizzata) parti.Add(LocalizationService.Instance.Traduci("MasterConflittoMostrataOraTesto"));
            return string.Join(" · ", parti);
        }
    }
}

public partial class ImpostazioniViewModel
{
    [ObservableProperty] private IReadOnlyList<MasterConflittoRiga> conflittiMaster = Array.Empty<MasterConflittoRiga>();
    [ObservableProperty] private bool risoluzioneConflittoMasterInCorso;

    public bool HaConflittiMaster => ConflittiMaster.Count > 0;
    public bool PuoRisolvereConflittoMaster => _state.EMaster && _state.PuoScrivere && !RisoluzioneConflittoMasterInCorso;

    partial void OnConflittiMasterChanged(IReadOnlyList<MasterConflittoRiga> value) => OnPropertyChanged(nameof(HaConflittiMaster));

    partial void OnRisoluzioneConflittoMasterInCorsoChanged(bool value) => OnPropertyChanged(nameof(PuoRisolvereConflittoMaster));

    [RelayCommand]
    private async Task AggiornaConflittiMaster() => await CaricaConflittiMasterAsync();

    /// <summary>Legge l'elenco dei conflitti fuori dal thread UI. Non lancia: l'errore va in MessaggioErrore.</summary>
    private async Task CaricaConflittiMasterAsync()
    {
        if (!_state.CartellaValida)
        {
            ConflittiMaster = Array.Empty<MasterConflittoRiga>();
            return;
        }
        var radice = _state.Paths.Root;
        try
        {
            var elenco = await Task.Run(() => PhaseDMasterConflictResolver.Elenca(radice));
            ConflittiMaster = elenco.Select(c => new MasterConflittoRiga(c)).ToList();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Aggiornamento conflitti cataloghi master", ex);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("MasterConflittoAggiornamentoFallitoFmt"), ex.Message);
        }
    }

    [RelayCommand]
    private Task UsaVersioneConflittoMaster(MasterConflittoVersioneRiga? riga) =>
        ComandoAsincrono.EseguiAsync(() => UsaVersioneConflittoMasterCoreAsync(riga), "Uso della versione in conflitto dell'anagrafica", m => MessaggioErrore = m);

    private async Task UsaVersioneConflittoMasterCoreAsync(MasterConflittoVersioneRiga? riga)
    {
        if (riga == null || !PuoRisolvereConflittoMaster) return;
        var conflitto = riga.Conflitto;
        var versione = riga.Versione;
        var azione = versione.Operazione == PhaseDMasterMutationOperation.Delete
            ? LocalizationService.Instance.Traduci("MasterConflittoAzioneEliminaTesto")
            : LocalizationService.Instance.Traduci("MasterConflittoAzioneUsaTesto");
        if (!await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("UsaQuestaVersioneTitolo"),
                riga.Titolo + "\n" + riga.Dettaglio + "\n\n" + string.Format(LocalizationService.Instance.Traduci("MasterConflittoConfermiFmt"), azione) +
                LocalizationService.Instance.Traduci("MasterConflittoAltreVersioniTesto"),
                LocalizationService.Instance.Traduci("ConfermaSemplice"), LocalizationService.Instance.Traduci("Annulla")))
            return;

        RisoluzioneConflittoMasterInCorso = true;
        Messaggio = null;
        MessaggioErrore = null;
        try
        {
            var paths = _state.Paths;
            var operatore = _state.Config.OperatoreAttivo;
            var isMaster = _state.EMaster;
            var esito = await Task.Run(() =>
            {
                var risoluzione = PhaseDMasterConflictResolver.Risolvi(
                    paths, conflitto.Catalogo, conflitto.EntityUid, versione.VersionId, operatore, isMaster);
                _state.RegistraAudit("RisoluzioneConflittoMaster", $"{conflitto.Catalogo}/{conflitto.BusinessKey}",
                    $"uid={conflitto.EntityUid:N}; scelta={riga.Sha8}; operazione={risoluzione.Operazione}; mutation={risoluzione.MutationId:N}");
                return risoluzione;
            });
            _state.RicaricaTutto();
            _state.NotificaDatiRicaricati();
            Messaggio = esito.Operazione == PhaseDMasterMutationOperation.Delete
                ? LocalizationService.Instance.Traduci("MasterConflittoSceltaEliminataMsg")
                : LocalizationService.Instance.Traduci("MasterConflittoSceltaVersioneMsg");
            await CaricaConflittiMasterAsync();
            _onCambiato();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Risoluzione conflitto cataloghi master", ex);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("RisoluzioneNonEseguitaFmt"), ex.Message);
        }
        finally
        {
            RisoluzioneConflittoMasterInCorso = false;
            OnPropertyChanged(nameof(PuoRisolvereConflittoMaster));
        }
    }
}
