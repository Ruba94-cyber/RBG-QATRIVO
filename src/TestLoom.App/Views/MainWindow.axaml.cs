using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

public partial class MainWindow : Window
{
    private bool _chiusuraConfermata;
    public bool RiduciInTrayAllaChiusura { get; init; }

    /// <summary>Ultimo modo di layout applicato: evita di riapplicare classi e token a ogni ridimensionamento.</summary>
    private ModoLayout? _modoApplicato;

    public MainWindow()
    {
        InitializeComponent();
        // Layout adattivo (6.6.1): il modo segue la dimensione client reale, con isteresi (LayoutAdattivo). Ogni cambio
        // aggiorna le classi di questa finestra (Window.compatto / Window.minimo) e i token DynamicResource; i dialoghi
        // leggono LayoutAdattivo.ModoCorrente all'apertura. Resized arriva prima del layout della finestra.
        Resized += (_, e) => AggiornaLayout(e.ClientSize);
        Opened += (_, _) => AggiornaLayout(ClientSize);
        Closing += OnClosing;
        KeyDown += OnKeyDown;
        // Esc su un overlay aperto (notifiche, guida, ricerca) deve chiuderlo anche quando il controllo con il
        // fuoco (es. DataGrid della pagina sotto) gestirebbe Esc per conto suo prima che salga alla finestra.
        AddHandler(KeyDownEvent, OnEscOverlayTunnel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.RichiestaAttivazioneSottoscrivi(() => Avalonia.Threading.Dispatcher.UIThread.Post(PortaInPrimoPiano));
        };

        // Unico punto di implementazione della conferma per tutta l'app (navigazione, logout, "Torna alla
        // lista", chiusura finestra): stesso testo ovunque invece di ripeterlo/farlo divergere per punto.
        ConfirmService.Handler = async (titolo, messaggio, testoSi, testoNo) =>
        {
            var dialog = ConfirmDialog.Crea(titolo, messaggio, testoSi, testoNo);
            return await dialog.ShowDialog<bool>(DialogoPiuInAlto());
        };

        // Stessa idea di ConfirmService: un solo punto reale (finestra Avalonia) dietro un servizio che i
        // ViewModel possono chiamare senza dipendere da Avalonia — usato dal pulsante "Espandi" sui campi
        // di testo lunghi (Addendum, richiesta utente: scrivere tanto in un piccolo TextBox multilinea
        // annegato nel form è scomodo, una finestra dedicata più grande lo rende comodo).
        // 6.6.0: "Copia report" degli import. Unico punto che tocca gli appunti di sistema (vedi ClipboardService).
        ClipboardService.Handler = async testo =>
        {
            if (Clipboard is not { } appunti) return false;
            await appunti.SetTextAsync(testo);
            return true;
        };

        TextEditService.Handler = async (titolo, valoreCorrente) =>
        {
            var dialog = TextEditDialog.Crea(titolo, valoreCorrente);
            return await dialog.ShowDialog<string?>(this);
        };

        // Stessa idea di ConfirmService/TextEditService: picker generico dietro un servizio, usato al posto
        // delle liste sempre visibili per i campi con elenchi potenzialmente enormi (feedback utente
        // 2026-08-20) — componente, veicoli, test collegati/correlati, anomalie, requisiti, funzionalità, operatori.
        SelezionaDialogService.Handler = async (titolo, voci, multiSelezione) =>
        {
            var dialog = SelezionaDialog.Crea(titolo, voci, multiSelezione);
            // Chiusura con la X / Esc / Alt+F4: ShowDialog restituisce null (nessun risultato impostato). Va
            // trattata come annullamento, altrimenti i chiamanti (ScegliEcu, ScegliFunzionalita...) andavano in
            // NullReferenceException non gestita (crash.log dell'altro PC, 2026-08-21 -> 2026-10-06).
            return await dialog.ShowDialog<IReadOnlyList<VoceSelezionabile>?>(this) ?? Array.Empty<VoceSelezionabile>();
        };

        MergeConflittoDialogService.Handler = async conflitti =>
        {
            var dialog = MergeConflittoDialog.Crea(conflitti);
            return await dialog.ShowDialog<IReadOnlyDictionary<string, SceltaMerge>?>(this);
        };
    }

    private void AggiornaLayout(Size dimensione)
    {
        if (LayoutAdattivo.Aggiorna(dimensione.Width, dimensione.Height) is { } modo) ApplicaModo(modo);
    }

    /// <summary>Classi della finestra e token DynamicResource per il modo dato. I token stanno in Application.Resources
    /// cosi' li vedono anche i dialoghi aperti dopo; i valori di Standard coincidono con quelli di App.axaml.</summary>
    private void ApplicaModo(ModoLayout modo)
    {
        if (modo == _modoApplicato) return;
        _modoApplicato = modo;
        foreach (var classe in LayoutAdattivo.TutteLeClassi) Classes.Remove(classe);
        foreach (var classe in LayoutAdattivo.Classi(modo)) Classes.Add(classe);

        var token = LayoutAdattivo.Token(modo);
        var risorse = Application.Current?.Resources;
        if (risorse == null) return;
        risorse["PagePadding"] = new Thickness(token.PagePadding);
        risorse["CardPadding"] = new Thickness(token.CardPadding);
        risorse["SpaziaturaSezioni"] = token.SpaziaturaSezioni;
        risorse["TitoloPagina"] = token.TitoloPagina;
        risorse["KpiNumero"] = token.KpiNumero;
        risorse["KpiNumeroPiccolo"] = token.KpiNumeroPiccolo;
        risorse["LarghezzaSidebar"] = token.LarghezzaSidebar;
        risorse["MaxLarghezzaPagina"] = token.MaxLarghezzaPagina;
        risorse["AltezzaMinimaTabella"] = token.AltezzaMinimaTabella;
    }

    /// <summary>Finestra a cui agganciare una conferma: se e aperto un dialogo modale (es. il Dettaglio anomalia) la
    /// conferma ne diventa figlia, altrimenti due dialoghi figli della stessa finestra principale si
    /// sovrapporrebbero senza che il primo risulti bloccato.</summary>
    private Window DialogoPiuInAlto()
    {
        Window corrente = this;
        while (corrente.OwnedWindows.Count > 0 && corrente.OwnedWindows[^1] is { IsVisible: true } successivo) corrente = successivo;
        return corrente;
    }

    /// <summary>Punto unico delle scorciatoie globali (Addendum: audit UX 2026-08-26, punto 6). Sta qui e non
    /// nelle singole view perché deve funzionare qualunque sia il controllo con il fuoco: gli eventi di
    /// tastiera risalgono fino alla finestra, e nessuno dei controlli attraversati gestisce queste
    /// combinazioni. Ogni ramo marca e.Handled SOLO se l'azione è stata davvero eseguita, altrimenti la
    /// combinazione resta disponibile al controllo che ha il fuoco (es. Ctrl+F dentro un campo di testo su
    /// una pagina che non dichiara un filtro).</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // Ricerca globale: la sua gestione precede tutto il resto perché mentre l'overlay è aperto il fuoco
        // è nel suo campo e Invio/Esc devono appartenere a lei, non alla pagina sotto.
        if (ctrl && e.Key == Key.K)
        {
            vm.ApriRicercaGlobaleCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RicercaGlobaleTextBox.Focus());
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && vm.MostraRicercaGlobale && RicercaGlobaleTextBox.IsFocused)
        {
            vm.ApriPrimoRisultatoRicerca();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            // Un livello per pressione: prima gli overlay, poi la modifica in corso nella pagina.
            e.Handled = vm.ChiudiOverlayDaTastiera() || vm.AnnullaDaTastiera();
            return;
        }
        // Alt+← come nei browser: torna alla posizione precedente (con conferma se ci sono modifiche).
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Left && !vm.MostraRicercaGlobale)
        {
            if (vm.PuoTornareIndietro) vm.TornaIndietroCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (!ctrl) return;

        switch (e.Key)
        {
            case Key.S:
                // Il TestCase ha un wrapper transazionale dedicato. Passare dall'interfaccia generica
                // IScorciatoiePagina richiamerebbe il vecchio SalvaCommand e aggirerebbe il Unit of Work.
                // Tutti gli altri editor continuano invece a usare la pipeline generica preesistente.
                if (vm.CurrentPage is TestCaseEditorViewModel test && test.MostraAzioniModifica)
                {
                    test.SalvaSicuroCommand.Execute(null);
                    e.Handled = true;
                }
                else
                {
                    e.Handled = vm.SalvaDaTastiera();
                }
                break;
            case Key.N:
                e.Handled = vm.NuovoDaTastiera();
                break;
            case Key.F:
                e.Handled = ScorciatoieTastiera.PortaFuocoAlFiltro(PaginaCorrenteHost);
                break;
            // Ctrl+1..9 sulla riga numeri e sul tastierino: chi ha il tastierino lo usa per i numeri e si
            // aspetta che funzioni identico.
            case >= Key.D1 and <= Key.D9:
                e.Handled = vm.VaiASezioneDaTastiera(e.Key - Key.D1 + 1);
                break;
            case >= Key.NumPad1 and <= Key.NumPad9:
                e.Handled = vm.VaiASezioneDaTastiera(e.Key - Key.NumPad1 + 1);
                break;
        }
    }

    private void OnEscOverlayTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || DataContext is not MainViewModel vm) return;
        if (vm.ChiudiOverlayDaTastiera()) e.Handled = true;
    }

    private void PortaInPrimoPiano()
    {
        Show();
        // Solo una finestra minimizzata torna Normale: una massimizzata (scelta dell'utente) resta tale.
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>Gestore di chiusura: non deve mai far cadere il processo. Un'eccezione di chiusura (es. manutenzione storage)
    /// viene registrata e la chiusura prosegue; il corpo è in <see cref="ChiudiFinestra"/>.</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        DiagnosticTraceService.Registra("Chiusura", "finestra principale: evento Closing ricevuto");
        try { await ChiudiFinestra(e); }
        catch (Exception ex) { CrashLogService.Registra("Chiusura finestra principale", ex); }
    }

    private async Task ChiudiFinestra(WindowClosingEventArgs e)
    {
        ChiusuraDiagnostica.Inizia("finestra principale");
        if (_chiusuraConfermata)
        {
            (DataContext as IDisposable)?.Dispose();
            ChiusuraDiagnostica.Registra("finestra: uscita confermata, ViewModel disposto");
            return;
        }
        if (RiduciInTrayAllaChiusura)
        {
            e.Cancel = true;
            Hide();
            ChiusuraDiagnostica.Registra("finestra: nascosta nella tray");
            return;
        }
        if (DataContext is not MainViewModel vm) return;
        if (!vm.HaModificheNonSalvate())
        {
            // Nessuna modifica in sospeso non significa "nessun lock attivo": un editor master data aperto
            // in modifica senza aver ancora cambiato nulla tiene comunque il file bloccato (feedback utente
            // 2026-08-24, punto 5) — altrimenti restava agganciato fino al timeout di 20 minuti.
            // Closing è un async void: annulliamo subito l'evento, prima del primo await, altrimenti Avalonia
            // può chiudere la finestra mentre il rilascio del lock sta ancora aspettando l'I/O.
            e.Cancel = true;
            if (!await vm.RilasciaLockEditorAttivoAsync())
            {
                ChiusuraDiagnostica.Registra("finestra: chiusura annullata, rilascio lock in attesa di retry");
                return;
            }
            ChiusuraDiagnostica.Registra("finestra: lock editor rilasciato, chiusura confermata");
            _chiusuraConfermata = true;
            Close(); // il secondo Closing dispone il ViewModel e lascia terminare il ciclo UI normalmente.
            return;
        }

        e.Cancel = true;
        ChiusuraDiagnostica.Registra("finestra: modifiche non salvate, dialogo di conferma");
        await UiExceptionGuard.Esegui(async () =>
        {
            if (await vm.PreparaUscitaDefinitivaAsync())
            {
                _chiusuraConfermata = true;
                Close();
            }
        }, LocalizationService.Instance.Traduci("ConfermaChiusuraFinestraTitolo"));
    }

    public void MostraDaTray() => PortaInPrimoPiano();

    /// <summary>Avviso Windows per le nuove notifiche (Impostazioni: "Abilita notifiche Windows"): fa lampeggiare il
    /// pulsante della barra quando la finestra non è in primo piano. Nessun dialogo modale; con la finestra nascosta
    /// nella tray resta il solo tooltip dell'icona.</summary>
    public void SegnalaNuoveNotifiche()
    {
        if (!IsVisible || (IsActive && WindowState != WindowState.Minimized)) return;
        NotificaTaskbarService.Lampeggia(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
    }
    public void ConsentiChiusuraDefinitiva() => _chiusuraConfermata = true;

    /// <summary>"Esci" dal menu della tray: stessa protezione delle modifiche non salvate della chiusura finestra.
    /// Prima l'uscita dalla tray saltava la conferma e scartava in silenzio l'input dell'editor aperto.</summary>
    public async Task<bool> RichiediUscitaDefinitivaAsync()
    {
        if (DataContext is MainViewModel vm)
        {
            if (vm.HaModificheNonSalvate()) PortaInPrimoPiano(); // la finestra deve essere visibile per il dialogo
            if (!await vm.PreparaUscitaDefinitivaAsync())
            {
                // Un errore nel rilascio del lock annulla l'uscita e lascia disponibile il comando di retry.
                // Anche l'uscita dalla tray deve quindi riportare la finestra davanti all'utente.
                PortaInPrimoPiano();
                return false;
            }
        }
        _chiusuraConfermata = true;
        return true;
    }
}
