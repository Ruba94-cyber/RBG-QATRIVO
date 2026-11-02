using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>
/// Browser-only TICKET preparation. TestLoom never submits the Work Item: operators
/// review the Ticketing form and press Save there. REST is used for read-only reconciliation.
/// </summary>
internal static class PolarionCreateTicketDialog
{
    private static string L(string it, string en, string fr) => LocalizationService.Instance.Scegli(it, en, fr);

    public static Task OpenNcAsync(Window owner, AppState state, NonConformita nc, TestCase? sourceTest,
        string? selectedTestContext = null)
    {
        var title = !string.IsNullOrWhiteSpace(nc.DescrizioneBreveSegnalata)
            ? nc.DescrizioneBreveSegnalata
            : sourceTest?.DescrizioneBreve ?? "";
        if (string.IsNullOrWhiteSpace(title)) title = "NC " + nc.Id;
        var sections = sourceTest is null
            ? TicketExportService.GeneraTesto(nc, state, NcSezioni.Tutte.Where(x => x != NcSezioni.Allegati).ToHashSet())
            : TicketExportService.GeneraTesto(sourceTest, state, PolarionSezioni.Minime.ToHashSet());
        var text = "TestLoom NC: " + nc.Id + "\n\n" + sections;
        if (!string.IsNullOrWhiteSpace(selectedTestContext))
            text += "\n\n--- TestLoom Test evidence selected by the user ---\n" + selectedTestContext;
        var priority = nc.Severita switch
        {
            SeveritaAnomalia.Highest => "P1", SeveritaAnomalia.High => "P2",
            SeveritaAnomalia.Medium => "P3", SeveritaAnomalia.Low => "P4",
            SeveritaAnomalia.Lowest => "P5", _ => ""
        };
        var severity = nc.Priorita.ToString();
        return OpenAsync(owner, state, nc.Id, nc.Uid, false, title, text, priority, severity,
            nc.CollegamentiPolarion.Select(x => x.Codice).ToList(), sourceTest);
    }

    public static Task OpenCrAsync(Window owner, AppState state, ChangeRequest cr,
        string? selectedTestContext = null, TestCase? sourceTest = null)
    {
        var text = "TestLoom CR: " + cr.Id + "\n\n" +
            TicketExportService.GeneraTesto(cr, state, CrSezioni.Tutte.Where(x => x != CrSezioni.Allegati).ToHashSet());
        if (!string.IsNullOrWhiteSpace(selectedTestContext))
            text += "\n\n--- TestLoom Test evidence selected by the user ---\n" + selectedTestContext;
        return OpenAsync(owner, state, cr.Id, cr.Uid, true, cr.Titolo, text, "", "",
            cr.TicketCollegati.Select(x => x.Riferimento).ToList(), sourceTest);
    }

    private static async Task OpenAsync(Window owner, AppState state, string sourceId, Guid sourceUid,
        bool isCr, string suggestedTitle, string suggestedDescription, string suggestedPriority,
        string suggestedSeverity, IReadOnlyList<string> existingLinks, TestCase? sourceTest)
    {
        if (!state.PuoScrivere || state.OperatoreCorrente is null) return;
        var loc = LocalizationService.Instance;
        var receiptStore = new PolarionCreationReceiptStore(state.Paths);
        PolarionCreationReceiptStore.Receipt? pendingReceipt = null;
        var receiptIsCorrupt = false;
        try { pendingReceipt = receiptStore.Read(isCr, sourceId, sourceUid); }
        catch (Exception) { receiptIsCorrupt = true; /* Refuse to submit while a marker is unreadable. */ }
        var settings = PolarionApiSettingsService.Load(state.Paths);
        var dialog = new Window
        {
            Title = loc.Scegli("Anteprima creazione TICKET", "TICKET creation preview", "Aperçu de création TICKET"),
            Width = 700, MinWidth = 510, Height = 650, MinHeight = 410,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        AutomationProperties.SetAutomationId(dialog, "PolarionCreate_Preview_Window");
        UiInteractionTraceService.Attach(dialog, "TICKET.Create");
        var layout = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        layout.Children.Add(new TextBlock { Text = isCr ? "TICKET · New Request (to evaluate)" : "TICKET · Bug", FontSize = 19, FontWeight = FontWeight.SemiBold });
        layout.Children.Add(new TextBlock
        {
            Text = loc.Scegli("Origine TestLoom: ", "TestLoom source: ", "Source TestLoom : ") + sourceId +
                (existingLinks.Count > 0 ? L("\nESTA già collegati: ", "\nAlready linked ESTAs: ", "\nESTA déjà associés : ") + string.Join(", ", existingLinks) : ""),
            TextWrapping = TextWrapping.Wrap
        });
        if (receiptIsCorrupt)
            layout.Children.Add(new TextBlock
            {
                Text = L(
                    "La ricevuta locale di creazione TICKET non è leggibile. La creazione rimane bloccata finché la ricevuta non viene verificata e ripristinata.",
                    "The local TICKET creation receipt is unreadable. Creation is blocked until the receipt is reconciled and repaired.",
                    "Le reçu local de création TICKET est illisible. La création reste bloquée jusqu'à sa vérification et sa réparation."),
                Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap
            });
        if (pendingReceipt is not null)
            layout.Children.Add(new TextBlock
            {
                Text = L("Creazione TICKET precedente da riconciliare. ",
                    "Previous TICKET creation awaiting reconciliation. ",
                    "Création TICKET précédente en attente de rapprochement. ") +
                    (pendingReceipt.CreatedTicketId is null
                        ? L("Verifica in Ticketing prima di riprovare.",
                            "Check Ticketing before trying again.",
                            "Vérifiez dans Ticketing avant de réessayer.")
                        : L("ID TICKET creato: ", "Created TICKET ID: ", "ID TICKET créé : ") +
                          pendingReceipt.CreatedTicketId +
                          L(". Completa il collegamento con TestLoom prima di crearne un altro.",
                            ". Complete the TestLoom link before creating another.",
                            ". Terminez l'association TestLoom avant une nouvelle création.")),
                Foreground = Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap
            });
        // Browser-only creation is deliberate: never ask the user to enable
        // corporate REST POST/PATCH just to open the Ticketing creation form.
        layout.Children.Add(new TextBlock
        {
            Text = loc.Scegli(
                "Creazione nel browser: nessun invio REST da TestLoom. Dopo Salva, TestLoom leggerà il ticket e lo collegherà automaticamente quando verificabile.",
                "Browser creation: TestLoom sends no REST write. After Save, TestLoom will read and automatically link the ticket once verified.",
                "Création dans le navigateur : TestLoom n'effectue aucune écriture REST. Après l'enregistrement, TestLoom vérifiera et associera le ticket."),
            Classes = { "Muted" }, TextWrapping = TextWrapping.Wrap
        });
        var titleBox = new TextBox { Text = suggestedTitle, MaxLength = 512 };
        AutomationProperties.SetAutomationId(titleBox, "PolarionCreate_Title_TextBox");
        AddField(layout, L("Titolo", "Title", "Titre"), titleBox);
        var description = new TextBox { Text = suggestedDescription, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 185, MaxHeight = 300 };
        AutomationProperties.SetAutomationId(description, "PolarionCreate_Description_TextBox");
        AddField(layout, L("Descrizione", "Description", "Description"), description);
        var priorities = new[] { "", "P1", "P2", "P3", "P4", "P5" };
        var priorityBox = new ComboBox { ItemsSource = priorities, SelectedItem = priorities.Contains(suggestedPriority) ? suggestedPriority : "" };
        AddField(layout, L("Priorità (facoltativa)", "Priority (optional)", "Priorité (facultative)"), priorityBox);
        var severities = new[] { "", "Enhancement", "Trivial", "Minor", "Normal", "Major", "Critical" };
        var severityBox = new ComboBox { ItemsSource = severities,
            SelectedItem = severities.FirstOrDefault(x => x.Equals(suggestedSeverity, StringComparison.OrdinalIgnoreCase)) ?? "" };
        AddField(layout, L("Severità (facoltativa)", "Severity (optional)", "Gravité (facultative)"), severityBox);
        // These are Ticketing identities, intentionally independent of the two TestLoom users.
        var browserAssigneeChoices = new[] { "" }.Concat(settings.AssegnatariEsta).ToArray();
        var browserAssignee = new ComboBox
        {
            ItemsSource = browserAssigneeChoices, SelectedItem = "",
        };
        AutomationProperties.SetAutomationId(browserAssignee, "PolarionCreate_BrowserAssignee");
        AddField(layout, L("Assegnatario Ticketing (facoltativo; da verificare nel browser)",
            "Ticketing assignee (optional; verify in browser)",
            "Responsable Ticketing (facultatif ; vérifier dans le navigateur)"), browserAssignee);
        layout.Children.Add(new TextBlock
        {
            Text = L(
                "Precompilazione sperimentale: TestLoom prova a inserire tipo, titolo, descrizione, sottotipo, priorità, severità e assegnatario. " +
                "Alcuni campi personalizzati potrebbero restare vuoti. Descrizione e TestLoomREF compaiono nell'URL del browser, anche nella cronologia locale. Verifica i campi prima di premere Salva.",
                "Experimental prefill: TestLoom attempts to fill type, title, description, subtype, priority, severity and assignee. " +
                "Some custom fields may remain empty. The description and TestLoomREF appear in the browser URL and local history. Check every field before clicking Save.",
                "Préremplissage expérimental : TestLoom tente de remplir le type, le titre, la description, le sous-type, la priorité, la gravité et le responsable. " +
                "Certains champs personnalisés peuvent rester vides. La description et TestLoomREF figurent dans l'URL et l'historique local. Vérifiez les champs avant d'enregistrer."),
            Classes = { "Muted" }, TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(new TextBlock
        {
            Text = loc.Scegli("Con la modalità browser il salvataggio avviene con l'utente autenticato su Ticketing; il token condiviso serve solo alle letture TestLoom.",
                "Browser saves use your signed-in Ticketing account; the shared token is used for TestLoom reads only.",
                "En mode navigateur, l'enregistrement utilise le compte connecté à Ticketing; le jeton partagé sert uniquement à la lecture TestLoom."),
            Classes = { "Muted" }, TextWrapping = TextWrapping.Wrap
        });
        // Once prepared, copy EXACTLY the frozen draft fields used for handoff
        // (rather than any subsequently edited, unsaved textbox contents).
        PolarionBrowserHandoffService.Prepared? preparedHandoff = null;
        string frozenTitle = "", frozenDescription = "", frozenPriority = "", frozenSeverity = "",
            frozenAssignee = "";
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, "PolarionCreate_Result_Text");
        layout.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = loc.Scegli("Chiudi", "Close", "Fermer"), Classes = { "Secondary" } };
        var browser = new Button
        {
            Content = L("Apri nuovo TICKET precompilato in Ticketing", "Open prefilled TICKET form in Ticketing",
                "Ouvrir le formulaire TICKET prérempli dans Ticketing"), Classes = { "Primary" },
            IsEnabled = !receiptIsCorrupt && pendingReceipt is null &&
                settings.ErroreCaricamento is null &&
                Uri.TryCreate(settings.ApiRoot, UriKind.Absolute, out var browserHost) &&
                browserHost.Scheme == Uri.UriSchemeHttps
        };
        AutomationProperties.SetAutomationId(browser, "PolarionCreate_OpenBrowser");
        var copyRow = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        Button CopyFieldButton(string label, string automationId, Func<string> content)
        {
            var button = new Button { Content = label, Classes = { "Secondary" }, IsEnabled = false,
                Margin = new Thickness(0, 3, 6, 3) };
            AutomationProperties.SetAutomationId(button, automationId);
            button.Click += async (_, _) =>
            {
                if (preparedHandoff is null) return;
                var clipboard = TopLevel.GetTopLevel(dialog)?.Clipboard;
                if (clipboard is null) return;
                try
                {
                    await clipboard.SetTextAsync(content());
                    status.Text = L("Copiato negli appunti: ", "Copied to clipboard: ", "Copié dans le presse-papiers : ") + label;
                }
                catch (Exception)
                {
                    status.Text = L("Impossibile copiare il campo selezionato.",
                        "Could not copy the selected field.", "Impossible de copier le champ sélectionné.");
                }
            };
            copyRow.Children.Add(button);
            return button;
        }
        var copies = new[]
        {
            CopyFieldButton(L("Copia riepilogo completo", "Copy full summary", "Copier le résumé complet"), "TICKET_Copy_Summary",
                () => preparedHandoff!.ClipboardText),
            CopyFieldButton(L("Copia titolo", "Copy title", "Copier le titre"), "TICKET_Copy_Title", () => frozenTitle),
            CopyFieldButton(L("Copia descrizione + TestLoomREF", "Copy description + TestLoomREF", "Copier description + TestLoomREF"), "TICKET_Copy_Description",
                () => PolarionBrowserHandoffService.DescriptionWithMarker(
                    frozenDescription, preparedHandoff!.Receipt)),
            CopyFieldButton(L("Copia classificazione", "Copy classification", "Copier la classification"), "TICKET_Copy_Classification",
                () => isCr ? "new_request" : "bug"),
            CopyFieldButton(L("Copia assegnatario", "Copy assignee", "Copier le responsable"), "TICKET_Copy_Assignee", () => frozenAssignee),
            CopyFieldButton(L("Copia priorità", "Copy priority", "Copier la priorité"), "TICKET_Copy_Priority", () => frozenPriority),
            CopyFieldButton(L("Copia severità", "Copy severity", "Copier la gravité"), "TICKET_Copy_Severity", () => frozenSeverity)
        };
        layout.Children.Add(new TextBlock
        {
            Text = L(
                "TestLoom tenta la precompilazione tramite URL. Se Ticketing ignora alcuni campi, usa i pulsanti di copia senza ricreare il ticket. La descrizione con TestLoomREF è disponibile negli appunti.",
                "TestLoom attempts URL prefill. If Ticketing ignores some fields, use the copy buttons without starting a second creation. The TestLoomREF description is available on the clipboard.",
                "TestLoom tente le préremplissage via URL. Si Ticketing ignore certains champs, utilisez les boutons de copie sans recréer le ticket. La description TestLoomREF est disponible dans le presse-papiers."),
            Classes = { "Muted" }, TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(copyRow);
        actions.Children.Add(cancel);
        actions.Children.Add(browser);
        // No REST-submit button in this dialog; the only save is in Ticketing.
        cancel.Click += (_, _) => dialog.Close();
        layout.Children.Add(actions);
        var ticketId = new TextBox
        {
            PlaceholderText = L("TICKET-12345 (facoltativo, se salvato in Ticketing)",
                "TICKET-12345 (optional, if saved in Ticketing)",
                "TICKET-12345 (facultatif, si enregistré dans Ticketing)"),
            MaxLength = 70
        };
        AutomationProperties.SetAutomationId(ticketId, "PolarionCreate_VerifyBrowserId");
        AddField(layout, L("Riconciliazione del ticket salvato in Ticketing",
            "Reconcile the ticket saved in Ticketing", "Rapprocher le ticket enregistré dans Ticketing"), ticketId);
        var verify = new Button
        {
            Content = L("Verifica salvataggio e collega a TestLoom", "Verify save and link to TestLoom",
                "Vérifier l’enregistrement et associer à TestLoom"), Classes = { "Secondary" }
        };
        AutomationProperties.SetAutomationId(verify, "PolarionCreate_VerifyBrowser");
        layout.Children.Add(verify);
        dialog.Content = new ScrollViewer { Content = layout };

        browser.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(titleBox.Text) || string.IsNullOrWhiteSpace(description.Text))
            {
                status.Text = L("Compila titolo e descrizione prima di preparare il ticket.",
                    "Enter a title and description before preparing the ticket.",
                    "Renseignez le titre et la description avant de préparer le ticket.");
                return;
            }
            browser.IsEnabled = false;
            try
            {
                DiagnosticTraceService.Registra("TICKET.BrowserDraft", "prepare source=" + sourceId +
                    " kind=" + (isCr ? "CR" : "NC"));
                var handoff = PolarionBrowserHandoffService.Prepare(state, isCr, sourceId, sourceUid,
                    titleBox.Text, description.Text,
                    priorityBox.SelectedItem as string, severityBox.SelectedItem as string,
                    browserAssignee.SelectedItem as string, sourceTest);
                preparedHandoff = handoff;
                frozenTitle = titleBox.Text!;
                frozenDescription = description.Text!;
                frozenPriority = priorityBox.SelectedItem as string ?? "";
                frozenSeverity = severityBox.SelectedItem as string ?? "";
                frozenAssignee = browserAssignee.SelectedItem as string ?? "";
                foreach (var copy in copies) copy.IsEnabled = true;
                var clipboard = TopLevel.GetTopLevel(dialog)?.Clipboard;
                try
                {
                    if (clipboard is not null)
                        await clipboard.SetTextAsync(PolarionBrowserHandoffService.DescriptionWithMarker(
                            frozenDescription, handoff.Receipt));
                }
                catch (Exception ex)
                {
                    // Best effort only: enterprise clipboard permissions must
                    // not prevent opening the already prepared Ticketing form.
                    DiagnosticTraceService.Registra("TICKET.BrowserDraft",
                        "clipboard-unavailable kind=" + ex.GetType().Name);
                }
                Process.Start(new ProcessStartInfo(handoff.BrowserUrl.AbsoluteUri) { UseShellExecute = true });
                DiagnosticTraceService.Registra("TICKET.BrowserDraft", "opened create form for source=" + sourceId +
                    " prefill-attempted=" + handoff.FullPrefillAttempted);
                status.Text = handoff.FullPrefillAttempted
                    ? L(
                        "Modulo TICKET aperto con precompilazione sperimentale. Controlla sottotipo, descrizione con TestLoomREF, priorità, severità e assegnatario. " +
                        "Se tutti i campi sono corretti, premi Salva in Ticketing. TestLoom collegherà automaticamente l'TICKET " +
                            (sourceTest is null ? "alla NC/CR." : "alla NC/CR e al Test di origine."),
                        "TICKET form opened with experimental prefill. Check subtype, description with TestLoomREF, priority, severity and assignee. " +
                        "If all fields are correct, click Save in Ticketing. TestLoom will automatically link the TICKET " +
                            (sourceTest is null ? "to the NC/CR." : "to the NC/CR and originating Test."),
                        "Formulaire TICKET ouvert avec préremplissage expérimental. Vérifiez le sous-type, la description TestLoomREF, la priorité, la gravité et le responsable. " +
                        "Si tout est correct, enregistrez dans Ticketing. TestLoom associera automatiquement l'TICKET " +
                            (sourceTest is null ? "à la NC/CR." : "à la NC/CR et au Test d'origine."))
                    : L("Ticketing aperto con precompilazione parziale. ",
                        "Ticketing opened with partial prefill. ",
                        "Ticketing ouvert avec préremplissage partiel. ") +
                      (handoff.PrefillNotice ?? "") +
                      L(" Usa Copia descrizione + TestLoomREF e completa i campi mancanti prima di salvare.",
                        " Use Copy description + TestLoomREF and complete missing fields before saving.",
                        " Utilisez Copier description + TestLoomREF et complétez les champs manquants avant l'enregistrement.");
                status.Foreground = Brushes.DarkGreen;
            }
            catch (Exception ex)
            {
                status.Text = ex is InvalidOperationException &&
                    ex.Message.Contains("unsaved or changed data", StringComparison.Ordinal)
                    ? L("Il Test contiene modifiche non salvate o è cambiato su disco. Salva o ricarica il Test prima di preparare l'TICKET.",
                        "The Test has unsaved changes or changed on disk. Save or reload it before preparing the TICKET.",
                        "Le Test contient des modifications non enregistrées ou a changé sur le disque. Enregistrez-le ou rechargez-le avant de préparer l'TICKET.")
                    : L("Impossibile aprire Ticketing. Se esiste una bozza pendente, riconciliala prima di riprovare.",
                        "Could not open Ticketing. If a draft is pending, reconcile it before trying again.",
                        "Impossible d'ouvrir Ticketing. Si un brouillon est en attente, rapprochez-le avant de réessayer.");
                status.Foreground = Brushes.Firebrick;
                // Validation failures before receipt creation can be corrected
                // in the same dialog. Never allow another Prepare if a marker
                // was already persisted and the browser may have opened.
                try { browser.IsEnabled = receiptStore.Read(isCr, sourceId, sourceUid) is null; }
                catch (Exception) { browser.IsEnabled = false; }
                DiagnosticTraceService.Registra("TICKET.BrowserDraft",
                    "prepare-failed kind=" + ex.GetType().Name + " retry-allowed=" + browser.IsEnabled);
            }
        };

        verify.Click += async (_, _) =>
        {
            verify.IsEnabled = false;
            try
            {
                var receipt = receiptStore.Read(isCr, sourceId, sourceUid);
                if (receipt?.BrowserCorrelationMarker is null)
                {
                    status.Text = L("Nessuna creazione browser in attesa per questa NC/CR.",
                        "No pending browser creation for this NC/CR.",
                        "Aucune création dans le navigateur en attente pour cette NC/CR.");
                    return;
                }
                var id = string.IsNullOrWhiteSpace(ticketId.Text) ? null : ticketId.Text.Trim().ToUpperInvariant();
                DiagnosticTraceService.Registra("TICKET.BrowserVerify", "manual-check source=" + sourceId +
                    " has-provided-id=" + (id is not null));
                var result = await PolarionBrowserHandoffService.TryReconcileAsync(
                    state, receipt, explicitlySavedId: id);
                status.Text = result.Message + (result.TicketId is null ? "" : " " + result.TicketId);
                status.Foreground = result.Linked ? Brushes.DarkGreen : Brushes.DarkOrange;
                if (result.Linked)
                {
                    browser.IsEnabled = false;
                }
            }
            catch (Exception ex)
            {
                status.Text = L("Verifica non disponibile (", "Verification unavailable (", "Vérification indisponible (") +
                    ex.GetType().Name +
                    L("). Nessun nuovo ticket è stato creato.", "). No new ticket was created.",
                        "). Aucun nouveau ticket n'a été créé.");
                status.Foreground = Brushes.Firebrick;
            }
            finally { verify.IsEnabled = true; }
        };

        await dialog.ShowDialog<bool>(owner);
    }

    private static void AddField(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(control);
    }

}
