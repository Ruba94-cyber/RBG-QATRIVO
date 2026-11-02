using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Azioni del centro notifiche separate dal MainViewModel principale per mantenere isolato il
/// refactor dello storage bounded. Tutte le operazioni passano dal repository: la UI non conosce file/ledger.</summary>
public partial class MainViewModel
{
    private bool _normalizzandoConteggioNotifiche;

    // Ultimo conteggio autorevole (ledger completo, tipi silenziati esclusi) e l'operatore a cui si riferisce.
    // -1 = nessuna lettura ancora. I conteggi parziali delle 50 righe non lo modificano mai.
    private int _conteggioNotificheAutorevole = -1;
    private string? _operatoreConteggioNotifiche;

    // Riepilogo notifiche (6.6.0): l'emissione avviene una sola volta per avvio, alla prima ricarica con un operatore valido.
    private bool _riepilogoAvvioDaVerificare = true;

    /// <summary>Notifiche riepilogabili arrivate dal riepilogo precedente ed emesse in questo avvio (0 = nessun riepilogo
    /// nuovo). La shell la legge una sola volta all'avvio per l'avviso Windows: un avviso per riepilogo, non per notifica.</summary>
    public int NuoveNotificheRiepilogoAllAvvio { get; private set; }

    /// <summary>Elementi raccolti dal riepilogo in vigore (non letti). Vuoto = nessuna voce di riepilogo nel centro.</summary>
    public ObservableCollection<NotificaApp> RiepilogoElementi { get; } = new();

    [ObservableProperty] private string? riepilogoTitolo;
    [ObservableProperty] private string? riepilogoDettaglio;

    /// <summary>Riepilogo aperto: mostra gli elementi raccolti, ciascuno con il proprio pulsante "segna come letta".</summary>
    [ObservableProperty] private bool riepilogoAperto;

    /// <summary>Avviso alla shell (taskbar e tray) quando il conteggio autorevole cambia: (precedente, nuovo). Il
    /// precedente è -1 alla prima lettura o dopo un cambio operatore, quindi non va letto come notifica nuova.</summary>
    public event Action<int, int>? ConteggioNotificheRilevantiCambiato;

    /// <summary>
    /// Assegna un conteggio gia calcolato dalla lettura COMPLETA del ledger senza far scattare una seconda
    /// GetAll nello stesso giro. Il callback OnNotificheNonLetteChanged resta invece attivo per i vecchi punti
    /// del MainViewModel che assegnano un conteggio derivato dalle sole 50 righe visibili.
    /// </summary>
    private void ImpostaConteggioNotificheAutorevole(int value)
    {
        // P3.10: il ricalcolo può arrivare da un thread non UI (DatiRicaricati sollevato in background). Badge, binding,
        // tooltip e lampeggio della tray devono essere toccati solo dal thread UI: si accoda l'intera operazione (l'ultimo
        // valore accodato vince). Nei test headless (Application.Current nullo) resta sincrona sul thread chiamante,
        // come in InserisciInCacheSeAssenteAsync.
        if (Avalonia.Application.Current != null && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ImpostaConteggioNotificheAutorevole(value));
            return;
        }

        var operatore = _state.Config.OperatoreAttivo;
        var precedente = string.Equals(operatore, _operatoreConteggioNotifiche, StringComparison.Ordinal)
            ? _conteggioNotificheAutorevole
            : -1;
        _operatoreConteggioNotifiche = operatore;
        _conteggioNotificheAutorevole = value;

        if (NotificheNonLette != value)
        {
            try
            {
                _normalizzandoConteggioNotifiche = true;
                NotificheNonLette = value;
            }
            finally
            {
                _normalizzandoConteggioNotifiche = false;
            }
        }
        OnPropertyChanged(nameof(TestoMenuAccount));

        // Avviso alla shell a ogni conteggio autorevole: la shell aggiorna il tooltip e decide da sola se c'è un aumento
        // (precedente >= 0 && nuovo > precedente). I conteggi parziali delle 50 righe non passano da qui.
        try
        {
            ConteggioNotificheRilevantiCambiato?.Invoke(precedente, value);
        }
        catch (Exception ex)
        {
            // L'avviso Windows è accessorio: un suo errore non deve interrompere il centro notifiche.
            CrashLogService.Registra("MainViewModel.ConteggioNotificheRilevantiCambiato", ex);
        }
    }

    /// <summary>
    /// Ricarica il centro notifiche dalla fonte autorevole dopo qualunque mutazione. La UI continua a mostrare
    /// solo le 50 voci più recenti per non trasformare il flyout in un archivio infinito, ma il badge deve
    /// contare TUTTE le notifiche non lette dell'operatore, esclusi i tipi silenziati, e non soltanto quelle 50 visibili.
    /// </summary>
    private void RicaricaCentroNotifiche()
    {
        NotificheRecenti.Clear();
        if (!_state.CartellaValida || string.IsNullOrWhiteSpace(_state.Config.OperatoreAttivo))
        {
            ImpostaRiepilogo(null);
            ImpostaConteggioNotificheAutorevole(0);
            return;
        }

        // Una sola lettura del ledger: da questa stessa fotografia ricaviamo sia le 50 righe UI sia il badge.
        // In precedenza l'assegnazione di NotificheNonLette richiamava il callback di normalizzazione, causando
        // immediatamente una seconda GetAll e quindi doppio I/O su OneDrive per apertura/mark/delete.
        // Le notifiche di tipi silenziati restano nella lista; il badge le esclude (NotificaPreferenzeService).
        var operatore = _state.Config.OperatoreAttivo;
        var tutte = _state.Notifiche.GetAll(operatore);
        var ultimo = LeggiUltimoRiepilogo(operatore);
        if (_riepilogoAvvioDaVerificare)
        {
            _riepilogoAvvioDaVerificare = false;
            ultimo = EmettiRiepilogoAllAvvio(tutte, ultimo, operatore);
        }

        // Con il riepilogo in vigore, le notifiche che esso raccoglie compaiono solo dentro la voce di riepilogo.
        var riepilogo = NotificaRiepilogoService.Calcola(tutte, _state.Config, ultimo);
        ImpostaRiepilogo(riepilogo);
        var nelRiepilogo = riepilogo is null ? new HashSet<Guid>() : riepilogo.Elementi.Select(n => n.Uid).ToHashSet();
        foreach (var n in tutte.Where(n => !nelRiepilogo.Contains(n.Uid)).Take(50)) NotificheRecenti.Add(n);
        ImpostaConteggioNotificheAutorevole(NotificaRiepilogoService.ConteggioBadge(tutte, _state.Config, ultimo));
    }

    /// <summary>
    /// Questo callback normalizza un valore assegnato a mano (per esempio da SegnaTutteLette) contro il ledger completo, con lo stesso criterio di RicaricaCentroNotifiche: un conteggio parziale non può riportare il badge a un valore sbagliato. Il guard evita la ricorsione.
    /// </summary>
    partial void OnNotificheNonLetteChanged(int value)
    {
        if (_normalizzandoConteggioNotifiche || !_state.CartellaValida ||
            string.IsNullOrWhiteSpace(_state.Config.OperatoreAttivo)) return;

        // Il valore ricevuto può essere parziale: il badge si ricalcola sempre sul ledger completo, escludendo i tipi silenziati.
        var operatore = _state.Config.OperatoreAttivo;
        var tutte = _state.Notifiche.GetAll(operatore);
        ImpostaConteggioNotificheAutorevole(NotificaRiepilogoService.ConteggioBadge(tutte, _state.Config, LeggiUltimoRiepilogo(operatore)));
    }

    partial void OnMostraNotificheChanged(bool value)
    {
        if (value) RicaricaCentroNotifiche();
    }

    [RelayCommand]
    private void SegnaNotificaLetta(NotificaApp? notifica)
    {
        if (notifica == null || notifica.Letta) return;
        _state.Notifiche.MarkRead(notifica);
        RicaricaCentroNotifiche();
    }

    /// <summary>Apre il record collegato dalla notifica, lasciandone invariato lo stato di lettura.</summary>
    [RelayCommand]
    private async Task ApriElementoNotifica(NotificaApp? notifica)
    {
        if (notifica is not { HaElementoApribile: true }) return;
        MostraNotifiche = false;

        switch (notifica.Tipo)
        {
            case "Assegnazione":
            case "Classificazione":
            case "Conflitto":
                NavigaATestCase(notifica.ElementoId);
                break;
            case "ChangeRequest":
                NavigaAChangeRequest(notifica.ElementoId);
                break;
            case "SegnalazioneRete":
            case "nc":
                NavigaASegnalazioneRete(notifica.ElementoId);
                break;
            case "WuDaDecidere":
                break;
            case "ConflittoSincronizzazione":
                await VaiAImpostazioniGenerale();
                break;
        }
    }

    /// <summary>Il repository esegue un'unica append fisica per tutte le righe, invece di N aperture del
    /// ledger: stesso risultato funzionale, meno I/O e meno churn OneDrive.</summary>
    [RelayCommand]
    private void SegnaTutteNotificheLette()
    {
        if (!_state.CartellaValida || string.IsNullOrWhiteSpace(_state.Config.OperatoreAttivo)) return;
        _state.Notifiche.MarkAllRead(_state.Config.OperatoreAttivo);
        RicaricaCentroNotifiche();
    }

    [RelayCommand]
    private void EliminaNotifica(NotificaApp? notifica)
    {
        if (notifica == null) return;
        _state.Notifiche.Delete(notifica);
        RicaricaCentroNotifiche();
    }

    [RelayCommand]
    private void EliminaNotificheLette()
    {
        _state.Notifiche.DeleteRead(_state.Config.OperatoreAttivo);
        RicaricaCentroNotifiche();
    }

    partial void OnRiepilogoApertoChanged(bool value) => OnPropertyChanged(nameof(RiepilogoTestoApriChiudi));

    /// <summary>Etichetta del pulsante Apri/Chiudi della voce di riepilogo.</summary>
    public string RiepilogoTestoApriChiudi => RiepilogoAperto
        ? LocalizationService.Instance.Traduci("RiepilogoNotificheChiudiBtn")
        : LocalizationService.Instance.Traduci("RiepilogoNotificheApriBtn");

    /// <summary>True quando il centro mostra la voce di riepilogo (almeno una notifica raccolta).</summary>
    public bool HaRiepilogoNotifiche => RiepilogoElementi.Count > 0;

    /// <summary>Apre o chiude la voce di riepilogo: mostra o nasconde le notifiche che raccoglie.</summary>
    [RelayCommand]
    private void ApriRiepilogoNotifiche() => RiepilogoAperto = !RiepilogoAperto;

    /// <summary>Segna come lette le notifiche del riepilogo, e solo quelle. È l'unica azione che cambia lo stato di lettura
    /// di un riepilogo: calcolo ed emissione non leggono né eliminano nulla.</summary>
    [RelayCommand]
    private void SegnaRiepilogoLetto()
    {
        if (RiepilogoElementi.Count == 0 || !_state.CartellaValida || string.IsNullOrWhiteSpace(_state.Config.OperatoreAttivo)) return;
        _state.Notifiche.MarkReadMany(RiepilogoElementi.ToList());
        RicaricaCentroNotifiche();
    }

    /// <summary>Istante dell'ultimo riepilogo dell'operatore (stato locale di questo PC), o null se non ce n'è ancora.</summary>
    private DateTimeOffset? LeggiUltimoRiepilogo(string operatore) =>
        ViewStateService.LeggiData(_state, NotificaRiepilogoService.ChiaveUltimo(operatore));

    /// <summary>
    /// Emissione all'avvio: in Giornaliero al primo avvio del giorno, in AllAvvio ad ogni avvio. Il riepilogo prende l'istante
    /// corrente e le notifiche riepilogabili arrivate prima di esso entrano nella voce. Non modifica il ledger.
    /// </summary>
    private DateTimeOffset? EmettiRiepilogoAllAvvio(IReadOnlyList<NotificaApp> tutte, DateTimeOffset? ultimo, string operatore)
    {
        try
        {
            var esito = NotificaRiepilogoService.Emetti(_state.Config, tutte, ultimo, DateTimeOffset.Now);
            if (!esito.Emesso) return ultimo;
            ViewStateService.ScriviData(_state, NotificaRiepilogoService.ChiaveUltimo(operatore), esito.Ultimo);
            NuoveNotificheRiepilogoAllAvvio = esito.Nuove;
            return esito.Ultimo;
        }
        catch (Exception ex)
        {
            // Il riepilogo è accessorio: un errore nel salvarne l'istante non deve impedire il centro notifiche.
            CrashLogService.Registra("MainViewModel.RiepilogoNotifiche", ex);
            return ultimo;
        }
    }

    /// <summary>Aggiorna la voce di riepilogo del centro. Senza notifiche raccolte la voce scompare e si richiude.</summary>
    private void ImpostaRiepilogo(NotificaRiepilogoService.Riepilogo? riepilogo)
    {
        RiepilogoElementi.Clear();
        if (riepilogo is null)
        {
            RiepilogoTitolo = null;
            RiepilogoDettaglio = null;
            RiepilogoAperto = false;
        }
        else
        {
            foreach (var n in riepilogo.Elementi) RiepilogoElementi.Add(n);
            RiepilogoTitolo = riepilogo.Elementi.Count == 1
                ? string.Format(LocalizationService.Instance.Traduci("RiepilogoNotificaSingolaFmt"), 1)
                : string.Format(LocalizationService.Instance.Traduci("RiepilogoNotificheTitoloFmt"), riepilogo.Elementi.Count);
            RiepilogoDettaglio = string.Join("   ·   ", riepilogo.PerTipo.Select(v => $"{v.Conteggio} {v.Etichetta}"));
        }
        OnPropertyChanged(nameof(HaRiepilogoNotifiche));
        OnPropertyChanged(nameof(RiepilogoTestoApriChiudi));
    }
}
