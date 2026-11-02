using CommunityToolkit.Mvvm.ComponentModel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Report a giri, stesso schema della Home (vedi HomeViewModel.Giro). Ogni ricalcolo (cambio di filtro o di asse,
/// rivisita con Aggiorna) cattura sul thread UI tutto cio' che serve come copie, calcola su un thread di pool dentro
/// UNA lettura logica dei master (<see cref="MasterLetturaScope"/>, un solo caricamento per catalogo) e applica sul
/// thread UI solo se il giro e' ancora l'ultimo (contatore di generazione). I cambi di filtro ravvicinati si
/// coalizzano: il giro parte <see cref="RitardoCoalescenza"/> dopo l'ultimo. Senza modalita' in background (default:
/// test e percorsi esistenti) gli stessi passi girano in linea sul thread chiamante, con risultati identici.
/// Restano sul thread UI la paginazione, le marcature N.A. e l'ordinamento degli assi sugli oggetti gia' applicati.
/// </summary>
public partial class ReportViewModel
{
    private bool _aggiornaInBackground;
    private int _generazioneGiro;
    private bool _listeDaRicaricare;
    private CancellationTokenSource? _ritardoGiro;
    private readonly List<Task> _giri = new();
    // Accesso serializzato alla query indicizzata: giri superati e correnti possono sovrapporsi sul pool.
    private readonly object _letturaIndiceGate = new();
    // Impronta del catalogo dei campi di sistema all'ultima conciliazione: se invariata, la conciliazione non rilegge il catalogo.
    private string? _improntaCampiSistemaConciliati;
    /// <summary>Quante volte il catalogo dei campi di sistema e' stato conciliato con filtri e dimensioni (per i test: un giro a
    /// catalogo invariato non lo rilegge).</summary>
    internal int ConciliazioniCampiSistema { get; private set; }

    /// <summary>Ritardo di coalescenza dei cambi di filtro in background: piu' cambi ravvicinati producono un solo giro.</summary>
    internal TimeSpan RitardoCoalescenza { get; set; } = TimeSpan.FromMilliseconds(180);

    /// <summary>Vero mentre un giro in background non ha ancora applicato i suoi dati: la vista mostra un indicatore discreto.</summary>
    [ObservableProperty] private bool caricamentoInCorso;

    /// <summary>Giri le cui letture sono state scartate perche' superate da un giro piu' recente.</summary>
    internal int GiriScartati { get; private set; }

    /// <summary>Ultima eccezione di un giro in background (registrata anche nel log dei crash): per i test.</summary>
    internal Exception? UltimaEccezioneAggiornamento { get; private set; }

    /// <summary>Solo test: eseguito all'inizio di ogni calcolo di giro (puo' bloccare, per simulare un giro lento).</summary>
    internal Action? GateLetturaPerTest { get; set; }

    /// <summary>Solo test: eseguito all'inizio di ogni calcolo di giro (puo' lanciare, per simulare un guasto).</summary>
    internal Action? GuastoLetturaPerTest { get; set; }

    internal void ImpostaAggiornamentoInBackgroundPerTest(bool valore) => _aggiornaInBackground = valore;

    /// <summary>Attende i giri avviati e i ricalcoli in attesa di coalescenza: dati applicati, oppure giri superati e conclusi.
    /// Con il calcolo in linea (default) e' gia' completo.</summary>
    public async Task AttendiAggiornamentoAsync()
    {
        while (true)
        {
            Task[] attesi;
            lock (_giri) attesi = _giri.Where(t => !t.IsCompleted).ToArray();
            if (attesi.Length == 0) return;
            await Task.WhenAll(attesi);
        }
    }

    /// <summary>Richiede il giro per le sezioni con il flag acceso (anteprima, matrice, liste). In linea gira subito; in
    /// background il cambio si coalizza, salvo <paramref name="immediato"/> (rivisita, che non ha altri cambi da attendere).
    /// Ogni richiesta supera i giri non ancora applicati. Non fa nulla durante l'inizializzazione o un raggruppamento di cambi:
    /// in quel caso i flag restano accesi e il giro parte alla fine del raggruppamento.</summary>
    private void RichiediGiro(bool immediato)
    {
        if (_disposed || _inizializzazioneMatrice || _refreshInCorso > 0) return;
        if (!_listeDaRicaricare && !_anteprimaDaRigenerare && !_matriceDaRigenerare) return;
        _generazioneGiro++;
        if (_anteprimaDaRigenerare) OnPropertyChanged(nameof(HaFiltriAttivi));
        if (!_aggiornaInBackground)
        {
            EseguiGiroInLinea();
            return;
        }
        CaricamentoInCorso = true;
        if (immediato)
        {
            AnnullaRitardo();
            PartiGiro();
        }
        else AvviaRitardo();
    }

    /// <summary>Giro in linea: cattura, calcolo e applicazione sul thread chiamante (modalita' sincrona).</summary>
    private void EseguiGiroInLinea()
    {
        if (_disposed) return;
        var richiesta = CatturaGiro();
        ApplicaGiro(richiesta, CalcolaGiro(richiesta));
    }

    /// <summary>Primo giro in background (costruttore con aggiornaInBackground): cattura sul thread UI, calcolo unico sul pool
    /// (liste, selezioni, anteprima e matrice), applicazione sul thread UI, poi <see cref="CompletaCostruzione"/>. Il primo giro non
    /// puo' essere superato: le liste si applicano sempre. Una sola operazione tracciata, cosi' <see cref="AttendiAggiornamentoAsync"/> la attende.</summary>
    private void PartiPrimoGiroInBackground()
    {
        if (_disposed) return;
        CaricamentoInCorso = true;
        var richiesta = CatturaGiro();
        var giro = EseguiPrimoGiroAsync(richiesta);
        lock (_giri)
        {
            _giri.RemoveAll(t => t.IsCompleted);
            _giri.Add(giro);
        }
    }

    /// <summary>Il completamento parte anche dopo un guasto del primo giro (registrato nel log dei crash): la pagina resta utilizzabile,
    /// e l'indicatore si spegne comunque.</summary>
    private async Task EseguiPrimoGiroAsync(RichiestaGiroReport richiesta)
    {
        try
        {
            var risultato = await Task.Run(() => CalcolaGiro(richiesta));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || richiesta.Generazione != _generazioneGiro) return;
                if (ApplicaGiro(richiesta, risultato)) CaricamentoInCorso = false;
            }).GetTask();
        }
        catch (Exception ex)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || richiesta.Generazione != _generazioneGiro) return;
                UltimaEccezioneAggiornamento = ex;
                CaricamentoInCorso = false;
            }).GetTask();
            CrashLogService.Registra("Report: primo giro in background", ex);
        }
        try
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_disposed && richiesta.Generazione == _generazioneGiro) CompletaCostruzione();
            }).GetTask();
        }
        catch (Exception ex)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_disposed && richiesta.Generazione == _generazioneGiro) CaricamentoInCorso = false;
            }).GetTask();
            CrashLogService.Registra("Report: completamento della costruzione", ex);
        }
    }

    /// <summary>Coalescenza: il giro parte <see cref="RitardoCoalescenza"/> dopo l'ultima richiesta. Chiamato sul thread UI.</summary>
    private void AvviaRitardo()
    {
        AnnullaRitardo();
        var ritardo = new CancellationTokenSource();
        _ritardoGiro = ritardo;
        var attesa = AttendiRitardoEPartiAsync(ritardo);
        lock (_giri)
        {
            _giri.RemoveAll(t => t.IsCompleted);
            _giri.Add(attesa);
        }
    }

    private void AnnullaRitardo()
    {
        var ritardo = _ritardoGiro;
        _ritardoGiro = null;
        try { ritardo?.Cancel(); }
        catch (ObjectDisposedException)
        {
            // Il delay può concludersi sul pool nello stesso momento in cui la pagina viene chiusa.
            // In quel caso il task ha già rilasciato la sorgente e non resta alcun callback valido.
        }
    }

    private async Task AttendiRitardoEPartiAsync(CancellationTokenSource ritardo)
    {
        var token = ritardo.Token;
        try
        {
            await Task.Delay(RitardoCoalescenza, token);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || token.IsCancellationRequested || !ReferenceEquals(_ritardoGiro, ritardo)) return;
                _ritardoGiro = null;
                PartiGiro();
            }).GetTask();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_ritardoGiro, ritardo)) _ritardoGiro = null;
            ritardo.Dispose();
        }
    }

    /// <summary>Sul thread UI: cattura cio' che il giro legge e lo avvia in background.</summary>
    private void PartiGiro()
    {
        if (_disposed) return;
        var richiesta = CatturaGiro();
        var giro = EseguiGiroAsync(richiesta);
        lock (_giri)
        {
            _giri.RemoveAll(t => t.IsCompleted);
            _giri.Add(giro);
        }
    }

    /// <summary>Un giro in background: calcolo su pool, applicazione sul thread UI solo se e' ancora l'ultimo. Un guasto
    /// viene registrato nel log dei crash e l'indicatore si spegne: la pagina resta utilizzabile.</summary>
    private async Task EseguiGiroAsync(RichiestaGiroReport richiesta)
    {
        try
        {
            var risultato = await Task.Run(() => CalcolaGiro(richiesta));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
                if (richiesta.Generazione != _generazioneGiro)
                {
                    GiriScartati++;
                    // CatturaGiro ha consumato le richieste di liste, anteprima e matrice, e un cambio successivo ne chiede solo alcune
                    // (un asse rifa' la matrice ma non l'anteprima, un filtro non chiede le liste): il giro che ha superato questo puo'
                    // non riportarle. Le si riarma tutte e si chiede un giro che le soddisfi.
                    RiarmaRichieste(richiesta);
                    RichiediGiro(immediato: false);
                    return;
                }
                // Se l'applicazione ha richiesto un nuovo giro (un filtro e' cambiato durante Ripopola), l'indicatore resta acceso.
                if (ApplicaGiro(richiesta, risultato)) CaricamentoInCorso = false;
            }).GetTask();
        }
        catch (Exception ex)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
                UltimaEccezioneAggiornamento = ex;
                // Il giro fallito non ha applicato nulla: le richieste che aveva consumato restano armate. Senza un cambio piu' recente
                // non si ritenta da soli (un guasto ripetibile non deve diventare un ciclo); se un cambio e' arrivato durante il giro,
                // il suo giro va richiesto anche qui, perche' le richieste riarmate non restino ferme se quel giro e' gia' partito.
                RiarmaRichieste(richiesta);
                if (richiesta.Generazione == _generazioneGiro) CaricamentoInCorso = false;
                else RichiediGiro(immediato: false);
            }).GetTask();
            CrashLogService.Registra("Report: aggiornamento in background", ex);
        }
    }

    /// <summary>Riarma le richieste che un giro ha consumato in CatturaGiro: un giro superato o fallito non le ha applicate.</summary>
    private void RiarmaRichieste(RichiestaGiroReport richiesta)
    {
        if (richiesta.Liste) _listeDaRicaricare = true;
        if (richiesta.Anteprima) _anteprimaDaRigenerare = true;
        if (richiesta.Matrice) _matriceDaRigenerare = true;
    }

    /// <summary>Sul thread UI: copia tutto cio' che il calcolo legge (nessun oggetto condiviso con il pool) e consuma i flag.</summary>
    private RichiestaGiroReport CatturaGiro()
    {
        // Campi di sistema: il catalogo si rilegge solo quando cambia (impronta), e filtri e dimensioni si allineano PRIMA di
        // leggere i flag, le selezioni e i predicati: un campo abilitato o disabilitato dal catalogo compare o sparisce anche su una
        // pagina gia' in cache, senza riavvio (vedi ConciliaCampiCustom).
        if (_state.CartellaValida)
        {
            using var letturaCatalogo = MasterLetturaScope.Apri();
            // Il catalogo dei campi di sistema si rilegge solo se e' cambiato dall'ultima conciliazione (impronta del catalogo, come
            // quella delle altre liste). Senza impronta (archivio non su Phase D) si concilia ad ogni giro, come prima.
            var impronta = ImprontaCampiSistema();
            if (impronta == null || impronta != _improntaCampiSistemaConciliati)
            {
                ConciliazioniCampiSistema++;
                if (ConciliaCampiCustom()) _listeDaRicaricare = true;
                _improntaCampiSistemaConciliati = impronta;
            }
        }
        var anteprima = _anteprimaDaRigenerare;
        var matrice = _matriceDaRigenerare && CampiXMatrice.Count + CampiYMatrice.Count > 0;
        var liste = _listeDaRicaricare;
        _anteprimaDaRigenerare = false;
        _matriceDaRigenerare = false;
        _listeDaRicaricare = false;
        var primo = _primoGiroPendente;
        _primoGiroPendente = false;

        var cartellaValida = _state.CartellaValida;
        // Impronte dei cataloghi sotto una sola lettura logica: senza, ogni catalogo rifarebbe il refresh Phase D sul thread UI.
        string? improntaCataloghi = null;
        if (liste && cartellaValida)
        {
            using var letturaMaster = MasterLetturaScope.Apri();
            // La composizione dei filtri e delle dimensioni dei campi di sistema fa parte dell'impronta: se cambia, le liste si
            // ricalcolano anche con i cataloghi invariati (i valori dei campi nuovi o tolti vengono riletti).
            var impronta = CatturaImpronteCataloghi();
            improntaCataloghi = impronta == null ? null : impronta + "|" + FirmaCampiCustom();
        }
        var marcature = matrice
            ? MatriceNonApplicabileService.Leggi(_state.Config, IdCampi(CampiXMatrice), IdCampi(CampiYMatrice)).ToList()
            : new List<CellaNonApplicabile>();
        return new RichiestaGiroReport
        {
            Generazione = _generazioneGiro,
            PrimoGiro = primo,
            Persistite = primo ? CatturaSelezioniPersistite() : null,
            CartellaValida = cartellaValida,
            Anteprima = anteprima,
            Matrice = matrice,
            Liste = liste,
            Filtro = CostruisciFiltro(),
            Configurazione = CopiaConfigurazione(CostruisciConfigurazioneMatrice()),
            Marcature = marcature,
            PrimaRiga = IndiceOffsetMatrice(OffsetRigheMatrice),
            PrimaColonna = IndiceOffsetMatrice(OffsetColonneMatrice),
            NumeroRigheFinestra = _righePerFinestraMatrice,
            NumeroColonneFinestra = _colonnePerFinestraMatrice,
            MatriceRiutilizzabile = _matricePreparata != null,
            ImprontaCataloghi = improntaCataloghi,
            ImprontaUltima = _improntaCataloghiUltimoPopolamento,
            TestUltimo = _testUltimoPopolamento,
            EMaster = EMasterCorrente,
            EMasterUltimo = _eMasterUltimoPopolamento,
            // componente e veicolo non hanno più un secondo filtro dimensionale, ma servono ancora le opzioni
            // per i due controlli globali del pannello.
            DimensioniMatrice = FiltriMatrice.Select(f => f.Dimensione.Id)
                .Concat(new[] { TestLoom.App.Services.DimensioniMatrice.Componente, TestLoom.App.Services.DimensioniMatrice.Sottoveicolo })
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ChiaviCampiCustom = FiltriCampiCustomReport.Select(f => f.Chiave).ToList(),
            OpzioniCampiCustom = FiltriCampiCustomReport.ToDictionary(f => f.Chiave, f => (f.Tipo, f.OpzioniLista.ToList())),
            PercorsoIndice = cartellaValida ? _state.TestCases.LocalIndexDatabasePath : null,
            SnapshotPersistente = cartellaValida && _state.TestCases.HasPersistentSnapshot,
            Coordinatore = cartellaValida ? _state.TestCases.IndexRecovery : null,
        };
    }

    /// <summary>Sul thread UI, al primo giro: le selezioni salvate di ogni filtro (le stesse che leggeva <see cref="FiltroMultiSelect.LegaPersistenza"/>).</summary>
    private SelezioniFiltri CatturaSelezioniPersistite() => new(
        ViewStateService.LeggiLista(_state, ChiaveVistaFiltroTemplate).ToList(),
        FiltriCampiCustomReport.ToDictionary(f => f.Chiave, f => ViewStateService.LeggiLista(_state, "Report.FiltroCustom." + f.Chiave).ToList()),
        FiltriMatrice.ToDictionary(f => f.Dimensione.Id, f => ViewStateService.LeggiLista(_state, ChiaveVistaFiltro(f.Dimensione.Id)).ToList(), StringComparer.OrdinalIgnoreCase));

    /// <summary>Calcolo del giro, fuori dal thread UI. Costruisce solo oggetti nuovi o copie e non legge proprieta'
    /// osservabili. Una sola lettura logica dei master e un solo caricamento per catalogo, condiviso da liste,
    /// anteprima e matrice.</summary>
    private RisultatoGiroReport CalcolaGiro(RichiestaGiroReport r)
    {
        GateLetturaPerTest?.Invoke();
        GuastoLetturaPerTest?.Invoke();
        using var letturaMaster = MasterLetturaScope.Apri();
        var tutti = new Lazy<IReadOnlyList<TestCase>>(() => r.CartellaValida ? _state.TestCases.GetAll() : new List<TestCase>());
        var templates = new Lazy<List<TestTemplate>>(() => _state.Master.TestTemplate.Load());
        var veicoli = new Lazy<List<Veicolo>>(() => _state.Master.Veicoli.Load());
        var funzionalita = new Lazy<List<Funzionalita>>(() => _state.Master.Funzionalita.Load());
        var componente = new Lazy<List<Componente>>(() => _state.Master.Componente.Load());
        var manuali = new Lazy<List<StatoFunzionalitaEcuVeicolo>>(() => _state.Master.StatiFunzionalitaEcuVeicolo.Load());
        var risultato = new RisultatoGiroReport();

        if (r.Liste) risultato.Liste = CalcolaListe(r, tutti.Value, templates, veicoli, funzionalita, componente, manuali);

        // Primo giro: le selezioni persistite diventano effettive qui, intersecate con le opzioni appena calcolate (stesso criterio di
        // FiltroMultiSelect.CollegaSelezioneIniziale), cosi' anteprima e matrice di questo giro sono gia' quelle dello stato finale.
        var filtro = r.Filtro;
        var configurazione = r.Configurazione;
        if (r.PrimoGiro && risultato.Liste is { Invariati: false } primeListe)
        {
            risultato.Selezioni = CalcolaSelezioniEffettive(r, primeListe);
            filtro = FiltroConSelezioni(r, risultato.Selezioni);
            configurazione = ConfigurazioneConSelezioni(r.Configurazione, risultato.Selezioni);
        }

        if (!r.Anteprima && !r.Matrice) return risultato;
        // TIME (6.6.1): la matrice riceve i Test che passano i criteri non TIME e il totale del Test; la durata delle operazioni la applica
        // la matrice per combinazione (Stato, Stato %), per Test (Test %) o per tentativo (Tempo). Liste e anteprima restano a
        // livello di Test: un Test entra se almeno una sua operazione corrente effettiva corrisponde (CorrispondeTempoOperazione).
        var candidatiMatrice = LeggiFiltratiDaIndice(r, filtro)?.Where(filtro.CorrispondeTempoTotale).ToList()
            ?? tutti.Value.Where(filtro.CorrispondeMatrice).ToList();
        var filtrati = candidatiMatrice.Where(filtro.CorrispondeTempoOperazione).ToList();
        if (r.Anteprima) risultato.Anteprima = CalcolaAnteprima(r, filtro, filtrati);

        if (r.Matrice && r.CartellaValida)
        {
            // Matrice riusata solo se i cataloghi e l'archivio sono invariati e la matrice preparata e' ancora quella del filtro
            // corrente (ogni cambio di filtro o di asse la annulla in GeneraAnteprima/GeneraMatrice).
            var riusa = r.Liste && risultato.Liste is { Invariati: true } && r.MatriceRiutilizzabile;
            if (riusa)
            {
                risultato.MatriceRiutilizzata = true;
            }
            else
            {
                // Catalogo componente solo in modalita' Stato: serve all'universo "Non testato"; in Percentuale non si carica.
                var catalogoEcu = configurazione.ModalitaCella != ModalitaCellaMatrice.TestPercentuale ? componente.Value : null;
                var catalogoManuali = configurazione.ModalitaCella != ModalitaCellaMatrice.TestPercentuale ? manuali.Value : null;
                var matrice = ReportMatriceService.Prepara(candidatiMatrice, configurazione, veicoli.Value, funzionalita.Value, templates.Value, catalogoEcu, catalogoManuali,
                    filtro: filtro);
                // Totali di intestazione sulla finestra catturata: scaldano la cache di MatricePreparata. ApplicaMatrice
                // riusa questi totali se marcature e posizione dello scorrimento non sono cambiate nel frattempo.
                matrice.Finestra(r.PrimaRiga, r.NumeroRigheFinestra, r.PrimaColonna, r.NumeroColonneFinestra, r.Marcature);
                risultato.Matrice = matrice;
            }
        }
        return risultato;
    }

    /// <summary>Liste e filtri. Se archivio, cataloghi e ruolo sono invariati dall'ultimo popolamento non si calcola nulla;
    /// altrimenti si calcolano i valori disponibili per ciascun filtro (un solo passaggio sull'archivio).</summary>
    private static ListeGiroReport CalcolaListe(RichiestaGiroReport r, IReadOnlyList<TestCase> tutti,
        Lazy<List<TestTemplate>> templates, Lazy<List<Veicolo>> veicoli, Lazy<List<Funzionalita>> funzionalita,
        Lazy<List<Componente>> componente, Lazy<List<StatoFunzionalitaEcuVeicolo>> manuali)
    {
        var invariati = r.CartellaValida && r.ImprontaCataloghi != null && r.ImprontaCataloghi == r.ImprontaUltima
            && ReferenceEquals(tutti, r.TestUltimo) && r.EMaster == r.EMasterUltimo;
        if (invariati) return new ListeGiroReport { Invariati = true };

        var valoriTemplate = (r.CartellaValida ? templates.Value.Where(t => !t.Archiviata) : Enumerable.Empty<TestTemplate>())
            .Select(t => (Valore: t.Uid.ToString(), Etichetta: t.Nome)).ToList();
        var valoriCustom = r.ChiaviCampiCustom.ToDictionary(chiave => chiave, chiave =>
        {
            var (tipo, opzioni) = r.OpzioniCampiCustom.TryGetValue(chiave, out var definizione)
                ? definizione : (TipoCampoStrumento.Testo, new List<string>());
            return CampoSistemaCatalogoService.OpzioniFiltro(tipo, opzioni, CampoSistemaCatalogoService.ValoriDistinti(tutti, chiave));
        });
        var usaStatoCorrente = r.Configurazione.ModalitaCella != ModalitaCellaMatrice.TestPercentuale;
        var valoriDimensioni = ReportMatriceService.ValoriDisponibiliPerDimensioni(r.DimensioniMatrice, tutti,
            veicoli.Value, funzionalita.Value, templates.Value, r.Configurazione,
            usaStatoCorrente ? componente.Value : null, usaStatoCorrente ? manuali.Value : null);
        return new ListeGiroReport
        {
            Invariati = false,
            Impronta = r.ImprontaCataloghi,
            Tutti = tutti,
            ValoriTemplate = valoriTemplate,
            ValoriCustom = valoriCustom,
            ValoriDimensioni = valoriDimensioni,
        };
    }

    private AnteprimaGiroReport CalcolaAnteprima(RichiestaGiroReport r, ReportFilter filtro, List<TestCase> risultati)
    {
        if (!r.CartellaValida) return new AnteprimaGiroReport();
        var avvisoVolumeGrande = filtro.Componente.Count == 0 && filtro.Veicoli.Count == 0 && filtro.TemplateUid.Count == 0 && risultati.Count > 200;
        var ordinati = avvisoVolumeGrande
            ? SelezionaAnteprimaPiuRecenti(risultati, AnteprimaMaxRighe)
            : risultati.OrderByDescending(tc => tc.UltimaModifica);
        return new AnteprimaGiroReport
        {
            AvvisoVolumeGrande = avvisoVolumeGrande,
            Totale = risultati.Count,
            Righe = ordinati.Select(tc => new RigaAnteprima
            {
                Id = tc.Id, Componente = tc.Componente, Veicolo = string.Join(", ", tc.VeicoliEffettivi), Template = _state.NomeTestTemplate(tc.TestTemplateUid),
                Esito = tc.StatoCorrente?.Label() ?? "Non testato",
                Data = tc.DataCreazione.ToString("dd/MM/yyyy")
            }).ToList(),
        };
    }

    /// <summary>Lettura indicizzata del filtro, con la cache per token (Generazione dell'indice e revisione dell'indice): stesso
    /// filtro su indice invariato non rilegge l'archivio. Null se il filtro e' vuoto o l'indice non e' utilizzabile (fallback).</summary>
    private List<TestCase>? LeggiFiltratiDaIndice(RichiestaGiroReport r, ReportFilter filtro)
    {
        var haFiltro = filtro.Componente.Count > 0 || filtro.Veicoli.Count > 0 || filtro.TemplateUid.Count > 0 ||
            filtro.DataDa.HasValue || filtro.DataA.HasValue || filtro.CampiCustom.Values.Any(x => x.Count > 0) || filtro.HaFiltroTempo;
        if (!haFiltro || !r.SnapshotPersistente || string.IsNullOrWhiteSpace(r.PercorsoIndice) || r.Coordinatore == null) return null;
        var percorso = r.PercorsoIndice;
        var coordinatore = r.Coordinatore;

        lock (_letturaIndiceGate)
        {
            List<TestCase>? Leggi()
            {
                _archiveQuery ??= new LocalTestArchiveQueryService(percorso, coordinatore);
                if (_archiveQuery.Generazione != coordinatore.Generazione) _archiveQuery = new LocalTestArchiveQueryService(percorso, coordinatore);
                // Token di generazione come nella Home, letto PRIMA della query: se entrambi sono invariati dall'ultima lettura con lo
                // stesso filtro, il risultato precedente e' ancora esatto. Un commit sull'indice avanza la revisione e invalida la cache.
                var versione = _archiveQuery.LeggiRevisione();
                var chiave = ChiaveFiltroReport(filtro);
                if (_filtratiIndicizzati is { } precedente && precedente.Generazione == _archiveQuery.Generazione &&
                    precedente.Versione == versione && precedente.Chiave == chiave)
                    return precedente.Risultato;

                var custom = filtro.CampiCustom.ToDictionary(x => x.Key, x => (IReadOnlyCollection<string>)x.Value);
                // Nessun tetto di materializzazione: un risultato grande resta sul percorso indicizzato (vedi QueryAllAnalisi).
                var risultato = _archiveQuery.QueryAllAnalisi(new LocalTestArchiveQuery
                {
                    Componente = filtro.Componente.Count == 1 ? filtro.Componente[0] : null,
                    Vehicle = filtro.Veicoli.Count == 1 ? filtro.Veicoli[0] : null,
                    TemplateUids = filtro.TemplateUid,
                    CreatedAtOrAfter = filtro.DataDa,
                    CreatedAtOrBefore = filtro.DataA,
                    CustomFieldSelections = custom,
                    MatchStringsOrdinal = true
                });
                _filtratiIndicizzati = (_archiveQuery.Generazione, versione, chiave, risultato);
                return risultato;
            }

            return coordinatore.TryRead("Report filtrato", Leggi, out List<TestCase>? indicizzati) ? indicizzati : null;
        }
    }

    /// <summary>Applicazione sul thread UI. Restituisce false se durante l'applicazione delle liste un filtro e' cambiato
    /// (Ripopola ha rimosso una selezione): anteprima e matrice di questo giro sono superate e il giro ne chiede un altro.</summary>
    private bool ApplicaGiro(RichiestaGiroReport r, RisultatoGiroReport risultato)
    {
        if (_disposed || r.Generazione != _generazioneGiro) return false;
        if (risultato.Liste != null)
        {
            ApplicaListe(r, risultato.Liste);
            if (r.PrimoGiro && risultato.Selezioni != null) ApplicaSelezioniPrimoGiro(r, risultato.Selezioni);
            if (_anteprimaDaRigenerare || _matriceDaRigenerare)
            {
                RichiediGiro(immediato: false);
                return false;
            }
        }
        if (risultato.Anteprima != null) ApplicaAnteprima(risultato.Anteprima, r.CartellaValida);
        if (r.Matrice && r.CartellaValida)
        {
            if (risultato.MatriceRiutilizzata)
            {
                ApplicaMatrice();
            }
            else if (risultato.Matrice != null)
            {
                // Ordinamento personalizzato al momento dell'applicazione: il giro puo' essere partito prima di una modifica dell'ordine.
                risultato.Matrice.Riordina(
                    OrdineAssiReportService.Leggi(_state.Config, _state.RuoloAttivo, r.Configurazione.AsseX),
                    OrdineAssiReportService.Leggi(_state.Config, _state.RuoloAttivo, r.Configurazione.AsseY));
                _matricePreparata = risultato.Matrice;
                ApplicaMatrice();
            }
        }
        return true;
    }

    /// <summary>Primo giro: collega ogni filtro alla propria persistenza con la selezione gia' calcolata dal giro (vedi
    /// <see cref="FiltroMultiSelect.CollegaSelezioneIniziale"/>). Sul thread UI, durante la costruzione: nessun giro aggiuntivo.</summary>
    private void ApplicaSelezioniPrimoGiro(RichiestaGiroReport r, SelezioniFiltri selezioni)
    {
        var salvati = r.Persistite!;
        _refreshInCorso++;
        try
        {
            FiltroTemplate.CollegaSelezioneIniziale(_state, ChiaveVistaFiltroTemplate, salvati.Template, selezioni.Template);
            // I filtri dei campi di sistema si collegano in ApplicaListe (CollegaFiltriCampiCustomNuovi), come quelli aggiunti dopo.
            foreach (var filtro in FiltriMatrice)
            {
                // Per chiave, senza indicizzare: le dimensioni possono cambiare fra la cattura e l'applicazione (un campo di sistema
                // abilitato o disabilitato nel frattempo). Una dimensione non catturata parte senza selezione salvata.
                var id = filtro.Dimensione.Id;
                var salvatiDimensione = salvati.Dimensioni.TryGetValue(id, out var salvatiId) ? salvatiId : new List<string>();
                var selezionatiDimensione = selezioni.Dimensioni.TryGetValue(id, out var selezionatiId) ? selezionatiId : new List<string>();
                filtro.Filtro.CollegaSelezioneIniziale(_state, ChiaveVistaFiltro(id), salvatiDimensione, selezionatiDimensione);
                filtro.PersistenzaCollegata = true;
            }
        }
        finally { _refreshInCorso--; }
        NotificaFiltriAttiviCambiati();
    }

    private void ApplicaListe(RichiestaGiroReport r, ListeGiroReport liste)
    {
        if (liste.Invariati) return;
        _refreshInCorso++;
        try
        {
            FiltroTemplate.Ripopola(liste.ValoriTemplate);
            // Per chiave, non per posizione: i valori letti dal giro corrispondono alla composizione catturata con il giro stesso.
            foreach (var filtroCustom in FiltriCampiCustomReport)
                if (liste.ValoriCustom.TryGetValue(filtroCustom.Chiave, out var valoriCustom))
                    filtroCustom.Filtro.Ripopola(valoriCustom.Select(v => (v, v)));
            foreach (var filtro in FiltriMatrice)
                if (liste.ValoriDimensioni.TryGetValue(filtro.Dimensione.Id, out var valoriDimensione))
                    filtro.Filtro.Ripopola(valoriDimensione.Select(v => (v, v)));
            EcuDisponibili = liste.ValoriDimensioni[DimensioniMatrice.Componente];
            VeicoliDisponibili = liste.ValoriDimensioni[DimensioniMatrice.Sottoveicolo];
            CollegaFiltriCampiCustomNuovi();
            CollegaFiltriDimensioniMatriceNuove();
        }
        finally { _refreshInCorso--; }
        NotificaFiltriAttiviCambiati();
        OnPropertyChanged(nameof(EcuDisponibili));
        OnPropertyChanged(nameof(VeicoliDisponibili));
        _improntaCataloghiUltimoPopolamento = liste.Impronta;
        _testUltimoPopolamento = liste.Tutti;
        _eMasterUltimoPopolamento = r.EMaster;
    }

    /// <summary>Collega la persistenza dei filtri dei campi di sistema appena popolati, una sola volta per filtro: la selezione salvata
    /// si riapplica intersecata con le opzioni, come al primo giro. Sul thread UI dentro ApplicaListe (<c>_refreshInCorso</c> sospende
    /// gli avvii di giro); la notifica di Cambiato, se la selezione salvata cambia qualcosa, chiede comunque un nuovo giro.</summary>
    private void CollegaFiltriCampiCustomNuovi()
    {
        foreach (var custom in FiltriCampiCustomReport.Where(c => !c.PersistenzaCollegata && c.Filtro.Opzioni.Count > 0))
        {
            custom.PersistenzaCollegata = true;
            var chiave = "Report.FiltroCustom." + custom.Chiave;
            var salvati = ViewStateService.LeggiLista(_state, chiave).ToList();
            custom.Filtro.CollegaSelezioneIniziale(_state, chiave, salvati,
                SelezioneSalvataInOpzioni(salvati, custom.Filtro.Opzioni.Select(o => o.Valore)));
        }
    }

    /// <summary>Collega la persistenza dei filtri di dimensione della matrice creati dopo il primo giro (un campo di sistema abilitato
    /// dopo l'avvio), una sola volta per filtro, come i filtri dei campi di sistema: la selezione salvata si riapplica intersecata con
    /// le opzioni e ogni cambio successivo viene scritto. Sul thread UI dentro ApplicaListe (<c>_refreshInCorso</c> sospende gli avvii).</summary>
    private void CollegaFiltriDimensioniMatriceNuove()
    {
        foreach (var filtro in FiltriMatrice.Where(f => !f.PersistenzaCollegata && f.Filtro.Opzioni.Count > 0).ToList())
        {
            filtro.PersistenzaCollegata = true;
            var chiave = ChiaveVistaFiltro(filtro.Dimensione.Id);
            var salvati = ViewStateService.LeggiLista(_state, chiave).ToList();
            filtro.Filtro.CollegaSelezioneIniziale(_state, chiave, salvati,
                SelezioneSalvataInOpzioni(salvati, filtro.Filtro.Opzioni.Select(o => o.Valore)));
        }
    }

    /// <summary>Impronta dei due cataloghi dei campi di sistema (null se una delle due non ha impronta: nessuna cache).</summary>
    private string? ImprontaCampiSistema()
    {
        var dettagli = _state.Master.CampiSistemaDettagli.CatturaImpronteCatalogoCorrente();
        var pianificazione = _state.Master.CampiSistemaPianificazione.CatturaImpronteCatalogoCorrente();
        return dettagli == null || pianificazione == null ? null : dettagli + "|" + pianificazione;
    }

    /// <summary>Composizione dei filtri e delle dimensioni dei campi di sistema: fa parte dell'impronta delle liste (vedi CatturaGiro).</summary>
    private string FirmaCampiCustom() =>
        string.Join("\u0002", FiltriCampiCustomReport.Select(f => f.Chiave)) + "\u0003" +
        string.Join("\u0002", FiltriMatrice.Select(f => f.Dimensione.Id));

    private void ApplicaAnteprima(AnteprimaGiroReport anteprima, bool cartellaValida)
    {
        if (!cartellaValida)
        {
            Anteprima.ReplaceWith(Array.Empty<RigaAnteprima>());
            TotaleRisultatiAnteprima = 0;
            OnPropertyChanged(nameof(AnteprimaVuota));
            OnPropertyChanged(nameof(TestoConteggioAnteprima));
            OnPropertyChanged(nameof(AnteprimaLimitata));
            OnPropertyChanged(nameof(TestoAvvisoAnteprimaLimitata));
            return;
        }
        AvvisoVolumeGrande = anteprima.AvvisoVolumeGrande;
        TotaleRisultatiAnteprima = anteprima.Totale;
        Anteprima.ReplaceWith(anteprima.Righe);
        OnPropertyChanged(nameof(AnteprimaVuota));
        OnPropertyChanged(nameof(TestoConteggioAnteprima));
        OnPropertyChanged(nameof(AnteprimaLimitata));
        OnPropertyChanged(nameof(TestoAvvisoAnteprimaLimitata));
    }

    /// <summary>Copia profonda della configurazione della matrice: il calcolo in background non condivide liste con la UI.</summary>
    private static ConfigurazioneMatriceReport CopiaConfigurazione(ConfigurazioneMatriceReport c) => new()
    {
        CampiX = c.CampiX.ToList(),
        CampiY = c.CampiY.ToList(),
        ModalitaCella = c.ModalitaCella,
        BaseTempo = c.BaseTempo,
        FiltroTempoOperazione = c.FiltroTempoOperazione,
        FiltroTempoTotaleTest = c.FiltroTempoTotaleTest,
        StatiNascostiPresentazione = new HashSet<StatoCellaMatrice>(c.StatiNascostiPresentazione),
        Filtri = c.Filtri.ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        Ordini = c.Ordini.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        OrdineAsseX = c.OrdineAsseX?.ToList(),
        OrdineAsseY = c.OrdineAsseY?.ToList(),
        GruppiCompressi = new HashSet<string>(c.GruppiCompressi, StringComparer.Ordinal),
    };

    // ------------------------------------------------ strutture del giro

    private sealed class RichiestaGiroReport
    {
        public required int Generazione { get; init; }
        public required bool CartellaValida { get; init; }
        public required bool Anteprima { get; init; }
        public required bool Matrice { get; init; }
        public required bool Liste { get; init; }
        public required ReportFilter Filtro { get; init; }
        public required ConfigurazioneMatriceReport Configurazione { get; init; }
        public required IReadOnlyList<CellaNonApplicabile> Marcature { get; init; }
        public required int PrimaRiga { get; init; }
        public required int PrimaColonna { get; init; }
        public required int NumeroRigheFinestra { get; init; }
        public required int NumeroColonneFinestra { get; init; }
        public required bool MatriceRiutilizzabile { get; init; }
        public required string? ImprontaCataloghi { get; init; }
        public required string? ImprontaUltima { get; init; }
        public required IReadOnlyList<TestCase>? TestUltimo { get; init; }
        public required bool EMaster { get; init; }
        public required bool EMasterUltimo { get; init; }
        public required List<string> DimensioniMatrice { get; init; }
        public required List<string> ChiaviCampiCustom { get; init; }
        /// <summary>Tipo e opzioni del catalogo per chiave dei campi di sistema filtro (vedi <see cref="CampoSistemaCatalogoService.OpzioniFiltro"/>).</summary>
        public IReadOnlyDictionary<string, (TipoCampoStrumento Tipo, List<string> Opzioni)> OpzioniCampiCustom { get; init; } =
            new Dictionary<string, (TipoCampoStrumento Tipo, List<string> Opzioni)>();
        public required string? PercorsoIndice { get; init; }
        public required bool SnapshotPersistente { get; init; }
        public required LocalIndexRecoveryCoordinator? Coordinatore { get; init; }
        public bool PrimoGiro { get; init; }
        public SelezioniFiltri? Persistite { get; init; }
    }

    private sealed class RisultatoGiroReport
    {
        public ListeGiroReport? Liste { get; set; }
        public AnteprimaGiroReport? Anteprima { get; set; }
        public ReportMatriceService.MatricePreparata? Matrice { get; set; }
        public bool MatriceRiutilizzata { get; set; }
        public SelezioniFiltri? Selezioni { get; set; }
    }

    /// <summary>Selezioni di un giro per filtro: template, campi custom e dimensioni della matrice.</summary>
    private sealed record SelezioniFiltri(List<string> Template, Dictionary<string, List<string>> Custom, Dictionary<string, List<string>> Dimensioni);

    /// <summary>Categorie selezionate di default quando il Report non ha una selezione salvata (come nella costruzione in linea).</summary>
    private static readonly string[] CategorieDefaultReport = { "diagbase", "diagnosticabase", "diagavanzata", "diagnosticaavanzata", "programmazione" };

    /// <summary>Selezione salvata intersecata con le opzioni calcolate: stesso criterio di <see cref="FiltroMultiSelect.LegaPersistenza"/>
    /// (uguaglianza ordinale del valore); senza selezione salvata, nessuna voce.</summary>
    private static List<string> SelezioneSalvataInOpzioni(IReadOnlyList<string> salvati, IEnumerable<string> opzioni) =>
        salvati.Count == 0 ? new List<string>() : opzioni.Where(v => salvati.Contains(v)).ToList();

    /// <summary>Selezioni effettive del primo giro: persistite intersecate con le opzioni di questo giro; per le categorie senza
    /// selezione salvata, i default storici.</summary>
    private static SelezioniFiltri CalcolaSelezioniEffettive(RichiestaGiroReport r, ListeGiroReport liste)
    {
        var salvati = r.Persistite!;
        var template = SelezioneSalvataInOpzioni(salvati.Template, liste.ValoriTemplate.Select(v => v.Valore));
        var custom = r.ChiaviCampiCustom.ToDictionary(chiave => chiave,
            chiave => SelezioneSalvataInOpzioni(salvati.Custom[chiave], liste.ValoriCustom[chiave]));
        var dimensioni = r.DimensioniMatrice.ToDictionary(dimensione => dimensione, dimensione =>
            HaFiltroGlobale(dimensione) ? new List<string>()
            : dimensione == DimensioniMatrice.CategoriaFunzionalita && salvati.Dimensioni.GetValueOrDefault(dimensione, new List<string>()).Count == 0
                ? liste.ValoriDimensioni[dimensione]
                    .Where(v => CategorieDefaultReport.Contains(UdtLogImportService.NormalizzaPerConfronto(v).ToLowerInvariant())).ToList()
                : SelezioneSalvataInOpzioni(salvati.Dimensioni.GetValueOrDefault(dimensione, new List<string>()), liste.ValoriDimensioni[dimensione]),
            StringComparer.OrdinalIgnoreCase);
        return new SelezioniFiltri(template, custom, dimensioni);
    }

    /// <summary>Filtro del Report con le selezioni effettive al posto di quelle lette dall'interfaccia (che al primo giro sono vuote).</summary>
    private static ReportFilter FiltroConSelezioni(RichiestaGiroReport r, SelezioniFiltri s)
    {
        var filtro = new ReportFilter { DataDa = r.Filtro.DataDa, DataA = r.Filtro.DataA };
        filtro.Componente.AddRange(r.Filtro.Componente);
        filtro.Veicoli.AddRange(r.Filtro.Veicoli);
        filtro.TemplateUid.AddRange(s.Template.Select(Guid.Parse));
        filtro.CampiCustom = s.Custom.ToDictionary(x => x.Key, x => x.Value.ToList());
        return filtro;
    }

    /// <summary>Configurazione della matrice con le selezioni effettive delle dimensioni.</summary>
    private static ConfigurazioneMatriceReport ConfigurazioneConSelezioni(ConfigurazioneMatriceReport c, SelezioniFiltri s) => new()
    {
        CampiX = c.CampiX.ToList(),
        CampiY = c.CampiY.ToList(),
        ModalitaCella = c.ModalitaCella,
        BaseTempo = c.BaseTempo,
        FiltroTempoOperazione = c.FiltroTempoOperazione,
        FiltroTempoTotaleTest = c.FiltroTempoTotaleTest,
        StatiNascostiPresentazione = new HashSet<StatoCellaMatrice>(c.StatiNascostiPresentazione),
        Filtri = s.Dimensioni.ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        Ordini = c.Ordini.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        OrdineAsseX = c.OrdineAsseX?.ToList(),
        OrdineAsseY = c.OrdineAsseY?.ToList(),
        GruppiCompressi = new HashSet<string>(c.GruppiCompressi, StringComparer.Ordinal),
    };

    private sealed class ListeGiroReport
    {
        public bool Invariati { get; init; }
        public string? Impronta { get; init; }
        public IReadOnlyList<TestCase>? Tutti { get; init; }
        public List<(string Valore, string Etichetta)> ValoriTemplate { get; init; } = new();
        public Dictionary<string, List<string>> ValoriCustom { get; init; } = new();
        public Dictionary<string, List<string>> ValoriDimensioni { get; init; } = new();
    }

    private sealed class AnteprimaGiroReport
    {
        public bool AvvisoVolumeGrande { get; init; }
        public int Totale { get; init; }
        public List<RigaAnteprima> Righe { get; init; } = new();
    }
}
