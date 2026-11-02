using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Esportazione di un pacchetto diagnostico da allegare a una segnalazione di problema (feedback utente
/// 2026-09-17): raccoglie i log applicativi già esistenti (CrashLogService, DiagnosticTraceService,
/// PerformanceDiagnosticsService, tutti già circolari/bounded per progetto) in un unico file, invece di
/// dover cercare a mano sotto %APPDATA%. Stesso schema code-behind + picker nativo di ReportViewModel.
/// EsportaExcelVerso. Espone anche il percorso della cartella log con un link diretto per apirla in Esplora
/// risorse (feedback utente 2026-09-17: "metti... il percorso... con tanto di link premibile").
/// </summary>
public partial class ImpostazioniViewModel
{
    public string PercorsoCartellaLogDiagnostico => CrashLogService.LogDirectory;

    /// <summary>Stesso schema di ApriCartellaAllegati, ma qui la cartella viene creata se manca: a differenza
    /// degli allegati (dove la creazione sarebbe un side-effect indesiderato di una semplice consultazione),
    /// l'utente sta chiedendo esplicitamente "dove sono i log" e si aspetta che il percorso esista sempre.</summary>
    [RelayCommand]
    private void ApriCartellaLogDiagnostico()
    {
        try
        {
            Directory.CreateDirectory(PercorsoCartellaLogDiagnostico);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PercorsoCartellaLogDiagnostico) { UseShellExecute = true });
            MessaggioErrore = null;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Apertura cartella log diagnostico", ex);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("LogDiagnosticoNonRiuscitoFmt"), ex.Message);
        }
    }

    /// <summary>Pacchetto diagnostico (ZIP): generazione su thread di background con Annulla; il file compare solo a
    /// scrittura completa (vedi EsportazioneInBackground).</summary>
    public async Task EsportaLogDiagnosticoVerso(string percorso)
    {
        if (EsportazioneInCorso) return;
        EsportazioneInCorso = true;
        Messaggio = LocalizationService.Instance.Traduci("GenerazioneReportInCorso");
        EsitoEsportazione esito;
        try
        {
            esito = await _esportazione.EseguiAsync(percorso,
                (temporaneo, ct) => File.WriteAllBytes(temporaneo, DiagnosticExportService.GeneraZip(_state, ct)));
        }
        finally
        {
            EsportazioneInCorso = false;
        }
        switch (esito.Stato)
        {
            case StatoEsportazione.Completata:
                Messaggio = string.Format(LocalizationService.Instance.Traduci("LogDiagnosticoSalvatoFmt"), Path.GetFileName(percorso));
                MessaggioErrore = null;
                break;
            case StatoEsportazione.Annullata:
                Messaggio = LocalizationService.Instance.Traduci("EsportazioneAnnullata");
                MessaggioErrore = null;
                break;
            default:
                Messaggio = null;
                if (esito.Errore != null) CrashLogService.Registra("Esportazione pacchetto diagnostico", esito.Errore);
                MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("LogDiagnosticoNonRiuscitoFmt"), esito.Errore?.Message);
                break;
        }
    }

    private readonly EsportazioneInBackground _esportazione = new();

    /// <summary>Vero mentre il pacchetto diagnostico e in generazione: la view disabilita il pulsante e mostra Annulla.</summary>
    [ObservableProperty] private bool esportazioneInCorso;

    [RelayCommand]
    private void AnnullaEsportazione() => _esportazione.Annulla();

    /// <summary>Diagnostica in sola lettura: elenca le NC il cui aggancio per codice a un Test non e coerente
    /// (codice test riassegnato dopo eliminazione definitiva). Non modifica alcun dato.</summary>
    [RelayCommand]
    private void VerificaNcAgganciate()
    {
        try
        {
            var nc = _state.NonConformita.PeekAllReadOnly();
            var incoerenti = NcAgganciTestService.ElencaAgganciIncoerenti(nc, _state.TestCases.GetById, _state.TestCases.GetByUid);
            var loc = LocalizationService.Instance;
            Messaggio = incoerenti.Count == 0
                ? string.Format(loc.Traduci("DiagNcAgganciateNessunaFmt"), nc.Count)
                : string.Format(loc.Traduci("DiagNcAgganciateTrovateFmt"), incoerenti.Count,
                    incoerenti.Count(x => x.Motivo == MotivoAgganciaIncoerente.NcPrecedenteAlTest),
                    string.Join(", ", incoerenti.Take(10).Select(x => $"{x.NcId}→{x.TestId}")));
            MessaggioErrore = null;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Diagnostica NC agganciate", ex);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("LogDiagnosticoNonRiuscitoFmt"), ex.Message);
        }
    }
}
