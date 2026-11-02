using Avalonia.Controls;
using Avalonia.Interactivity;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views.Controls;

namespace TestLoom.App.Views;

public partial class TestCaseListView : UserControl
{
    // Sottoscrizione tenuta a mano, ora simmetrica su Attached/DetachedFromVisualTree (Addendum: audit
    // prestazioni 2026-08-25; corretto 2026-09-25 per il riuso View del ViewLocator). Il commento originale
    // ("la View viene ricreata ad ogni visita") non e' piu' vero: TestCaseListViewModel e' una pagina
    // cache-viva in MainViewModel._pagineCache e ora il ViewLocator RIUSA la stessa istanza di View per la
    // stessa istanza di ViewModel. Con solo DataContextChanged+Detached, dopo la prima uscita dalla pagina
    // Test l'handler veniva scollegato e non si riagganciava mai piu' alla rivisita (DataContext invariato,
    // nessun DataContextChanged) — "Azzera selezione" smetteva di funzionare. Ora Attached riaggancia sempre.
    private TestCaseListViewModel? _vmSottoscritto;
    private Action? _handlerAzzeramentoSelezione;

    public TestCaseListView()
    {
        InitializeComponent();
        CollegaFiltroData(ListaDataDa, nameof(TestCaseListViewModel.FiltroDataDa));
        CollegaFiltroData(ListaDataA, nameof(TestCaseListViewModel.FiltroDataA));

        // Ponte VM -> View per azzerare davvero la selezione visiva della griglia (Addendum: correzione
        // post-collaudo). Il VM non ha un riferimento alla DataGrid: quando pulisce Selezionati, avvisa da
        // qui, e qui si spegne anche la selezione reale — altrimenti la griglia restava evidenziata come se
        // qualcosa fosse ancora selezionato, mentre il VM non ne sapeva più nulla.
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += (_, _) => ScollegaViewModel();
    }

    /// <summary>Filtri di data (CalendarDatePicker): un testo digitato non valido viene segnalato al ViewModel, che azzera il
    /// vincolo del campo e mostra il messaggio sotto i filtri. La segnalazione si toglie al tasto premuto o all'uscita dal campo.</summary>
    private void CollegaFiltroData(Avalonia.Controls.CalendarDatePicker picker, string campo) =>
        FiltroDataCalendario.Collega(picker,
            () => { if (DataContext is TestCaseListViewModel vm) vm.SegnalaDataNonValida(campo); },
            () => { if (DataContext is TestCaseListViewModel vm) vm.AzzeraDataNonValida(campo); });

    private void OnAttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is TestCaseListViewModel vm && !ReferenceEquals(_vmSottoscritto, vm))
            CollegaViewModel(vm);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        ScollegaViewModel();
        if (DataContext is TestCaseListViewModel vm) CollegaViewModel(vm);
    }

    /// <summary>Filtri lista a selezione multipla (richiesta utente 6.4.2): stesso schema di NonConformitaView/
    /// ReportView/HomeView, flyout condiviso con ricerca oltre 10 voci.</summary>
    private void OnApriFiltroMultiSelect(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not FiltroMultiSelect filtro) return;
        FiltroMultiSelectFlyoutHelper.Apri(button, filtro);
    }

    private void CollegaViewModel(TestCaseListViewModel vm)
    {
        _vmSottoscritto = vm;
        _handlerAzzeramentoSelezione = () => TabellaTest.SelectedItems.Clear();
        vm.RichiestaAzzeramentoSelezioneGriglia += _handlerAzzeramentoSelezione;
    }

    private void ScollegaViewModel()
    {
        if (_vmSottoscritto != null && _handlerAzzeramentoSelezione != null)
            _vmSottoscritto.RichiestaAzzeramentoSelezioneGriglia -= _handlerAzzeramentoSelezione;
        _vmSottoscritto = null;
        _handlerAzzeramentoSelezione = null;
    }

    /// <summary>Sincronizza TestCaseListViewModel.Selezionati con la selezione reale della DataGrid (Addendum:
    /// audit UX 2026-08-26, punto 2). Necessario nel code-behind perché Avalonia 12 non espone
    /// DataGrid.SelectedItems come proprietà bindabile a due vie: il ViewModel non può osservarla da solo.
    ///
    /// Riscritto per leggere sempre grid.SelectedItems per intero (Addendum: correzione post-collaudo —
    /// segnalato dall'utente: in selezione multipla la riga selezionata per prima a volte restava fuori da
    /// Selezionati, e dopo un'azione di massa una selezione visivamente ancora presente risultava vuota al
    /// comando successivo). La versione precedente applicava solo il DELTA di ogni evento
    /// (e.AddedItems/e.RemovedItems), che presuppone che OGNI singolo evento SelectionChanged arrivi qui
    /// completo e nell'ordine giusto — un'assunzione fragile: un evento perso, duplicato, o il Reset della
    /// ItemsSource dopo Righe.ReplaceWith (che la DataGrid non notifica sempre con un SelectionChanged
    /// equivalente all'evento incrementale atteso) bastava a far divergere Selezionati dalla selezione vera.
    /// Rileggendo grid.SelectedItems per intero ad ogni evento, Selezionati non può mai desincronizzarsi:
    /// è sempre esattamente uno specchio di ciò che la griglia mostra selezionato in quel momento.</summary>
    private void OnSelezioneCambiata(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not TestCaseListViewModel vm || sender is not DataGrid grid) return;
        var selezionateOra = grid.SelectedItems.Cast<TestCase>().ToList();
        vm.Selezionati.Clear();
        foreach (var tc in selezionateOra) vm.Selezionati.Add(tc);
        vm.NotificaSelezioneCambiata();
    }

    /// <summary>"Verifica anomalie": apre l'elenco compatto; ogni riga apre lo stesso pop-up della pagina Anomalie. Dopo una modifica la lista
    /// si aggiorna.</summary>
    private async void OnVerificaAnomalie(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TestCaseListViewModel vm || TopLevel.GetTopLevel(this) is not Window proprietario) return;
        await UiExceptionGuard.Esegui(async () =>
        {
            var elenco = vm.PreparaVerificaAnomalie();
            if (elenco == null || elenco.Righe.Count == 0) return;
            var modificato = await AnomalieTestElencoWindow.Apri(proprietario, elenco);
            if (modificato) vm.Aggiorna();
        }, "Verifica anomalie dalla Lista Test");
    }

    /// <summary>Click sulle voci del menu "Cambia stato…" della barra azioni di massa. Tag+Click invece di
    /// Command dati-guidato perché il MenuFlyout vive in un popup separato dall'albero visuale nominato
    /// (stesso motivo di OnImpostaColoreEvidenziato in TestCaseEditorView).</summary>
    private void OnCambiaStatoSelezionati(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string statoTesto } || DataContext is not TestCaseListViewModel vm) return;
        if (!Enum.TryParse<StatoLavoroTest>(statoTesto, out var stato)) return;
        vm.CambiaStatoSelezionatiCommand.Execute(stato);
    }
}
