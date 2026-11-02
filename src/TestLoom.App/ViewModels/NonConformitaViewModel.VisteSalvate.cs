using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Viste salvate della pagina Anomalie/CR, stesso modello di TestCaseListViewModel: una combinazione di
/// filtri, ordinamento e ricerca libera salvata con un nome e riapplicata con un clic. Ogni filtro ha gia' la sua
/// persistenza automatica (ultima selezione per PC/operatore, ViewStateService); la vista invece e' un salvataggio
/// ESPLICITO con nome, conservato in AppConfig.VisteSalvate sotto ChiaveVisteSalvate.</summary>
public partial class NonConformitaViewModel
{
    private const string ChiaveVisteSalvate = "anomalie-list";
    private const char SeparatoreValori = '\u001F';

    public ObservableCollection<VistaSalvataFiltri> VisteSalvate { get; } = new();

    [ObservableProperty] private VistaSalvataFiltri? vistaSelezionata;

    // Vero mentre la vista appena salvata viene riportata nel combo: e' gia' applicata, non va riapplicata.
    private bool _sospendiApplicaVista;

    private void CaricaVisteSalvate()
    {
        VisteSalvate.Clear();
        foreach (var v in _state.Config.VisteSalvate.GetValueOrDefault(ChiaveVisteSalvate) ?? new List<VistaSalvataFiltri>())
            VisteSalvate.Add(v);
    }

    partial void OnVistaSelezionataChanged(VistaSalvataFiltri? value)
    {
        if (value != null && !_sospendiApplicaVista) ApplicaVista(value);
        EliminaVistaSelezionataCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Un cambio manuale dei filtri (fuori da un batch) non e' piu' "la vista selezionata": svuota il combo,
    /// cosi' riscegliere la stessa vista la riapplica (stesso criterio di FiltroCambiato in TestCaseListViewModel).</summary>
    private void FiltroManualeCambiato()
    {
        if (_refreshInCorso == 0 && VistaSelezionata != null) VistaSelezionata = null;
    }

    private Dictionary<string, string?> CatturaFiltriCorrenti() => new()
    {
        ["Testo"] = string.IsNullOrWhiteSpace(FiltroTesto) ? null : FiltroTesto,
        ["Ticket"] = string.IsNullOrWhiteSpace(FiltroTicket) ? null : FiltroTicket,
        ["Tipo"] = FiltroTipo.ToString(),
        ["Stato"] = FiltroStato?.ToString(),
        ["StatoDettaglio"] = FiltroStatoDettaglio?.ToString(),
        ["Destinatario"] = FiltroDestinatario?.ToString(),
        ["StatoCr"] = FiltroStatoCr?.ToString(),
        ["TicketEsta"] = FiltroTicketEsta.ToString(),
        ["Gamma"] = Serializza(FiltroGamma),
        ["Veicolo"] = Serializza(FiltroVeicolo),
        ["Componente"] = Serializza(FiltroEcu),
        ["Funzionalita"] = Serializza(FiltroFunzionalita),
        ["Assegnatario"] = Serializza(FiltroAssegnatario),
        ["CreatoreCr"] = Serializza(FiltroCreatoreCr),
        ["Priorita"] = Serializza(FiltroPriorita),
        ["Gravita"] = Serializza(FiltroGravita),
        ["StatoAnomalia"] = Serializza(FiltroStatoAnomalia),
        ["DataAperturaDa"] = IsoDataPerVista(DataAperturaDa),
        ["DataAperturaA"] = IsoDataPerVista(DataAperturaA),
        ["DataChiusuraDa"] = IsoDataPerVista(DataChiusuraDa),
        ["DataChiusuraA"] = IsoDataPerVista(DataChiusuraA),
        ["DurataMinimaGiorni"] = FiltroDurataMinimaGiorni,
        ["UnitaDurata"] = FiltroUnitaDurata.ToString(),
        ["OrdinamentoChiave"] = OrdinamentoChiave.ToString(),
        ["OrdinamentoDecrescente"] = OrdinamentoDecrescente ? "1" : "0",
    };

    /// <summary>Riapplica tutti i filtri della vista. Un solo Ricarica() alla fine: con _refreshInCorso > 0 ogni setter
    /// (testo, multi-select, date, ordinamento) non ricalcola da solo, stesso guard di ReimpostaFiltri.</summary>
    private void ApplicaVista(VistaSalvataFiltri vista)
    {
        var v = vista.Valori;
        _refreshInCorso++;
        try
        {
            FiltroTesto = v.GetValueOrDefault("Testo") ?? "";
            FiltroTicket = v.GetValueOrDefault("Ticket") ?? "";
            FiltroTipo = TryEnum<FiltroTipoAnomaliaOCr>(v.GetValueOrDefault("Tipo"), out var tipo) ? tipo : FiltroTipoAnomaliaOCr.Entrambi;
            FiltroStato = TryEnum<StatoNonConformita>(v.GetValueOrDefault("Stato"), out var stato) ? stato : null;
            FiltroStatoDettaglio = TryEnum<StatoDettaglioNc>(v.GetValueOrDefault("StatoDettaglio"), out var dettaglio) ? dettaglio : null;
            FiltroDestinatario = TryEnum<DestinatarioSegnalazione>(v.GetValueOrDefault("Destinatario"), out var destinatario) ? destinatario : null;
            FiltroStatoCr = TryEnum<StatoChangeRequest>(v.GetValueOrDefault("StatoCr"), out var statoCr) ? statoCr : null;
            FiltroTicketEsta = TryEnum<FiltroPresenzaTicketEsta>(v.GetValueOrDefault("TicketEsta"), out var ticketEsta) ? ticketEsta : FiltroPresenzaTicketEsta.Tutti;
            ApplicaSelezione(FiltroGamma, v.GetValueOrDefault("Gamma"));
            ApplicaSelezione(FiltroVeicolo, v.GetValueOrDefault("Veicolo"));
            ApplicaSelezione(FiltroEcu, v.GetValueOrDefault("Componente"));
            ApplicaSelezione(FiltroFunzionalita, v.GetValueOrDefault("Funzionalita"));
            ApplicaSelezione(FiltroAssegnatario, v.GetValueOrDefault("Assegnatario"));
            ApplicaSelezione(FiltroCreatoreCr, v.GetValueOrDefault("CreatoreCr"));
            ApplicaSelezione(FiltroPriorita, v.GetValueOrDefault("Priorita"));
            ApplicaSelezione(FiltroGravita, v.GetValueOrDefault("Gravita"));
            ApplicaSelezione(FiltroStatoAnomalia, v.GetValueOrDefault("StatoAnomalia"));
            FiltroDataAperturaDa = DataFiltroPersistita(v.GetValueOrDefault("DataAperturaDa"));
            FiltroDataAperturaA = DataFiltroPersistita(v.GetValueOrDefault("DataAperturaA"));
            FiltroDataChiusuraDa = DataFiltroPersistita(v.GetValueOrDefault("DataChiusuraDa"));
            FiltroDataChiusuraA = DataFiltroPersistita(v.GetValueOrDefault("DataChiusuraA"));
            FiltroDurataMinimaGiorni = v.GetValueOrDefault("DurataMinimaGiorni") ?? "";
            FiltroUnitaDurata = TryEnum<UnitaDurataNc>(v.GetValueOrDefault("UnitaDurata"), out var unita) ? unita : UnitaDurataNc.Giorni;
            AzzeraTutteLeDataNonValide();
            OrdinamentoChiave = TryEnum<AnomalieOrdinamentoChiave>(v.GetValueOrDefault("OrdinamentoChiave"), out var chiave) ? chiave : AnomalieOrdinamentoChiave.Apertura;
            OrdinamentoDecrescente = v.GetValueOrDefault("OrdinamentoDecrescente") != "0";
        }
        finally { _refreshInCorso--; _debounceTesto?.Cancel(); }
        PaginaRighe = 1;
        PaginaCr = 1;
        Ricarica();
    }

    [RelayCommand]
    private Task SalvaVistaCorrente() =>
        ComandoAsincrono.EseguiAsync(SalvaVistaCorrenteCoreAsync, "Salvataggio della vista delle non conformità", m => MessaggioErrore = m);

    private async Task SalvaVistaCorrenteCoreAsync()
    {
        var nome = await TextEditService.Modifica(LocalizationService.Instance.Traduci("SalvaVistaTitolo"), "");
        if (string.IsNullOrWhiteSpace(nome)) return;
        nome = nome.Trim();

        if (!_state.Config.VisteSalvate.TryGetValue(ChiaveVisteSalvate, out var lista))
        {
            lista = new List<VistaSalvataFiltri>();
            _state.Config.VisteSalvate[ChiaveVisteSalvate] = lista;
        }
        lista.RemoveAll(v => v.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase));
        lista.Add(new VistaSalvataFiltri { Nome = nome, Valori = CatturaFiltriCorrenti() });
        _state.ConfigService.Save(_state.Config);
        CaricaVisteSalvate();
        // La vista appena salvata coincide gia' con i filtri correnti: va solo selezionata nel combo, non riapplicata.
        _sospendiApplicaVista = true;
        try { VistaSelezionata = VisteSalvate.First(v => v.Nome == nome); }
        finally { _sospendiApplicaVista = false; }
    }

    private bool PuoEliminareVistaSelezionata() => VistaSelezionata != null;

    [RelayCommand(CanExecute = nameof(PuoEliminareVistaSelezionata))]
    private Task EliminaVistaSelezionata() =>
        ComandoAsincrono.EseguiAsync(EliminaVistaSelezionataCoreAsync, "Eliminazione della vista salvata", m => MessaggioErrore = m);

    private async Task EliminaVistaSelezionataCoreAsync()
    {
        var vista = VistaSelezionata;
        if (vista == null) return;
        if (!await ConfirmService.Chiedi(LocalizationService.Instance.Traduci("EliminaVistaTitoloConferma"),
            string.Format(LocalizationService.Instance.Traduci("EliminaVistaBodyFmt"), vista.Nome),
            LocalizationService.Instance.Traduci("Elimina"), LocalizationService.Instance.Traduci("Annulla"))) return;

        if (_state.Config.VisteSalvate.TryGetValue(ChiaveVisteSalvate, out var lista))
            lista.RemoveAll(v => v.Nome == vista.Nome);
        _state.ConfigService.Save(_state.Config);
        VistaSelezionata = null;
        CaricaVisteSalvate();
    }

    private static string? Serializza(FiltroMultiSelect filtro)
    {
        var sel = filtro.Selezionati;
        return sel.Count == 0 ? null : string.Join(SeparatoreValori, sel);
    }

    private static HashSet<string> DeserializzaValori(string? grezzo) =>
        string.IsNullOrEmpty(grezzo)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : grezzo.Split(SeparatoreValori, StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Riporta la selezione di un multi-select al valore salvato. I valori non piu' nel catalogo (componente
    /// rinominata, assegnatario uscito dai dati) restano opzioni selezionate invece di essere scartati: cosi' il
    /// filtro non si allarga in silenzio (stesso criterio di ConSelezionatiMancanti, usato anche al refresh).</summary>
    private static void ApplicaSelezione(FiltroMultiSelect filtro, string? grezzo)
    {
        var valori = DeserializzaValori(grezzo);
        if (valori.Count > 0)
        {
            var noti = new HashSet<string>(filtro.Opzioni.Select(o => o.Valore), StringComparer.OrdinalIgnoreCase);
            var mancanti = valori.Where(x => !noti.Contains(x)).ToList();
            if (mancanti.Count > 0)
                filtro.Popola(filtro.Opzioni.Select(o => (o.Valore, o.Etichetta)).Concat(mancanti.Select(m => (m, m))).ToList());
        }
        foreach (var o in filtro.Opzioni.ToList())
        {
            var voluto = valori.Contains(o.Valore);
            if (o.Selezionato != voluto) o.Selezionato = voluto;
        }
    }

    /// <summary>Catalogo + valori selezionati fuori catalogo, da dare a Ripopola: Ripopola scarterebbe in silenzio le
    /// selezioni non piu' presenti nel catalogo (vedi ApplicaSelezione).</summary>
    private static IEnumerable<(string, string)> ConSelezionatiMancanti(FiltroMultiSelect filtro, IEnumerable<string> catalogo)
    {
        var elenco = catalogo.ToList();
        var noti = new HashSet<string>(elenco, StringComparer.Ordinal);
        foreach (var sel in filtro.Selezionati)
            if (noti.Add(sel)) elenco.Add(sel);
        return elenco.Select(x => (x, x));
    }

    private static string? IsoDataPerVista(DateTime? data) =>
        data?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
