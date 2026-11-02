using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

public sealed record RecordForkRamoRiga(RecordFork Fork, RecordForkBranch Branch)
{
    public string Descrizione => Branch.IsDelete
        ? string.Format(LocalizationService.Instance.Traduci("RecordForkEliminazioneDaFmt"), Branch.DeviceId)
        : string.Format(LocalizationService.Instance.Traduci("RecordForkRevisioneDaFmt"), Branch.NewRevision?.ToString() ?? "?", Branch.DeviceId);
    public string Dettaglio => $"{Branch.Utc.ToLocalTime():dd/MM/yyyy HH:mm:ss}" +
        (string.IsNullOrWhiteSpace(Branch.Operator) ? "" : $" · {Branch.Operator}");
    public string AutomationId => $"RecordFork_Ramo_{Branch.EventId}";
}

public sealed record RecordForkRiga(RecordFork Fork)
{
    public string Titolo => $"{TipoRecord} {Fork.EntityId}";
    public string TipoRecord => Fork.Domain switch
    {
        DomainRecordJournalService.NonConformitaDomain => LocalizationService.Instance.Traduci("RecordForkTipoNonConformitaTesto"),
        DomainRecordJournalService.ChangeRequestDomain => LocalizationService.Instance.Traduci("RecordForkTipoChangeRequestTesto"),
        DomainRecordJournalService.SegnalazioneDomain => LocalizationService.Instance.Traduci("RecordForkTipoSegnalazioneTesto"),
        _ => Fork.Domain
    };
    public string Riepilogo => string.Format(LocalizationService.Instance.Traduci("RecordForkRiepilogoFmt"), Fork.Branches.Count, Fork.BaseRevision?.ToString() ?? "?") +
        LocalizationService.Instance.Traduci("RecordForkBloccatoMsg");
    public string RilevatoIl => Fork.DetectedUtc == default ? "" : Fork.DetectedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public IReadOnlyList<RecordForkRamoRiga> Rami => Fork.Branches.Select(b => new RecordForkRamoRiga(Fork, b)).ToList();
}

public partial class ImpostazioniViewModel
{
    [ObservableProperty] private bool risoluzioneRecordForkInCorso;

    public IReadOnlyList<RecordForkRiga> RecordForks => _state.CartellaValida && _state.RecordJournal != null
        ? _state.RecordJournal.OpenForks.Select(f => new RecordForkRiga(f)).ToList()
        : Array.Empty<RecordForkRiga>();
    public bool HaRecordForks => RecordForks.Count > 0;
    public bool PuoRisolvereRecordFork => _state.EMaster && _state.PuoScrivere && !RisoluzioneRecordForkInCorso;

    partial void OnRisoluzioneRecordForkInCorsoChanged(bool value) => OnPropertyChanged(nameof(PuoRisolvereRecordFork));

    private void NotificaRecordForkProperties()
    {
        OnPropertyChanged(nameof(RecordForks));
        OnPropertyChanged(nameof(HaRecordForks));
        OnPropertyChanged(nameof(PuoRisolvereRecordFork));
    }

    [RelayCommand]
    private async Task AggiornaRecordForks()
    {
        if (!_state.CartellaValida || _state.RecordJournal == null) return;
        try
        {
            await Task.Run(() => _state.RecordJournal.Refresh());
            NotificaRecordForkProperties();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Aggiornamento conflitti fra postazioni", ex);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("RecordForkAggiornamentoFallitoFmt"), ex.Message);
        }
    }

    [RelayCommand]
    private Task MantieniVersioneCorrenteRecordFork(RecordForkRiga? riga) =>
        ComandoAsincrono.EseguiAsync(() => MantieniVersioneCorrenteRecordForkCoreAsync(riga), "Mantenimento della versione corrente di un fork", m => MessaggioErrore = m);

    private async Task MantieniVersioneCorrenteRecordForkCoreAsync(RecordForkRiga? riga)
    {
        if (riga == null || !PuoRisolvereRecordFork) return;
        if (!await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("RecordForkMantieniTitolo"),
                string.Format(LocalizationService.Instance.Traduci("RecordForkChiudereConflittoFmt"), riga.Titolo) + "\n\n" +
                LocalizationService.Instance.Traduci("RecordForkAltreVersioniConsultabiliMsg"),
                LocalizationService.Instance.Traduci("RecordForkMantieniBtn"), LocalizationService.Instance.Traduci("Annulla")))
            return;
        await EseguiRisoluzioneRecord(() => RecordForkResolutionService.KeepCurrent(_state, riga.Fork, _state.Config.OperatoreAttivo));
    }

    [RelayCommand]
    private Task ApplicaRamoRecordFork(RecordForkRamoRiga? ramo) =>
        ComandoAsincrono.EseguiAsync(() => ApplicaRamoRecordForkCoreAsync(ramo), "Applicazione di un ramo di fork", m => MessaggioErrore = m);

    private async Task ApplicaRamoRecordForkCoreAsync(RecordForkRamoRiga? ramo)
    {
        if (ramo == null || !PuoRisolvereRecordFork) return;
        var azione = ramo.Branch.IsDelete ? LocalizationService.Instance.Traduci("RecordForkAzioneEliminaTesto") : LocalizationService.Instance.Traduci("RecordForkAzioneSalvaTesto");
        if (!await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("UsaQuestaVersioneTitolo"),
                ramo.Descrizione + "\n\n" + string.Format(LocalizationService.Instance.Traduci("RecordForkConfermiFmt"), azione) +
                LocalizationService.Instance.Traduci("RecordForkAltreVersioniRestanoMsg"),
                LocalizationService.Instance.Traduci("ConfermaSemplice"), LocalizationService.Instance.Traduci("Annulla")))
            return;
        await EseguiRisoluzioneRecord(() => RecordForkResolutionService.ApplyBranch(_state, ramo.Fork, ramo.Branch, _state.Config.OperatoreAttivo));
    }

    private async Task EseguiRisoluzioneRecord(Func<RecordForkResolutionService.Outcome> azione)
    {
        RisoluzioneRecordForkInCorso = true;
        Messaggio = null;
        MessaggioErrore = null;
        try
        {
            var esito = await Task.Run(azione);
            _state.RicaricaTutto();
            _state.NotificaDatiRicaricati();
            Messaggio = esito.Message;
            _onCambiato();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Risoluzione conflitto fra postazioni", ex);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("RisoluzioneNonEseguitaFmt"), ex.Message);
        }
        finally
        {
            RisoluzioneRecordForkInCorso = false;
            NotificaRecordForkProperties();
        }
    }
}
