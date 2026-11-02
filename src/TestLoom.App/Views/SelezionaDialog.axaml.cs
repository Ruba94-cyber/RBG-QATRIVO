using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

public partial class SelezionaDialog : Window
{
    private List<VoceSelezionabile> _tutte = new();
    private bool _multiSelezione;
    private PickerMotore<VoceSelezionabile>? _motore;
    private PickerRicercaController<VoceSelezionabile>? _ricerca;
    private IReadOnlyList<VoceSelezionabile> _vociVisibili = Array.Empty<VoceSelezionabile>();

    // Selezione multipla conservata fra un filtro e l'altro, in ordine di scelta: cambiare la ricerca (o muovere
    // il cursore con le frecce) non fa perdere le voci gia' spuntate.
    private readonly List<VoceSelezionabile> _scelte = new();
    private bool _aggiornandoLista;
    private bool _navigazioneTastiera;

    // Testo dell'ultima ricerca mostrata. Se la casella lo supera (ricerca ancora in attesa del debounce), frecce e
    // Invio la allineano prima di agire: l'azione deve riguardare l'elenco che l'utente vede.
    private string _testoApplicato = string.Empty;

    public SelezionaDialog()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this);
        FiltroBox.TextChanged += (_, _) => _ricerca?.Digita(FiltroBox.Text);
        FiltroBox.KeyDown += OnFiltroKeyDown;
        // Tunnel: spazio e frecce vanno gestiti prima del comportamento predefinito della ListBox (che in selezione
        // multipla sostituirebbe la selezione a ogni freccia).
        Lista.AddHandler(InputElement.KeyDownEvent, OnListaTunnelKeyDown, RoutingStrategies.Tunnel);
        Lista.KeyDown += OnListaKeyDown;
        Lista.KeyUp += (_, _) => { if (_navigazioneTastiera) { _navigazioneTastiera = false; RipristinaScelteVisibili(); } };
        Lista.SelectionChanged += OnListaSelectionChanged;
        Lista.DoubleTapped += (_, _) => { if (!_multiSelezione) Conferma(); };
        SelezionaVisibiliButton.Click += (_, _) => ApplicaSelezioneVisibile(true);
        DeselezionaVisibiliButton.Click += (_, _) => ApplicaSelezioneVisibile(false);
        SelezionaButton.Click += (_, _) => Conferma();
        AnnullaButton.Click += (_, _) => Close(Array.Empty<VoceSelezionabile>());
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(Array.Empty<VoceSelezionabile>()); };
        Opened += (_, _) => FiltroBox.Focus();
        Closed += (_, _) => _ricerca?.Dispose();
    }

    /// <summary>Mostra un esito di ricerca: stessi dati alla ListBox virtualizzata (nessun UI item creato per voce),
    /// contatore sempre aggiornato, selezione multipla ripristinata sulle voci ancora visibili.</summary>
    private void Applica(PickerRisultato<VoceSelezionabile> risultato)
    {
        _testoApplicato = risultato.Query;
        _vociVisibili = risultato.Voci;
        _aggiornandoLista = true;
        try
        {
            Lista.ItemsSource = risultato.Voci;
            RipristinaScelteVisibili();
        }
        finally { _aggiornandoLista = false; }
        ConteggioText.Text = TestoConteggio(risultato);
    }

    private void RipristinaScelteVisibili()
    {
        if (!_multiSelezione || _scelte.Count == 0 || Lista.SelectedItems is not { } selezione) return;
        var daRipristinare = new HashSet<VoceSelezionabile>(_scelte);
        _aggiornandoLista = true;
        try
        {
            for (var i = 0; i < _vociVisibili.Count; i++)
            {
                var voce = _vociVisibili[i];
                if (daRipristinare.Contains(voce) && !selezione.Contains(voce)) selezione.Add(voce);
            }
        }
        finally { _aggiornandoLista = false; }
    }

    private static string TestoConteggio(PickerRisultato<VoceSelezionabile> risultato)
    {
        var loc = LocalizationService.Instance;
        if (risultato.Voci.Count == 0) return loc.Traduci("PickerNessunRisultato");
        return string.IsNullOrWhiteSpace(risultato.Query)
            ? string.Format(loc.Traduci("PickerVociFmt"), risultato.TotaleSorgente)
            : string.Format(loc.Traduci("PickerRisultatiFmt"), risultato.Voci.Count);
    }

    private void OnListaSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_aggiornandoLista || _navigazioneTastiera || !_multiSelezione) return;
        foreach (var rimossa in e.RemovedItems.OfType<VoceSelezionabile>()) _scelte.Remove(rimossa);
        foreach (var aggiunta in e.AddedItems.OfType<VoceSelezionabile>())
            if (!_scelte.Contains(aggiunta)) _scelte.Add(aggiunta);
    }

    /// <summary>Ricerca ancora in attesa del debounce: calcola e mostra subito il risultato per il testo corrente (il
    /// calcolo e' di pochi millisecondi anche a 100k voci, misurato in PickerRicercaBenchmarkTests) e annulla
    /// l'eventuale ricerca precedente, cosi' la freccia o Invio non agiscono su un elenco superato.</summary>
    private void AllineaRicercaPrimaDellAzione()
    {
        var testo = FiltroBox.Text ?? string.Empty;
        if (_motore == null || testo == _testoApplicato) return;
        _ricerca?.Annulla();
        Applica(_motore.Cerca(testo));
    }

    /// <summary>Freccia dal campo di ricerca: il cursore passa alla lista (prima voce se nulla e' evidenziato).</summary>
    private void OnFiltroKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Enter) AllineaRicercaPrimaDellAzione();
        switch (e.Key)
        {
            case Key.Down when _vociVisibili.Count > 0:
                if (Lista.SelectedIndex < 0) PosizionaCursore(0);
                Lista.Focus();
                e.Handled = true;
                break;
            case Key.Enter:
                ConfermaDaTastiera();
                e.Handled = true;
                break;
        }
    }

    private void OnListaTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (_multiSelezione && e.Key == Key.Space && Lista.SelectedItem is VoceSelezionabile corrente)
        {
            AlternaScelta(corrente);
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            _navigazioneTastiera = true;
    }

    private void OnListaKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ConfermaDaTastiera();
                e.Handled = true;
                break;
            case Key.Up when Lista.SelectedIndex <= 0:
                FiltroBox.Focus();
                e.Handled = true;
                break;
        }
    }

    private void PosizionaCursore(int indice)
    {
        _navigazioneTastiera = true; // non e' una scelta dell'utente: non deve spuntare la voce
        try { Lista.SelectedIndex = indice; }
        finally { _navigazioneTastiera = false; }
    }

    private void AlternaScelta(VoceSelezionabile voce)
    {
        if (_scelte.Remove(voce))
        {
            Lista.SelectedItems?.Remove(voce);
            return;
        }
        _scelte.Add(voce);
        if (Lista.SelectedItems is { } selezione && !selezione.Contains(voce)) selezione.Add(voce);
    }

    private void ApplicaSelezioneVisibile(bool selezionata)
    {
        AllineaRicercaPrimaDellAzione();
        if (!_multiSelezione || Lista.SelectedItems is not { } selezione) return;
        _aggiornandoLista = true;
        try
        {
            foreach (var voce in _vociVisibili)
            {
                if (selezionata)
                {
                    if (!_scelte.Contains(voce)) _scelte.Add(voce);
                    if (!selezione.Contains(voce)) selezione.Add(voce);
                }
                else
                {
                    _scelte.Remove(voce);
                    selezione.Remove(voce);
                }
            }
        }
        finally { _aggiornandoLista = false; }
    }

    private void ConfermaDaTastiera()
    {
        if (_multiSelezione)
        {
            // Enter conferma le voci spuntate; senza spunte conferma la voce evidenziata dal cursore.
            if (_scelte.Count == 0 && Lista.SelectedItem is VoceSelezionabile corrente) _scelte.Add(corrente);
            if (_scelte.Count > 0) Conferma();
            return;
        }
        if (Lista.SelectedItem == null && _vociVisibili.Count > 0) PosizionaCursore(0);
        if (Lista.SelectedItem != null) Conferma();
    }

    private void Conferma()
    {
        IReadOnlyList<VoceSelezionabile> scelte = _multiSelezione
            ? _scelte.ToList()
            : Lista.SelectedItems?.Cast<VoceSelezionabile>().ToList() ?? new List<VoceSelezionabile>();
        Close(scelte);
    }

    public static SelezionaDialog Crea(string titolo, IReadOnlyList<VoceSelezionabile> voci, bool multiSelezione)
    {
        var d = new SelezionaDialog { Title = titolo, _tutte = voci.ToList(), _multiSelezione = multiSelezione };
        var loc = LocalizationService.Instance;
        d.TitoloText.Text = titolo;
        d.SuggerimentoMultiploText.Text = loc.Traduci("PickerSuggerimentoMultiplo");
        d.FiltroBox.PlaceholderText = loc.Traduci("PickerCercaPlaceholder");
        d.AzioniSelezioneMultipla.IsVisible = multiSelezione;
        d.SelezionaVisibiliButton.Content = loc.Traduci("PickerSelezionaVisibili");
        d.DeselezionaVisibiliButton.Content = loc.Traduci("PickerDeselezionaVisibili");
        d.AnnullaButton.Content = loc.Traduci("AnnullaSemplice");
        // SelectionMode.Toggle (feedback utente 2026-08-20): senza, la selezione multipla di Avalonia richiede
        // tenere premuto Ctrl per ogni voce oltre la prima — poco intuitivo. Con Toggle un click semplice
        // aggiunge/rimuove la voce dalla selezione, come una checkbox.
        d.Lista.SelectionMode = multiSelezione ? (SelectionMode.Multiple | SelectionMode.Toggle) : SelectionMode.Single;
        d.SuggerimentoMultiploText.IsVisible = multiSelezione;
        d.SelezionaButton.Content = loc.Traduci(multiSelezione ? "PickerAggiungiSelezionati" : "PickerSeleziona");

        // Ricerca: debounce, calcolo fuori dal thread UI, scarto dei risultati superati (vedi PickerRicercaController).
        d._motore = new PickerMotore<VoceSelezionabile>(d._tutte, PickerTesto.ChiaveVoce);
        d._ricerca = new PickerRicercaController<VoceSelezionabile>(d._motore, d.Applica, PickerRicercaController<VoceSelezionabile>.DebounceDefaultMs);
        d.Applica(new PickerRisultato<VoceSelezionabile>(string.Empty, d._tutte, d._tutte.Count));
        _ = d._motore.PreparaAsync(); // chiavi normalizzate in anticipo: il primo tasto non paga il costo di costruzione
        return d;
    }
}
