using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Dettaglio guasto di un requisito Extra nella sezione Extra dell'Audit (6.5.0, nessuna pagina dedicata): drill-down dal
/// requisito alla lista VIRTUALIZZATA (100k guasto) con stato Da fare/Validato/Completo/Non conforme, conflitti, passo
/// mancante, ultimo test e data, navigazione al test. Filtri principali sempre visibili (ricerca guasto, "solo non
/// completi"), altri nel pannello collassato (componente, stato); valori mantenuti per PC (ViewStateService, mai nel file
/// condiviso). Calcolo fuori dal thread UI (DtcAvanzamentoProvider).</summary>
public partial class AuditViewModel
{
    private const string ChDtcTesto = "Audit.Guasto.Testo", ChDtcSolo = "Audit.Guasto.SoloNonCompleti", ChDtcEcu = "Audit.Guasto.Componente", ChDtcStato = "Audit.Guasto.Stato";
    private List<DtcDettaglioRiga> _dtcTutte = new();
    private CancellationTokenSource? _dtcDettaglioCts;
    private CancellationTokenSource? _dtcTestoCts;
    private bool _dtcEcuLegato;
    private Action<string?>? _navigaATest;

    public BulkObservableCollection<DtcDettaglioRiga> DtcRighe { get; } = new();
    public FiltroMultiSelect FiltroDtcEcu { get; } = new();
    public FiltroMultiSelect FiltroDtcStato { get; } = new();

    private string? _dtcRequisitoId;
    public string? DtcRequisitoId { get => _dtcRequisitoId; private set { if (SetProperty(ref _dtcRequisitoId, value)) OnPropertyChanged(nameof(MostraDettaglioDtc)); } }
    public bool MostraDettaglioDtc => !string.IsNullOrEmpty(DtcRequisitoId);

    private string _dtcTitolo = "";
    public string DtcTitolo { get => _dtcTitolo; private set => SetProperty(ref _dtcTitolo, value); }
    private string _dtcRiepilogo = "";
    public string DtcRiepilogo { get => _dtcRiepilogo; private set => SetProperty(ref _dtcRiepilogo, value); }
    private bool _dtcInCalcolo;
    public bool DtcInCalcolo { get => _dtcInCalcolo; private set => SetProperty(ref _dtcInCalcolo, value); }

    private string _dtcFiltroTesto = "";
    public string DtcFiltroTesto
    {
        get => _dtcFiltroTesto;
        set
        {
            if (!SetProperty(ref _dtcFiltroTesto, value ?? "")) return;
            OnPropertyChanged(nameof(HaFiltriDtcAttivi));
            ApplicaFiltriDtc();
            _dtcTestoCts?.Cancel();
            var cts = _dtcTestoCts = new CancellationTokenSource();
            var v = _dtcFiltroTesto;
            _ = Task.Delay(700, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) ViewStateService.Scrivi(_state, ChDtcTesto, string.IsNullOrWhiteSpace(v) ? null : v);
            }, TaskScheduler.Default);
        }
    }

    private bool _dtcSoloNonCompleti;
    public bool DtcSoloNonCompleti
    {
        get => _dtcSoloNonCompleti;
        set
        {
            if (!SetProperty(ref _dtcSoloNonCompleti, value)) return;
            ViewStateService.Scrivi(_state, ChDtcSolo, value ? "1" : null);
            OnPropertyChanged(nameof(HaFiltriDtcAttivi));
            ApplicaFiltriDtc();
        }
    }

    public bool HaFiltriDtcAttivi => !string.IsNullOrWhiteSpace(DtcFiltroTesto) || DtcSoloNonCompleti || FiltroDtcEcu.HaSelezione || FiltroDtcStato.HaSelezione;
    public string AltriFiltriDtcTesto => string.Format(LocalizationService.Instance.Traduci("AltriFiltriFmt"),
        (FiltriPrincipaliConfig.EPrincipale(FiltriPrincipaliConfig.PaginaAuditDtc, FiltriPrincipaliConfig.Componente) ? 0 : (FiltroDtcEcu.HaSelezione ? 1 : 0))
        + (FiltriPrincipaliConfig.EPrincipale(FiltriPrincipaliConfig.PaginaAuditDtc, FiltriPrincipaliConfig.StatoDtc) ? 0 : (FiltroDtcStato.HaSelezione ? 1 : 0)));

    private void InizializzaDettaglioDtc(Action<string?>? navigaATest)
    {
        _navigaATest = navigaATest;
        FiltroDtcStato.Popola(new[] { ("DaFare", TestLoom.App.Services.LocalizationService.Instance.Traduci("DtcStatoDaFareTesto")), ("Validato", TestLoom.App.Services.LocalizationService.Instance.Traduci("DtcStatoValidatoTesto")), ("Completo", TestLoom.App.Services.LocalizationService.Instance.Traduci("DtcStatoCompletoTesto")), ("NonConforme", TestLoom.App.Services.LocalizationService.Instance.Traduci("DtcStatoNonConformeTesto")) });
        try
        {
            _dtcFiltroTesto = ViewStateService.Leggi(_state, ChDtcTesto) ?? "";
            _dtcSoloNonCompleti = ViewStateService.Leggi(_state, ChDtcSolo) == "1";
        }
        catch { _dtcFiltroTesto = ""; _dtcSoloNonCompleti = false; }
        FiltroDtcStato.LegaPersistenza(_state, ChDtcStato);
        FiltroDtcEcu.Cambiato += OnFiltroDtcCambiato;
        FiltroDtcStato.Cambiato += OnFiltroDtcCambiato;
    }

    private void OnFiltroDtcCambiato()
    {
        OnPropertyChanged(nameof(HaFiltriDtcAttivi));
        OnPropertyChanged(nameof(AltriFiltriDtcTesto));
        ApplicaFiltriDtc();
    }

    /// <summary>"Azzera": svuota tutti i filtri guasto E la loro persistenza.</summary>
    public void AzzeraFiltriDtc()
    {
        DtcFiltroTesto = ""; DtcSoloNonCompleti = false;
        FiltroDtcEcu.DeselezionaTutto(); FiltroDtcStato.DeselezionaTutto();
        ViewStateService.Scrivi(_state, ChDtcTesto, null);
    }

    public void ChiudiDettaglioDtc() { _dtcDettaglioCts?.Cancel(); DtcRequisitoId = null; DtcRighe.ReplaceWith(Array.Empty<DtcDettaglioRiga>()); }

    /// <summary>Drill-down dal requisito Extra: carica in background il dettaglio guasto di quel requisito.</summary>
    [RelayCommand]
    private Task ApriDettaglioDtc(string? requisitoId) => CaricaDettaglioDtcAsync(requisitoId);

    public async Task CaricaDettaglioDtcAsync(string? requisitoId)
    {
        _dtcDettaglioCts?.Cancel();
        if (string.IsNullOrWhiteSpace(requisitoId) || !_state.CartellaValida) { ChiudiDettaglioDtc(); return; }
        var cts = _dtcDettaglioCts = new CancellationTokenSource();
        DtcRequisitoId = requisitoId;
        DtcTitolo = $"Guasto · {requisitoId}";
        DtcRiepilogo = "";
        DtcInCalcolo = true;
        try
        {
            var risultato = await Task.Run(() =>
            {
                var req = _state.Master.Requisiti.Load().FirstOrDefault(r => r.Id.Equals(requisitoId, StringComparison.OrdinalIgnoreCase) && RequisitoApplicabilita.ED4D(r));
                return req == null ? DtcAvanzamentoRisultato.Vuoto : DtcAvanzamentoProvider.Calcola(_state, new[] { req }, requisitoId, cts.Token);
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            _dtcTutte = risultato.PerDtc.Select(a => new DtcDettaglioRiga(a)).ToList();
            var nonAllineate = _dtcTutte.Count(r => r.NonAllineata);
            DtcRiepilogo = string.Format(LocalizationService.Instance.Traduci("DtcRiepilogoReqFmt"), risultato.Totale.Completi, risultato.Totale.Totale)
                + (risultato.PerRequisito.FirstOrDefault() is { Coperto: true } ? " ✓" : "")
                + (nonAllineate > 0 ? " · " + string.Format(LocalizationService.Instance.Traduci("DtcNonAllineateFmt"), nonAllineate) : ""); // voci con veicolo non piu dichiarato
            FiltroDtcEcu.Ripopola(_dtcTutte.Select(r => r.Componente).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, NaturalStringComparer.Instance).Select(x => (x, x)));
            if (!_dtcEcuLegato && _dtcTutte.Count > 0) { _dtcEcuLegato = true; FiltroDtcEcu.LegaPersistenza(_state, ChDtcEcu); }
            ApplicaFiltriDtc();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CrashLogService.Registra("Dettaglio guasto Audit", ex); }
        finally { if (!cts.IsCancellationRequested) DtcInCalcolo = false; }
    }

    private void ApplicaFiltriDtc()
    {
        IEnumerable<DtcDettaglioRiga> q = _dtcTutte;
        var f = (DtcFiltroTesto ?? "").Trim();
        if (f.Length > 0) q = q.Where(r => r.Guasto.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Veicolo.Contains(f, StringComparison.OrdinalIgnoreCase));
        var componente = FiltroDtcEcu.Selezionati;
        if (componente.Count > 0) { var s = new HashSet<string>(componente, StringComparer.OrdinalIgnoreCase); q = q.Where(r => s.Contains(r.Componente)); }
        var stati = FiltroDtcStato.Selezionati;
        if (stati.Count > 0) { var s = new HashSet<string>(stati, StringComparer.Ordinal); q = q.Where(r => s.Contains(r.Stato.ToString())); }
        if (DtcSoloNonCompleti) q = q.Where(r => r.Stato != StatoDtc.Completo);
        DtcRighe.ReplaceWith(q);
        DtcTitolo = DtcRequisitoId == null ? "" : $"Guasto · {DtcRequisitoId} ({DtcRighe.Count}/{_dtcTutte.Count})";
    }

    [RelayCommand]
    private void ApriTestDtc(string? testId)
    {
        if (!string.IsNullOrWhiteSpace(testId)) _navigaATest?.Invoke(testId);
    }

    /// <summary>Esporta tutte le righe del drill-down correntemente filtrate, anche oltre l'eventuale pagina visibile,
    /// su thread di background con Annulla: il CSV compare solo a scrittura completa, mai un file parziale.</summary>
    public async Task EsportaDettaglioDtcCsv(string percorso)
    {
        if (!_state.CartellaValida || EsportazioneInCorso || string.IsNullOrWhiteSpace(DtcRequisitoId)) return;
        // DtcRighe rappresenta l'intero insieme filtrato, non la porzione virtualizzata attualmente renderizzata.
        // Catturarlo prima dell'await mantiene coerente l'export se l'operatore cambia filtro mentre viene scritto.
        var righe = DtcRighe.ToList();
        await EseguiEsportazione(percorso, (temporaneo, ct) => AuditReportService.ScriviDettaglioDtcCsv(righe, temporaneo, ct),
            registraCronologia: false);
    }
}
