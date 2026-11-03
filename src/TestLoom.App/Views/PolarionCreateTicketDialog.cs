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
        var test = sourceTest ?? (string.IsNullOrWhiteSpace(nc.TestCaseId) ? null : state.TestCases.GetById(nc.TestCaseId));
        var shortDescription = !string.IsNullOrWhiteSpace(nc.DescrizioneBreveSegnalata)
            ? nc.DescrizioneBreveSegnalata
            : test?.DescrizioneBreve ?? "";
        var veicoli = nc.DaSegnalazioneRete || test is null ? nc.VeicoliSegnalati : test.VeicoliEffettivi;
        var componente = nc.DaSegnalazioneRete || test is null ? nc.EcuSegnalate : test.Componente;
        var funzionalita = nc.DaSegnalazioneRete || test is null ? nc.FunzionalitaSegnalate : test.Funzionalita;
        var strumenti = !string.IsNullOrWhiteSpace(nc.StrumentoSegnalato) ? new[] { nc.StrumentoSegnalato }
            : test?.StrumentiUsati.ToArray() ?? Array.Empty<string>();
        var title = PolarionTicketTitle.Build(strumenti, PolarionTicketTitle.GammaDi(state, veicoli), veicoli, componente,
            funzionalita, shortDescription);
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
        var title = PolarionTicketTitle.Build(cr.StrumentiImpattati, PolarionTicketTitle.GammaDi(state, cr.VeicoliImpattati),
            cr.VeicoliImpattati, cr.EcuImpattate, cr.FunzionalitaImpattate,
            string.IsNullOrWhiteSpace(cr.DescrizioneBreve) ? cr.Titolo : cr.DescrizioneBreve);
        if (string.IsNullOrWhiteSpace(title)) title = cr.Titolo;
        return OpenAsync(owner, state, cr.Id, cr.Uid, true, title, text, "", "",
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
                        ? L("Se hai chiuso il modulo senza salvare, riaprilo con il pulsante qui sotto (stessa bozza) oppure annullala; se hai salvato, usa la verifica.",
                            "If you closed the form without saving, reopen it with the button below (same draft) or discard it; if you saved, use the verification.",
                            "Si vous avez fermé le formulaire sans enregistrer, rouvrez-le avec le bouton ci-dessous (même brouillon) ou annulez-le ; si vous avez enregistré, utilisez la vérification.")
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
            Content = L("Compila in automatico (profilo browser TestLoom)", "Fill automatically (TestLoom browser profile)",
                "Remplir automatiquement (profil navigateur TestLoom)"), Classes = { "Secondary" },
            IsEnabled = !receiptIsCorrupt && (pendingReceipt is null || CanReopen(pendingReceipt)) &&
                settings.ErroreCaricamento is null &&
                Uri.TryCreate(settings.ApiRoot, UriKind.Absolute, out var browserHost) &&
                browserHost.Scheme == Uri.UriSchemeHttps
        };
        var ReopenLabel = L("Riapri e ricompila (profilo TestLoom, stessa bozza)",
            "Reopen and fill again (TestLoom profile, same draft)",
            "Rouvrir et remplir à nouveau (profil TestLoom, même brouillon)");
        if (pendingReceipt is not null && CanReopen(pendingReceipt)) browser.Content = ReopenLabel;
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
                "TestLoom apre il modulo in Edge e compila titolo e descrizione con il codice TestLoomREF; gli altri campi restano da impostare in Ticketing. Se la compilazione non riesce, o preferisci la tua sessione del browser, usa \"Apri nel mio browser\" e i pulsanti di copia.",
                "TestLoom opens the form in Edge and fills title and description with the TestLoomREF code; the other fields are set in Ticketing. If filling fails, use the copy buttons.",
                "TestLoom ouvre le formulaire dans Edge et remplit le titre et la description avec le code TestLoomREF ; les autres champs se règlent dans Ticketing. Si le remplissage échoue, utilisez les boutons de copie."),
            Classes = { "Muted" }, TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(copyRow);
        actions.Children.Add(cancel);
        // Second, always available way: open the link in the operator's own (already signed-in) browser and paste.
        var browserPlain = new Button
        {
            Content = L("Apri in Ticketing nel mio browser (consigliato)", "Open in Ticketing in my own browser (recommended)",
                "Ouvrir dans Ticketing avec mon navigateur (recommandé)"),
            Classes = { "Primary" }, IsEnabled = browser.IsEnabled
        };
        AutomationProperties.SetAutomationId(browserPlain, "PolarionCreate_OpenBrowserPlain");
        browser.PropertyChanged += (_, e) =>
        {
            if (e.Property == Button.IsEnabledProperty) browserPlain.IsEnabled = browser.IsEnabled;
        };
        actions.Children.Add(browserPlain);
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
        var discard = new Button
        {
            Content = L("Annulla bozza non salvata", "Discard unsaved draft", "Annuler le brouillon non enregistré"),
            Classes = { "Secondary" }, IsEnabled = pendingReceipt is not null && !receiptIsCorrupt
        };
        AutomationProperties.SetAutomationId(discard, "PolarionCreate_DiscardUnsaved");
        layout.Children.Add(discard);
        dialog.Content = new ScrollViewer { Content = layout };

        var closing = new CancellationTokenSource();
        dialog.Closed += (_, _) => closing.Cancel();
        async Task<AutofillResult> RunAutofillAsync(Uri url, string title, string text)
        {
            try { return await PolarionFormAutofill.RunAsync(url, title, text, closing.Token,
                browserExe: PolarionBrowserLauncher.EseguibileProfiloDitm(state.Config)); }
            catch (OperationCanceledException) { return new AutofillResult(AutofillOutcome.OpenedNotFilled, "dialog-closed"); }
        }

        async Task OpenFormAsync(bool autofill)
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
                var existing = receiptStore.Read(isCr, sourceId, sourceUid);
                var handoff = existing is not null
                    ? PolarionBrowserHandoffService.Reopen(state, existing, titleBox.Text, description.Text)
                    : PolarionBrowserHandoffService.Prepare(state, isCr, sourceId, sourceUid,
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
                var clipboardOk = false;
                try
                {
                    if (clipboard is not null)
                    {
                        await clipboard.SetTextAsync(PolarionBrowserHandoffService.DescriptionWithMarker(
                            frozenDescription, handoff.Receipt));
                        clipboardOk = true;
                    }
                }
                catch (Exception ex)
                {
                    // Best effort only: enterprise clipboard permissions must
                    // not prevent opening the already prepared Ticketing form.
                    DiagnosticTraceService.Registra("TICKET.BrowserDraft",
                        "clipboard-unavailable kind=" + ex.GetType().Name);
                }
                status.Text = !autofill
                    ? L("Apro Ticketing nel tuo browser...", "Opening Ticketing in your browser...", "Ouverture de Ticketing dans votre navigateur...")
                    : L("Apro Ticketing nel browser: se richiesto accedi, TestLoom compilerà titolo e descrizione appena il modulo è pronto (fino a 3 minuti)...",
                    "Opening Ticketing in the browser: sign in if asked, TestLoom fills title and description as soon as the form is ready (up to 3 minutes)...",
                    "Ouverture de Ticketing dans le navigateur : connectez-vous si nécessaire, TestLoom remplit le titre et la description dès que le formulaire est prêt (jusqu'à 3 minutes)...");
                status.Foreground = Brushes.DarkOrange;
                var fill = autofill
                    ? await RunAutofillAsync(handoff.BrowserUrl, frozenTitle,
                        PolarionBrowserHandoffService.DescriptionWithMarker(frozenDescription, handoff.Receipt))
                    : new AutofillResult(AutofillOutcome.NotLaunched, "manual-paste");
                if (fill.Outcome == AutofillOutcome.NotLaunched)
                    Process.Start(new ProcessStartInfo(handoff.BrowserUrl.AbsoluteUri) { UseShellExecute = true });
                DiagnosticTraceService.Registra("TICKET.BrowserDraft", "opened create form for source=" + sourceId +
                    " autofill=" + fill.Outcome);
                status.Text = !autofill && !clipboardOk
                    ? L("Ticketing aperto nel tuo browser, ma gli appunti non sono disponibili: usa \"Copia titolo\" e \"Copia descrizione + TestLoomREF\" e incolla in Ticketing prima di salvare.",
                        "Ticketing opened in your browser, but the clipboard is unavailable: use \"Copy title\" and \"Copy description + TestLoomREF\" and paste into Ticketing before saving.",
                        "Ticketing ouvert dans votre navigateur, mais le presse-papiers est indisponible : utilisez « Copier le titre » et « Copier description + TestLoomREF » et collez dans Ticketing avant d'enregistrer.")
                    : !autofill
                    ? L("Ticketing aperto nel tuo browser. La descrizione con TestLoomREF è negli appunti: incollala in Ticketing, usa \"Copia titolo\" per il titolo e premi Salva. " +
                        "Se Ticketing mostra la pagina precedente, usa di nuovo il pulsante per riaprire il link.",
                        "Ticketing opened in your browser. The description with TestLoomREF is on the clipboard: paste it into Ticketing, use \"Copy title\" for the title and click Save. " +
                        "If Ticketing shows the previous page, press the button again to reopen the link.",
                        "Ticketing ouvert dans votre navigateur. La description avec TestLoomREF est dans le presse-papiers : collez-la dans Ticketing, utilisez « Copier le titre » pour le titre et enregistrez. " +
                        "Si Ticketing affiche la page précédente, appuyez à nouveau sur le bouton pour rouvrir le lien.")
                    : fill.Outcome == AutofillOutcome.Filled
                    ? L("Titolo e descrizione (con TestLoomREF) compilati in Ticketing. Controlla, completa gli altri campi e premi Salva: TestLoom collegherà l'TICKET dopo il salvataggio. " +
                        "Se chiudi il modulo senza salvare, usa \"Annulla bozza non salvata\".",
                        "Title and description (with TestLoomREF) filled in Ticketing. Review, complete the other fields and click Save: TestLoom will link the TICKET after you save. " +
                        "If you close the form without saving, use \"Discard unsaved draft\".",
                        "Titre et description (avec TestLoomREF) remplis dans Ticketing. Vérifiez, complétez les autres champs et enregistrez : TestLoom associera l'TICKET après l'enregistrement. " +
                        "Si vous fermez le formulaire sans enregistrer, utilisez « Annuler le brouillon non enregistré ».")
                    : L("Ticketing aperto ma i campi non sono stati compilati automaticamente. ",
                        "Ticketing opened but the fields were not filled automatically. ",
                        "Ticketing ouvert mais les champs n'ont pas été remplis automatiquement. ") +
                      L("Usa Copia titolo e Copia descrizione + TestLoomREF e incolla in Ticketing prima di salvare.",
                        "Use Copy title and Copy description + TestLoomREF and paste into Ticketing before saving.",
                        "Utilisez Copier le titre et Copier description + TestLoomREF et collez dans Ticketing avant d'enregistrer.");
                discard.IsEnabled = true;
                browser.IsEnabled = true;
                browser.Content = ReopenLabel;
                status.Foreground = fill.Outcome == AutofillOutcome.Filled ? Brushes.DarkGreen : Brushes.DarkOrange;
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
                try { var left = receiptStore.Read(isCr, sourceId, sourceUid); browser.IsEnabled = left is null || CanReopen(left); }
                catch (Exception) { browser.IsEnabled = false; }
                DiagnosticTraceService.Registra("TICKET.BrowserDraft",
                    "prepare-failed kind=" + ex.GetType().Name + " retry-allowed=" + browser.IsEnabled);
            }
        }
        browser.Click += async (_, _) => await OpenFormAsync(true);
        browserPlain.Click += async (_, _) => await OpenFormAsync(false);

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

        discard.Click += async (_, _) =>
        {
            discard.IsEnabled = false;
            try
            {
                var receipt = receiptStore.Read(isCr, sourceId, sourceUid);
                if (receipt is null)
                {
                    status.Text = L("Nessuna bozza in attesa per questa NC/CR.", "No pending draft for this NC/CR.",
                        "Aucun brouillon en attente pour cette NC/CR.");
                    browser.IsEnabled = !receiptIsCorrupt;
                    return;
                }
                var result = await PolarionBrowserHandoffService.TryDiscardUnsavedAsync(state, receipt);
                status.Text = result.Message;
                status.Foreground = result.Outcome == PolarionBrowserHandoffService.DiscardOutcome.MustVerify
                    ? Brushes.DarkOrange : Brushes.DarkGreen;
                if (result.Outcome == PolarionBrowserHandoffService.DiscardOutcome.Discarded)
                    browser.IsEnabled = !receiptIsCorrupt && settings.ErroreCaricamento is null &&
                        settings.LetturaAbilitata && settings.HaToken;
                else discard.IsEnabled = result.Outcome == PolarionBrowserHandoffService.DiscardOutcome.MustVerify;
            }
            catch (Exception ex)
            {
                status.Text = L("Impossibile annullare ora (", "Cannot discard now (", "Impossible d'annuler maintenant (") +
                    ex.GetType().Name + L("). Riprova o verifica con l'ID del ticket.", "). Retry or verify with the ticket ID.",
                        "). Réessayez ou vérifiez avec l'ID du ticket.");
                status.Foreground = Brushes.Firebrick;
                discard.IsEnabled = true;
            }
        };

        await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>A pending draft whose ticket was never verified can be reopened any number of times.</summary>
    private static bool CanReopen(PolarionCreationReceiptStore.Receipt receipt) =>
        receipt.CreatedTicketId is null && receipt.BrowserCorrelationMarker is not null;

    private static void AddField(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(control);
    }

}
