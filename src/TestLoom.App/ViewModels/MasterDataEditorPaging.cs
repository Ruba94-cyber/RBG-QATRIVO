using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Parte scalabile di MasterDataEditorViewModelBase. In consultazione i cataloghi configurati usano finestre
/// SQLite locali bounded. In Phase D anche la modifica resta bounded: dopo il lock si riallinea la pagina
/// corrente alla source of truth e il commit avviene come patch per UID, quindi una pagina da 200 righe non puo
/// cancellare quelle non caricate. Solo un workspace legacy non ancora migrato promuove ancora al JSON completo.
/// </summary>
public abstract partial class MasterDataEditorViewModelBase<T> where T : MasterEntity, new()
{
    private const int DimensionePaginaMaster = 200;
    private readonly MasterDataPagedReadSource<T>? _pagedRead;
    private bool _visualizzazionePaginata;
    private bool _modificaPatchBounded;
    /// <summary>Vero solo se la finestra caricata per la modifica NON copre l'intero catalogo (esiste una pagina
    /// successiva o precedente): con tutto il catalogo in una pagina l'avviso 'modifica limitata' sarebbe fuorviante.</summary>
    private bool _modificaFinestraParziale;

    [ObservableProperty] private long paginaCorrente = 1;
    [ObservableProperty] private bool paginaPrecedenteDisponibile;
    [ObservableProperty] private bool paginaSuccessivaDisponibile;

    public bool UsaPaginazione => _pagedRead != null && _visualizzazionePaginata;
    public int DimensionePagina => DimensionePaginaMaster;

    /// <summary>Salva pagina e paginazione restano disponibili durante una sessione bounded. Ogni pagina e' un
    /// commit esplicito separato; Salva finale chiude la sessione, Salva pagina mantiene lock e modificabilita'.</summary>
    public bool MostraSalvaPagina => InModifica && _modificaPatchBounded && _modificaFinestraParziale;

    /// <summary>La barra pagina/frecce si vede solo quando esiste realmente piu' di una pagina (feedback
    /// utente: un catalogo ad alta crescita con poche righe mostrava comunque i controlli, bloccati su "1" e
    /// senza nulla su cui navigare — sembrava rotto anziche' semplicemente "tutto qui"). Sotto la soglia di
    /// pagina la vista resta indistinguibile da un elenco unico come gli altri master data.</summary>
    public bool MostraControlliPaginazione => UsaPaginazione && (PaginaCorrente > 1 || PaginaSuccessivaDisponibile);

    partial void OnInModificaChanged(bool value)
    {
        OnPropertyChanged(nameof(UsaPaginazione));
        OnPropertyChanged(nameof(MostraControlliPaginazione));
        OnPropertyChanged(nameof(MostraAvvisoModificaLimitata));
        OnPropertyChanged(nameof(MostraSalvaPagina));
        OnPropertyChanged(nameof(MostraModifica));
        OnPropertyChanged(nameof(MostraModificaPerCreazione));
        NotificaStatoVuoto();
        Unione.NotificaInModificaCambiata();
        if (!value) AzzeraImport();
        if (!value)
        {
            _modificaPatchBounded = false;
            _modificaFinestraParziale = false;
            _modificaSeguitaAForzatura = false;
            _baselineSourceFingerprint = null;
        }
    }

    partial void OnPaginaCorrenteChanged(long value) => OnPropertyChanged(nameof(MostraControlliPaginazione));
    partial void OnPaginaSuccessivaDisponibileChanged(bool value) => OnPropertyChanged(nameof(MostraControlliPaginazione));

    /// <summary>Attiva la consultazione paginata solo dopo che il coordinator ha verificato/riconciliato la
    /// fonte autorevole. Se SQLite non è disponibile o l'aggiornamento fallisce, ritorna false e il chiamante
    /// ripiega sul percorso legacy: degrado prestazionale, mai degrado di integrità.</summary>
    private bool ProvaAttivaVisualizzazionePaginata(bool refreshIndice, bool reimpostaPagina)
    {
        if (_pagedRead == null) return false;
        try
        {
            if (refreshIndice) _pagedRead.Refresh();
            _visualizzazionePaginata = true;
            if (reimpostaPagina) PaginaCorrente = 1;
            _snapshot = new List<T>();
            _snapshotPerChiave.Clear();
            CaricaPaginaPaginataCore();
            OnPropertyChanged(nameof(UsaPaginazione));
            return true;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra($"Paginazione master {typeof(T).Name}: fallback compatibilita", ex);
            _visualizzazionePaginata = false;
            PaginaPrecedenteDisponibile = false;
            PaginaSuccessivaDisponibile = false;
            OnPropertyChanged(nameof(UsaPaginazione));
            return false;
        }
    }

    /// <summary>
    /// includeArchived:false (consultazione) mostra solo righe attive, comportamento storico del browsing
    /// paginato. includeArchived:true (ingresso in modifica, vedi CaricaFotografiaAutorevoleCompletaPerModifica)
    /// include invece le archiviate nella finestra: chi modifica deve poter distinguere una riga archiviata da
    /// una realmente assente, esattamente come fa il caricamento legacy completo via Items.Where(!EArchiviata).
    /// </summary>
    private void CaricaPaginaPaginataCore(bool includeArchived = false)
    {
        if (_pagedRead == null || !_visualizzazionePaginata) return;

        using var _ = SospendiStatoVuoto();
        var pagina = _pagedRead.ReadPage(_testoRicercaApplicato, PaginaCorrente, DimensionePaginaMaster, includeArchived);
        // Se una cancellazione/archiviazione remota ha reso invalida l'ultima pagina, torniamo alla prima
        // invece di mostrare una griglia vuota che farebbe pensare a un catalogo privo di dati.
        if (pagina.Items.Count == 0 && PaginaCorrente > 1)
        {
            PaginaCorrente = 1;
            pagina = _pagedRead.ReadPage(_testoRicercaApplicato, PaginaCorrente, DimensionePaginaMaster, includeArchived);
        }

        // ReplaceWith solleva un solo Reset invece di Clear()+N Add() separati: con una pagina da 200 righe
        // quella sequenza di eventi poteva lasciare la DataGrid virtualizzante disallineata sull'estensione
        // reale di scroll (feedback utente: "vedo solo una ventina di righe con scrollbar" su un catalogo
        // paginato). Fuori da modifica la query ha già escluso le archiviate, quindi Items/ItemsAttivi
        // coincidono; in modifica Items resta la finestra piena (comportamento del caricamento legacy
        // completo) e ItemsAttivi la filtra come RicalcolaItemsAttivi fa per il percorso non paginato.
        Items.ReplaceWith(pagina.Items);
        ItemsAttivi.ReplaceWith(includeArchived ? pagina.Items.Where(i => !EArchiviata(i)) : pagina.Items);
        PaginaPrecedenteDisponibile = PaginaCorrente > 1;
        PaginaSuccessivaDisponibile = pagina.HasNext;
    }

    // Ricerca dei cataloghi paginati (componente, Requisiti, Funzionalita'): la casella mostra subito il testo digitato
    // (FiltroTesto), ma la query sul read-model parte solo dopo una pausa di DebounceRicerca, come in GlobalSearch e
    // nell'elenco Test. Invio applica subito il testo corrente; svuotare il campo si applica subito. Una ricerca superata
    // da una digitazione successiva (o da Invio) viene scartata con il contatore di generazione. L'ordinamento per
    // rilevanza della query non cambia.
    private string _testoRicercaApplicato = "";
    private int _generazioneRicerca;
    private CancellationTokenSource? _ricercaCts;
    private Task _ricercaPendente = Task.CompletedTask;

    /// <summary>Ritardo della ricerca paginata dopo l'ultimo tasto (stesso valore di GlobalSearch). Impostabile per i test.</summary>
    internal TimeSpan DebounceRicerca { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Ricerca in attesa del debounce: completa quando e' stata applicata o scartata (per i test).</summary>
    internal Task RicercaPendente => _ricercaPendente;

    private void ProgrammaRicercaPaginata(string testo)
    {
        var generazione = ++_generazioneRicerca;
        _ricercaCts?.Cancel();
        _ricercaCts = null;
        if (string.IsNullOrEmpty(testo) || DebounceRicerca <= TimeSpan.Zero)
        {
            ApplicaRicercaPaginata(testo ?? string.Empty);
            return;
        }
        var cts = _ricercaCts = new CancellationTokenSource();
        _ricercaPendente = AttendiERicercaPaginata(testo, generazione, cts.Token);
    }

    private async Task AttendiERicercaPaginata(string testo, int generazione, CancellationToken token)
    {
        try { await Task.Delay(DebounceRicerca, token); }
        catch (OperationCanceledException) { return; }
        // Una digitazione piu' recente o un Invio ha superato questa ricerca: il suo risultato non si mostra.
        if (token.IsCancellationRequested || generazione != _generazioneRicerca) return;
        ApplicaRicercaPaginata(testo);
    }

    private void ApplicaRicercaPaginata(string testo)
    {
        if (InModifica && HaModifiche())
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataSalvaPaginaPrimaDiNavigare");
            return;
        }
        var ricercaPrecedente = _testoRicercaApplicato;
        var paginaPrecedente = PaginaCorrente;
        _testoRicercaApplicato = testo;
        if (!UsaPaginazione) return;
        PaginaCorrente = 1;
        if (!RicaricaPaginaPaginataSenzaRefresh() && InModifica)
        {
            _testoRicercaApplicato = ricercaPrecedente;
            PaginaCorrente = paginaPrecedente;
        }
    }

    /// <summary>Invio nella casella di ricerca: applica subito il testo corrente, senza attendere il debounce.</summary>
    [RelayCommand]
    private void ConfermaRicerca() => ConfermaRicercaImmediata();

    internal void ConfermaRicercaImmediata()
    {
        ++_generazioneRicerca;
        _ricercaCts?.Cancel();
        _ricercaCts = null;
        ApplicaRicercaPaginata(FiltroTesto ?? string.Empty);
    }

    /// <summary>Usato dal filtro e dai pulsanti pagina: non rivalida la fonte a ogni carattere/click; la
    /// validazione forte avviene all'apertura, su DatiRicaricati e dopo ogni commit. Se il DB locale fallisce
    /// durante una query, il fallback ricarica la fonte legacy completa solo nei workspace non Phase D.</summary>
    private bool RicaricaPaginaPaginataSenzaRefresh()
    {
        if (!UsaPaginazione) return false;
        try
        {
            CaricaPaginaPaginataCore(includeArchived: InModifica);
            if (InModifica)
            {
                _modificaFinestraParziale = PaginaPrecedenteDisponibile || PaginaSuccessivaDisponibile;
                AggiornaSnapshotDaItems();
                OnPropertyChanged(nameof(MostraAvvisoModificaLimitata));
                OnPropertyChanged(nameof(MostraSalvaPagina));
            }
            return true;
        }
        catch (Exception ex)
        {
            if (InModifica)
            {
                CrashLogService.Registra($"Query pagina master {typeof(T).Name} durante modifica", ex);
                MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataPaginaNonCaricata");
                return false;
            }
            CrashLogService.Registra($"Query pagina master {typeof(T).Name}: fallback compatibilita", ex);
            _visualizzazionePaginata = false;
            ApplicaListaAutorevole(_repo.Load());
            AggiornaSnapshotDaItems();
            PaginaPrecedenteDisponibile = false;
            PaginaSuccessivaDisponibile = false;
            OnPropertyChanged(nameof(UsaPaginazione));
            return true;
        }
    }

    /// <summary>
    /// Va chiamata DOPO TryAcquire(lock). Se Phase D e SavePatch sono disponibili, riallinea l'indice e rilegge
    /// soltanto la pagina corrente dalla fotografia causale piu recente, che diventa la baseline del patch.
    /// L'editing resta quindi O(page-size) in RAM. Il caricamento completo sopravvive esclusivamente come ponte
    /// per workspace legacy non ancora migrati a Phase D.
    /// </summary>
    private void CaricaFotografiaAutorevoleCompletaPerModifica()
    {
        var bounded = _pagedRead?.SupportsBoundedWrite == true;
        ApplicaFotografiaModifica(LeggiFotografiaModifica(bounded, _testoRicercaApplicato, PaginaCorrente, DimensionePaginaMaster));
    }

    /// <summary>Fotografia autorevole da applicare in modifica, già letta fuori dal thread UI: nessun campo
    /// del ViewModel viene toccato qui, solo I/O (refresh dell'indice, lettura pagina o catalogo) e impronta.</summary>
    internal sealed record FotografiaModifica(bool Paginata, MasterDataPage<T>? Pagina, long PaginaEffettiva, List<T>? Lista, string? Impronta);

    /// <summary>Fase di lettura (anche su thread pool): non tocca lo stato UI. Legge pagina/catalogo e impronta; in
    /// caso di pagina vuota oltre la prima ripiega sulla prima, come la lettura sul thread UI.</summary>
    internal FotografiaModifica LeggiFotografiaModifica(bool bounded, string testo, long paginaRichiesta, int dimensione)
    {
        if (bounded && _pagedRead != null)
        {
            _pagedRead.Refresh();
            var pagina = _pagedRead.ReadPage(testo, paginaRichiesta, dimensione, includeArchived: true);
            var effettiva = paginaRichiesta;
            if (pagina.Items.Count == 0 && paginaRichiesta > 1)
            {
                effettiva = 1;
                pagina = _pagedRead.ReadPage(testo, effettiva, dimensione, includeArchived: true);
            }
            return new FotografiaModifica(true, pagina, effettiva, null, _repo.CatturaImpronteCatalogoCorrente());
        }
        var lista = _repo.Load();
        return new FotografiaModifica(false, null, 1, lista, _repo.CatturaImpronteCatalogoCorrente());
    }

    /// <summary>Fase UI: applica la fotografia letta da <see cref="LeggiFotografiaModifica"/>.</summary>
    private void ApplicaFotografiaModifica(FotografiaModifica f)
    {
        if (f.Paginata)
        {
            _visualizzazionePaginata = true;
            if (PaginaCorrente != f.PaginaEffettiva) PaginaCorrente = f.PaginaEffettiva;
            var pagina = f.Pagina!;
            using (SospendiStatoVuoto())
            {
                Items.ReplaceWith(pagina.Items);
                ItemsAttivi.ReplaceWith(pagina.Items.Where(i => !EArchiviata(i)));
            }
            PaginaPrecedenteDisponibile = PaginaCorrente > 1;
            PaginaSuccessivaDisponibile = pagina.HasNext;
            AggiornaSnapshotDaItems();
            _modificaFinestraParziale = PaginaSuccessivaDisponibile || PaginaCorrente > 1;
            _visualizzazionePaginata = true;
            _modificaPatchBounded = true;
            _baselineSourceFingerprint = f.Impronta;
            OnPropertyChanged(nameof(UsaPaginazione));
            OnPropertyChanged(nameof(MostraAvvisoModificaLimitata));
            OnPropertyChanged(nameof(MostraSalvaPagina));
            return;
        }

        _modificaPatchBounded = false;
        _modificaFinestraParziale = false;
        _visualizzazionePaginata = false;
        ApplicaListaAutorevole(f.Lista!);
        AggiornaSnapshotDaItems();
        _baselineSourceFingerprint = f.Impronta;
        PaginaPrecedenteDisponibile = false;
        PaginaSuccessivaDisponibile = false;
        OnPropertyChanged(nameof(UsaPaginazione));
    }

    private bool SalvaComePatchBoundedSeAttivo(IReadOnlyList<T> staged)
    {
        // Usato solo dall'azione archivio one-shot per chiave (EseguiAzioneArchivioPerChiave): la finestra
        // baseline qui e' la sola pagina caricata, non l'intero catalogo, quindi la policy "Master mai
        // automerge silenzioso" non si applica — richiederebbe che l'intero catalogo stia in una pagina.
        // Un'azione mirata su una singola riga per chiave resta corretta con il merge per-UID di Phase D.
        if (!_modificaPatchBounded || _pagedRead == null) return false;
        _pagedRead.SavePatch(staged, _snapshot);
        return true;
    }

    [RelayCommand]
    private void PaginaPrecedente()
    {
        if (!UsaPaginazione || PaginaCorrente <= 1) return;
        if (InModifica && HaModifiche())
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataSalvaPaginaPrimaDiNavigare");
            return;
        }
        var paginaPrecedente = PaginaCorrente;
        PaginaCorrente--;
        if (!RicaricaPaginaPaginataSenzaRefresh() && InModifica) PaginaCorrente = paginaPrecedente;
    }

    [RelayCommand]
    private void PaginaSuccessiva()
    {
        if (!UsaPaginazione || !PaginaSuccessivaDisponibile) return;
        if (InModifica && HaModifiche())
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("MasterDataSalvaPaginaPrimaDiNavigare");
            return;
        }
        var paginaPrecedente = PaginaCorrente;
        PaginaCorrente++;
        if (!RicaricaPaginaPaginataSenzaRefresh() && InModifica) PaginaCorrente = paginaPrecedente;
    }

    /// <summary>Compatibilita per operazioni amministrative full-scan. La consultazione normale dell'Archivio
    /// usa CaricaPaginaElementiArchivio; questo metodo deve restare confinato a migrazione/legacy e verra
    /// eliminato dai comandi bulk prima del gate finale.</summary>
    public IReadOnlyList<T> CaricaElementiArchivio() => _repo.Load().Where(EArchiviata).ToList();

    public string ChiaveArchivio(T item) => Chiave(item);

    public Task<string?> RipristinaDaArchivio(string chiave) =>
        EseguiAzioneArchivioPerChiave(chiave, eliminaDefinitivamente: false);

    public Task<string?> EliminaDefinitivamenteDaArchivio(string chiave) =>
        EseguiAzioneArchivioPerChiave(chiave, eliminaDefinitivamente: true);

    /// <summary>
    /// Azione archivio one-shot per chiave stabile. Nei workspace Phase D ad alta crescita questo percorso verra
    /// sostituito dal patch per UID; il fallback corrente conserva integrita per workspace legacy durante la
    /// migrazione. Changelog/timestamp restano staged e un conflitto non sporca oggetti live.
    /// </summary>
    private async Task<string?> EseguiAzioneArchivioPerChiave(string chiave, bool eliminaDefinitivamente)
    {
        if (!PuoModificare) return LocalizationService.Instance.Traduci("ArchivioAzioneNonAutorizzata");
        AggiornaStatoLock();
        if (LockDetenutoDaAltri) return LockInfoTesto;
        var risultato = await LockGate.EseguiAsync(
            _filePath,
            () => Task.Run(() => _state.Locks.TryAcquireNuovaSessione(_filePath, _state.Config.OperatoreAttivo)));
        if (!risultato.Acquisito)
        {
            AggiornaStatoLock();
            return LockInfoTesto ?? LocalizationService.Instance.Traduci("ArchivioAzioneNonAutorizzata");
        }

        string? errore = null;
        try
        {
            CaricaFotografiaAutorevoleCompletaPerModifica();
            var item = Items.FirstOrDefault(i =>
                string.Equals(Chiave(i), chiave, StringComparison.OrdinalIgnoreCase));
            if (item == null && _pagedRead != null)
            {
                // La ricarica sopra include già le archiviate nella finestra corrente, ma resta pur sempre una
                // finestra: una voce archiviata fuori dalla pagina caricata risulterebbe comunque "non più
                // presente" anche se non era mai stata toccata da nient'altro (feedback utente). TryGetByKey
                // legge la riga per chiave esatta indipendentemente dall'archiviazione e dalla finestra corrente;
                // se esiste la aggiungiamo a Items/snapshot come baseline appena letta.
                item = _pagedRead.TryGetByKey(chiave);
                if (item != null)
                {
                    Items.Add(item);
                    AggiornaSnapshotDaItems();
                }
            }
            if (item == null)
            {
                errore = LocalizationService.Instance.Traduci("MasterDataVoceNonPiuPresente");
            }
            else
            {
                MessaggioErrore = null;
                InModifica = true;
                if (eliminaDefinitivamente) EliminaDefinitivamente(item);
                else Ripristina(item);
                errore = MessaggioErrore;
                if (errore == null)
                {
                    var daSalvare = Clona(Items);
                    RegistraModifiche(daSalvare);
                    var prima = _snapshot;
                    if (!SalvaComePatchBoundedSeAttivo(daSalvare))
                        _repo.Save(daSalvare, _snapshot);
                    DopoPersistenza(daSalvare, prima); // es. Requisiti: tombstone dell'archivio guasto delle righe eliminate definitivamente
                }
            }
        }
        catch (Exception ex)
        {
            errore = string.Format(
                LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
            MessaggioErrore = errore;
        }
        finally
        {
            InModifica = false;
            try
            {
                ProgrammaRilascioSessioneEsatta(
                    risultato.DetenutoDa?.SessionId,
                    dopoSalvataggio: errore == null);
                if (RilascioSessioneLockInCorso is { } rilascio)
                    await rilascio;
                if (RilascioLockInAttesa)
                {
                    var avvisoRilascio = MessaggioErrore ??
                        LocalizationService.Instance.Traduci("SalvatoSbloccoLockNonRiuscito");
                    errore = errore == null ? avvisoRilascio : $"{errore}\n\n{avvisoRilascio}";
                }
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Rilascio lock dopo azione archivio per chiave fallito", ex);
            }
            finally
            {
                FermaHeartbeat();
                try { Ricarica(); }
                catch (Exception ex) { CrashLogService.Registra("Ricarica master dopo azione archivio per chiave fallita", ex); }
            }
        }
        return errore;
    }
}
