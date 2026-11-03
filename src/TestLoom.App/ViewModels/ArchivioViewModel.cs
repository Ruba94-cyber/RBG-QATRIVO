using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Una riga archiviata di qualunque tipo (master data o TestCase): Ripristina/EliminaDefinitivamente
/// sono chiusure già legate all'istanza/oggetto giusti, così ArchivioViewModel non deve conoscere i tipi
/// concreti caso per caso. Ritornano null se l'azione è riuscita, altrimenti il messaggio d'errore da mostrare.</summary>
public sealed class VoceArchivio
{
    /// <summary>Identità tecnica stabile usata dai batch per non ritentare all'infinito una riga bloccata.</summary>
    public required string IdTecnico { get; init; }
    public required string TipoEntita { get; init; }
    public required string Etichetta { get; init; }
    public DateTime UltimaModifica { get; init; }
    public string ModificatoDa { get; init; } = "";
    /// <summary>Stato leggibile della voce (solo per i tipi che ne hanno uno, es. le NC); vuoto altrimenti.</summary>
    public string Stato { get; init; } = "";
    /// <summary>False se per questo tipo l'Archivio offre solo l'eliminazione definitiva (le NC: nessun ripristino da UI).</summary>
    public bool RipristinoDisponibile { get; init; } = true;
    public required Func<Task<string?>> Ripristina { get; init; }
    public required Func<Task<string?>> EliminaDefinitivamente { get; init; }
    /// <summary>Le eliminazioni master aggiornano ObservableCollection e proprietà degli editor; vanno eseguite sul dispatcher UI.</summary>
    public bool EliminazioneRichiedeDispatcherUi { get; init; }
    /// <summary>Testo di avviso (non bloccante) da confermare esplicitamente prima di EliminaDefinitivamente,
    /// o null se non serve (es. un Test citato da Non Conformita/Segnalazioni: le NC non vengono mai cancellate).</summary>
    public Func<string?>? AvvisoEliminazione { get; init; }
    /// <summary>Chiavi di localizzazione di titolo/pulsante della conferma e del motivo di salto nello svuotamento
    /// massivo; null = quelle dei Test citati da NC.</summary>
    public string? ChiaveTitoloConferma { get; init; }
    public string? ChiaveBottoneConferma { get; init; }
    public string? ChiaveMotivoSaltoSvuotamento { get; init; }
}

/// <summary>Archivio unificato. La consultazione è globalmente paginata: totali e offset sono a 64 bit,
/// mentre le singole finestre restano piccole e bounded. Anche "Svuota archivio completo" percorre le
/// sorgenti a batch e non materializza l'archivio intero.</summary>
public partial class ArchivioViewModel : ViewModelBase, IDisposable
{
    private const int DimensionePaginaArchivio = 250;
    private const int DimensioneBatchSvuotamento = 100;
    private readonly AppState _state;
    private bool _disposeRequested;
    private bool _childrenDisposed;

    private readonly RequisitiViewModel _requisiti;
    private readonly CategoriaViewModel _categorie;
    private readonly FunzionalitaViewModel _funzionalita;
    private readonly EcuViewModel _ecu;
    private readonly GammaViewModel _gamma;
    private readonly VeicoliViewModel _veicoli;
    private readonly StrumentiViewModel _strumenti;
    private readonly TestTemplateViewModel _testTemplate;
    private readonly CampoSistemaDefinizioneViewModel _campiPianificazione;
    private readonly CampoSistemaDefinizioneViewModel _campiDettagli;
    private readonly OperatoriViewModel _operatori;

    public ObservableCollection<VoceArchivio> Voci { get; } = new();
    public bool Vuoto => TotaleVoci == 0;
    public bool PuoModificare => _state.EMaster && _state.PuoScrivere;
    /// <summary>"Svuota archivio" compare solo se c'e' qualcosa da svuotare e l'utente puo' farlo (nascosto, non disabilitato).</summary>
    public bool MostraSvuota => !Vuoto && PuoModificare;
    public bool MostraAvanzamentoSvuotamento => IsBusy && !string.IsNullOrWhiteSpace(ProgressoSvuotamento);

    [ObservableProperty] private long paginaCorrente = 1;
    [ObservableProperty] private long totaleVoci;
    public long TotalePagine => Math.Max(1L, checked((TotaleVoci + DimensionePaginaArchivio - 1L) / DimensionePaginaArchivio));
    public bool PaginaPrecedenteDisponibile => PaginaCorrente > 1 && !IsBusy;
    public bool PaginaSuccessivaDisponibile => PaginaCorrente < TotalePagine && !IsBusy;
    public bool MostraPaginazione => TotaleVoci > DimensionePaginaArchivio;
    public string RiepilogoPaginazione
    {
        get
        {
            if (TotaleVoci == 0) return "0";
            var primo = checked((PaginaCorrente - 1L) * DimensionePaginaArchivio + 1L);
            var ultimo = Math.Min(TotaleVoci, primo + Voci.Count - 1L);
            return $"{primo:N0}–{ultimo:N0} / {TotaleVoci:N0}";
        }
    }

    [ObservableProperty] private string? messaggioErrore;
    public bool HaMessaggioErrore => !string.IsNullOrEmpty(MessaggioErrore);
    partial void OnMessaggioErroreChanged(string? value) => OnPropertyChanged(nameof(HaMessaggioErrore));

    [ObservableProperty] private string? messaggio;
    public bool HaMessaggio => !string.IsNullOrEmpty(Messaggio);
    partial void OnMessaggioChanged(string? value) => OnPropertyChanged(nameof(HaMessaggio));

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool svuotamentoIndeterminato;
    [ObservableProperty] private long svuotamentoCompletati;
    [ObservableProperty] private long svuotamentoTotale;
    [ObservableProperty] private string? progressoSvuotamento;

    // Gate usati dai test per osservare in modo deterministico lo stato busy e il progresso senza dipendere
    // dalla velocita' del disco. In produzione restano null e non aggiungono lavoro al percorso.
    internal Action? GatePrimaDiSvuotarePerTest { get; set; }
    internal Action? GateDopoPrimoElementoSvuotatoPerTest { get; set; }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(MostraAvanzamentoSvuotamento));
        OnPropertyChanged(nameof(MostraSvuota));
        OnPropertyChanged(nameof(PaginaPrecedenteDisponibile));
        OnPropertyChanged(nameof(PaginaSuccessivaDisponibile));
        PaginaPrecedenteCommand.NotifyCanExecuteChanged();
        PaginaSuccessivaCommand.NotifyCanExecuteChanged();
        RipristinaVoceCommand.NotifyCanExecuteChanged();
        EliminaVoceDefinitivamenteCommand.NotifyCanExecuteChanged();
        SvuotaArchivioCommand.NotifyCanExecuteChanged();

        if (value) return;
        SvuotamentoIndeterminato = false;
        SvuotamentoCompletati = 0;
        SvuotamentoTotale = 0;
        ProgressoSvuotamento = null;
    }

    partial void OnProgressoSvuotamentoChanged(string? value) =>
        OnPropertyChanged(nameof(MostraAvanzamentoSvuotamento));

    [RelayCommand]
    private void CancellaMessaggio() => Messaggio = null;

    public ArchivioViewModel(AppState state)
    {
        _state = state;
        // 11 sotto-editor master data, ciascuno con un proprio Ricarica()->Load() Phase D: senza uno scope
        // accorpato (vedi MasterLetturaScope, gia' usato da Home/Report/Audit/Anomalie-CR) ognuno riscansiona
        // da zero la stessa cartella journal condivisa e riscrive le meta SQLite, anche a contenuto invariato
        // (causa condivisa individuata in fase di profilazione 6.4.0 — stessa firma di TestCaseEditorViewModel).
        using var letturaMaster = MasterLetturaScope.Apri();
        _requisiti = new RequisitiViewModel(state);
        _categorie = new CategoriaViewModel(state);
        _funzionalita = new FunzionalitaViewModel(state);
        _ecu = new EcuViewModel(state);
        _gamma = new GammaViewModel(state);
        _veicoli = new VeicoliViewModel(state);
        _strumenti = new StrumentiViewModel(state);
        _testTemplate = new TestTemplateViewModel(state);
        _campiPianificazione = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Pianificazione);
        _campiDettagli = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        _operatori = new OperatoriViewModel(state);
        // contenutoFresco:true SOLO qui: i sotto-editor sopra sono appena stati costruiti e il loro stesso
        // costruttore ha gia' riallineato ciascuna fonte un istante fa. Qualunque Ricarica() successiva
        // (paginazione, dopo un Ripristina/Elimina, Svuota archivio) puo' invece avvenire a distanza di tempo
        // o dopo una mutazione reale e deve tornare a riallinearsi per non mostrare uno stato ormai stantio.
        Ricarica(reimpostaPagina: true, contenutoFresco: true);
    }

    public void Dispose()
    {
        if (IsBusy)
        {
            // Un'operazione confermata resta atomica per record anche se si cambia schermata: le sorgenti
            // private restano vive finche' il worker non rilascia i lock delle singole scritture.
            _disposeRequested = true;
            return;
        }
        DisposeSorgentiArchivio();
    }

    private void DisposeSorgentiArchivio()
    {
        if (_childrenDisposed) return;
        _childrenDisposed = true;
        _requisiti.Dispose(); _categorie.Dispose(); _funzionalita.Dispose(); _ecu.Dispose(); _gamma.Dispose();
        _veicoli.Dispose(); _strumenti.Dispose(); _testTemplate.Dispose(); _campiPianificazione.Dispose();
        _campiDettagli.Dispose(); _operatori.Dispose();
    }

    [RelayCommand(CanExecute = nameof(PuoNavigareArchivio))]
    private void PaginaPrecedente()
    {
        if (!PaginaPrecedenteDisponibile) return;
        PaginaCorrente--;
        Ricarica();
    }

    [RelayCommand(CanExecute = nameof(PuoNavigareArchivio))]
    private void PaginaSuccessiva()
    {
        if (!PaginaSuccessivaDisponibile) return;
        PaginaCorrente++;
        Ricarica();
    }

    private bool PuoNavigareArchivio() => !IsBusy;

    private void Ricarica(bool reimpostaPagina = false, bool contenutoFresco = false)
    {
        if (reimpostaPagina) PaginaCorrente = 1;
        ApplicaPaginaArchivio(CreaPaginaArchivio(PaginaCorrente, contenutoFresco));
    }

    private async Task RicaricaSenzaBloccareUiAsync()
    {
        // L'ultimo refresh puo' riallineare undici cataloghi master. Tenerlo fuori dalla UI evita un singolo
        // congelamento al termine di uno svuotamento lungo.
        var pagina = await Task.Run(() => CreaPaginaArchivio(1, contenutoFresco: false));
        ApplicaPaginaArchivio(pagina);
    }

    private ArchivioPaginaSnapshot CreaPaginaArchivio(long paginaRichiesta, bool contenutoFresco)
    {
        if (!_state.CartellaValida)
            return new ArchivioPaginaSnapshot(0, 1, Array.Empty<VoceArchivio>(), null);

        // Stessa ragione del costruttore: una rivisita paginata interroga di nuovo gli 11 cataloghi master.
        using var letturaMaster = MasterLetturaScope.Apri();
        try
        {
            var sources = CreaSorgentiArchivio(refresh: !contenutoFresco);
            var totale = SommaConteggi(sources);
            var pagina = Math.Clamp(paginaRichiesta, 1L, Math.Max(1L, checked((totale + DimensionePaginaArchivio - 1L) / DimensionePaginaArchivio)));

            var offsetGlobale = checked((pagina - 1L) * DimensionePaginaArchivio);
            var rimanenti = DimensionePaginaArchivio;
            long cursor = 0;
            var voci = new List<VoceArchivio>(DimensionePaginaArchivio);
            foreach (var source in sources)
            {
                if (rimanenti <= 0) break;
                if (source.Count <= 0) continue;

                var fine = checked(cursor + source.Count);
                if (offsetGlobale >= fine)
                {
                    cursor = fine;
                    continue;
                }

                var offsetLocale = Math.Max(0L, offsetGlobale - cursor);
                var disponibili = source.Count - offsetLocale;
                var take = (int)Math.Min((long)rimanenti, disponibili);
                if (take > 0)
                {
                    voci.AddRange(source.Load(offsetLocale, take));
                    rimanenti -= take;
                }
                cursor = fine;
            }
            return new ArchivioPaginaSnapshot(totale, pagina, voci, null);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Archivio unificato: caricamento pagina", ex);
            return new ArchivioPaginaSnapshot(
                0,
                1,
                Array.Empty<VoceArchivio>(),
                string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message));
        }
    }

    private void ApplicaPaginaArchivio(ArchivioPaginaSnapshot pagina)
    {
        Voci.Clear();
        foreach (var voce in pagina.Voci) Voci.Add(voce);
        TotaleVoci = pagina.Totale;
        PaginaCorrente = pagina.Pagina;
        MessaggioErrore = pagina.Errore;
        NotificaPaginazione();
    }

    private static long SommaConteggi(IEnumerable<ArchiveSource> sources)
    {
        long total = 0;
        foreach (var source in sources) total = checked(total + source.Count);
        return total;
    }

    private List<ArchiveSource> CreaSorgentiArchivio(bool refresh = true)
    {
        var loc = LocalizationService.Instance;
        var result = new List<ArchiveSource>();
        AggiungiSorgenteMaster(result, _requisiti, loc.Traduci("RequisitiTitolo"), r => r.Id, refresh);
        AggiungiSorgenteMaster(result, _categorie, loc.Traduci("CategorieTitolo"), c => c.Nome, refresh);
        AggiungiSorgenteMaster(result, _funzionalita, loc.Traduci("FunzTitolo"), f => f.Nome, refresh);
        AggiungiSorgenteMaster(result, _ecu, loc.Traduci("EcuTitolo"), e => e.Nome, refresh);
        AggiungiSorgenteMaster(result, _gamma, loc.Traduci("GammaTitolo"), g => g.Nome, refresh);
        AggiungiSorgenteMaster(result, _veicoli, loc.Traduci("VeicoliTitolo"), v => v.Nome, refresh);
        AggiungiSorgenteMaster(result, _strumenti, loc.Traduci("StrumentiTitolo"), s => s.Nome, refresh);
        AggiungiSorgenteMaster(result, _testTemplate, loc.Traduci("TplTitolo"), t => t.Nome, refresh);
        AggiungiSorgenteMaster(result, _campiPianificazione, _campiPianificazione.Titolo, c => c.Etichetta, refresh);
        AggiungiSorgenteMaster(result, _campiDettagli, _campiDettagli.Titolo, c => c.Etichetta, refresh);
        AggiungiSorgenteMaster(result, _operatori, loc.Traduci("OperatoriTitolo"), o => o.Nome, refresh);

        var testQuery = CreaQueryTestScalabile();
        var coordinatore = _state.TestCases.IndexRecovery;
        var query = new LocalTestArchiveQuery { DeletionScope = LocalTestDeletionScope.DeletedOnly };
        if (testQuery != null && coordinatore != null &&
            coordinatore.TryRead("Conteggio Test archiviati", () => testQuery.QuerySlice(query, 0, 1).TotalCount, out long testCount))
        {
            result.Add(new ArchiveSource(testCount, (offset, take) =>
            {
                // Una corruzione puo' emergere solo al caricamento della pagina, dopo che il conteggio era
                // riuscito: anche questo accesso deve segnalare il guasto al coordinatore e usare i JSON.
                if (coordinatore.TryRead("Pagina Test archiviati", () => testQuery.QuerySlice(query, offset, take),
                        out LocalTestArchiveSlice pagina))
                    return pagina.Items.Select(CreaVoceTest).ToList();
                return SkipLong(_state.TestCases.GetAll(includeEliminati: true).Where(t => t.Eliminato), offset)
                    .Take(take).Select(CreaVoceTest).ToList();
            }));
        }
        else
        {
            var tests = _state.TestCases.GetAll(includeEliminati: true);
            var totaleTestLegacy = tests.LongCount(t => t.Eliminato);
            result.Add(new ArchiveSource(totaleTestLegacy, (offset, take) => SkipLong(tests.Where(t => t.Eliminato), offset)
                .Take(take).Select(CreaVoceTest).ToList()));
        }

        var crCount = _state.ChangeRequests.QueryArchivedSlice(0, 1).TotalCount;
        result.Add(new ArchiveSource(crCount, (offset, take) =>
            _state.ChangeRequests.QueryArchivedSlice(offset, take).Items.Select(CreaVoceCr).ToList()));

        var ncCount = _state.NonConformita.ContaArchiviate();
        result.Add(new ArchiveSource(ncCount, (offset, take) =>
            _state.NonConformita.QueryArchivedSlice(offset, take).Items.Select(CreaVoceNc).ToList()));
        return result;
    }

    private LocalTestArchiveQueryService? CreaQueryTestScalabile()
    {
        if (!_state.TestCases.HasPersistentSnapshot || string.IsNullOrWhiteSpace(_state.TestCases.LocalIndexDatabasePath)) return null;
        try { return new LocalTestArchiveQueryService(_state.TestCases.LocalIndexDatabasePath!, _state.TestCases.IndexRecovery); }
        catch (Exception ex)
        {
            CrashLogService.Registra("Archivio Test: apertura read-model SQLite", ex);
            return null;
        }
    }

    private void AggiungiSorgenteMaster<T>(
        ICollection<ArchiveSource> sources,
        MasterDataEditorViewModelBase<T> vm,
        string tipoEntita,
        Func<T, string> etichetta,
        bool refresh = true)
        where T : MasterEntity, new()
    {
        // refresh:false (solo dalla primissima Ricarica del costruttore, vedi Ricarica/contenutoFresco): il
        // costruttore di vm (RequisitiViewModel/FunzionalitaViewModel/ecc., appena istanziato in
        // ArchivioViewModel) ha gia' riallineato la fonte un istante prima nel suo stesso Ricarica() — senza
        // questo, ogni apertura dell'Archivio pagava la stessa riconciliazione dell'intero catalogo fino a 3
        // volte per catalogo, anche a Archivio vuoto (feedback utente). Qualunque Ricarica() successiva torna
        // a riallinearsi normalmente, perche' nel frattempo puo' essere avvenuta una mutazione reale.
        var count = vm.ContaElementiArchivio(refresh);
        sources.Add(new ArchiveSource(count, (offset, take) =>
        {
            var slice = vm.CaricaPaginaElementiArchivio(offset, take, refresh);
            return slice.Items.Select(item => CreaVoceMaster(vm, tipoEntita, etichetta, item)).ToList();
        }));
    }

    private static VoceArchivio CreaVoceMaster<T>(
        MasterDataEditorViewModelBase<T> vm,
        string tipoEntita,
        Func<T, string> etichetta,
        T item)
        where T : MasterEntity, new()
    {
        var chiave = vm.ChiaveArchivio(item);
        return new VoceArchivio
        {
            IdTecnico = $"master:{typeof(T).FullName}:{chiave}",
            TipoEntita = tipoEntita,
            Etichetta = etichetta(item),
            UltimaModifica = item.UltimaModifica,
            ModificatoDa = item.ModificatoDa,
            Ripristina = () => vm.RipristinaDaArchivio(chiave),
            EliminaDefinitivamente = () => vm.EliminaDefinitivamenteDaArchivio(chiave),
            EliminazioneRichiedeDispatcherUi = true,
        };
    }

    private VoceArchivio CreaVoceTest(TestCase tc)
    {
        var loc = LocalizationService.Instance;
        return new VoceArchivio
        {
            IdTecnico = tc.Uid != Guid.Empty ? $"test:{tc.Uid:N}" : $"test-id:{tc.Id}",
            TipoEntita = loc.Traduci("ArchivioTipoTest"),
            Etichetta = tc.Id,
            UltimaModifica = tc.DataEliminazione ?? tc.UltimaModifica,
            ModificatoDa = tc.ModificatoDa,
            Ripristina = () => Task.FromResult(RipristinaTest(tc)),
            EliminaDefinitivamente = () => Task.FromResult(EliminaTestDefinitivamente(tc)),
            AvvisoEliminazione = () => NcAgganciTestService.AvvisoEliminazioneDefinitiva(
                tc.Id, _state.NonConformita.PeekAllReadOnly(), _state.Segnalazioni.GetAll(), tc.Uid),
        };
    }

    private VoceArchivio CreaVoceCr(ChangeRequest cr) => new()
    {
        IdTecnico = cr.Uid != Guid.Empty ? $"cr:{cr.Uid:N}" : $"cr-id:{cr.Id}",
        TipoEntita = "Change Request",
        Etichetta = $"{cr.Id} - {cr.Titolo}",
        UltimaModifica = cr.DataArchiviazione ?? cr.UltimaModifica,
        ModificatoDa = cr.Changelog.LastOrDefault()?.Operatore ?? "",
        Ripristina = () => Task.FromResult(RipristinaCr(cr)),
        EliminaDefinitivamente = () => Task.FromResult(EliminaCrDefinitivamente(cr)),
    };

    private VoceArchivio CreaVoceNc(NonConformita nc)
    {
        var titolo = !string.IsNullOrWhiteSpace(nc.DescrizioneBreveSegnalata) ? nc.DescrizioneBreveSegnalata
            : (nc.Descrizione ?? "").Split('\n', 2)[0].Trim();
        if (titolo.Length > 120) titolo = titolo[..120] + "…";
        if (titolo.Length == 0) titolo = nc.TestCaseId;
        return new VoceArchivio
        {
            IdTecnico = nc.Uid != Guid.Empty ? $"nc:{nc.Uid:N}" : $"nc-id:{nc.Id}",
            TipoEntita = LocalizationService.Instance.Traduci("ArchivioTipoNc"),
            Etichetta = titolo.Length == 0 ? nc.Id : $"{nc.Id} - {titolo}",
            Stato = nc.StatoWorkflow.Label(),
            UltimaModifica = nc.DataArchiviazione ?? nc.DataChiusura ?? nc.DataApertura,
            ModificatoDa = nc.Changelog.LastOrDefault()?.Operatore ?? "",
            RipristinoDisponibile = false,
            Ripristina = () => Task.FromResult<string?>(LocalizationService.Instance.Traduci("ArchivioNcRipristinoNonDisponibile")),
            EliminaDefinitivamente = () => Task.FromResult(EliminaNcDefinitivamente(nc)),
            // Irreversibile (solo il cestino di recupero tecnico): conferma esplicita sempre, e mai nello svuotamento massivo.
            AvvisoEliminazione = () => string.Format(LocalizationService.Instance.Traduci("ArchivioEliminaNcAvvisoFmt"), nc.Id),
            ChiaveTitoloConferma = "ArchivioEliminaNcTitolo",
            ChiaveBottoneConferma = "ArchivioEliminaNcConferma",
            ChiaveMotivoSaltoSvuotamento = "ArchivioEliminaNcSvuotaSaltato",
        };
    }

    private string? EliminaNcDefinitivamente(NonConformita nc)
    {
        try
        {
            // Il ruolo/lo stato condiviso vengono rivalutati qui, dopo la conferma: la UI puo' essere rimasta aperta.
            if (!PuoModificare) return LocalizationService.Instance.Traduci("ArchivioAzioneNonAutorizzata");
            // Stesso percorso di "Elimina" delle Anomalie: transazione, scollegamento delle segnalazioni,
            // journal + cestino di recupero e rimozione dall'indice nel repository; rollback e riallineamento su errore.
            AnomaliaAzioniService.Elimina(_state, nc);
            return null;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra($"Archivio: eliminazione definitiva NC {nc.Id}", ex);
            return string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
        }
    }

    private void NotificaPaginazione()
    {
        OnPropertyChanged(nameof(Vuoto));
        OnPropertyChanged(nameof(MostraSvuota));
        OnPropertyChanged(nameof(TotalePagine));
        OnPropertyChanged(nameof(PaginaPrecedenteDisponibile));
        OnPropertyChanged(nameof(PaginaSuccessivaDisponibile));
        OnPropertyChanged(nameof(MostraPaginazione));
        OnPropertyChanged(nameof(RiepilogoPaginazione));
        SvuotaArchivioCommand.NotifyCanExecuteChanged();
    }

    private string? RipristinaTest(TestCase tc)
    {
        try { _state.TestCases.Restore(tc, _state.Config.OperatoreAttivo); }
        catch (Exception ex) { return string.Format(LocalizationService.Instance.Traduci("RipristinoNonRiuscitoFmt"), ex.Message); }
        _state.RicaricaTutto();
        return null;
    }

    private string? EliminaTestDefinitivamente(TestCase tc)
    {
        try
        {
            var query = CreaQueryTestScalabile();
            var coordinatore = _state.TestCases.IndexRecovery;
            var indicizzato = false;
            var letto = query != null && coordinatore != null &&
                coordinatore.TryRead("Referenze Test archiviato", () => query.IsReferencedByAnotherTest(tc.Id), out indicizzato);
            var referenziato = letto ? indicizzato : _state.TestCases.GetAll(includeEliminati: true)
                .Any(t => t.Id != tc.Id && (t.TestCorrelati.Contains(tc.Id) || t.TestCollegatoRichiestoId == tc.Id));
            if (referenziato) return LocalizationService.Instance.Traduci("MasterDataEliminaReferenziata");
            _state.TestCases.EliminaDefinitivamente(tc);
            return null;
        }
        catch (Exception ex)
        {
            return string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
        }
    }

    private string? RipristinaCr(ChangeRequest cr)
    {
        try { _state.ChangeRequests.Ripristina(cr, _state.Config.OperatoreAttivo); }
        catch (Exception ex) { return string.Format(LocalizationService.Instance.Traduci("RipristinoNonRiuscitoFmt"), ex.Message); }
        _state.RicaricaTutto();
        return null;
    }

    private string? EliminaCrDefinitivamente(ChangeRequest cr)
    {
        try
        {
            // La parte CR->CR è paginata; la parte NC verrà instradata sullo stesso entity-index nel successivo
            // step del refactor, mantenendo fin d'ora invariata la regola di integrità referenziale.
            var referenziata = _state.NonConformita.GetAll().Any(n => n.ChangeRequestIds.Contains(cr.Id, StringComparer.OrdinalIgnoreCase))
                || _state.ChangeRequests.ReferenziataDaAltraCr(cr.Id);
            if (referenziata) return LocalizationService.Instance.Traduci("MasterDataEliminaReferenziata");
            _state.ChangeRequests.EliminaDefinitivamente(cr, _state.Config.OperatoreAttivo);
            return null;
        }
        catch (Exception ex)
        {
            return string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(PuoEseguireAzioneArchivio))]
    private Task RipristinaVoce(VoceArchivio? voce) =>
        ComandoAsincrono.EseguiAsync(
            () => RipristinaVoceCoreAsync(voce), "Ripristino dell'archivio", m => MessaggioErrore = m);

    private async Task RipristinaVoceCoreAsync(VoceArchivio? voce)
    {
        if (voce == null || IsBusy) return;
        if (!PuoModificare)
        {
            ImpostaEsito(LocalizationService.Instance.Traduci("ArchivioAzioneNonAutorizzata"));
            return;
        }
        IsBusy = true;
        try
        {
            var esito = await voce.Ripristina();
            Ricarica();
            ImpostaEsito(esito);
        }
        finally
        {
            IsBusy = false;
            if (_disposeRequested) DisposeSorgentiArchivio();
        }
    }

    [RelayCommand(CanExecute = nameof(PuoEseguireAzioneArchivio))]
    private Task EliminaVoceDefinitivamente(VoceArchivio? voce) =>
        ComandoAsincrono.EseguiAsync(() => EliminaVoceDefinitivamenteCoreAsync(voce), "Eliminazione definitiva dell'archivio", m => MessaggioErrore = m);

    private bool PuoEseguireAzioneArchivio(VoceArchivio? voce) => voce != null && PuoModificare && !IsBusy;

    private async Task EliminaVoceDefinitivamenteCoreAsync(VoceArchivio? voce)
    {
        if (voce == null || IsBusy) return;
        if (!PuoModificare)
        {
            ImpostaEsito(LocalizationService.Instance.Traduci("ArchivioAzioneNonAutorizzata"));
            return;
        }
        IsBusy = true;
        try
        {
            var avviso = voce.AvvisoEliminazione?.Invoke();
            if (avviso != null &&
                !await ConfirmService.Chiedi(
                    LocalizationService.Instance.Traduci(voce.ChiaveTitoloConferma ?? "EliminaTestConNcTitolo"), avviso,
                    LocalizationService.Instance.Traduci(voce.ChiaveBottoneConferma ?? "EliminaTestConNcConferma"), LocalizationService.Instance.Traduci("Annulla")))
                return;
            if (!PuoModificare) return;
            var esito = await voce.EliminaDefinitivamente();
            Ricarica();
            ImpostaEsito(esito);
        }
        finally
        {
            IsBusy = false;
            if (_disposeRequested) DisposeSorgentiArchivio();
        }
    }

    [RelayCommand(CanExecute = nameof(PuoEseguireSvuotamentoArchivio))]
    private Task SvuotaArchivio() =>
        ComandoAsincrono.EseguiAsync(SvuotaArchivioCoreAsync, "Svuotamento dell'archivio", m => MessaggioErrore = m);

    private bool PuoEseguireSvuotamentoArchivio() => PuoModificare && !IsBusy && TotaleVoci > 0;

    private async Task SvuotaArchivioCoreAsync()
    {
        if (!PuoEseguireSvuotamentoArchivio()) return;
        if (!await ConfirmService.Chiedi(
                LocalizationService.Instance.Traduci("SvuotaArchivioTitoloConferma"),
                string.Format(LocalizationService.Instance.Traduci("SvuotaArchivioMessaggioConfermaFmt"), TotaleVoci),
                LocalizationService.Instance.Traduci("SvuotaArchivioBtn"), LocalizationService.Instance.Traduci("Annulla")))
            return;
        if (!PuoEseguireSvuotamentoArchivio()) return;

        IsBusy = true;
        MessaggioErrore = null;
        Messaggio = null;
        SvuotamentoIndeterminato = true;
        ProgressoSvuotamento = $"{LocalizationService.Instance.Traduci("SvuotaArchivioBtn")}…";
        var progresso = new ProgressoDispatcherUi<BulkDeleteProgress>(AggiornaProgressoSvuotamento);
        try
        {
            var risultato = await Task.Run(() => EseguiSvuotamentoArchivio(progresso));
            await RicaricaSenzaBloccareUiAsync();

            if (risultato.AutorizzazioneRevocata)
                MessaggioErrore = LocalizationService.Instance.Traduci("ArchivioAzioneNonAutorizzata");
            else if (risultato.ErroreFatale != null)
                MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), risultato.ErroreFatale);

            if (!risultato.AutorizzazioneRevocata && risultato.ErroreFatale == null)
            {
                Messaggio = risultato.Falliti == 0 || risultato.MotiviFallimento.Count == 0
                    ? string.Format(LocalizationService.Instance.Traduci("ArchivioSvuotatoFmt"), risultato.Riusciti, risultato.Falliti)
                    : string.Format(LocalizationService.Instance.Traduci("ArchivioSvuotatoConMotiviFmt"), risultato.Riusciti, risultato.Falliti, string.Join(" · ", risultato.MotiviFallimento));
            }
        }
        finally
        {
            IsBusy = false;
            if (_disposeRequested) DisposeSorgentiArchivio();
        }
    }

    private async Task<BulkDeleteResult> EseguiSvuotamentoArchivio(IProgress<BulkDeleteProgress> progresso)
    {
        long riusciti = 0;
        long falliti = 0;
        long elaborati = 0;
        long totale = 0;
        var fallitiIds = new HashSet<string>(StringComparer.Ordinal);
        var motiviFalliti = new List<string>();
        string? erroreFatale = null;
        string voceCorrente = string.Empty;
        long ultimoConteggioSegnalato = 0;

        try
        {
            GatePrimaDiSvuotarePerTest?.Invoke();
            var sources = CreaSorgentiArchivio();
            totale = SommaConteggi(sources);
            progresso.Report(new BulkDeleteProgress(elaborati, totale, string.Empty));
            var cronometroProgresso = Stopwatch.StartNew();
            var ultimoAggiornamento = TimeSpan.Zero;

            foreach (var source in sources)
            {
                long offset = 0;
                while (PuoModificare)
                {
                    var batch = source.Load(offset, DimensioneBatchSvuotamento);
                    if (batch.Count == 0) break;

                    var bloccatiNelBatch = 0;
                    foreach (var voce in batch)
                    {
                        if (fallitiIds.Contains(voce.IdTecnico))
                        {
                            bloccatiNelBatch++;
                            continue;
                        }

                        string? errore;
                        try
                        {
                            // Un Test citato da NC/Segnalazioni richiede conferma esplicita per singolo Test: lo
                            // svuotamento massivo lo salta (nessuna NC resta orfana senza una scelta consapevole).
                            errore = voce.AvvisoEliminazione?.Invoke() != null
                                ? LocalizationService.Instance.Traduci(voce.ChiaveMotivoSaltoSvuotamento ?? "EliminaTestConNcSvuotaSaltato")
                                : await EliminaVoceInBackgroundAsync(voce);
                        }
                        catch (Exception ex)
                        {
                            CrashLogService.Registra($"Svuota archivio: {voce.TipoEntita}/{voce.Etichetta}", ex);
                            errore = ex.Message;
                        }

                        if (errore == null) riusciti++;
                        else
                        {
                            fallitiIds.Add(voce.IdTecnico);
                            falliti++;
                            bloccatiNelBatch++;
                            if (!motiviFalliti.Contains(errore, StringComparer.Ordinal)) motiviFalliti.Add(errore);
                        }

                        elaborati++;
                        voceCorrente = $"{voce.TipoEntita}: {voce.Etichetta}";
                        if (elaborati == 1 || elaborati % 10 == 0 || cronometroProgresso.Elapsed - ultimoAggiornamento >= TimeSpan.FromMilliseconds(200))
                        {
                            progresso.Report(new BulkDeleteProgress(elaborati, totale, voceCorrente));
                            ultimoConteggioSegnalato = elaborati;
                            ultimoAggiornamento = cronometroProgresso.Elapsed;
                        }
                        if (elaborati == 1) GateDopoPrimoElementoSvuotatoPerTest?.Invoke();
                    }

                    offset = checked(offset + bloccatiNelBatch);
                    if (bloccatiNelBatch == batch.Count && batch.Count < DimensioneBatchSvuotamento) break;
                }
            }

            if (elaborati != ultimoConteggioSegnalato)
                progresso.Report(new BulkDeleteProgress(elaborati, totale, voceCorrente));
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Svuota archivio: batch bounded", ex);
            erroreFatale = ex.Message;
        }

        return new BulkDeleteResult(riusciti, falliti, motiviFalliti, erroreFatale, !PuoModificare);
    }

    private void AggiornaProgressoSvuotamento(BulkDeleteProgress progresso)
    {
        SvuotamentoCompletati = progresso.Elaborati;
        SvuotamentoTotale = progresso.Totale;
        SvuotamentoIndeterminato = progresso.Totale <= 0;
        ProgressoSvuotamento = string.Format(
            LocalizationService.Instance.Traduci("ArchivioSvuotamentoAvanzamentoFmt"),
            progresso.Elaborati,
            progresso.Totale,
            progresso.VoceCorrente);
    }

    internal static async Task<string?> EliminaVoceInBackgroundAsync(VoceArchivio voce)
    {
        if (!voce.EliminazioneRichiedeDispatcherUi || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            return await voce.EliminaDefinitivamente();

        // EseguiAzioneArchivioPerChiave carica/aggiorna Items, ItemsAttivi e proprietà dell'editor, poi chiama
        // Ricarica nel finally. Il commit master completo va quindi sul dispatcher per ogni tipo, non solo per
        // le definizioni CampoSistema che notificano anche AppState.DatiRicaricati.
        return await Avalonia.Threading.Dispatcher.UIThread
            .InvokeAsync(() => voce.EliminaDefinitivamente())
            .ConfigureAwait(false);
    }

    private sealed class ProgressoDispatcherUi<T>(Action<T> applica) : IProgress<T>
    {
        public void Report(T value)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                applica(value);
                return;
            }

            // Il worker aspetta l'applicazione del progresso: le notifiche dei binding restano sul dispatcher
            // e l'ordine non può invertirsi rispetto alla conclusione dell'operazione.
            Avalonia.Threading.Dispatcher.UIThread
                .InvokeAsync(() => applica(value))
                .GetTask()
                .GetAwaiter()
                .GetResult();
        }
    }

    private sealed record BulkDeleteProgress(long Elaborati, long Totale, string VoceCorrente);

    private sealed record BulkDeleteResult(
        long Riusciti,
        long Falliti,
        IReadOnlyList<string> MotiviFallimento,
        string? ErroreFatale,
        bool AutorizzazioneRevocata);

    private sealed record ArchivioPaginaSnapshot(
        long Totale,
        long Pagina,
        IReadOnlyList<VoceArchivio> Voci,
        string? Errore);

    private static IEnumerable<TItem> SkipLong<TItem>(IEnumerable<TItem> source, long count)
    {
        using var e = source.GetEnumerator();
        while (count > 0 && e.MoveNext()) count--;
        while (e.MoveNext()) yield return e.Current;
    }

    private void ImpostaEsito(string? errore)
    {
        if (errore != null) { MessaggioErrore = errore; Messaggio = null; }
        else { MessaggioErrore = null; Messaggio = LocalizationService.Instance.Traduci("ArchivioAzioneEseguitaMsg"); }
    }

    private sealed record ArchiveSource(long Count, Func<long, int, IReadOnlyList<VoceArchivio>> Load);
}
