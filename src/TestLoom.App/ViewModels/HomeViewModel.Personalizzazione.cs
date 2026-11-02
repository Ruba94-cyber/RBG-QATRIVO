using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Sezioni personalizzabili della Home. L'elenco e' nell'ordine predefinito, cioe' l'ordine che la Home aveva
/// prima della personalizzazione, con "Recenti" in fondo. Ordine e visibilita' sono preferenze LOCALI al PC
/// (ViewStateService, chiave "Home.Layout"), mai nella cartella condivisa.
/// </summary>
public static class HomeSezioni
{
    public const string Riepilogo = "riepilogo";
    public const string Avvisi = "avvisi";
    public const string Kpi = "kpi";
    public const string Grafici = "grafici";
    public const string Liste = "liste";
    public const string Recenti = "recenti";

    public static readonly IReadOnlyList<string> Predefinite = new[] { Riepilogo, Avvisi, Kpi, Grafici, Liste, Recenti };

    /// <summary>Chiave di traduzione del nome della sezione nel pannello "Personalizza Home".</summary>
    public static string ChiaveTitolo(string id) => id switch
    {
        Riepilogo => "HomeSezioneRiepilogo",
        Avvisi => "HomeSezioneAvvisi",
        Kpi => "HomeSezioneKpi",
        Grafici => "HomeSezioneGrafici",
        Liste => "HomeSezioneListe",
        _ => "HomeSezioneRecenti",
    };

    /// <summary>AutomationId del contenitore della sezione nella vista (il riordino della vista lo cerca cosi').</summary>
    public static string AutomationId(string id) => "Home_Sezione_" + id;
}

/// <summary>Riga del pannello "Personalizza Home": spunta di visibilita' e spostamento su/giu'.</summary>
public sealed class VoceLayoutHome : ObservableObject
{
    private readonly Action<string, bool> _visibilitaCambiata;
    private bool _visibile;

    public VoceLayoutHome(string id, string titolo, bool visibile, Action<string, bool> visibilitaCambiata)
    {
        Id = id;
        Titolo = titolo;
        _visibile = visibile;
        _visibilitaCambiata = visibilitaCambiata;
    }

    public string Id { get; }
    public string Titolo { get; }

    public bool Visibile
    {
        get => _visibile;
        set { if (SetProperty(ref _visibile, value)) _visibilitaCambiata(Id, value); }
    }

    public string AutomationIdCheckBox => $"Home_Layout_{Id}_CheckBox";
    public string AutomationIdSu => $"Home_Layout_{Id}_Su_Button";
    public string AutomationIdGiu => $"Home_Layout_{Id}_Giu_Button";
}

/// <summary>Riga della sezione "Recenti" della Home: elemento aperto di recente, con il titolo attuale.</summary>
public sealed class VoceRecenteHome
{
    public VoceRecenteHome(RecentItemsService.ElementoRecente elemento, string titolo)
    {
        Elemento = elemento;
        Titolo = titolo;
    }

    public RecentItemsService.ElementoRecente Elemento { get; }
    public string Tipo => Elemento.Tipo;
    public string Id => Elemento.Id;
    public string Titolo { get; }

    /// <summary>Titolo mostrato: se l'elemento non ha un titolo, il suo Id.</summary>
    public string TitoloVisualizzato => string.IsNullOrWhiteSpace(Titolo) ? Id : Titolo;

    public string EtichettaTipo => LocalizationService.Instance.Traduci(Tipo switch
    {
        RecentItemsService.TipoTest => "HomeRecentiTipoTest",
        RecentItemsService.TipoChangeRequest => "HomeRecentiTipoCr",
        _ => "HomeRecentiTipoNc",
    });

    public string AperturaTesto => Elemento.AperturaUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    public string AutomationIdApri => $"Home_Recente_{Tipo}_{Id}_Button";
}

public partial class HomeViewModel
{
    private const string ChiaveLayout = "Home.Layout";

    private readonly Action<RecentItemsService.ElementoRecente>? _apriRecente;
    private readonly Action _vaiAAnomalie;

    /// <summary>Ordine e visibilita' correnti. Senza preferenza salvata: l'ordine predefinito, tutto visibile.</summary>
    private List<(string Id, bool Visibile)> _layout = NormalizzaLayout(Array.Empty<string>());

    /// <summary>Ordine o visibilita' cambiati: la vista riordina i propri contenitori su questo evento.</summary>
    public event Action? LayoutCambiato;

    /// <summary>Id delle sezioni nell'ordine corrente.</summary>
    public IReadOnlyList<string> OrdineSezioni => _layout.Select(v => v.Id).ToList();

    /// <summary>Righe del pannello "Personalizza Home", nell'ordine corrente.</summary>
    public ObservableCollection<VoceLayoutHome> VociLayout { get; } = new();

    [ObservableProperty] private bool mostraPersonalizzazione;

    public bool SezioneVisibile(string id) => _layout.Any(v => v.Id == id && v.Visibile);

    private bool SezioneGraficiVisibile => SezioneVisibile(HomeSezioni.Grafici);

    /// <summary>Riepilogo, Avvisi e KPI leggono lo stesso sommario dei Test: finche' tutte e tre sono nascoste non si legge nulla.</summary>
    private bool SezioneTestataVisibile =>
        SezioneVisibile(HomeSezioni.Riepilogo) || SezioneVisibile(HomeSezioni.Avvisi) || SezioneVisibile(HomeSezioni.Kpi);

    /// <summary>La copertura requisiti e' un dato della sola sezione KPI.</summary>
    private bool SezioneKpiVisibile => SezioneVisibile(HomeSezioni.Kpi);

    private bool SezioneListeVisibile => SezioneVisibile(HomeSezioni.Liste);

    public bool MostraSezioneRiepilogo => SezioneVisibile(HomeSezioni.Riepilogo);

    /// <summary>Il contenitore degli avvisi compare solo se c'e' un avviso da mostrare: niente spazio vuoto in Home.</summary>
    public bool MostraSezioneAvvisi => SezioneVisibile(HomeSezioni.Avvisi) && MostraAvvisoTestSenzaEcu;
    public bool MostraSezioneKpi => SezioneVisibile(HomeSezioni.Kpi);
    public bool MostraSezioneGrafici => SezioneVisibile(HomeSezioni.Grafici);
    public bool MostraSezioneListe => SezioneVisibile(HomeSezioni.Liste);
    public bool MostraSezioneRecenti => SezioneVisibile(HomeSezioni.Recenti);

    /// <summary>Elementi aperti di recente (dal piu' recente), gia' filtrati da quelli che non esistono piu'.</summary>
    public BulkObservableCollection<VoceRecenteHome> Recenti { get; } = new();
    public bool RecentiVuoti => Recenti.Count == 0;

    /// <summary>Spunta/togli una sezione dal pannello. Salvato subito, locale al PC.</summary>
    public void ImpostaSezioneVisibile(string id, bool visibile)
    {
        var indice = _layout.FindIndex(v => v.Id == id);
        if (indice < 0 || _layout[indice].Visibile == visibile) return;
        _layout[indice] = (id, visibile);
        SalvaLayout();
        NotificaLayout();
    }

    /// <summary>Sposta una sezione di una posizione: -1 su, +1 giu'. Ai limiti non fa nulla.</summary>
    public void SpostaSezione(string id, int direzione)
    {
        var indice = _layout.FindIndex(v => v.Id == id);
        var destinazione = indice + direzione;
        if (indice < 0 || direzione == 0 || destinazione < 0 || destinazione >= _layout.Count) return;
        (_layout[indice], _layout[destinazione]) = (_layout[destinazione], _layout[indice]);
        VociLayout.Move(indice, destinazione);
        SalvaLayout();
        NotificaLayout();
    }

    /// <summary>Ripristina ordine e visibilita' predefiniti: rimuove la preferenza salvata sul PC e torna al layout del team, se pubblicato.</summary>
    [RelayCommand]
    public void RipristinaLayoutPredefinito()
    {
        _layout = LayoutPredefinito();
        ViewStateService.ScriviLista(_state, ChiaveLayout, Array.Empty<string>());
        RicostruisciVociLayout();
        NotificaLayout();
    }

    [RelayCommand]
    public void TogglePersonalizzazione() => MostraPersonalizzazione = !MostraPersonalizzazione;

    [RelayCommand]
    private void SpostaSezioneSu(string? id)
    {
        if (id != null) SpostaSezione(id, -1);
    }

    [RelayCommand]
    private void SpostaSezioneGiu(string? id)
    {
        if (id != null) SpostaSezione(id, 1);
    }

    [RelayCommand]
    private void ApriRecente(VoceRecenteHome? voce)
    {
        if (voce != null) _apriRecente?.Invoke(voce.Elemento);
    }

    [RelayCommand]
    private void VaiAAnomalie() => _vaiAAnomalie();

    private void CaricaLayout(AppState state)
    {
        var stato = CaricaLayoutTeam(state);
        var personale = ViewStateService.LeggiLista(state, ChiaveLayout);
        // Precedenza: layout personale di questo PC, poi layout del team, poi predefinito di sistema.
        _layout = HaRigheLayoutValide(personale) ? NormalizzaLayout(personale) : LayoutPredefinito();
        RicostruisciVociLayout();
        if (stato == StatoLayoutTeam.NonLeggibile)
            MessaggioErroreLayoutTeam = LocalizationService.Instance.Traduci("HomeLayoutTeamNonLeggibile");
    }

    private void SalvaLayout() =>
        ViewStateService.ScriviLista(_state, ChiaveLayout,
            _layout.Select(v => v.Id + ":" + (v.Visibile ? "1" : "0")).ToList());

    private void RicostruisciVociLayout()
    {
        VociLayout.Clear();
        foreach (var (id, visibile) in _layout)
            VociLayout.Add(new VoceLayoutHome(id, LocalizationService.Instance.Traduci(HomeSezioni.ChiaveTitolo(id)),
                visibile, ImpostaSezioneVisibile));
    }

    private void NotificaLayout()
    {
        OnPropertyChanged(nameof(OrdineSezioni));
        OnPropertyChanged(nameof(MostraSezioneRiepilogo));
        OnPropertyChanged(nameof(MostraSezioneAvvisi));
        OnPropertyChanged(nameof(MostraSezioneKpi));
        OnPropertyChanged(nameof(MostraSezioneGrafici));
        OnPropertyChanged(nameof(MostraSezioneListe));
        OnPropertyChanged(nameof(MostraSezioneRecenti));
        // Le sezioni nascoste finora non hanno letto nulla: quelle appena rese visibili si calcolano ora, una volta sola.
        EseguiAggiornamentoPendente();
        LayoutCambiato?.Invoke();
    }

    /// <summary>
    /// Ordine e visibilita' da una lista salvata ("id:1" visibile, "id:0" nascosta). Voci sconosciute, duplicate o
    /// malformate vengono saltate. Le sezioni mancanti (ad esempio quelle di una versione futura) si aggiungono in
    /// fondo, visibili, nell'ordine predefinito.
    /// </summary>
    private static List<(string Id, bool Visibile)> NormalizzaLayout(IEnumerable<string> salvate)
    {
        var risultato = new List<(string Id, bool Visibile)>(HomeSezioni.Predefinite.Count);
        foreach (var riga in salvate)
        {
            if (!TryLeggiRigaLayout(riga, out var id, out var visibile)) continue;
            if (risultato.Any(v => v.Id == id)) continue;
            risultato.Add((id, visibile));
        }
        foreach (var id in HomeSezioni.Predefinite)
            if (!risultato.Any(v => v.Id == id)) risultato.Add((id, true));
        return risultato;
    }

    /// <summary>Riga "id:1" (visibile) o "id:0" (nascosta) con un id di sezione noto. Ogni altra riga e' malformata.</summary>
    private static bool TryLeggiRigaLayout(string? riga, out string id, out bool visibile)
    {
        id = string.Empty;
        visibile = false;
        if (string.IsNullOrEmpty(riga)) return false;
        var separatore = riga.LastIndexOf(':');
        if (separatore <= 0 || separatore == riga.Length - 1) return false;
        var candidato = riga[..separatore];
        var flag = riga[(separatore + 1)..];
        if (!HomeSezioni.Predefinite.Contains(candidato) || flag is not ("0" or "1")) return false;
        id = candidato;
        visibile = flag == "1";
        return true;
    }

    /// <summary>Vero se la lista contiene almeno una riga valida: solo allora esiste una preferenza personale.</summary>
    private static bool HaRigheLayoutValide(IEnumerable<string> righe) =>
        righe.Any(riga => TryLeggiRigaLayout(riga, out _, out _));

    /// <summary>Titolo attuale dell'elemento, oppure null se non esiste piu' (o non e' leggibile in questo momento).</summary>
    private string? TitoloElementoRecente(RecentItemsService.ElementoRecente elemento)
    {
        try
        {
            return elemento.Tipo switch
            {
                RecentItemsService.TipoTest => _state.TestCases.GetById(elemento.Id)?.DescrizioneBreve,
                RecentItemsService.TipoChangeRequest => _state.ChangeRequests.GetById(elemento.Id)?.Titolo,
                RecentItemsService.TipoSegnalazioneRete => _state.NonConformita.GetById(elemento.Id)?.Descrizione,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Recenti Home", ex);
            return null;
        }
    }
}
