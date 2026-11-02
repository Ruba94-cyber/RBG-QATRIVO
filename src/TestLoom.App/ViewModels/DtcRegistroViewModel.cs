using System.Collections.ObjectModel;
using ClosedXML.Excel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

public sealed record DtcRegistroVeicoloFiltro(string Etichetta, string? Valore);
public sealed record DtcRegistroPassoFiltro(string Etichetta, PassoDtc? Valore);

public sealed record DtcRegistroDettaglio(
    string Strumento,
    string StrumentiTest,
    string Nota,
    string Deviazione,
    string Problema,
    string MotivazioneNonApplicabile);

/// <summary>Registro consultabile dello storico guasto per veicolo. La pagina legge righe paginabili dall'indice locale
/// e carica i testi di problema dal Test autorevole solo per la selezione corrente e per l'export.</summary>
public partial class DtcRegistroViewModel : ViewModelBase, IPaginaAggiornabile, IDisposable
{
    private const int DimensionePagina = 250;
    private readonly AppState _state;
    private readonly Action<string?> _apriTest;
    private readonly DtcRegistroAssociazioneService _associazione;
    private readonly DtcAssociazioneFlusso _flusso;
    private readonly EsportazioneInBackground _esportazione = new();
    private CancellationTokenSource? _caricamentoCts;
    private LocalDtcRegistroQueryService? _query;
    private long _generazioneIndice = -1;
    private int _generazioneCaricamento;
    private bool _sospendiFiltri;
    private bool _disposed;

    public ObservableCollection<RigaDtcRegistro> Righe { get; } = new();
    public ObservableCollection<DtcRegistroVeicoloFiltro> VeicoliFiltro { get; } = new();
    public ObservableCollection<DtcRegistroPassoFiltro> PassiFiltro { get; } = new();

    [ObservableProperty] private string filtroTesto = "";
    [ObservableProperty] private bool soloProblemi;
    [ObservableProperty] private bool soloSenzaRequisito;
    [ObservableProperty] private DtcRegistroVeicoloFiltro? veicoloSelezionato;
    [ObservableProperty] private DtcRegistroPassoFiltro? passoSelezionato;
    [ObservableProperty] private RigaDtcRegistro? selezionata;
    [ObservableProperty] private DtcRegistroDettaglio? dettaglio;
    [ObservableProperty] private bool caricamento;
    [ObservableProperty] private long totaleRisultati;
    [ObservableProperty] private int paginaCorrente = 1;
    [ObservableProperty] private string? messaggioErrore;
    [ObservableProperty] private string? messaggioExport;
    [ObservableProperty] private bool esportazioneInCorso;
    [ObservableProperty] private string? messaggioAssociazione;
    [ObservableProperty] private bool associazioneInCorso;

    public bool HaDettaglio => Selezionata != null;
    public int PagineTotali => (int)Math.Max(1, Math.Ceiling(TotaleRisultati / (double)DimensionePagina));
    public bool PuoPaginaPrecedente => PaginaCorrente > 1;
    public bool PuoPaginaSuccessiva => PaginaCorrente < PagineTotali;
    public bool MostraPaginazione => TotaleRisultati > DimensionePagina;
    public bool PuoEsportare => TotaleRisultati > 0 && !Caricamento && !EsportazioneInCorso;
    public bool PuoAssociareRequisito => _state.PuoScrivere && !AssociazioneInCorso && RigaCorrenteSelezionata();
    public bool MostraAvvisoRevisioneStorica => _state.PuoScrivere && RigaSenzaRequisitoSelezionata()
        && Selezionata != null && !RigaCorrenteSelezionata();
    public string TestoAssociaRequisito => LocalizationService.Instance.Scegli(
        "Associa a requisito Extra", "Assign to Extra requirement", "Associer à une exigence Extra");
    public string TestoAvvisoRevisioneStorica => LocalizationService.Instance.Scegli(
        "Questa riga appartiene a una REV storica e resta in sola lettura. Seleziona la riga della REV corrente per associarla.",
        "This row belongs to a historical revision and is read-only. Select the current revision row to assign it.",
        "Cette ligne appartient à une révision historique et reste en lecture seule. Sélectionnez la ligne de la révision courante pour l’associer.");
    public string? MessaggioPagina => MessaggioAssociazione ?? MessaggioExport;
    public bool HaFiltriAttivi => !string.IsNullOrWhiteSpace(FiltroTesto) || VeicoloSelezionato?.Valore != null
        || PassoSelezionato?.Valore != null || SoloProblemi || SoloSenzaRequisito;
    public string TestoTotale => string.Format(LocalizationService.Instance.Traduci("DtcRegistroTotaleFmt"), TotaleRisultati);

    public DtcRegistroViewModel(AppState state, Action<string?>? apriTest = null)
    {
        _state = state;
        _apriTest = apriTest ?? (_ => { });
        _associazione = new DtcRegistroAssociazioneService(state);
        _flusso = new DtcAssociazioneFlusso(state);
        RicreaPassiFiltro(null);
        PopolaFiltroVeicoli(Array.Empty<string>());
        LocalizationService.Instance.PropertyChanged += OnLinguaCambiata;
        Aggiorna();
    }

    public void Aggiorna()
    {
        if (_disposed) return;
        // L'indice viene aggiornato anche senza una nuova generazione di recovery: rilegge le opzioni
        // veicolo a ogni refresh, così i guasti aggiunti dall'ultima visita diventano subito filtrabili.
        _ = CaricaPaginaAsync(caricaVeicoli: true);
    }

    partial void OnFiltroTestoChanged(string value) => CambiaFiltro();
    partial void OnSoloProblemiChanged(bool value) => CambiaFiltro();
    partial void OnSoloSenzaRequisitoChanged(bool value) => CambiaFiltro();
    partial void OnVeicoloSelezionatoChanged(DtcRegistroVeicoloFiltro? value) => CambiaFiltro();
    partial void OnPassoSelezionatoChanged(DtcRegistroPassoFiltro? value) => CambiaFiltro();
    partial void OnSelezionataChanged(RigaDtcRegistro? value)
    {
        OnPropertyChanged(nameof(HaDettaglio));
        OnPropertyChanged(nameof(PuoAssociareRequisito));
        OnPropertyChanged(nameof(MostraAvvisoRevisioneStorica));
        if (value != null) MessaggioAssociazione = null;
        Dettaglio = RisolviDettaglio(value);
    }
    partial void OnAssociazioneInCorsoChanged(bool value)
    {
        OnPropertyChanged(nameof(PuoAssociareRequisito));
        OnPropertyChanged(nameof(PuoAssociareSelezionate));
        OnPropertyChanged(nameof(PuoAssociareDaRequisito));
    }
    partial void OnMessaggioAssociazioneChanged(string? value) => OnPropertyChanged(nameof(MessaggioPagina));
    partial void OnMessaggioExportChanged(string? value) => OnPropertyChanged(nameof(MessaggioPagina));
    partial void OnTotaleRisultatiChanged(long value)
    {
        OnPropertyChanged(nameof(PagineTotali));
        OnPropertyChanged(nameof(PuoPaginaSuccessiva));
        OnPropertyChanged(nameof(MostraPaginazione));
        OnPropertyChanged(nameof(TestoTotale));
        OnPropertyChanged(nameof(PuoEsportare));
    }
    partial void OnPaginaCorrenteChanged(int value)
    {
        OnPropertyChanged(nameof(PuoPaginaPrecedente));
        OnPropertyChanged(nameof(PuoPaginaSuccessiva));
        _ = CaricaPaginaAsync();
    }
    partial void OnCaricamentoChanged(bool value) => OnPropertyChanged(nameof(PuoEsportare));
    partial void OnEsportazioneInCorsoChanged(bool value) => OnPropertyChanged(nameof(PuoEsportare));

    private void CambiaFiltro()
    {
        if (_sospendiFiltri) return;
        OnPropertyChanged(nameof(HaFiltriAttivi));
        if (PaginaCorrente == 1) _ = CaricaPaginaAsync();
        else PaginaCorrente = 1;
    }

    [RelayCommand]
    private void PaginaPrecedente()
    {
        if (PuoPaginaPrecedente) PaginaCorrente--;
    }

    [RelayCommand]
    private void PaginaSuccessiva()
    {
        if (PuoPaginaSuccessiva) PaginaCorrente++;
    }

    [RelayCommand]
    private void AzzeraFiltri()
    {
        _sospendiFiltri = true;
        try
        {
            FiltroTesto = "";
            SoloProblemi = false;
            SoloSenzaRequisito = false;
            VeicoloSelezionato = VeicoliFiltro.FirstOrDefault();
            PassoSelezionato = PassiFiltro.FirstOrDefault();
        }
        finally { _sospendiFiltri = false; }
        CambiaFiltro();
    }

    [RelayCommand]
    private void ChiudiDettaglio() => Selezionata = null;

    [RelayCommand]
    private void ApriTest(string? testId)
    {
        if (!string.IsNullOrWhiteSpace(testId)) _apriTest(testId);
    }

    [RelayCommand]
    private async Task AssociaRequisito()
    {
        var riga = Selezionata;
        if (riga == null || !PuoAssociareRequisito) return;
        await EseguiAssociazioneAsync(() => _flusso.AssociaRigheAsync(new[] { riga }));
    }

    /// <summary>Righe selezionate nella griglia (selezione multipla): le imposta la view a ogni cambio di selezione.</summary>
    public IReadOnlyList<RigaDtcRegistro> RigheSelezionate { get; private set; } = Array.Empty<RigaDtcRegistro>();

    public void ImpostaRigheSelezionate(IEnumerable<RigaDtcRegistro> righe)
    {
        RigheSelezionate = righe.ToList();
        OnPropertyChanged(nameof(PuoAssociareSelezionate));
        OnPropertyChanged(nameof(TestoAssociaSelezionate));
    }

    private int RigheSelezionateAssociabili() => RigheSelezionate.Count(r => string.IsNullOrWhiteSpace(r.Requisito));

    public bool PuoAssociareSelezionate => _state.PuoScrivere && !AssociazioneInCorso && RigheSelezionateAssociabili() > 1;

    public string TestoAssociaSelezionate => string.Format(LocalizationService.Instance.Scegli(
        "Associa le {0} selezionate a requisito Extra", "Assign the {0} selected to Extra requirement", "Associer les {0} sélectionnées à une exigence Extra"),
        RigheSelezionateAssociabili());

    public bool PuoAssociareDaRequisito => _state.PuoScrivere && !AssociazioneInCorso;

    public string TestoAssociaDaRequisito => LocalizationService.Instance.Scegli(
        "Da requisito Extra…", "From Extra requirement…", "Depuis une exigence Extra…");

    /// <summary>Associa insieme tutte le righe selezionate (senza requisito, REV corrente) a un requisito Extra scelto.</summary>
    [RelayCommand]
    private async Task AssociaSelezionate()
    {
        var righe = RigheSelezionate.Where(r => string.IsNullOrWhiteSpace(r.Requisito)).ToList();
        if (righe.Count == 0 || !PuoAssociareSelezionate) return;
        await EseguiAssociazioneAsync(() => _flusso.AssociaRigheAsync(righe));
    }

    /// <summary>Al contrario: parte da un requisito Extra (anche senza guasto) e sceglie dalla lista quali guasto associargli.</summary>
    [RelayCommand]
    private async Task AssociaDaRequisito()
    {
        if (!PuoAssociareDaRequisito) return;
        await EseguiAssociazioneAsync(() => _flusso.DaRequisitoAsync());
    }

    private async Task EseguiAssociazioneAsync(Func<Task<string?>> operazione)
    {
        AssociazioneInCorso = true;
        MessaggioAssociazione = null;
        try
        {
            var messaggio = await operazione();
            if (messaggio == null) return;
            MessaggioAssociazione = messaggio;
            Aggiorna();
        }
        catch (DtcRegistroAssociazioneException ex)
        {
            MessaggioAssociazione = ex.Stato switch
            {
                StatoAssociazioneDtcRegistro.RigaStorica => TestoAvvisoRevisioneStorica,
                StatoAssociazioneDtcRegistro.RequisitoNonCompatibile => LocalizationService.Instance.Scegli(
                    "Il requisito scelto non contiene una tupla compatibile per tutti i componenti dell’operazione.",
                    "The selected requirement has no compatible tuple for every component in this operation.",
                    "L’exigence choisie ne contient pas de tuple compatible pour chaque componente de l’opération."),
                _ => LocalizationService.Instance.Scegli(
                    "La riga è cambiata o non è più corrente. Aggiorna il registro e riprova.",
                    "The row changed or is no longer current. Refresh the register and try again.",
                    "La ligne a changé ou n’est plus courante. Actualisez le registre et réessayez.")
            };
        }
        catch (Exception ex)
        {
            MessaggioAssociazione = string.Format(LocalizationService.Instance.Scegli(
                "Associazione non salvata: {0}", "Assignment was not saved: {0}", "Association non enregistrée : {0}"), ex.Message);
            CrashLogService.Registra("Associazione requisito dal registro guasto", ex);
        }
        finally
        {
            AssociazioneInCorso = false;
        }
    }


    private bool RigaSenzaRequisitoSelezionata() => Selezionata != null && string.IsNullOrWhiteSpace(Selezionata.Requisito);

    private bool RigaCorrenteSelezionata()
    {
        var riga = Selezionata;
        if (riga == null || !string.IsNullOrWhiteSpace(riga.Requisito) || !Guid.TryParse(riga.TestUid, out var uid)) return false;
        var test = _state.TestCases.GetByUid(uid);
        return test != null && !test.Eliminato && riga.RevisioneIndice == test.Esecuzioni.Count - 1;
    }

    private async Task CaricaPaginaAsync(bool caricaVeicoli = false)
    {
        _caricamentoCts?.Cancel();
        var generazione = ++_generazioneCaricamento;
        if (!_state.CartellaValida || !_state.TestCases.HasPersistentSnapshot
            || string.IsNullOrWhiteSpace(_state.TestCases.LocalIndexDatabasePath))
        {
            Caricamento = false;
            Righe.Clear();
            TotaleRisultati = 0;
            MessaggioErrore = LocalizationService.Instance.Traduci("DtcRegistroIndiceNonDisponibile");
            return;
        }

        var cts = _caricamentoCts = new CancellationTokenSource();
        var token = cts.Token;
        Caricamento = true;
        MessaggioErrore = null;
        MessaggioExport = null;
        Selezionata = null;
        try
        {
            var query = OttieniQuery();
            var filtro = CreaFiltro();
            var pagina = PaginaCorrente;
            var result = await Task.Run(() =>
            {
                var veicoli = caricaVeicoli ? query.LeggiVeicoli(token) : null;
                // Se il veicolo selezionato non è più nell'indice, PopolaFiltroVeicoli lo riporta a
                // "Tutti i veicoli". Usa lo stesso filtro risolto per la pagina: altrimenti la UI mostra
                // "Tutti" ma le righe restano filtrate sul veicolo eliminato.
                var filtroRisolto = filtro;
                if (veicoli != null && filtro.Veicolo != null
                    && !veicoli.Contains(filtro.Veicolo, StringComparer.OrdinalIgnoreCase))
                    filtroRisolto = filtro with { Veicolo = null };
                return (Veicoli: veicoli, Pagina: query.LeggiPagina(filtroRisolto, pagina, DimensionePagina, token));
            }, token);
            if (token.IsCancellationRequested || generazione != _generazioneCaricamento) return;
            if (result.Veicoli != null)
            {
                PopolaFiltroVeicoli(result.Veicoli);
            }
            Righe.Clear();
            foreach (var riga in result.Pagina.Righe) Righe.Add(riga);
            TotaleRisultati = result.Pagina.Totale;
            if (PaginaCorrente > PagineTotali) PaginaCorrente = PagineTotali;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generazione == _generazioneCaricamento)
            {
                MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("DtcRegistroErroreFmt"), ex.Message);
                CrashLogService.Registra("Caricamento registro guasto", ex);
            }
        }
        finally
        {
            if (generazione == _generazioneCaricamento) Caricamento = false;
        }
    }

    private LocalDtcRegistroQueryService OttieniQuery()
    {
        var generazione = _state.TestCases.LocalIndexGeneration;
        if (_query == null || _generazioneIndice != generazione)
        {
            _query = new LocalDtcRegistroQueryService(_state.TestCases.LocalIndexDatabasePath!);
            _generazioneIndice = generazione;
        }
        return _query;
    }

    private FiltroDtcRegistro CreaFiltro() => new(
        FiltroTesto,
        VeicoloSelezionato?.Valore,
        PassoSelezionato?.Valore,
        SoloProblemi,
        SoloSenzaRequisito);

    private void PopolaFiltroVeicoli(IEnumerable<string> veicoli)
    {
        var corrente = VeicoloSelezionato?.Valore;
        _sospendiFiltri = true;
        try
        {
            VeicoliFiltro.Clear();
            VeicoliFiltro.Add(new DtcRegistroVeicoloFiltro(LocalizationService.Instance.Traduci("DtcRegistroTuttiVeicoli"), null));
            foreach (var veicolo in veicoli) VeicoliFiltro.Add(new DtcRegistroVeicoloFiltro(veicolo, veicolo));
            VeicoloSelezionato = VeicoliFiltro.FirstOrDefault(v => string.Equals(v.Valore, corrente, StringComparison.OrdinalIgnoreCase))
                ?? VeicoliFiltro.FirstOrDefault();
        }
        finally { _sospendiFiltri = false; }
    }

    private void RicreaPassiFiltro(PassoDtc? corrente)
    {
        _sospendiFiltri = true;
        try
        {
            PassiFiltro.Clear();
            PassiFiltro.Add(new DtcRegistroPassoFiltro(LocalizationService.Instance.Traduci("DtcRegistroTuttiPassi"), null));
            PassiFiltro.Add(new DtcRegistroPassoFiltro(LocalizationService.Instance.Traduci("DtcRegistroValidazione"), PassoDtc.Validazione));
            PassiFiltro.Add(new DtcRegistroPassoFiltro(LocalizationService.Instance.Traduci("DtcRegistroDevalidazione"), PassoDtc.Devalidazione));
            PassoSelezionato = PassiFiltro.FirstOrDefault(p => p.Valore == corrente) ?? PassiFiltro[0];
        }
        finally { _sospendiFiltri = false; }
    }

    private DtcRegistroDettaglio? RisolviDettaglio(RigaDtcRegistro? riga)
    {
        if (riga == null || !Guid.TryParse(riga.TestUid, out var uid)) return null;
        var test = _state.TestCases.GetByUid(uid);
        var revisione = test?.Esecuzioni.ElementAtOrDefault(riga.RevisioneIndice);
        var condizione = revisione?.Condizioni.ElementAtOrDefault(riga.TentativoIndice);
        if (condizione == null) return null;
        var strumenti = !string.IsNullOrWhiteSpace(condizione.Strumento)
            ? condizione.Strumento
            : string.Join(", ", condizione.ValoriStrumenti.Keys.Where(k => !EvidenzaOperazione.EChiaveRiservata(k)).Select(k => k.Split("::", 2)[0]).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        var strumentiTest = string.Join(", ", (test?.StrumentiUsati ?? new List<string>())
            .Select(s => (s ?? "").Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        return new DtcRegistroDettaglio(
            strumenti,
            strumentiTest,
            condizione.Nota ?? "",
            condizione.DescrizioneDeviazione ?? "",
            condizione.DescrizioneProblema ?? "",
            condizione.MotivazioneNonApplicabile ?? "");
    }

    /// <summary>Colonne esportabili del registro (identificativo = chiave dell'etichetta).</summary>
    private static readonly string[] ChiaviColonneExport =
    {
        "DtcRegistroColVeicolo", "DtcRegistroColEcu", "DtcRegistroColRequisito", "DtcRegistroColAssociazione", "DtcRegistroColDtc",
        "DtcRegistroColPasso", "DtcRegistroColEsito", "DtcRegistroColTest", "DtcRegistroColRevisione",
        "DtcRegistroColData", "DtcRegistroColOperatore", "DtcRegistroColStrumento", "DtcRegistroColStrumentiTest", "DtcRegistroColNota",
        "DtcRegistroColDeviazione", "DtcRegistroColProblema", "DtcRegistroColMotivazioneNa", "DtcRegistroColTestUid"
    };

    /// <summary>Campi della pivot del registro: solo il numero di revisione e' davvero numerico.</summary>
    internal static IReadOnlyList<ExportColonna> CampiPivotRegistro() => ChiaviColonneExport
        .Select(k => new ExportColonna(k, LocalizationService.Instance.Traduci(k), k == "DtcRegistroColRevisione")).ToList();

    /// <summary>Cosa il Registro guasto puo' esportare e quali filtri si possono modificare dal dialogo (quelli a video).</summary>
    internal ExportDescrittore CreaDescrittoreExport()
    {
        var loc = LocalizationService.Instance;
        var sezioni = new List<ExportSezione>
        {
            new(DtcSezioniExport.Registro, loc.Traduci("DtcRegistroFoglioExcel"),
                ChiaviColonneExport.Select(k => new ExportColonna(k, loc.Traduci(k))).ToList()),
            new("pivot", "Pivot", PredefinitaAttiva: false),
        };
        var veicoli = VeicoliFiltro.ToList();
        var passi = PassiFiltro.ToList();
        var filtri = new List<ExportFiltro>
        {
            new ExportFiltroTesto("testo", loc.Scegli("Testo", "Text", "Texte"), () => FiltroTesto, v => FiltroTesto = v ?? ""),
            new ExportFiltroSingolo("veicolo", loc.Scegli("Veicolo", "Vehicle", "Véhicule"), veicoli.Where(v => v.Valore != null).Select(v => v.Etichetta).ToList(),
                () => VeicoloSelezionato?.Valore == null ? null : VeicoloSelezionato.Etichetta,
                v => VeicoloSelezionato = veicoli.FirstOrDefault(x => x.Valore != null && x.Etichetta == v) ?? veicoli.FirstOrDefault(x => x.Valore == null)),
            new ExportFiltroSingolo("passo", loc.Scegli("Passo", "Step", "Étape"), passi.Where(p => p.Valore != null).Select(p => p.Etichetta).ToList(),
                () => PassoSelezionato?.Valore == null ? null : PassoSelezionato.Etichetta,
                v => PassoSelezionato = passi.FirstOrDefault(x => x.Valore != null && x.Etichetta == v) ?? passi.FirstOrDefault(x => x.Valore == null)),
            new ExportFiltroBooleano("soloproblemi", loc.Scegli("Solo problemi", "Problems only", "Problèmes uniquement"), () => SoloProblemi, v => SoloProblemi = v),
            new ExportFiltroBooleano("senzarequisito", loc.Scegli("Solo senza requisito", "Without requirement only", "Sans exigence uniquement"), () => SoloSenzaRequisito, v => SoloSenzaRequisito = v),
        };
        return new ExportDescrittore("guasto-registro", loc.Traduci("ExpDlgTitoloDtc"), sezioni, "", filtri, [new ExportFontePivot("registro", loc.Traduci("DtcRegistroFoglioExcel"), CampiPivotRegistro())]);
    }

    /// <summary>Stato applicativo per il dialogo di export (salvataggio delle configurazioni per operatore).</summary>
    internal AppState StatoPerExport => _state;

    public async Task<EsitoEsportazione> EsportaExcelVerso(string percorso, ExportConfigurazione? scelta = null)
    {
        if (_disposed || EsportazioneInCorso || !_state.TestCases.HasPersistentSnapshot
            || string.IsNullOrWhiteSpace(_state.TestCases.LocalIndexDatabasePath))
            return new EsitoEsportazione(StatoEsportazione.Fallita, new InvalidOperationException(LocalizationService.Instance.Traduci("DtcRegistroIndiceNonDisponibile")));
        EsportazioneInCorso = true;
        MessaggioExport = null;
        try
        {
            var query = OttieniQuery();
            var filtro = CreaFiltro();
            var risultato = await _esportazione.EseguiAsync(percorso, (temporaneo, ct) =>
            {
                using var workbook = new XLWorkbook();
                var headers = ChiaviColonneExport;
                var indici = ExportColonneSelezione.Indici(headers, scelta?.ColonneDi(DtcSezioniExport.Registro));
                var intestazioni = ExportColonneSelezione.Estrai(headers.Select(LocalizationService.Instance.Traduci).ToArray(), indici);
                var writer = scelta == null || scelta.HaSezione(DtcSezioniExport.Registro)
                    ? new ExcelExportTableWriter(workbook, LocalizationService.Instance.Traduci("DtcRegistroFoglioExcel"), intestazioni) : null;
                var progressivo = 0;
                var righePivot = new List<IReadOnlyList<object?>>();
                foreach (var row in query.StreamRighe(filtro, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    var detail = RisolviDettaglio(row);
                    var values = new object?[]
                    {
                        row.Veicolo, row.Componente, row.Requisito, row.StatoAssociazioneTesto, row.Guasto, row.PassoTesto, row.EsitoTesto,
                        row.TestId, row.NumeroRevisione, row.Data, row.Operatore,
                        detail?.Strumento ?? "", detail?.StrumentiTest ?? "", detail?.Nota ?? "", detail?.Deviazione ?? "",
                        detail?.Problema ?? "", detail?.MotivazioneNonApplicabile ?? "", row.TestUid
                    };
                    writer?.AggiungiRiga($"{row.TestId} / {row.Guasto} / {++progressivo}", ExportColonneSelezione.Estrai(values, indici));
                    if (scelta?.HaSezione("pivot") == true) righePivot.Add(values);
                }
                writer?.Completa();
                if (scelta?.HaSezione("pivot") == true) ExportPivotService.Scrivi(workbook, CampiPivotRegistro(), righePivot, scelta, ct);
                // Il file temporaneo termina in .tmp per la scrittura atomica; ClosedXML richiede l'estensione solo
                // quando riceve un percorso, quindi scriviamo su stream e lasciamo invariato il nome temporaneo.
                using var stream = new FileStream(temporaneo, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                workbook.SaveAs(stream);
            });
            if (risultato.Stato == StatoEsportazione.Fallita)
                MessaggioExport = string.Format(LocalizationService.Instance.Traduci("DtcRegistroErroreExportFmt"), risultato.Errore?.Message ?? "");
            else if (risultato.Stato == StatoEsportazione.Completata)
                MessaggioExport = LocalizationService.Instance.Traduci("DtcRegistroExportCompletato");
            return risultato;
        }
        finally { EsportazioneInCorso = false; }
    }

    private void OnLinguaCambiata(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationService.Lingua)) return;
        var veicolo = VeicoloSelezionato?.Valore;
        var passo = PassoSelezionato?.Valore;
        _sospendiFiltri = true;
        try
        {
            PopolaFiltroVeicoli(VeicoliFiltro.Skip(1).Select(v => v.Valore!).ToArray());
            RicreaPassiFiltro(passo);
        }
        finally { _sospendiFiltri = false; }
        VeicoloSelezionato = VeicoliFiltro.FirstOrDefault(v => string.Equals(v.Valore, veicolo, StringComparison.OrdinalIgnoreCase))
            ?? VeicoliFiltro.FirstOrDefault();
        OnPropertyChanged(nameof(TestoTotale));
        OnPropertyChanged(nameof(TestoAssociaRequisito));
        OnPropertyChanged(nameof(TestoAvvisoRevisioneStorica));
        CambiaFiltro();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _caricamentoCts?.Cancel();
        _caricamentoCts?.Dispose();
        LocalizationService.Instance.PropertyChanged -= OnLinguaCambiata;
    }
}
