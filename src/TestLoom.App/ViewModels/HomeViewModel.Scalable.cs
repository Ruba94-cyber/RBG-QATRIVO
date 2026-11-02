using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Percorsi read-side scalabili della Home. Il file HomeViewModel storico mantiene la logica/fallback completa;
/// questi helper vengono usati quando esiste una fotografia SQLite valida, così il percorso normale non
/// materializza l'intero archivio Test.
/// </summary>
public partial class HomeViewModel
{
    private const int HomeListPageSize = 100;
    private LocalTestArchiveQueryService? _homeArchiveQuery;
    private LocalHomeDashboardQueryService? _homeDashboardQuery;
    private (long Conteggio, long Revisione)? _dashboardSenzaRevisioni;

    [ObservableProperty] private long paginaDaFare = 1;
    [ObservableProperty] private long paginaFatti = 1;
    public long TotaleDaFare { get; private set; }
    public long TotaleFatti { get; private set; }
    public long PagineDaFare => Math.Max(1L, checked((TotaleDaFare + HomeListPageSize - 1L) / HomeListPageSize));
    public long PagineFatti => Math.Max(1L, checked((TotaleFatti + HomeListPageSize - 1L) / HomeListPageSize));
    public bool PuoPaginaDaFarePrecedente => PaginaDaFare > 1;
    public bool PuoPaginaDaFareSuccessiva => PaginaDaFare < PagineDaFare;
    public bool PuoPaginaFattiPrecedente => PaginaFatti > 1;
    public bool PuoPaginaFattiSuccessiva => PaginaFatti < PagineFatti;
    public string TestoPaginaDaFare => LocalizationService.Instance.Scegli(
        $"{TotaleDaFare:N0} da fare · pagina {PaginaDaFare}/{PagineDaFare}",
        $"{TotaleDaFare:N0} to do · page {PaginaDaFare}/{PagineDaFare}",
        $"{TotaleDaFare:N0} à faire · page {PaginaDaFare}/{PagineDaFare}");
    public string TestoPaginaFatti => LocalizationService.Instance.Scegli(
        $"{TotaleFatti:N0} fatti · pagina {PaginaFatti}/{PagineFatti}",
        $"{TotaleFatti:N0} done · page {PaginaFatti}/{PagineFatti}",
        $"{TotaleFatti:N0} terminés · page {PaginaFatti}/{PagineFatti}");

    /// <summary>
    /// Ritorna i servizi scalabili solo se l'indice e' sano, ricreandoli quando la generazione e' cambiata.
    /// Le connessioni e le cache di pagina della generazione precedente puntano a un file che dopo una
    /// ricostruzione non esiste piu': tenerle sarebbe il modo piu' rapido per restare degradati per sempre.
    /// </summary>
    private LocalTestArchiveQueryService? GetScalableArchiveQuery()
    {
        var coordinatore = _state.CartellaValida ? _state.TestCases.IndexRecovery : null;
        if (coordinatore != null && _statoIndiceCollegato != coordinatore)
        {
            // Cambio cartella condivisa: il coordinatore precedente va scollegato, altrimenti a ogni
            // ricreazione del repository si accumulano handler sullo stesso ViewModel.
            if (_statoIndiceCollegato != null) _statoIndiceCollegato.StatoCambiato -= OnStatoIndiceCambiato;
            coordinatore.StatoCambiato += OnStatoIndiceCambiato;
            _statoIndiceCollegato = coordinatore;
        }

        if (!_state.CartellaValida || !_state.TestCases.HasPersistentSnapshot ||
            string.IsNullOrWhiteSpace(_state.TestCases.LocalIndexDatabasePath))
        {
            // Degradato o in ricostruzione: percorso lento, ma NON si butta via la generazione osservata,
            // cosi' al ritorno in salute i servizi vengono ricreati sulla nuova.
            return null;
        }

        var generazione = _state.TestCases.LocalIndexGeneration;
        if (_homeArchiveQuery != null && _generazioneIndiceUsata == generazione) return _homeArchiveQuery;

        try
        {
            _homeArchiveQuery = new LocalTestArchiveQueryService(_state.TestCases.LocalIndexDatabasePath!, coordinatore);
            _homeDashboardQuery = new LocalHomeDashboardQueryService(_state.TestCases.LocalIndexDatabasePath!, coordinatore);
            _generazioneIndiceUsata = generazione;
            return _homeArchiveQuery;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Read-model scalabile Home", ex);
            _homeArchiveQuery = null;
            _homeDashboardQuery = null;
            _generazioneIndiceUsata = -1;
            return null;
        }
    }

    /// <summary>
    /// Unico punto in cui la Home esegue una lettura indicizzata. Classificazione, recupero raggruppato e
    /// l'unico retry consentito stanno nel coordinatore: nel ViewModel non deve esistere alcun retry
    /// ricorsivo, e un catch generico non deve piu' produrre un fallback invisibile e permanente.
    /// </summary>
    private bool EseguiSuIndice(string operazione, Action lettura)
    {
        var coordinatore = _state.CartellaValida ? _state.TestCases.IndexRecovery : null;
        var ok = TentaIndice(coordinatore, operazione, lettura);
        if (!ok && coordinatore != null) AggiornaAvvisoIndice();
        return ok;
    }

    // ------------------------------------------------ avviso non modale sull'indice

    private LocalIndexRecoveryCoordinator? _statoIndiceCollegato;
    private long _generazioneIndiceUsata = -1;
    private LocalIndexHealth _saluteIndice = LocalIndexHealth.Healthy;
    private bool _ricostruzioneFallita;
    private bool _ricostruzioneCompletataDaNotificare;

    /// <summary>Visibile solo quando c'e' qualcosa di realmente nuovo da dire: niente notifiche duplicate.</summary>
    public bool MostraAvvisoIndice =>
        _saluteIndice != LocalIndexHealth.Healthy || _ricostruzioneCompletataDaNotificare;

    public bool PuoRiprovareIndice => _saluteIndice == LocalIndexHealth.Unavailable;

    public string AvvisoIndiceTesto
    {
        get
        {
            var loc = LocalizationService.Instance;
            if (_saluteIndice == LocalIndexHealth.Recovering)
                return loc.Traduci("HomeIndiceRicostruzione");
            if (_saluteIndice == LocalIndexHealth.Unavailable)
            {
                var motivo = _state.TestCases.IndexRecovery?.Motivo ?? loc.Traduci("HomeIndiceMotivoSconosciuto");
                var prefisso = loc.Traduci(_ricostruzioneFallita ? "HomeIndiceRicostruzioneFallita" : "HomeIndiceNonAggiornato");
                return string.Format(loc.Traduci("HomeIndiceNonDisponibileFmt"), prefisso, motivo);
            }
            return loc.Traduci("HomeIndiceRicostruito");
        }
    }

    private void OnStatoIndiceCambiato(LocalIndexStatus stato)
    {
        void Applica()
        {
            var eraDegradato = _saluteIndice != LocalIndexHealth.Healthy;
            _ricostruzioneCompletataDaNotificare = eraDegradato && stato.Salute == LocalIndexHealth.Healthy;
            _saluteIndice = stato.Salute;
            _ricostruzioneFallita = stato.UltimaRicostruzioneFallita;
            NotificaAvvisoIndice();
        }

        // L'evento arriva dal thread del recupero: le notifiche di binding devono restare sul thread UI.
        try
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Applica();
            else Avalonia.Threading.Dispatcher.UIThread.Post(Applica);
        }
        catch { Applica(); }
    }

    private void AggiornaAvvisoIndice()
    {
        var coordinatore = _state.CartellaValida ? _state.TestCases.IndexRecovery : null;
        if (coordinatore == null) return;
        var stato = coordinatore.Stato;
        var eraDegradato = _saluteIndice != LocalIndexHealth.Healthy;
        _ricostruzioneCompletataDaNotificare = eraDegradato && stato.Salute == LocalIndexHealth.Healthy;
        _saluteIndice = stato.Salute;
        _ricostruzioneFallita = stato.UltimaRicostruzioneFallita;
        NotificaAvvisoIndice();
    }

    private void NotificaAvvisoIndice()
    {
        OnPropertyChanged(nameof(MostraAvvisoIndice));
        OnPropertyChanged(nameof(PuoRiprovareIndice));
        OnPropertyChanged(nameof(AvvisoIndiceTesto));
    }

    /// <summary>Riprova esplicita dell'utente. Condivide il single flight: non genera una seconda ricostruzione.</summary>
    [RelayCommand]
    private async Task RiprovaIndice()
    {
        var coordinatore = _state.CartellaValida ? _state.TestCases.IndexRecovery : null;
        if (coordinatore == null) return;
        await coordinatore.RichiediRecupero("Ricostruzione richiesta dall'utente");
        AggiornaAvvisoIndice();
        _homeArchiveQuery = null;
        _homeDashboardQuery = null;
        _generazioneIndiceUsata = -1;
    }

    /// <summary>Ritorna true quando tutti i KPI header sono stati risolti senza GetAll().</summary>
    private bool TryAggiornaKpiTestataScalabile(bool testata, bool copertura)
    {
        // 6.7.1: lo Stato % filtrato (operatore, TIME, tipo di Test, ...) non si legge dalle fonti dell'indice: va sul percorso in memoria.
        if (testata && ModalitaPercentualeConformita != 1 && FiltroStatoConformita().HaFiltriAttivi) return false;
        var query = GetScalableArchiveQuery();
        if (query == null) return false;
        return EseguiSuIndice("KPI scalabili Home", () =>
        {
            if (testata)
            {
                PerfContatori.IncrementaHomeLetturaSommario();
                var summary = query.GetHomeSummary(_state.Config.OperatoreAttivo);
                TotaleTestCase = summary.TotalTests;
                DaRitestare = summary.ToRetest;
                MieiAttivi = summary.MyActive;
                ImpostaDistribuzioneConformita(ModalitaPercentualeConformita == 1
                    ? summary.TestStatusDistribution
                    : HomeConformitySummaryService.CalcolaStatoCorrente(query.GetHomeFunctionalitySources(),
                        _state.Master.Veicoli.Load(), _state.Master.Funzionalita.Load(), _state.Master.Componente.Load(),
                        _state.Master.StatiFunzionalitaEcuVeicolo.Load()));
                TotaleNcAperte = _state.NonConformita.CountByState(StatoNonConformita.Aperta);
                ChangeRequestAperte = ContaChangeRequestAperte();
                DaClassificare = _state.Segnalazioni.CountByState(StatoSegnalazione.DaClassificare);
                TestSenzaEcu = summary.WithoutEcu;
            }
            if (copertura)
            {
                PerfContatori.IncrementaHomeLetturaCopertura();
                CoperturaRequisitiPercentuale = query.GetRequirementCoverage(
                    _state.Master.Requisiti.Load(), soloBanco: false, soloVeicolo: false).PercentualeCompletamento;
            }
            NotificaKpiTestata();
        });
    }

    private void NotificaKpiTestata()
    {
        OnPropertyChanged(nameof(TotaleTestCase));
        OnPropertyChanged(nameof(TotaleNcAperte));
        OnPropertyChanged(nameof(DaClassificare));
        OnPropertyChanged(nameof(DaRitestare));
        OnPropertyChanged(nameof(MieiAttivi));
        OnPropertyChanged(nameof(CoperturaRequisitiPercentuale));
        OnPropertyChanged(nameof(TassoConformitaPercentuale));
        OnPropertyChanged(nameof(TestoTassoConformita));
        OnPropertyChanged(nameof(RiepilogoTesto));
        OnPropertyChanged(nameof(TestSenzaEcu));
        OnPropertyChanged(nameof(MostraAvvisoTestSenzaEcu));
        OnPropertyChanged(nameof(TestSenzaEcuTesto));
        OnPropertyChanged(nameof(MostraSezioneAvvisi));
        OnPropertyChanged(nameof(ChangeRequestAperte));
    }

    /// <summary>
    /// Da fare/Fatti sono due viste operative, non un export: a scala elevata mostrano 100 righe per pagina.
    /// La ricerca testuale usa le stesse condizioni di Corrisponde (note e deviazioni incluse, proiettate nell'indice
    /// v13) con funzioni SQL esatte, quindi resta paginata anche con testo.
    /// </summary>
    private bool TryAggiornaDaFareEFattiScalabile()
    {
        var query = GetScalableArchiveQuery();
        if (query == null || !PercorsoIndicizzatoAttivo) return false;
        // Ricerca testuale con le stesse condizioni di Corrisponde, tradotte in funzioni SQL esatte (OrdinalIgnoreCase).
        // Resta il percorso in memoria solo per la ricerca di "?": NomeTestTemplate restituisce "?" per i template
        // sconosciuti, quindi corrisponderebbero Test che la query per Uid non rappresenta.
        var testoHome = string.IsNullOrWhiteSpace(_testoRicercaApplicato) ? null : _testoRicercaApplicato;
        if (testoHome != null && "?".Contains(testoHome, StringComparison.OrdinalIgnoreCase)) return false;
        var templateTesto = testoHome == null ? null : TemplateUidConNomeContenente(testoHome);
        return EseguiSuIndice("Da fare/Fatti scalabili Home", () =>
        {
            PerfContatori.IncrementaHomeLetturaListe();
            var allowedVehicles = VeicoliConsentitiHome();
            var assignee = SoloMiei ? _state.Config.OperatoreAttivo : null;
            var activeStates = new[]
            {
                StatoLavoroTest.Pianificato,
                StatoLavoroTest.InCorso,
                StatoLavoroTest.DaRivedere,
                StatoLavoroTest.InAttesa,
                StatoLavoroTest.DaRitestare
            };

            var todo = query.Query(new LocalTestArchiveQuery
            {
                WorkStates = activeStates,
                Assignee = assignee,
                AllowedVehicles = allowedVehicles,
                TestoHome = testoHome,
                TemplateUidTestoHome = templateTesto,
                Page = PaginaDaFare,
                PageSize = HomeListPageSize
            });
            var done = query.Query(new LocalTestArchiveQuery
            {
                WorkState = StatoLavoroTest.Concluso,
                Assignee = assignee,
                AllowedVehicles = allowedVehicles,
                TestoHome = testoHome,
                TemplateUidTestoHome = templateTesto,
                Page = PaginaFatti,
                PageSize = HomeListPageSize
            });

            PaginaDaFare = todo.Page;
            PaginaFatti = done.Page;
            TotaleDaFare = todo.TotalCount;
            TotaleFatti = done.TotalCount;
            DaFare.ReplaceWith(todo.Items);
            Fatti.ReplaceWith(done.Items);
            NotificaPagineHome();
        });
    }

    /// <summary>Uid dei template il cui nome contiene il testo, dalla stessa istantanea dei nomi usata da Corrisponde.</summary>
    private IReadOnlyCollection<Guid> TemplateUidConNomeContenente(string testo) =>
        _state.NomiTestTemplateCorrenti
            .Where(voce => voce.Value.Contains(testo, StringComparison.OrdinalIgnoreCase))
            .Select(voce => voce.Key)
            .ToList();

    private IReadOnlyCollection<string>? VeicoliConsentitiHome()
    {
        var op = _state.OperatoreCorrente;
        // RuoloEffettivo (piano 6.4.2, §4b): mai il Ruolo grezzo per decidere lo scoping per Gamma.
        if (op == null || op.RuoloEffettivo != RuoloOperatore.Standard || op.GammeAssegnate.Count == 0) return null;
        var gamme = op.GammeAssegnate.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _state.Master.Veicoli.Load().Where(v => gamme.Contains(v.GammaNome)).Select(v => v.Nome).ToList();
    }

    [RelayCommand]
    private void PaginaDaFarePrecedente()
    {
        if (!PuoPaginaDaFarePrecedente) return;
        PaginaDaFare--;
        _ = TryAggiornaDaFareEFattiScalabile();
    }

    [RelayCommand]
    private void PaginaDaFareSuccessiva()
    {
        if (!PuoPaginaDaFareSuccessiva) return;
        PaginaDaFare++;
        _ = TryAggiornaDaFareEFattiScalabile();
    }

    [RelayCommand]
    private void PaginaFattiPrecedente()
    {
        if (!PuoPaginaFattiPrecedente) return;
        PaginaFatti--;
        _ = TryAggiornaDaFareEFattiScalabile();
    }

    [RelayCommand]
    private void PaginaFattiSuccessiva()
    {
        if (!PuoPaginaFattiSuccessiva) return;
        PaginaFatti++;
        _ = TryAggiornaDaFareEFattiScalabile();
    }

    private void ResetPagineHome()
    {
        PaginaDaFare = 1;
        PaginaFatti = 1;
    }

    private void NotificaPagineHome()
    {
        OnPropertyChanged(nameof(TotaleDaFare));
        OnPropertyChanged(nameof(TotaleFatti));
        OnPropertyChanged(nameof(PagineDaFare));
        OnPropertyChanged(nameof(PagineFatti));
        OnPropertyChanged(nameof(PuoPaginaDaFarePrecedente));
        OnPropertyChanged(nameof(PuoPaginaDaFareSuccessiva));
        OnPropertyChanged(nameof(PuoPaginaFattiPrecedente));
        OnPropertyChanged(nameof(PuoPaginaFattiSuccessiva));
        OnPropertyChanged(nameof(TestoPaginaDaFare));
        OnPropertyChanged(nameof(TestoPaginaFatti));
    }
}


