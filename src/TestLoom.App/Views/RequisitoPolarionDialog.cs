using System.Diagnostics;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>Colori condivisi dei badge Ticketing dei requisiti (stessa scala del colore dello stato TICKET nelle tabelle NC/CR).</summary>
public static class RequisitoPolarionUi
{
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static readonly IBrush Neutro = B("#8A8F98"), Ok = B("#2E9E5B"), Lavoro = B("#3B82F6"), Aperto = B("#D97706"), Vecchio = B("#DC2626");

    public static IBrush Pennello(PolarionStatoClasse classe, bool obsoleto) =>
        obsoleto && classe >= PolarionStatoClasse.Aperto ? Vecchio
        : classe switch
        {
            PolarionStatoClasse.Chiuso => Ok,
            PolarionStatoClasse.InLavorazione => Lavoro,
            PolarionStatoClasse.Aperto => Aperto,
            _ => Neutro
        };

    public static string EtichettaClasse(PolarionStatoClasse c) => c switch
    {
        PolarionStatoClasse.NonCollegato => LocalizationService.Instance.Scegli("Senza collegamento Ticketing", "No Ticketing link", "Sans lien Ticketing"),
        PolarionStatoClasse.NonDisponibile => LocalizationService.Instance.Scegli("Stato non disponibile", "Status not available", "Statut indisponible"),
        PolarionStatoClasse.Aperto => LocalizationService.Instance.Scegli("Aperto", "Open", "Ouvert"),
        PolarionStatoClasse.InLavorazione => LocalizationService.Instance.Scegli("In lavorazione", "In progress", "En cours"),
        _ => LocalizationService.Instance.Scegli("Chiuso/approvato", "Closed/approved", "Clos/approuvé")
    };

    /// <summary>Testo semplice di un commento Ticketing (HTML ridotto a testo: mai markup eseguibile).</summary>
    public static string TestoPuro(string? rich)
    {
        if (string.IsNullOrWhiteSpace(rich)) return "";
        var t = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(rich, "<[^>]*>", " "));
        return string.Join(" ", t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Apre l'URL nel browser scelto per Ticketing (Impostazioni) o, in mancanza, in quello predefinito.</summary>
    public static void ApriInBrowser(AppState state, Uri url)
    {
        var exe = PolarionBrowserLauncher.EseguibileScelto(state.Config);
        if (exe is not null) Process.Start(new ProcessStartInfo(exe, "\"" + url.AbsoluteUri + "\"") { UseShellExecute = false });
        else Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }
}

/// <summary>Dettaglio in sola lettura dello stato Ticketing di un requisito: stato, risoluzione, commenti, aggiorna e apri in Ticketing.</summary>
public sealed class RequisitoPolarionDialog : Window
{
    private readonly AppState _state;
    private readonly Requisito _requisito;
    private readonly StackPanel _contenuto = new() { Spacing = 8 };
    private readonly TextBlock _messaggio = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _aggiorna = new();
    private static string L(string it, string en, string fr) => LocalizationService.Instance.Scegli(it, en, fr);

    public RequisitoPolarionDialog(AppState state, Requisito requisito)
    {
        _state = state; _requisito = requisito;
        Title = L("Ticketing · requisito ", "Ticketing · requirement ", "Ticketing · exigence ") + requisito.Id;
        Width = 640; Height = 560; MinWidth = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Avalonia.Automation.AutomationProperties.SetAutomationId(this, "RequisitoPolarion_Dialog");

        var chiudi = new Button { Content = L("Chiudi", "Close", "Fermer"), HorizontalAlignment = HorizontalAlignment.Right };
        chiudi.Click += (_, _) => Close();
        Avalonia.Automation.AutomationProperties.SetAutomationId(chiudi, "RequisitoPolarion_Chiudi_Button");
        var apri = new Button { Content = L("Apri in Ticketing", "Open in Ticketing", "Ouvrir dans Ticketing") };
        apri.Click += (_, _) => Apri();
        Avalonia.Automation.AutomationProperties.SetAutomationId(apri, "RequisitoPolarion_Apri_Button");
        _aggiorna.Content = L("Aggiorna da Ticketing", "Refresh from Ticketing", "Actualiser depuis Ticketing");
        _aggiorna.Click += async (_, _) => await AggiornaAsync();
        Avalonia.Automation.AutomationProperties.SetAutomationId(_aggiorna, "RequisitoPolarion_Aggiorna_Button");
        var barra = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        barra.Children.Add(apri); barra.Children.Add(_aggiorna);

        var root = new DockPanel { Margin = new Thickness(16) };
        var basso = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        basso.Children.Add(barra); Grid.SetColumn(chiudi, 1); basso.Children.Add(chiudi);
        DockPanel.SetDock(basso, Dock.Bottom);
        root.Children.Add(basso);
        root.Children.Add(new ScrollViewer { Content = _contenuto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Content = root;
        Ricostruisci();
    }

    private static TextBlock Riga(string testo, bool grassetto = false, IBrush? colore = null) =>
        new() { Text = testo, TextWrapping = TextWrapping.Wrap, FontWeight = grassetto ? FontWeight.SemiBold : FontWeight.Normal, Foreground = colore };

    private void Ricostruisci()
    {
        _contenuto.Children.Clear();
        _contenuto.Children.Add(Riga(_requisito.Id + (string.IsNullOrWhiteSpace(_requisito.Testo) ? "" : " — " + _requisito.Testo), true));
        var link = RequisitoPolarionRegole.Di(_requisito);
        if (link is null)
        {
            _contenuto.Children.Add(Riga(RequisitoPolarionUi.EtichettaClasse(PolarionStatoClasse.NonCollegato)));
            _aggiorna.IsEnabled = false;
            return;
        }
        var stato = RequisitoPolarionStatoBuilder.Costruisci(_requisito, ProgettoConfigurato, CaricaSnapshot, DateTimeOffset.UtcNow);
        _contenuto.Children.Add(Riga("Ticketing: " + link.Value, false));
        var etichetta = stato.Classe == PolarionStatoClasse.NonDisponibile ? stato.TestoBreve(LocalizationService.Instance) : stato.Stato ?? "";
        _contenuto.Children.Add(Riga(L("Stato: ", "Status: ", "Statut : ") + etichetta, true, RequisitoPolarionUi.Pennello(stato.Classe, stato.Obsoleto)));
        if (stato.Classe != PolarionStatoClasse.NonDisponibile)
        {
            _contenuto.Children.Add(Riga(L("Risoluzione: ", "Resolution: ", "Résolution : ") +
                (string.IsNullOrWhiteSpace(stato.Risoluzione) ? L("non indicata", "not specified", "non précisée") : stato.Risoluzione)));
            if (!string.IsNullOrWhiteSpace(stato.Titolo)) _contenuto.Children.Add(Riga(stato.Titolo!));
            _contenuto.Children.Add(Riga(L("Letto da Ticketing il ", "Read from Ticketing on ", "Lu depuis Ticketing le ") +
                (stato.LettoAlUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "—") + (stato.Obsoleto ? "  ⚠ >24h" : "")));
            var snapshot = SnapshotSicuro(link.Value);
            _contenuto.Children.Add(Riga(L("Commenti", "Comments", "Commentaires") + $" ({snapshot?.Comments.Count ?? 0})", true));
            foreach (var c in (snapshot?.Comments ?? new()).OrderByDescending(x => x.Created ?? DateTimeOffset.MinValue))
            {
                var b = new Border { BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = UiTema.Separatore, Padding = new Thickness(0, 4) };
                var sp = new StackPanel { Spacing = 2 };
                sp.Children.Add(Riga((c.AuthorId ?? "?") + " · " + (c.Created?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? ""), true));
                sp.Children.Add(Riga(RequisitoPolarionUi.TestoPuro(c.Text)));
                b.Child = sp;
                _contenuto.Children.Add(b);
            }
        }
        else _contenuto.Children.Add(Riga(L("Nessun dato in cache: usare «Aggiorna da Ticketing» (richiede lettura API abilitata) oppure attendere la sincronizzazione.",
            "No cached data: use “Refresh from Ticketing” (needs API reading enabled) or wait for the sync.",
            "Aucune donnée en cache : utilisez « Actualiser depuis Ticketing » (lecture API requise) ou attendez la synchronisation.")));
        _contenuto.Children.Add(_messaggio);
    }

    private bool ProgettoConfigurato(string progetto)
    {
        try { return PolarionApiSettingsService.Load(_state.Paths).ProgettoConfigurato(progetto); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private PolarionTicketSnapshot? CaricaSnapshot(string progetto, string id) => new PolarionSnapshotStore(_state.Paths, false).Load(progetto, id);

    private PolarionTicketSnapshot? SnapshotSicuro(RequisitoPolarionLink l)
    {
        try { return CaricaSnapshot(l.Progetto, l.WorkItemId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { return null; }
    }

    private void Apri()
    {
        try
        {
            var link = RequisitoPolarionRegole.Di(_requisito);
            var url = link is null ? null : RequisitoPolarionRegole.UrlBrowser(PolarionApiSettingsService.Load(_state.Paths).ApiRoot, link.Value);
            if (url is null) { _messaggio.Text = L("API root HTTPS non configurata: impossibile aprire Ticketing.", "HTTPS API root not configured: cannot open Ticketing.", "API root HTTPS non configurée : impossible d'ouvrir Ticketing."); return; }
            RequisitoPolarionUi.ApriInBrowser(_state, url);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _messaggio.Text = L("Impossibile aprire il browser.", "Could not open the browser.", "Impossible d'ouvrir le navigateur.");
        }
    }

    private async Task AggiornaAsync()
    {
        var link = RequisitoPolarionRegole.Di(_requisito);
        if (link is null) return;
        _aggiorna.IsEnabled = false;
        _messaggio.Text = L("Aggiornamento in corso…", "Refreshing…", "Actualisation en cours…");
        var paths = _state.Paths;
        try
        {
            var esito = await Task.Run(async () =>
            {
                var s = PolarionApiSettingsService.Load(paths);
                if (s.ErroreCaricamento is not null || !s.LetturaAbilitata || !s.HaToken) return 1;
                if (!s.ProgettoConfigurato(link.Value.Progetto)) return 2;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var http = PolarionReadClient.CreateWindowsProxyHttpClient();
                var client = new PolarionReadClient(http, new Uri(s.ApiRoot, UriKind.Absolute), s.LeggiToken());
                await new PolarionTicketRefreshService(client, new PolarionSnapshotStore(paths, _state.PuoScrivere))
                    .RefreshAsync(link.Value.Progetto, link.Value.WorkItemId, cts.Token, requisito: true);
                return 0;
            });
            _messaggio.Text = esito switch
            {
                1 => L("Lettura Ticketing non abilitata o configurazione incompleta.", "Ticketing reading is not enabled or the configuration is incomplete.", "Lecture Ticketing non activée ou configuration incomplète."),
                2 => L("Il progetto del collegamento non è tra quelli configurati.", "The link's project is not among the configured ones.", "Le projet du lien ne figure pas parmi ceux configurés."),
                _ => ""
            };
            if (esito == 0) { RequisitoPolarionCache.Invalida(paths); RequisitoPolarionCache.Richiedi(_state, true); }
        }
        catch (HttpRequestException ex)
        {
            _messaggio.Text = ex.StatusCode is { } code ? L("Ticketing non raggiungibile (HTTP ", "Ticketing unreachable (HTTP ", "Ticketing injoignable (HTTP ") + (int)code + ")"
                : L("Ticketing non raggiungibile.", "Ticketing unreachable.", "Ticketing injoignable.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _messaggio.Text = L("Aggiornamento non disponibile (offline o risposta non valida).", "Refresh unavailable (offline or invalid response).", "Actualisation indisponible (hors ligne ou réponse invalide).");
        }
        Ricostruisci();
        _aggiorna.IsEnabled = true;
    }
}

/// <summary>Apre il dettaglio Ticketing di un requisito dato il suo ID (lettura del catalogo fuori dal thread UI).</summary>
public static class RequisitoPolarionDialogHelper
{
    public static async void Apri(Control origine, AppState state, string requisitoId)
    {
        try
        {
            var requisito = await Task.Run(() => state.Master.Requisiti.Load()
                .FirstOrDefault(r => string.Equals(r.Id, requisitoId, StringComparison.Ordinal)));
            if (requisito is null) return;
            var dialog = new RequisitoPolarionDialog(state, requisito);
            if (TopLevel.GetTopLevel(origine) is Window owner) await dialog.ShowDialog(owner);
            else dialog.Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }
}
