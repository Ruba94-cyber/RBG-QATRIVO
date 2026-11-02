using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>
/// TICKET read-only panel reused by NC and CR quick-edit dialogs. It does not modify the editor's
/// draft, workflow or save pipeline. Shows cache immediately; HTTP refresh is asynchronous.
/// </summary>
internal static class PolarionQuickEditPanel
{
    private static readonly Regex TicketIdPattern = new(@"(?<![A-Za-z0-9])TICKET-[0-9]+(?![0-9])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static List<(string Id, string Url)> TicketReferences(IEnumerable<(string Id, string Url)> links)
    {
        var unique = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, url) in links)
        {
            var match = TicketIdPattern.Match(id ?? "");
            if (!match.Success) match = TicketIdPattern.Match(url ?? "");
            if (match.Success && !unique.ContainsKey(match.Value))
                unique.Add(match.Value.ToUpperInvariant(), url ?? "");
        }
        return unique.Select(pair => (pair.Key, pair.Value)).ToList();
    }

    public static Control Create(DataPaths paths, IEnumerable<(string Id, string Url)> links, Window dialog, string operatorId, bool isCr = false, bool testloomClosed = false, bool operatorCanWrite = false, Func<bool>? operatorCanWriteNow = null)
    {
        var references = TicketReferences(links);
        var panel = new StackPanel { Spacing = 8 };
        if (references.Count == 0) return panel;
        var loc = LocalizationService.Instance;
        PolarionLocalReadTracker? tracker = string.IsNullOrWhiteSpace(operatorId) ? null : new PolarionLocalReadTracker(paths, operatorId);
        panel.Children.Add(new TextBlock
        {
            Text = "TICKET / Ticketing",
            FontWeight = FontWeight.SemiBold,
            FontSize = 15
        });
        var effective = new TextBlock { Classes = { "Muted" }, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(effective, "PolarionQuick_EffectiveCriticality");
        panel.Children.Add(effective);
        var snapshots = new Dictionary<string, PolarionTicketSnapshot>(StringComparer.OrdinalIgnoreCase);
        void UpdateEffective()
        {
            var criticality = PolarionCriticalityService.Evaluate(snapshots.Values, isCr, testloomClosed);
            effective.Text = criticality is null ? "" : "TestLoom / TICKET · " +
                (string.IsNullOrEmpty(criticality.Priority) ? "—" : criticality.Priority) + " · " +
                (string.IsNullOrEmpty(criticality.Severity) ? "—" : criticality.Severity);
        }

        var token = new CancellationTokenSource();
        dialog.Closed += (_, _) => token.Cancel();
        var refreshers = new List<Func<Task>>();

        foreach (var (id, storedUrl) in references)
        {
            // Link assente (ticket digitato solo come TICKET-n): derivato dall'host Ticketing configurato.
            var url = string.IsNullOrWhiteSpace(storedUrl) ? PolarionTicketLinkService.DerivaUrl(paths, id) : storedUrl;
            var ticket = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 6) };
            ticket.Children.Add(new TextBlock { Text = id, FontWeight = FontWeight.SemiBold });

            var status = new TextBlock { Text = loc.Scegli("Nessun dato in cache", "No cached data", "Aucune donnée en cache"), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(status, "PolarionQuick_Status_" + id);
            ticket.Children.Add(status);

            var details = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "Muted" } };
            ticket.Children.Add(details);
            var fullDescription = new TextBlock { TextWrapping = TextWrapping.Wrap };
            ticket.Children.Add(new Expander
            {
                Header = loc.Scegli("Descrizione originale Ticketing", "Original Ticketing description", "Description Ticketing originale"),
                Content = fullDescription
            });
            var unread = new TextBlock { Classes = { "Muted" }, IsVisible = false };
            AutomationProperties.SetAutomationId(unread, "PolarionQuick_Unread_" + id);
            ticket.Children.Add(unread);
            void UpdateUnread()
            {
                try
                {
                    if (tracker is null) return;
                    var status = tracker.GetUnread(id);
                    unread.IsVisible = status.Comments > 0 || status.Fields;
                    unread.Text = (status.Comments > 0 ? status.Comments + " " +
                        loc.Scegli("nuovi commenti", "new comments", "nouveaux commentaires") : "") +
                        (status.Fields ? "  ·  " +
                            loc.Scegli("TICKET aggiornato", "TICKET updated", "TICKET mis à jour") : "");
                }
                catch (Exception) { unread.IsVisible = false; }
            }
            UpdateUnread();

            var comments = new StackPanel { Spacing = 5 };
            var expander = new Expander
            {
                Header = loc.Scegli("Commenti Ticketing", "Ticketing comments", "Commentaires Ticketing"),
                Content = comments
            };
            ticket.Children.Add(expander);
            expander.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.Property != Expander.IsExpandedProperty || !expander.IsExpanded) return;
                try { tracker?.MarkRead(id, comments: true, fields: false); UpdateUnread(); }
                catch (Exception) { /* Local optional read state is never critical to quick edit. */ }
            };

            var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            var refresh = new Button { Content = loc.Scegli("Aggiorna TICKET", "Refresh TICKET", "Actualiser TICKET"), Classes = { "Secondary" } };
            AutomationProperties.SetAutomationId(refresh, "PolarionQuick_Refresh_" + id);
            actions.Children.Add(refresh);
            if (Uri.TryCreate(url, UriKind.Absolute, out var link) && link.Scheme == Uri.UriSchemeHttps)
            {
                var open = new Button { Content = loc.Scegli("Apri in Ticketing", "Open in Ticketing", "Ouvrir dans Ticketing"), Classes = { "Link" } };
                AutomationProperties.SetAutomationId(open, "PolarionQuick_Open_" + id);
                open.Click += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true }); }
                    catch { /* Browser unavailable; TestLoom remains functional. */ }
                };
                actions.Children.Add(open);
            }
            // All editing now happens only in the operator's authenticated Ticketing browser.
            // TestLoom retains read-only GET refresh and the browser-open action above.
            ticket.Children.Add(actions);
            panel.Children.Add(ticket);

            void Render(PolarionTicketSnapshot snapshot)
            {
                status.Text = string.Join(" · ", new[]
                {
                    snapshot.Status ?? "—",
                    snapshot.Resolution ?? "—",
                    snapshot.TicketType ?? "—"
                });
                snapshots[snapshot.Id] = snapshot;
                UpdateEffective();
                details.Text = (snapshot.Title ?? "") + "\n" + loc.Scegli("Priorità: ", "Priority: ", "Priorité : ") +
                        (snapshot.Priority ?? "—")
                    + " · " + loc.Scegli("Severità: ", "Severity: ", "Gravité : ") + (snapshot.Severity ?? "—")
                    + "\n" + loc.Scegli("Creato: ", "Created: ", "Créé : ") +
                        (snapshot.Created?.ToLocalTime().ToString("g") ?? "—")
                    + " · " + loc.Scegli("Aggiornato: ", "Updated: ", "Mis à jour : ") +
                        (snapshot.Updated?.ToLocalTime().ToString("g") ?? "—")
                    + "\n" + loc.Scegli("Ultimo recupero: ", "Last fetched: ", "Dernière synchronisation : ") +
                        snapshot.RetrievedAtUtc.ToLocalTime().ToString("g")
                    + (snapshot.Assignees.Count == 0 ? "" :
                        "\n" + loc.Scegli("Assegnatari Ticketing: ", "Ticketing assignees: ", "Responsables Ticketing : ") +
                        string.Join(", ", snapshot.Assignees))
                    + (snapshot.CustomAssignees.Count == 0 ? "" :
                        "\n" + loc.Scegli("Assegnatari aggiuntivi: ", "Additional assignees: ", "Responsables supplémentaires : ") +
                        string.Join(", ", snapshot.CustomAssignees))
                    + (snapshot.LinkedWorkItems.Count == 0 ? "" :
                        "\n" + loc.Scegli("Work Item collegati: ", "Linked work items: ", "Work items associés : ") +
                        string.Join(", ", snapshot.LinkedWorkItems));
                // Show Ticketing source text literally: never interpret HTML as UI markup.
                fullDescription.Text = snapshot.Description ?? "";
                comments.Children.Clear();
                foreach (var comment in snapshot.Comments)
                {
                    var label = (comment.AuthorId ?? "—") + " · " + (comment.Created?.ToLocalTime().ToString("g") ?? "—");
                    comments.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
                    // Rich text is displayed as literal source, not rendered as executable HTML.
                    comments.Children.Add(new TextBlock { Text = comment.Text, TextWrapping = TextWrapping.Wrap });
                }
                expander.Header = loc.Scegli("Commenti Ticketing", "Ticketing comments", "Commentaires Ticketing") + " (" + snapshot.Comments.Count + ")";
                try
                {
                    tracker?.Observe(snapshot, PolarionNotificationScopeService.Mode.Passive);
                    if (expander.IsExpanded) tracker?.MarkRead(id, comments: true, fields: false);
                    tracker?.MarkRead(id, comments: false, fields: true); // the current status is visible now
                    UpdateUnread();
                }
                catch (Exception) { /* Don't block the quick editor on a corrupt local read cursor. */ }
            }

            try
            {
                var cached = new PolarionSnapshotStore(paths).Load("TICKET_Tickets", id);
                if (cached != null) Render(cached);
            }
            catch (Exception)
            {
                // A malformed or identity-mismatched cache must not prevent opening NC/CR quick edit.
                status.Text = loc.Scegli("Cache TICKET non leggibile", "TICKET cache unavailable", "Cache TICKET indisponible");
            }

            async Task RefreshAsync()
            {
                if (token.IsCancellationRequested) return;
                refresh.IsEnabled = false;
                DiagnosticTraceService.Registra("Ticketing.ManualRefresh", "start ticket=" + id);
                try
                {
                    var settings = PolarionApiSettingsService.Load(paths);
                    if (!settings.LetturaAbilitata || !settings.HaToken || settings.ErroreCaricamento is not null)
                    {
                        details.Text = loc.Scegli("Sincronizzazione TICKET non configurata", "TICKET sync not configured", "Synchronisation TICKET non configurée");
                        DiagnosticTraceService.Registra("Ticketing.ManualRefresh", "not-configured ticket=" + id);
                        return;
                    }
                    using var http = PolarionReadClient.CreateWindowsProxyHttpClient();
                    var client = new PolarionReadClient(http, new Uri(settings.ApiRoot, UriKind.Absolute), settings.LeggiToken());
                    var stored = await new PolarionTicketRefreshService(client, new PolarionSnapshotStore(paths, operatorCanWriteNow?.Invoke() ?? operatorCanWrite))
                        .RefreshAndStoreAsync(settings.ProjectId, id, token.Token);
                    var snapshot = stored.Snapshot;
                    if (!token.IsCancellationRequested)
                    {
                        Render(snapshot);
                        DiagnosticTraceService.Registra("Ticketing.ManualRefresh",
                            "complete ticket=" + id + " comments=" + snapshot.Comments.Count);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    DiagnosticTraceService.Registra("Ticketing.ManualRefresh",
                        "failed ticket=" + id + " kind=" + ex.GetType().Name +
                        (ex is HttpRequestException httpError ? " http=" + (int?)httpError.StatusCode : ""));
                    if (!token.IsCancellationRequested)
                        details.Text = loc.Scegli("Aggiornamento non riuscito; dati precedenti conservati",
                            "Refresh failed; previous data retained", "Actualisation échouée; données conservées") +
                            " (" + (ex is HttpRequestException he && he.StatusCode is { } sc ? "HTTP " + (int)sc : ex.GetType().Name) + ")";
                }
                finally
                {
                    if (!token.IsCancellationRequested) refresh.IsEnabled = true;
                }
            }

            refresh.Click += async (_, _) => await RefreshAsync();
            refreshers.Add(RefreshAsync);
        }

        dialog.Opened += async (_, _) =>
        {
            foreach (var refresh in refreshers)
            {
                if (token.IsCancellationRequested) break;
                await refresh();
            }
        };
        return panel;
    }
}
