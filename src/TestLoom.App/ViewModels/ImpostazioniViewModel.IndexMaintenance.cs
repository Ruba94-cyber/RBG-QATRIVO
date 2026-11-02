using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Manutenzione del read-model locale. I JSON condivisi restano sempre la fonte autorevole: questa operazione
/// forza una riconciliazione completa dell'indice SQLite locale e non crea/modifica metadati condivisi.
/// </summary>
public partial class ImpostazioniViewModel
{
    [ObservableProperty] private bool ricostruzioneIndiceInCorso;

    public bool PuoRicostruireIndice => _state.CartellaValida && !RicostruzioneIndiceInCorso;

    partial void OnRicostruzioneIndiceInCorsoChanged(bool value)
    {
        OnPropertyChanged(nameof(PuoRicostruireIndice));
    }

    [RelayCommand]
    private Task RicostruisciIndiceLocale() =>
        ComandoAsincrono.EseguiAsync(RicostruisciIndiceLocaleCoreAsync, "Ricostruzione dell'indice locale", m => MessaggioErrore = m);

    private async Task RicostruisciIndiceLocaleCoreAsync()
    {
        if (!PuoRicostruireIndice) return;

        RicostruzioneIndiceInCorso = true;
        Messaggio = null;
        MessaggioErrore = null;
        var watch = Stopwatch.StartNew();
        var itemCount = 0;
        try
        {
            await Task.Run(() =>
            {
                // Primo passaggio: fotografia autorevole completa, rimozione path stale e rilevamento file
                // problematici/duplicati. ReconcileFull evita normalmente di rigenerare le proiezioni con hash
                // invariato per ragioni di performance.
                _state.TestCases.RefreshIndex();

                // Questa però è un'azione MANUALE di repair: forziamo quindi un Upsert per ogni JSON noto.
                // RefreshPaths rilegge il file e LocalTestIndexStore.Upsert ricostruisce sempre la proiezione
                // normalizzata/FTS, anche quando il fingerprint del Test non è cambiato. In questo modo il
                // comando ripara davvero una proiezione locale incoerente invece di limitarsi a riconciliarla.
                var tuttiIPercorsi = _state.TestCases.GetAll(includeEliminati: true)
                    .Select(_state.TestCases.PercorsoPer)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                itemCount = tuttiIPercorsi.Count;
                _state.TestCases.RefreshPaths(tuttiIPercorsi);
            });

            _state.NotificaDatiRicaricati();
            Messaggio = LocalizationService.Instance.Scegli(
                "Indice locale ricostruito correttamente dai file JSON autorevoli.",
                "Local index rebuilt successfully from the authoritative JSON files.",
                "Index local reconstruit avec succès à partir des fichiers JSON faisant foi.");

            OnPropertyChanged(nameof(NumeroFileConProblemi));
            OnPropertyChanged(nameof(HaFileConProblemi));
            OnPropertyChanged(nameof(FileConProblemi));
            OnPropertyChanged(nameof(NumeroDuplicatiId));
            OnPropertyChanged(nameof(HaDuplicatiId));
            OnPropertyChanged(nameof(DuplicatiId));
            _onCambiato();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Ricostruzione indice locale", ex);
            MessaggioErrore = LocalizationService.Instance.Scegli(
                $"Ricostruzione indice locale non riuscita: {ex.Message}",
                $"Local index rebuild failed: {ex.Message}",
                $"Échec de la reconstruction de l'index local : {ex.Message}");
        }
        finally
        {
            watch.Stop();
            PerformanceDiagnosticsService.Record(
                _state.TestCases.LocalWorkspaceDirectory,
                "testcases.manual-index-rebuild",
                watch.Elapsed,
                itemCount,
                MessaggioErrore == null ? "success" : "failed");
            RicostruzioneIndiceInCorso = false;
        }
    }
}
