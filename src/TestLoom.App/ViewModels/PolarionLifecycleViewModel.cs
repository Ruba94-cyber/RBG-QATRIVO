using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Context-specific TICKET analytics for the NC or CR table. Filtering a prepared set is instant;
/// shared cache/history reads are batched off the UI thread. No domain or Ticketing writes.
/// </summary>
public partial class PolarionLifecycleViewModel : ViewModelBase, IDisposable
{
    private static string L(string it, string en, string fr) => LocalizationService.Instance.Scegli(it, en, fr);

    private readonly AppState _state;
    private readonly string _scope;
    public string DashboardTitle => _scope + " · " +
        L("Analisi TICKET / Ticketing", "TICKET / Ticketing analysis", "Analyse TICKET / Ticketing");
    private readonly string? _selectedDitmId;
    private bool _disposed;

    public PolarionLifecycleViewModel(AppState state, string scope, string? selectedDitmId = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        if (scope is not ("NC" or "CR")) throw new ArgumentOutOfRangeException(nameof(scope));
        _scope = scope;
        _selectedDitmId = string.IsNullOrWhiteSpace(selectedDitmId) ? null : selectedDitmId.Trim();
    }

    /// <summary>Exact TestLoom source matching; substring search must not include NC-100 for NC-10.</summary>
    public static bool MatchesSource(string recordId, string? selectedDitmId) =>
        string.IsNullOrWhiteSpace(selectedDitmId) ||
        string.Equals(recordId, selectedDitmId.Trim(), StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _disposed = true;
        _polarionReportGeneration++;
        _polarionTimelineGeneration++;
    }

    private readonly List<PolarionReportService.Row> _polarionAllRows = new();
    private int _polarionReportGeneration;
    private bool _polarionReportLoading;
    private string _polarionReportQuery = "";
    private string _polarionReportStatus = "Tutti";
    private string _polarionReportType = "Tutti";
    private string _polarionReportPriority = "Tutti";
    private string _polarionReportSeverity = "Tutte";
    private string _polarionReportResolution = "Tutte";
    private string _polarionReportDateDimension = "Apertura";
    private string _polarionReportVehicle = "Tutti";
    private string _polarionReportRange = "Tutte";
    private string _polarionReportFrom = "";
    private string _polarionReportTo = "";
    private bool _polarionFilterBatch;
    private bool _polarionReportOnlyClosed;
    private bool _polarionReportIncludeArchived = true;
    private int _polarionReportCount;

    public bool PolarionReportLoading
    {
        get => _polarionReportLoading;
        private set
        {
            if (SetProperty(ref _polarionReportLoading, value))
                OnPropertyChanged(nameof(PolarionCanExport));
        }
    }

    public string PolarionReportQuery
    {
        get => _polarionReportQuery;
        set { if (SetProperty(ref _polarionReportQuery, value)) FilterPolarionRows(); }
    }
    public string PolarionReportStatus
    {
        get => _polarionReportStatus;
        set { if (SetProperty(ref _polarionReportStatus, value)) FilterPolarionRows(); }
    }
    public string PolarionReportType
    {
        get => _polarionReportType;
        set { if (SetProperty(ref _polarionReportType, value)) FilterPolarionRows(); }
    }
    public string PolarionReportPriority
    {
        get => _polarionReportPriority;
        set { if (SetProperty(ref _polarionReportPriority, value)) FilterPolarionRows(); }
    }
    public string PolarionReportSeverity
    {
        get => _polarionReportSeverity;
        set { if (SetProperty(ref _polarionReportSeverity, value)) FilterPolarionRows(); }
    }
    public string PolarionReportResolution
    {
        get => _polarionReportResolution;
        set { if (SetProperty(ref _polarionReportResolution, value)) FilterPolarionRows(); }
    }
    public string PolarionReportDateDimension
    {
        get => _polarionReportDateDimension;
        set { if (SetProperty(ref _polarionReportDateDimension, value)) FilterPolarionRows(); }
    }
    public string PolarionReportVehicle
    {
        get => _polarionReportVehicle;
        set { if (SetProperty(ref _polarionReportVehicle, value)) FilterPolarionRows(); }
    }
    public string PolarionReportRange
    {
        get => _polarionReportRange;
        set { if (SetProperty(ref _polarionReportRange, value)) FilterPolarionRows(); }
    }
    public string PolarionReportFrom
    {
        get => _polarionReportFrom;
        set { if (SetProperty(ref _polarionReportFrom, value)) { OnPropertyChanged(nameof(PolarionDateFilterError)); OnPropertyChanged(nameof(PolarionCanExport)); FilterPolarionRows(); } }
    }
    public string PolarionReportTo
    {
        get => _polarionReportTo;
        set { if (SetProperty(ref _polarionReportTo, value)) { OnPropertyChanged(nameof(PolarionDateFilterError)); OnPropertyChanged(nameof(PolarionCanExport)); FilterPolarionRows(); } }
    }
    public bool PolarionReportIncludeArchived
    {
        get => _polarionReportIncludeArchived;
        set { if (SetProperty(ref _polarionReportIncludeArchived, value)) FilterPolarionRows(); }
    }
    public bool PolarionReportOnlyClosed
    {
        get => _polarionReportOnlyClosed;
        set { if (SetProperty(ref _polarionReportOnlyClosed, value)) FilterPolarionRows(); }
    }

    public ObservableCollection<string> PolarionReportStatuses { get; } = new() { "Tutti" };
    public ObservableCollection<string> PolarionReportTypes { get; } = new() { "Tutti" };
    public ObservableCollection<string> PolarionReportPriorities { get; } = new() { "Tutti" };
    public ObservableCollection<string> PolarionReportSeverities { get; } = new() { "Tutte" };
    public ObservableCollection<string> PolarionReportResolutions { get; } = new() { "Tutte" };
    public IReadOnlyList<string> PolarionReportDateDimensions { get; } = ["Apertura", "Testing", "Chiusura"];
    public ObservableCollection<string> PolarionReportVehicles { get; } = new() { "Tutti" };
    public ObservableCollection<string> PolarionReportRanges { get; } = new() { "Tutte" };
    public ObservableCollection<PolarionReportService.Row> PolarionReportRows { get; } = new();
    private PolarionReportService.Row? _polarionReportSelectedRow;
    public PolarionReportService.Row? PolarionReportSelectedRow
    {
        get => _polarionReportSelectedRow;
        set
        {
            if (SetProperty(ref _polarionReportSelectedRow, value))
                _ = CaricaTimelinePolarionAsync(value);
        }
    }
    public ObservableCollection<PolarionReportService.Distribution> PolarionStatusDistribution { get; } = new();

    public sealed record PolarionTimelineEntry(string Event, string DateText, string Provenance);
    public sealed record PolarionMonthlyTrend(string Period, int Created, int TestingDetected, int ClosedDetected)
    {
        public string CreatedLabel => L("Aperti: ", "Opened: ", "Ouverts : ") + Created;
        public string TestingLabel => L("Testing rilevati: ", "Testing observed: ", "Tests observés : ") + TestingDetected;
        public string ClosedLabel => L("Closed rilevati: ", "Closures observed: ", "Clôtures observées : ") + ClosedDetected;
    }
    public ObservableCollection<PolarionTimelineEntry> PolarionReportTimeline { get; } = new();
    public ObservableCollection<PolarionMonthlyTrend> PolarionMonthlyTrends { get; } = new();
    private int _polarionTimelineGeneration;
    public string PolarionTimelineTitle { get; private set; } =
        L("Seleziona un ticket TICKET per vedere la timeline",
          "Select an TICKET to view its timeline", "Sélectionnez un TICKET pour afficher sa chronologie");


    public int PolarionReportCount
    {
        get => _polarionReportCount;
        private set { if (SetProperty(ref _polarionReportCount, value)) OnPropertyChanged(nameof(PolarionReportSummary)); }
    }
    public string PolarionReportSummary => PolarionReportCount +
        L(" ticket TICKET · ", " TICKET tickets · ", " tickets TICKET · ") + PolarionReportRows.Count +
        L(" righe visualizzate (max 250)", " rows displayed (max 250)", " lignes affichées (max 250)") +
        (PolarionHistoriesUnavailable > 0
            ? L(" · Cronologie non disponibili: ", " · Histories unavailable: ", " · Historiques indisponibles : ") + PolarionHistoriesUnavailable
            : "");
    public int PolarionHistoriesUnavailable { get; private set; }
    public int PolarionCountClosed { get; private set; }
    public int PolarionCountOpen { get; private set; }
    public int PolarionCountTesting { get; private set; }
    public int PolarionCountUnknown { get; private set; }
    public int PolarionCountObservedClosures { get; private set; }
    public int PolarionCountBaselineClosed { get; private set; }
    public int PolarionCountReopened { get; private set; }
    public bool PolarionCanExport => _polarionAllRows.Count > 0 &&
        !PolarionReportLoading && string.IsNullOrEmpty(PolarionDateFilterError);
    public int PolarionMaxDistribution => Math.Max(1, PolarionReportCount);
    public string? PolarionReportError { get; private set; }

    public string PolarionDateFilterError =>
        (!string.IsNullOrWhiteSpace(PolarionReportFrom) && ParseDate(PolarionReportFrom, false) is null) ||
        (!string.IsNullOrWhiteSpace(PolarionReportTo) && ParseDate(PolarionReportTo, true) is null)
        ? L("Data TICKET non valida: usare gg/mm/aaaa.",
            "Invalid TICKET date: use dd/mm/yyyy.", "Date TICKET invalide : utilisez jj/mm/aaaa.")
        : "";

    private static DateTimeOffset? ParseDate(string? text, bool end)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!DateTime.TryParseExact(text.Trim(), ["dd/MM/yyyy", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return null;
        return new DateTimeOffset(day.Date.AddDays(end ? 1 : 0).AddTicks(end ? -1 : 0),
            TimeZoneInfo.Local.GetUtcOffset(day.Date));
    }

    private void FilterPolarionRows()
    {
        if (_polarionFilterBatch) return;
        var from = ParseDate(PolarionReportFrom, end: false);
        var to = ParseDate(PolarionReportTo, end: true);
        // Una data digitata male NON deve essere interpretata come "nessun filtro":
        // impedisce di mostrare statistiche apparentemente filtrate ma in realta' globali.
        // Il messaggio di errore e' gia' esposto nella View accanto ai campi data.
        IReadOnlyList<PolarionReportService.Row> filtered = string.IsNullOrEmpty(PolarionDateFilterError)
            ? PolarionReportService.Apply(_polarionAllRows, new PolarionReportService.Filter(
                Query: PolarionReportQuery, Status: PolarionReportStatus, Type: PolarionReportType,
                Priority: PolarionReportPriority, Severity: PolarionReportSeverity, Resolution: PolarionReportResolution,
                Range: PolarionReportRange, Vehicle: PolarionReportVehicle,
                DateFrom: from, DateTo: to, DateDimension: PolarionReportDateDimension,
                OnlyObservedClosures: PolarionReportOnlyClosed, IncludeArchived: PolarionReportIncludeArchived))
            : Array.Empty<PolarionReportService.Row>();
        PolarionReportRows.Clear();
        foreach (var row in filtered.Take(250)) PolarionReportRows.Add(row);
        // When a user opens the dashboard from a specific NC/CR row and only one
        // linked TICKET matches, show its timeline immediately rather than requiring
        // a second click. Clear obsolete selection on any filter change.
        if (filtered.Count == 1)
        {
            if (!ReferenceEquals(PolarionReportSelectedRow, filtered[0]))
                PolarionReportSelectedRow = filtered[0];
        }
        else if (PolarionReportSelectedRow is not null &&
            !filtered.Any(r => ReferenceEquals(r, PolarionReportSelectedRow)))
            PolarionReportSelectedRow = null;
        PolarionReportCount = filtered.Count;
        var totals = PolarionReportService.Summarize(filtered);
        PolarionHistoriesUnavailable = totals.HistoriesUnavailable;
        PolarionCountClosed = totals.Closed;
        PolarionCountOpen = totals.Open;
        PolarionCountTesting = totals.Testing;
        PolarionCountUnknown = totals.NotSynced;
        PolarionCountObservedClosures = totals.ObservedClosures;
        PolarionCountBaselineClosed = totals.AlreadyClosedAtBaseline;
        PolarionCountReopened = totals.ObservedReopenings;
        PolarionStatusDistribution.Clear();
        foreach (var entry in totals.StatusDistribution) PolarionStatusDistribution.Add(entry);
        var monthly = filtered
            .SelectMany(row => new[]
            {
                (Date: row.CreatedAt, Kind: "created"),
                (Date: row.FirstTestingObservedAt, Kind: "testing"),
                (Date: row.FirstClosedObservedAt, Kind: "closed")
            })
            .Where(x => x.Date.HasValue)
            .GroupBy(x => LocalMonthKey(x.Date!.Value))
            .OrderBy(x => x.Key).ToArray();
        PolarionMonthlyTrends.Clear();
        foreach (var period in monthly.TakeLast(24))
            PolarionMonthlyTrends.Add(new PolarionMonthlyTrend(period.Key,
                period.Count(x => x.Kind == "created"),
                period.Count(x => x.Kind == "testing"),
                period.Count(x => x.Kind == "closed")));
        OnPropertyChanged(nameof(PolarionHistoriesUnavailable));
        OnPropertyChanged(nameof(PolarionCountClosed));
        OnPropertyChanged(nameof(PolarionCountOpen));
        OnPropertyChanged(nameof(PolarionCountTesting));
        OnPropertyChanged(nameof(PolarionCountUnknown));
        OnPropertyChanged(nameof(PolarionCountObservedClosures));
        OnPropertyChanged(nameof(PolarionCountBaselineClosed));
        OnPropertyChanged(nameof(PolarionCountReopened));
        OnPropertyChanged(nameof(PolarionMaxDistribution));
        OnPropertyChanged(nameof(PolarionReportSummary));
    }

    /// <summary>Creates an exact local-calendar date range from a chart month label.</summary>
    /// <summary>Monthly charts group by the same local calendar shown in date filters.</summary>
    public static string LocalMonthKey(DateTimeOffset instant) =>
        instant.ToLocalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static bool TryMonthRange(string? period, out string from, out string to)
    {
        from = to = "";
        if (!DateTime.TryParseExact(period, "yyyy-MM", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)) return false;
        var last = new DateTime(date.Year, date.Month,
            DateTime.DaysInMonth(date.Year, date.Month));
        from = new DateTime(date.Year, date.Month, 1).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        to = last.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        return true;
    }

    private void FiltraMesePolarion(string? period, string dimension)
    {
        if (!TryMonthRange(period, out var from, out var to)) return;
        _polarionFilterBatch = true;
        try
        {
            PolarionReportDateDimension = dimension;
            PolarionReportFrom = from;
            PolarionReportTo = to;
        }
        finally { _polarionFilterBatch = false; }
        FilterPolarionRows();
    }

    [RelayCommand]
    private void FiltraMeseApertura(string? period) => FiltraMesePolarion(period, "Apertura");

    [RelayCommand]
    private void FiltraMeseTesting(string? period) => FiltraMesePolarion(period, "Testing");

    [RelayCommand]
    private void FiltraMeseChiusura(string? period) => FiltraMesePolarion(period, "Chiusura");

    [RelayCommand]
    private void AzzeraDatePolarion()
    {
        _polarionFilterBatch = true;
        try
        {
            PolarionReportFrom = "";
            PolarionReportTo = "";
            PolarionReportDateDimension = "Apertura";
        }
        finally { _polarionFilterBatch = false; }
        FilterPolarionRows();
    }

    [RelayCommand]
    private void FiltraStatoPolarion(string? status)
    {
        if (!string.IsNullOrWhiteSpace(status)) PolarionReportStatus = status;
    }

    private async Task CaricaTimelinePolarionAsync(PolarionReportService.Row? selected)
    {
        var generation = ++_polarionTimelineGeneration;
        PolarionReportTimeline.Clear();
        PolarionTimelineTitle = selected is null
            ? L("Seleziona un ticket TICKET per vedere la timeline",
                "Select an TICKET to view its timeline", "Sélectionnez un TICKET pour afficher sa chronologie")
            : L("Timeline di ", "Timeline for ", "Chronologie de ") + selected.TicketId;
        OnPropertyChanged(nameof(PolarionTimelineTitle));
        if (selected is null || _disposed || !_state.CartellaValida) return;
        try
        {
            var paths = _state.Paths;
            var project = PolarionApiSettingsService.Load(paths).ProjectId;
            var history = await Task.Run(() =>
                new PolarionStatusHistoryService(paths).ReadMerged(project, selected.TicketId));
            if (generation != _polarionTimelineGeneration || _disposed) return;
            if (selected.CreatedAt.HasValue)
                PolarionReportTimeline.Add(new PolarionTimelineEntry(
                    L("Creazione TICKET", "TICKET created", "Création TICKET"),
                    selected.CreatedText, L("Ticketing: data di creazione (esatta)",
                        "Ticketing: created date (exact)", "Ticketing : date de création (exacte)")));
            if (history is null || history.Observations.Count == 0)
            {
                PolarionReportTimeline.Add(new PolarionTimelineEntry(
                    L("Storico non disponibile", "History unavailable", "Historique indisponible"),
                    "—", L("Nessuna transizione osservata da TestLoom",
                        "No state transitions observed by TestLoom", "Aucune transition observée par TestLoom")));
                return;
            }
            PolarionStatusHistoryService.Observation? previous = null;
            foreach (var entry in history.Observations.OrderBy(x => x.FirstSeenUtc))
            {
                var headline = previous is null
                    ? L("Stato iniziale osservato: ", "First observed status: ",
                        "Premier statut observé : ") + entry.Status
                    : L("Stato osservato: ", "Observed status: ",
                        "Statut observé : ") + previous.Status + " → " + entry.Status;
                if (!string.IsNullOrWhiteSpace(entry.Resolution)) headline += " / " + entry.Resolution;
                PolarionReportTimeline.Add(new PolarionTimelineEntry(headline,
                    entry.FirstSeenUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                    previous is null
                        ? L("Prima osservazione TestLoom, non inizio storico del ticket",
                            "First TestLoom observation, not the ticket's historical start",
                            "Première observation TestLoom, pas le début historique du ticket")
                        : L("Prima rilevazione TestLoom; la transizione reale potrebbe essere precedente",
                            "First TestLoom detection; the actual transition may be earlier",
                            "Première détection TestLoom ; la transition réelle peut être antérieure")));
                previous = entry;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (generation == _polarionTimelineGeneration && !_disposed)
                PolarionReportTimeline.Add(new PolarionTimelineEntry(
                    L("Timeline non leggibile", "Timeline unavailable", "Chronologie illisible"),
                    "—", L("Cache storica da verificare", "Historical cache needs review",
                        "Cache historique à vérifier")));
        }
    }

    [RelayCommand]
    private async Task AggiornaReportPolarionAsync()
    {
        if (_disposed || !_state.CartellaValida || PolarionReportLoading) return;
        PolarionReportLoading = true;
        PolarionReportError = null;
        OnPropertyChanged(nameof(PolarionReportError));
        var generation = ++_polarionReportGeneration;
        try
        {
            // Keep repository cache references in scope only for synchronous projection.
            var links = new List<PolarionReportService.Association>();
            if (_scope == "NC")
            foreach (var nc in _state.NonConformita.PeekAllReadOnly())
            {
                if (!MatchesSource(nc.Id, _selectedDitmId)) continue;
                // Match the same fallback as the main NC grid and quick-edit dialog:
                // when the NC has no TICKET of its own, use its linked TestCase TICKET references.
                var test = string.IsNullOrWhiteSpace(nc.TestCaseId) ? null : _state.TestCases.GetById(nc.TestCaseId);
                var direct = nc.CollegamentiPolarion
                    .Select(link => (Id: link.Codice, Url: link.Url));
                var candidates = nc.CollegamentiPolarion.Count > 0
                    ? direct
                    : (test?.TicketCollegati.Select(link => (Id: link.Riferimento, Url: link.Url))
                        ?? Enumerable.Empty<(string Id, string Url)>());
                var vehicles = nc.VeicoliSegnalati.Count > 0 ? nc.VeicoliSegnalati.ToList() :
                    test?.VeicoliEffettivi.ToList() ?? new List<string>();
                foreach (var (reference, url) in candidates)
                {
                    var id = PolarionQuickReportTicketId.Extract(reference, url);
                    if (id is not null) links.Add(new(id, "NC", nc.Id, vehicles));
                }
            }
            if (_scope == "CR")
            foreach (var cr in _state.ChangeRequests.PeekAllReadOnly())
            {
                if (!MatchesSource(cr.Id, _selectedDitmId)) continue;
                // Archived CRs remain in historical reporting, even though periodic
                // background polling correctly excludes their standalone tickets.
                foreach (var link in cr.TicketCollegati)
                {
                    var id = PolarionQuickReportTicketId.Extract(link.Riferimento, link.Url);
                    if (id is not null) links.Add(new(id, "CR", cr.Id, cr.VeicoliImpattati.ToList(), cr.Archiviata));
                }
            }
            // I ticket possono riferirsi a veicoli archiviati: la gamma storica
            // va mantenuta nel report, non sparisce con l'archiviazione del catalogo.
            var rangeMap = _state.Master.Veicoli.Load()
                .GroupBy(v => v.Nome, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderBy(v => v.Archiviata).First().GammaNome,
                    StringComparer.OrdinalIgnoreCase);
            var paths = _state.Paths;
            var project = "TICKET_Tickets";
            try
            {
                var config = PolarionApiSettingsService.Load(paths);
                if (!string.IsNullOrWhiteSpace(config.ProjectId)) project = config.ProjectId;
            }
            catch (IOException) { }

            var result = await Task.Run(() =>
            {
                var store = new PolarionSnapshotStore(paths);
                var history = new PolarionStatusHistoryService(paths);
                // Un solo elenco delle cartelle OneDrive per l'intero batch: ReadMerged
                // continuerà a verificare identità, revisioni e conflitti per ticket.
                var deviceFolders = history.SnapshotDeviceDirectories(project);
                return PolarionReportService.Build(links, id => store.Load(project, id),
                    id => history.ReadMerged(project, id, deviceFolders), rangeMap);
            });
            if (_disposed || generation != _polarionReportGeneration) return;
            _polarionAllRows.Clear();
            _polarionAllRows.AddRange(result);
            // Il reset degli ItemsSource puo' svuotare SelectedItem delle ComboBox.
            // Conserviamo i valori e sospendiamo i sette ricalcoli intermedi:
            // esattamente UN filtro sui dati al termine del refresh.
            var selected = (PolarionReportStatus, PolarionReportType, PolarionReportPriority,
                PolarionReportSeverity, PolarionReportResolution, PolarionReportVehicle, PolarionReportRange);
            _polarionFilterBatch = true;
            try
            {
                ReplaceOptions(PolarionReportStatuses, "Tutti", result.Select(x => x.Status), selected.PolarionReportStatus);
                ReplaceOptions(PolarionReportTypes, "Tutti", result.Select(x => x.Type), selected.PolarionReportType);
                ReplaceOptions(PolarionReportPriorities, "Tutti", result.Select(x => x.Priority), selected.PolarionReportPriority);
                ReplaceOptions(PolarionReportSeverities, "Tutte", result.Select(x => x.Severity), selected.PolarionReportSeverity);
                ReplaceOptions(PolarionReportResolutions, "Tutte", result.Select(x => x.Resolution), selected.PolarionReportResolution);
                ReplaceOptions(PolarionReportVehicles, "Tutti",
                    result.SelectMany(x => x.Vehicles.Split(", ", StringSplitOptions.RemoveEmptyEntries)), selected.PolarionReportVehicle);
                ReplaceOptions(PolarionReportRanges, "Tutte",
                    result.SelectMany(x => x.Ranges.Split(", ", StringSplitOptions.RemoveEmptyEntries)), selected.PolarionReportRange);
                PolarionReportStatus = selected.PolarionReportStatus;
                PolarionReportType = selected.PolarionReportType;
                PolarionReportPriority = selected.PolarionReportPriority;
                PolarionReportSeverity = selected.PolarionReportSeverity;
                PolarionReportResolution = selected.PolarionReportResolution;
                PolarionReportVehicle = selected.PolarionReportVehicle;
                PolarionReportRange = selected.PolarionReportRange;
            }
            finally { _polarionFilterBatch = false; }
            FilterPolarionRows();
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                PolarionReportError = L("Report TICKET non disponibile (", "TICKET report unavailable (",
                    "Rapport TICKET indisponible (") + ex.GetType().Name + ").";
                OnPropertyChanged(nameof(PolarionReportError));
            }
        }
        finally { if (!_disposed) PolarionReportLoading = false; }
    }

    private static void ReplaceOptions(ObservableCollection<string> items, string all,
        IEnumerable<string> values, string selected)
    {
        var list = values.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        list.RemoveAll(x => string.Equals(x, all, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, all);
        if (!string.IsNullOrWhiteSpace(selected) &&
            !list.Contains(selected, StringComparer.OrdinalIgnoreCase))
            list.Add(selected);
        // Non resettare la ComboBox se la lista opzioni non e' cambiata:
        // evita re-layout e perdita temporanea di SelectedItem.
        if (items.SequenceEqual(list, StringComparer.OrdinalIgnoreCase)) return;
        items.Clear();
        foreach (var item in list) items.Add(item);
    }

    [RelayCommand]
    private void ApriPolarionReport(PolarionReportService.Row? row)
    {
        if (row is null) return;
        try
        {
            var settings = PolarionApiSettingsService.Load(_state.Paths);
            if (!Uri.TryCreate(settings.ApiRoot, UriKind.Absolute, out var api) || api.Scheme != Uri.UriSchemeHttps)
                return;
            var portal = new Uri(api, "/ticketing/redirect/project/" + Uri.EscapeDataString(settings.ProjectId)
                + "/workitem?id=" + Uri.EscapeDataString(row.TicketId));
            Process.Start(new ProcessStartInfo(portal.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception) { PolarionReportError = L("Impossibile aprire il ticket TICKET.", "Could not open the TICKET ticket.", "Impossible d’ouvrir le ticket TICKET."); OnPropertyChanged(nameof(PolarionReportError)); }
    }

    public IReadOnlyList<PolarionReportService.Row> PolarionRigheFiltratePerExport()
    {
        if (!PolarionCanExport)
            throw new InvalidOperationException("TICKET report export unavailable or invalid date filter.");
        return PolarionReportService.Apply(_polarionAllRows, new PolarionReportService.Filter(
            Query: PolarionReportQuery, Status: PolarionReportStatus, Type: PolarionReportType,
            Priority: PolarionReportPriority, Severity: PolarionReportSeverity,
            Resolution: PolarionReportResolution, Range: PolarionReportRange, Vehicle: PolarionReportVehicle,
            DateFrom: ParseDate(PolarionReportFrom, false), DateTo: ParseDate(PolarionReportTo, true),
            DateDimension: PolarionReportDateDimension, OnlyObservedClosures: PolarionReportOnlyClosed,
            IncludeArchived: PolarionReportIncludeArchived)).ToList();
    }

    /// <summary>Legacy API per il ViewModel; l'export UI puo' catturare uno
    /// snapshot filtrato e serializzarlo su thread di lavoro senza bloccare Avalonia.</summary>
    public string PolarionCsv() => PolarionCsv(PolarionRigheFiltratePerExport());

    public static string PolarionCsv(IReadOnlyList<PolarionReportService.Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var builder = new StringBuilder("TICKET;Titolo;Tipo;Stato;Risoluzione;Priorita;Severita;NC_CR;Veicoli;Gamme;Archiviato;Apertura_Ticketing;Testing_rilevato_TestLoom;Chiusura_rilevata_TestLoom;Riapertura_rilevata_TestLoom;Apertura_Testing_limite_superiore;Apertura_Closed_limite_superiore;Testing_transizioni_osservate;Closed_transizioni_osservate;Reopen_transizioni_osservate;Qualita_dati\r\n");
        foreach (var row in rows)
        {
            var values = new[] { row.TicketId, row.Title, row.Type, row.Status, row.Resolution, row.Priority,
                row.Severity, row.Records, row.Vehicles, row.Ranges, row.ArchiveText, row.CreatedText, row.TestingText,
                row.ClosedText, row.ReopenedText, row.TestingUpperBoundText, row.ClosureUpperBoundText,
                row.HistoryUnavailable ? "" : row.TestingTransitionsObserved.ToString(CultureInfo.InvariantCulture),
                row.HistoryUnavailable ? "" : row.ClosureTransitionsObserved.ToString(CultureInfo.InvariantCulture),
                row.HistoryUnavailable ? "" : row.ReopenTransitionsObserved.ToString(CultureInfo.InvariantCulture), row.DataQuality };
            builder.AppendLine(string.Join(";", values.Select(CsvCell)));
        }
        return "\uFEFF" + builder.ToString();
    }

    private static string CsvCell(string? value)
    {
        var data = (value ?? "").Replace("\r", " ").Replace("\n", " ");
        // Ticketing/TestLoom text can be user-controlled. Protect spreadsheet CSV consumers
        // from formula execution even when a cell starts with whitespace.
        if (data.TrimStart().StartsWith("=", StringComparison.Ordinal) ||
            data.TrimStart().StartsWith("+", StringComparison.Ordinal) ||
            data.TrimStart().StartsWith("-", StringComparison.Ordinal) ||
            data.TrimStart().StartsWith("@", StringComparison.Ordinal))
            data = "'" + data;
        return "\"" + data.Replace("\"", "\"\"") + "\"";
    }
}

/// <summary>Extract a validated TICKET ID from existing TestLoom link fields without altering them.</summary>
internal static class PolarionQuickReportTicketId
{
    private static readonly System.Text.RegularExpressions.Regex Pattern = new(
        @"(?<![A-Za-z0-9])TICKET-[0-9]+(?![0-9])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static string? Extract(string? reference, string? url)
    {
        var match = Pattern.Match(reference ?? "");
        if (!match.Success) match = Pattern.Match(url ?? "");
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }
}
