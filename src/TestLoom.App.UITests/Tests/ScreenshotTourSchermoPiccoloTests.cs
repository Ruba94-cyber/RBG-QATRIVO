using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;
using FlaUI.Core.WindowsAPI;
using Xunit.Abstractions;
using Keyboard = FlaUI.Core.Input.Keyboard;
using Mouse = FlaUI.Core.Input.Mouse;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Giro di screenshot di TestLoom su schermo piccolo per una revisione UI/UX. Non giudica l'estetica: produce PNG
/// (finestra intera; per i dialog la finestra del dialog) e una diagnostica da UI Automation (scorrimento della
/// pagina, pulsanti tagliati dai bordi, stato dei pulsanti azione principali) in .ai/artifacts/ui-tour/.
/// Dati: demo completa sintetica (nessun dato reale), utente Master. Categoria "Tour": esclusa dalle regressioni
/// normali. Un passo che non trova un elemento salva MANCA_&lt;passo&gt;.png, registra il motivo e prosegue; il test
/// fallisce solo se meno del 70% dei passi riesce.
/// Il test non salva, non esporta e non conferma nulla: le modifiche di prova vengono scartate con "Sì" sulla
/// conferma di uscita, su un workspace temporaneo che Dispose() elimina.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Tour")]
public sealed class ScreenshotTourSchermoPiccoloTests
{
    private const double SogliaPassiRiusciti = 0.70;
    private const string RicercaTextBoxId = "Main_RicercaGlobale_TextBox";
    private const string SottoveicoloDemo = "Sottoveicolo-Test-Daily MY24";
    private readonly ITestOutputHelper _out;

    public ScreenshotTourSchermoPiccoloTests(ITestOutputHelper output) => _out = output;

    // Dichiarazioni per rendere il processo di test DPI-aware (PER_MONITOR_AWARE_V2 = -4, fallback system-aware).
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessDpiAwarenessContext(IntPtr valore);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr valore);
    private static readonly IntPtr DpiAwarenessPerMonitorV2 = new IntPtr(-4);

    // Ridimensionamento e misura nativi (il processo e' DPI-aware: tutte le misure sono in pixel fisici).
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int comando);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr dopo, int x, int y, int cx, int cy, uint flag);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out RettangoNativo rettangolo);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint azione, uint parametro, out RettangoNativo valore, uint fWinIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct RettangoNativo
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SpiGetWorkArea = 0x0030;

    /// <summary>Ripristina la finestra principale e la porta a una dimensione LOGICA, convertita in pixel fisici con la scala
    /// del monitor (DPI di finestra / 96). Ritorna la dimensione effettiva misurata, in logici.</summary>
    private static (double Width, double Height) RidimensionaFinestra(Window finestra, int larghezzaLogica, int altezzaLogica)
    {
        var hwnd = Hwnd(finestra);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("handle nativo della finestra principale non disponibile");
        var scala = GetDpiForWindow(hwnd) / 96.0;
        ShowWindow(hwnd, SwRestore);
        if (!Wait.TryForCondition(() => !IsZoomed(hwnd), TimeSpan.FromSeconds(10)))
            throw new TimeoutException("la finestra non esce dallo stato massimizzato");

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, (int)Math.Round(larghezzaLogica * scala), (int)Math.Round(altezzaLogica * scala), SwpNoZOrder | SwpNoActivate);
        var ultima = (Larghezza: -1, Altezza: -1);
        var stabile = 0;
        Wait.TryForCondition(() =>
        {
            GetWindowRect(hwnd, out var r);
            var misura = (Larghezza: r.Right - r.Left, Altezza: r.Bottom - r.Top);
            stabile = misura == ultima ? stabile + 1 : 0;
            ultima = misura;
            return stabile >= 4;
        }, TimeSpan.FromSeconds(15));

        GetWindowRect(hwnd, out var rettangolo);
        return ((rettangolo.Right - rettangolo.Left) / scala, (rettangolo.Bottom - rettangolo.Top) / scala);
    }

    /// <summary>Geometria della finestra principale: scala, dimensione fisica e logica, area di lavoro del monitor.</summary>
    private static (string Geometria, string Esterna, string Lavoro) MisuraNativa(Window finestra)
    {
        var hwnd = Hwnd(finestra);
        var scala = hwnd == IntPtr.Zero ? 1.0 : GetDpiForWindow(hwnd) / 96.0;
        GetWindowRect(hwnd, out var r);
        var larghezza = r.Right - r.Left;
        var altezza = r.Bottom - r.Top;
        SystemParametersInfo(SpiGetWorkArea, 0, out var area, 0);
        var bordi = finestra.BoundingRectangle;
        var geometria = $"scala {scala:0.##} (DPI {scala * 96:0}); finestra fisica {larghezza}x{altezza} px = " +
                        $"{larghezza / scala:0.#}x{altezza / scala:0.#} logici; rettangolo UIA {bordi.Width}x{bordi.Height} px";
        var esterna = $"{larghezza / scala:0.#}x{altezza / scala:0.#} logici (cornice inclusa)";
        var lavoro = $"{(area.Right - area.Left) / scala:0}x{(area.Bottom - area.Top) / scala:0} logici (area di lavoro del monitor, senza barra delle applicazioni)";
        return (geometria, esterna, lavoro);
    }

    /// <summary>Rende il processo di test DPI-aware e restituisce il livello effettivo (0 = non aware, 1 = system, 2 = per-monitor).
    /// Va chiamata prima di qualunque chiamata UIA o cattura: senza, le coordinate UIA sono logiche mentre
    /// la cattura copia pixel fisici e ogni schermata mostra solo il quadrante in alto a sinistra.</summary>
    private static int RendiProcessoDpiAware()
    {
        if (GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) == 0 && !SetProcessDpiAwarenessContext(DpiAwarenessPerMonitorV2))
            SetProcessDPIAware();
        return GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
    }

    // ------------------------------------------------------------------ passi del giro (ordine di esecuzione)

    // ------------------------------------------------------------------ esecuzione di un passo e diagnostica

    private sealed record PassoTour(string Nome, string[] Pulsanti, Func<TourCtx, Window?> Azione);

    private sealed record RigaTour(string Nome, string File, string Esito, string? Motivo, string Finestra, string Dettaglio);

    private sealed class TourCtx
    {
        public TourCtx(TestLoomApplication app, string cartella, string dimensione, string? idTest, ITestOutputHelper output)
        {
            App = app;
            Cartella = cartella;
            Dimensione = dimensione;
            IdTest = idTest;
            Output = output;
        }

        public TestLoomApplication App { get; }
        public string Cartella { get; }
        public string Dimensione { get; }
        public string? IdTest { get; }
        public ITestOutputHelper Output { get; }
        public Window Main { get; set; } = null!;
        public TestEditorPage? Editor { get; set; }
        public string Stato { get; set; } = "non misurato";
        public string Geometria { get; set; } = "non misurata";
        public string TracciaPath => Path.Combine(Cartella, "_traccia.txt");
        public string DimensioneEsterna { get; set; } = "non misurata";
        public string AreaLavoro { get; set; } = "non misurata";
        public int LivelloDpi { get; set; }
        public List<RigaTour> Righe { get; } = new();
    }

    private static void EseguiPasso(TourCtx t, PassoTour passo)
    {
        var esito = "OK";
        string? motivo = null;
        Window? dialogo = null;
        for (var tentativo = 1; tentativo <= 2; tentativo++)
        {
            try
            {
                RecuperaShell(t);
                dialogo = passo.Azione(t);
                break;
            }
            catch (COMException ex) when (tentativo == 1)
            {
                // Errore COM transitorio di UI Automation: si ritenta una sola volta, solo questo passo.
                Traccia(t, $"PASSO {passo.Nome}: COMException al primo tentativo, nuovo tentativo del solo passo ({Riga(ex)})");
                Thread.Sleep(800);
            }
            catch (Exception ex)
            {
                esito = "MANCA";
                motivo = Riga(ex);
                break;
            }
        }

        var finestra = dialogo ?? t.Main;
        var nomeFile = esito == "OK" ? $"{passo.Nome}.png" : $"MANCA_{passo.Nome}.png";
        string? motivoCattura = null;
        var dimensioniPng = "non salvato";
        try
        {
            dimensioniPng = Fotografa(finestra, Path.Combine(t.Cartella, nomeFile));
        }
        catch (Exception ex)
        {
            // Anche una cattura fallita non interrompe il giro: si prova la finestra principale come ripiego.
            esito = "MANCA";
            motivoCattura = $"cattura non riuscita ({Riga(ex)})";
            nomeFile = $"MANCA_{passo.Nome}.png";
            try { dimensioniPng = Fotografa(t.Main, Path.Combine(t.Cartella, nomeFile)); } catch { }
        }

        var motivoFinale = string.Join(" | ", new[] { motivo, motivoCattura }.Where(s => !string.IsNullOrEmpty(s)));
        var dettaglio = $"  - Finestra UIA {DimensioneUia(finestra)}; PNG salvato {dimensioniPng}\n" + SafeDiagnostica(finestra, passo.Pulsanti);
        t.Righe.Add(new RigaTour(passo.Nome, nomeFile, esito, motivoFinale.Length == 0 ? null : motivoFinale,
            dialogo is null ? "finestra principale" : "finestra di dialogo", dettaglio));
        t.Output.WriteLine($"[{t.Dimensione}] {passo.Nome}: {esito}{(motivoFinale.Length == 0 ? "" : " - " + motivoFinale)}");
        Traccia(t, $"PASSO {passo.Nome}: {esito}{(motivoFinale.Length == 0 ? "" : " - " + motivoFinale)}; principale={Identita(t.Main)}; finestre secondarie=[{Descrivi(FinestreSecondarie(t))}]; dialoghi=[{CercaDialogAperto(t).Id ?? "nessuno"}]; tutte=[{DettagliTutte(t)}]");
    }

    private static string SafeDiagnostica(Window finestra, IReadOnlyList<string> pulsanti)
    {
        try { return Diagnostica(finestra, pulsanti); }
        catch (Exception ex) { return $"  - diagnostica non disponibile: {Riga(ex)}\n"; }
    }

    private static string Diagnostica(Window finestra, IReadOnlyList<string> pulsanti)
    {
        var rect = finestra.BoundingRectangle;
        var tutti = finestra.FindAllDescendants();
        var sb = new StringBuilder();

        // Scorrimento (ScrollPattern): contenitori con barra orizzontale o verticale e relativa posizione.
        var scorrevoli = new List<string>();
        foreach (var e in tutti)
        {
            try
            {
                var sp = e.Patterns.Scroll.PatternOrDefault;
                if (sp == null) continue;
                var orizzontale = sp.HorizontallyScrollable.ValueOrDefault;
                var verticale = sp.VerticallyScrollable.ValueOrDefault;
                if (!orizzontale && !verticale) continue;
                scorrevoli.Add($"{Etichetta(e)} orizzontale={(orizzontale ? "si" : "no")} verticale={(verticale ? "si" : "no")} " +
                               $"posizione H={sp.HorizontalScrollPercent.ValueOrDefault:0}% V={sp.VerticalScrollPercent.ValueOrDefault:0}%");
            }
            catch { /* elemento non piu' valido: ignorato */ }
        }
        sb.AppendLine("  - Scorrimento: " + (scorrevoli.Count == 0 ? "nessun contenitore scorrevole rilevato" : string.Join("; ", scorrevoli.Take(6))));

        // Pulsanti tagliati: oltre il bordo destro/sinistro della finestra (overflow orizzontale) o sotto il bordo inferiore.
        var tagliatiOrizzontali = new List<string>();
        var sottoBordoInferiore = new List<string>();
        foreach (var b in tutti)
        {
            try
            {
                if (SafeControlType(b) != ControlType.Button) continue;
                var r = b.BoundingRectangle;
                if (r.Width <= 1 || r.Height <= 1) continue;
                if (r.Right > rect.Right + 1 || r.Left < rect.Left - 1) tagliatiOrizzontali.Add(Etichetta(b));
                else if (r.Bottom > rect.Bottom + 1) sottoBordoInferiore.Add(Etichetta(b));
            }
            catch { /* ignorato */ }
        }
        sb.AppendLine($"  - Pulsanti tagliati a destra/sinistra: {tagliatiOrizzontali.Count}" +
                      (tagliatiOrizzontali.Count == 0 ? "" : " -> " + string.Join("; ", tagliatiOrizzontali.Take(8))));
        sb.AppendLine($"  - Pulsanti sotto il bordo inferiore: {sottoBordoInferiore.Count}" +
                      (sottoBordoInferiore.Count == 0 ? " (normale se la pagina scorre)" : " (verificare se la pagina non scorre)"));

        // Pulsanti azione principali: presenza, stato rispetto alla finestra e testo visibile.
        foreach (var id in pulsanti)
        {
            var el = tutti.FirstOrDefault(e => SafeId(e) == id);
            string stato;
            if (el == null)
            {
                stato = "non trovato";
            }
            else
            {
                var r = el.BoundingRectangle;
                if (r.Width <= 1 || r.Height <= 1) stato = "dimensione nulla (non visibile)";
                else if (r.Right > rect.Right + 1) stato = "TAGLIATO a destra";
                else if (r.Left < rect.Left - 1) stato = "TAGLIATO a sinistra";
                else if (r.Bottom > rect.Bottom + 1) stato = "sotto il bordo inferiore (serve scorrere)";
                else if (r.Top < rect.Top - 1) stato = "sopra il bordo superiore";
                else stato = "visibile";
                stato += $", testo '{SafeName(el)}'";
            }
            sb.AppendLine($"  - {id}: {stato}");
        }
        return sb.ToString();
    }

    /// <summary>Salva la finestra intera come PNG e restituisce la dimensione in pixel del file (per la verifica di copertura).</summary>
    private static string Fotografa(Window finestra, string percorso)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(percorso)!);
        using var immagine = finestra.Capture();
        immagine.Save(percorso, ImageFormat.Png);
        return $"{immagine.Width}x{immagine.Height} px";
    }

    private static string DimensioneUia(Window finestra)
    {
        try
        {
            var r = finestra.BoundingRectangle;
            return $"{r.Width}x{r.Height} px";
        }
        catch (Exception ex)
        {
            return $"non leggibile ({Riga(ex)})";
        }
    }

    /// <summary>Riporta il giro a uno stato noto prima di ogni passo: chiude dialog/flyout residui (Esc, dal piu' recente),
    /// chiude il centro notifiche e la ricerca globale, rimette in primo piano la finestra principale.</summary>
    private static void RecuperaShell(TourCtx t)
    {
        Traccia(t, $"RecuperaShell inizio: secondarie=[{Descrivi(FinestreSecondarie(t))}]");
        for (var giro = 0; giro < 6; giro++)
        {
            var (id, elemento) = CercaDialogAperto(t);
            if (id is null || elemento is null) break;
            ChiudiDialog(t, id, elemento);
        }

        // Verifica dopo la chiusura: nessun dialog visibile nell'albero della finestra principale e nessuna finestra di primo
        // livello oltre alla principale. Se ne resta una, il passo non parte: la schermata risulterebbe coperta.
        var (residuo, _) = CercaDialogAperto(t);
        var residue = FinestreSecondarie(t);
        Traccia(t, $"RecuperaShell fine: residuo={residuo ?? "nessuno"}; secondarie=[{Descrivi(residue)}]");
        if (residuo is not null || residue.Count > 0)
            throw new InvalidOperationException($"finestra di dialogo ancora aperta: {residuo ?? Descrivi(residue)}");

        ShellHomeSettingsCoverageSupport.PortaInPrimoPiano(t.Main);
        new MainShellPage(t.Main, t.App).DismissNotificationsIfShown();
        if (ShellHomeSettingsCoverageSupport.VisibileInUia(ElementFinders.TryFindById(t.Main, RicercaTextBoxId)))
        {
            ShellHomeSettingsCoverageSupport.PortaInPrimoPiano(t.Main);
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Thread.Sleep(300);
        }
    }

    /// <summary>Finestre di primo livello del processo TestLoom diverse dalla principale (dialog, flyout), senza gli overlay di sistema.</summary>
    private static List<Window> FinestreSecondarie(TourCtx t)
    {
        var principale = Identita(t.Main);
        return t.App.TopLevelWindows()
            .Where(w => Identita(w) != principale)
            .Where(w => SafeId(w) is not ("Input Indicator overlay window" or "AvvioWindow"))
            .ToList();
    }

    /// <summary>Handle nativo della finestra. Attenzione: la proprieta' UIA Properties.NativeWindowHandle e' 0 su queste
    /// finestre Avalonia, mentre FrameworkAutomationElement.NativeWindowHandle e' l'handle reale.</summary>
    private static IntPtr Hwnd(Window w)
    {
        try { return w.FrameworkAutomationElement.NativeWindowHandle; }
        catch { return IntPtr.Zero; }
    }

    /// <summary>Identita' di una finestra: handle nativo se disponibile, altrimenti titolo e AutomationId.</summary>
    private static string Identita(Window w)
    {
        var h = Hwnd(w);
        return h != IntPtr.Zero ? "hwnd:" + h : "titolo:" + SafeTitle(w) + "|" + SafeId(w);
    }

    private static string SafeTitle(Window w)
    {
        try { return w.Title ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>Pulsanti di chiusura dei dialog senza effetti sui dati (annulla o chiudi senza salvare). Cercati nell'albero
    /// della finestra principale: su questo TestLoom i dialog non sono finestre di primo livello per UI Automation (la diagnostica
    /// mostra che TopLevelWindows restituisce solo la principale mentre i dialog sono aperti). Mai Salva, Genera, Esporta,
    /// Elimina, e mai il "Chiudi" del dettaglio NC, che cambia lo stato della non conformita'.</summary>
    private static readonly string[] IdPulsantiChiusuraDialog =
    {
        "AnomaliaDettaglio_Annulla_Button",
        "PolarionSelezione_Annulla_Button",
        "SelectDialog_Cancel_Button",
        "MissingFields_Close_Button",
        "CampiEditor_Chiudi_Button",
        "TplEditor_Chiudi_Button",
    };

    /// <summary>Conferma di abbandono di modifiche non salvate: si accetta, per scartare i dati di prova.</summary>
    private const string IdConfermaAbbandono = "Confirm_Yes_Button";

    /// <summary>Lista del flyout di filtro multi-selezione: si chiude con Esc dopo aver preso il focus.</summary>
    private const string IdFlyoutFiltro = "FiltroMultiSelect_Lista";

    /// <summary>Primo dialog visibile ancora aperto nell'albero della finestra principale (o null se non ce n'e').</summary>
    private static (string? Id, AutomationElement? Elemento) CercaDialogAperto(TourCtx t)
    {
        var tutti = t.Main.FindAllDescendants();
        foreach (var id in IdPulsantiChiusuraDialog.Append(IdConfermaAbbandono).Append(IdFlyoutFiltro))
        {
            var elemento = tutti.FirstOrDefault(e => SafeId(e) == id && ShellHomeSettingsCoverageSupport.VisibileInUia(e));
            if (elemento != null) return (id, elemento);
        }
        return (null, null);
    }

    /// <summary>Chiude il dialog con il suo pulsante di annullamento (per il flyout, con Esc) e verifica che sparisca.</summary>
    private static void ChiudiDialog(TourCtx t, string id, AutomationElement elemento)
    {
        var azione = id == IdConfermaAbbandono ? "conferma di abbandono accettata (dati di prova scartati)"
            : id == IdFlyoutFiltro ? "Esc sul flyout"
            : "pulsante di annullamento";
        if (id == IdFlyoutFiltro)
        {
            elemento.Focus();
            Keyboard.Type(VirtualKeyShort.ESCAPE);
        }
        else
        {
            elemento.AsButton().SafeInvoke();
        }

        var chiuso = Wait.TryForCondition(() => CercaDialogAperto(t).Id != id, TimeSpan.FromSeconds(4));
        Traccia(t, $"dialog {id}: {azione} -> {(chiuso ? "chiuso" : "ANCORA APERTO")}");
        t.Output.WriteLine($"[{t.Dimensione}] dialog {id}: {azione} -> {(chiuso ? "chiuso" : "ancora aperto")}");
    }

    /// <summary>Traccia diagnostica su file (aggiornata durante il giro): non deve mai interrompere il giro.</summary>
    private static void Traccia(TourCtx t, string riga)
    {
        try { File.AppendAllText(t.TracciaPath, $"{DateTime.Now:HH:mm:ss.fff} {riga}{Environment.NewLine}", Encoding.UTF8); }
        catch { }
    }

    private static string Descrivi(IEnumerable<Window> finestre) =>
        string.Join("; ", finestre.Select(w => $"{EtichettaFinestra(w)} hwnd={SafeHandle(w)}"));

    private static string SafeHandle(Window w) => Identita(w);

    /// <summary>Diagnostica: tutte le finestre di primo livello del processo, senza filtri (titolo, Id, handle, rettangolo).</summary>
    private static string DettagliTutte(TourCtx t)
    {
        try
        {
            return string.Join("; ", t.App.TopLevelWindows().Select(w =>
                $"'{SafeTitle(w)}'/{SafeId(w)} fae={Hwnd(w)} prop={PropHandle(w)} rett={RettangoStr(w)}"));
        }
        catch (Exception ex) { return "non leggibili: " + Riga(ex); }
    }

    private static string PropHandle(Window w)
    {
        try { return w.Properties.NativeWindowHandle.ValueOrDefault.ToString(); }
        catch { return "n/d"; }
    }

    private static string RettangoStr(Window w)
    {
        try { var r = w.BoundingRectangle; return $"{r.Left},{r.Top} {r.Width}x{r.Height}"; }
        catch { return "n/d"; }
    }

    private static string EtichettaFinestra(Window w)
    {
        string titolo;
        try { titolo = w.Title ?? string.Empty; }
        catch { titolo = string.Empty; }
        return $"'{titolo}' [{SafeId(w)}]";
    }

    private static bool StessaFinestra(Window a, Window b) => Identita(a) == Identita(b);

    // ------------------------------------------------------------------ navigazione e azioni di supporto

    /// <summary>Naviga alla sezione e, se compare la conferma di uscita per modifiche non salvate, la accetta:
    /// il dato e' di prova, non salvato, su un workspace temporaneo.</summary>
    private static MainShellPage Naviga(TourCtx t, string navId)
    {
        var shell = new MainShellPage(t.Main, t.App);
        shell.NavigateTo(navId);
        ConfermaUscitaSeCompare(t);
        return shell;
    }

    /// <summary>Se compare la conferma di uscita (modifiche non salvate), la accetta: il dato e' di prova, non salvato.</summary>
    private static void ConfermaUscitaSeCompare(TourCtx t)
    {
        if (!Wait.TryForCondition(() => !t.App.IsWindowClosedByContent("Confirm_Yes_Button"), TimeSpan.FromSeconds(2))) return;
        new ConfirmDialogPage(t.App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
        Wait.ForCondition(() => t.App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura conferma di uscita");
        t.Output.WriteLine($"[{t.Dimensione}] uscita con modifiche di prova non salvate: scartate (workspace temporaneo)");
    }

    private static ImpostazioniPage VaiImpostazioni(TourCtx t, string sottosezione)
    {
        var impostazioni = Naviga(t, "impostazioni").GoToImpostazioni();
        // Una modifica dei template ancora aperta (passo 23-24) blocca il cambio sottosezione: si esce con Annulla.
        if (ElementFinders.ExistsById(t.Main, "Tpl_Annulla_Button"))
            ElementFinders.WaitButtonById(t.Main, "Tpl_Annulla_Button").SafeInvoke();
        impostazioni.VaiA(sottosezione);
        ConfermaUscitaSeCompare(t);
        return impostazioni;
    }

    /// <summary>Apre "Tipi di test" > Modifica > "Gestisci contenuto" e restituisce la finestra di gestione
    /// contenuto (quella che elenca le sezioni con il pulsante "Gestisci campi").</summary>
    private static Window ApriGestisciContenuto(TourCtx t)
    {
        VaiImpostazioni(t, "tipiditest");
        if (ElementFinders.ExistsById(t.Main, "Tpl_Annulla_Button"))
            ElementFinders.WaitButtonById(t.Main, "Tpl_Annulla_Button").SafeInvoke();
        ElementFinders.WaitButtonById(t.Main, "Tpl_Modifica_Button").SafeInvoke();
        ElementFinders.WaitButtonById(t.Main, "TplGestisci_Button").SafeInvoke();
        return t.App.WaitForWindow(
            w => w.FindAllDescendants().Any(e => SafeId(e).StartsWith("TplGestisciCampi_", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15),
            "finestra 'Gestisci contenuto' del tipo di test");
    }

    /// <summary>Apre in modifica un Test della demo con piu' operazioni (condizioni nell'ultima esecuzione).</summary>
    private static TestEditorPage ApriTestConOperazioni(TourCtx t)
    {
        if (t.IdTest is null) throw new InvalidOperationException("nessun Test nella demo completa");
        var lista = Naviga(t, "test").GoToTestList();
        lista.Search(t.IdTest);
        var editor = lista.OpenRowById(t.IdTest);
        editor.EnterEditMode();
        Wait.ForCondition(() => editor.CanSave, "editor del Test in modifica (pulsante Salva visibile)");
        return editor;
    }

    private static void AssicuraEditor(TourCtx t)
    {
        if (ElementFinders.ExistsById(t.Main, "Editor_Save_Button")) return;
        t.Editor = ApriTestConOperazioni(t);
    }

    private static void ImpostaDimensione(TourCtx t, int larghezzaLogica, int altezzaLogica)
    {
        var finestra = t.Main;
        if (larghezzaLogica == 0)
        {
            if (finestra.Patterns.Window.TryGetPattern(out var stato) && stato.WindowVisualState.Value != WindowVisualState.Maximized)
                stato.SetWindowVisualState(WindowVisualState.Maximized);
            Wait.TryForCondition(() => finestra.Patterns.Window.TryGetPattern(out var s) && s.WindowVisualState.Value == WindowVisualState.Maximized,
                TimeSpan.FromSeconds(10));
            t.Stato = "massimizzata";
        }
        else
        {
            var misura = RidimensionaFinestra(finestra, larghezzaLogica, altezzaLogica);
            t.Output.WriteLine($"[{t.Dimensione}] ridimensionamento richiesto {larghezzaLogica}x{altezzaLogica} logici, misura reale {misura.Width:0.#}x{misura.Height:0.#} logici");
            t.Stato = "ripristinata e ridimensionata";
        }
        Thread.Sleep(1000);

        var misuraFinale = MisuraNativa(finestra);
        t.Geometria = misuraFinale.Geometria;
        t.DimensioneEsterna = misuraFinale.Esterna;
        t.AreaLavoro = misuraFinale.Lavoro;
    }

    /// <summary>Contenitore verticalmente scorrevole piu' grande della finestra (di norma il ScrollViewer della pagina).</summary>
    private static AutomationElement? TrovaScorrevolePrincipale(Window finestra)
    {
        AutomationElement? migliore = null;
        var areaMigliore = -1.0;
        foreach (var e in finestra.FindAllDescendants())
        {
            try
            {
                var sp = e.Patterns.Scroll.PatternOrDefault;
                if (sp == null || !sp.VerticallyScrollable.ValueOrDefault) continue;
                var r = e.BoundingRectangle;
                var area = (double)r.Width * r.Height;
                if (area > areaMigliore)
                {
                    areaMigliore = area;
                    migliore = e;
                }
            }
            catch { /* ignorato */ }
        }
        return migliore;
    }

    /// <summary>Scorre la pagina principale alla percentuale indicata; senza contenitore scorrevole usa la rotella.</summary>
    private static void ScorriVerticale(Window finestra, double percento)
    {
        var scorrevole = TrovaScorrevolePrincipale(finestra);
        if (scorrevole != null)
        {
            ImpostaScorrimento(scorrevole, percento);
            Thread.Sleep(400);
            return;
        }

        var c = finestra.BoundingRectangle;
        Mouse.MoveTo(new Point(c.Left + c.Width / 2, c.Top + c.Height / 2));
        for (var i = 0; i < 15; i++)
        {
            Mouse.Scroll(percento >= 50 ? -5 : 5);
            Thread.Sleep(60);
        }
        Thread.Sleep(300);
    }

    private static void ImpostaScorrimento(AutomationElement scorrevole, double percento) =>
        scorrevole.Patterns.Scroll.PatternOrDefault?.SetScrollPercent(ScrollPatternConstants.NoScroll, percento);

    /// <summary>Scorre la pagina del Report (ScrollViewer dedicato), con ripiego sul contenitore generico.</summary>
    private static void ScorriReport(TourCtx t, double percento)
    {
        var sp = ElementFinders.TryFindById(t.Main, "Report_Page_ScrollViewer")?.Patterns.Scroll.PatternOrDefault;
        if (sp != null)
        {
            sp.SetScrollPercent(ScrollPatternConstants.NoScroll, percento);
            Thread.Sleep(400);
        }
        else
        {
            ScorriVerticale(t.Main, percento);
        }
    }

    /// <summary>Scorre finche' l'elemento e' interamente dentro la finestra (scorrimento per percentuale, passi di 5%).</summary>
    private static void PortaInVista(Window finestra, AutomationElement e)
    {
        var scorrevole = TrovaScorrevolePrincipale(finestra);
        for (var p = 0.0; p <= 100; p += 5)
        {
            if (scorrevole != null) ImpostaScorrimento(scorrevole, p);
            Thread.Sleep(120);
            var r = e.BoundingRectangle;
            var w = finestra.BoundingRectangle;
            if (r.Height > 0 && r.Top >= w.Top && r.Bottom <= w.Bottom) return;
        }
        throw new InvalidOperationException("elemento non portabile in vista scorrendo la pagina");
    }

    private static AutomationElement? TrovaCombo(Window finestra, string prefisso, string suffisso) =>
        finestra.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox))
            .FirstOrDefault(e => SafeId(e).StartsWith(prefisso, StringComparison.Ordinal) && SafeId(e).EndsWith(suffisso, StringComparison.Ordinal));

    /// <summary>Test demo con il maggior numero di condizioni nell'ultima esecuzione (sezione operazioni dell'editor).</summary>
    private static (string? Id, int Condizioni) TestConPiuOperazioni(TestWorkspace ws)
    {
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var scelto = state.TestCases.GetAll()
            .Select(tc => (Id: tc.Id, Condizioni: tc.Esecuzioni.Count == 0 ? 0 : tc.Esecuzioni[^1].Condizioni.Count))
            .OrderByDescending(x => x.Condizioni)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return scelto.Id is null ? throw new InvalidOperationException("la demo completa non contiene Test") : (scelto.Id, scelto.Condizioni);
    }

    // ------------------------------------------------------------------ indice

    private static void ScriviIndice(TourCtx t)
    {
        var riusciti = t.Righe.Count(r => r.Esito == "OK");
        var sb = new StringBuilder();
        sb.AppendLine($"## Dimensione `{t.Dimensione}` ({t.Stato})");
        sb.AppendLine();
        sb.AppendLine($"- Processo di test: DPI awareness livello {t.LivelloDpi} (2 = per-monitor, 1 = system)");
        sb.AppendLine($"- Finestra principale (misura reale): {t.Geometria}");
        sb.AppendLine($"- Dimensione esterna: {t.DimensioneEsterna}");
        sb.AppendLine($"- Area di lavoro: {t.AreaLavoro}");
        sb.AppendLine($"- Passi riusciti: {riusciti} su {t.Righe.Count}");
        sb.AppendLine();
        foreach (var r in t.Righe)
        {
            sb.AppendLine($"### {r.Nome} - {r.Esito}");
            sb.AppendLine($"- File: `{t.Dimensione}/{r.File}`");
            sb.AppendLine($"- Finestra fotografata: {r.Finestra}");
            if (!string.IsNullOrEmpty(r.Motivo)) sb.AppendLine($"- Motivo: {r.Motivo}");
            sb.Append(r.Dettaglio);
            sb.AppendLine();
        }
        File.WriteAllText(Path.Combine(t.Cartella, "_diagnostica.txt"), sb.ToString(), Encoding.UTF8);

        var radice = Path.GetDirectoryName(t.Cartella)!;
        var indice = new StringBuilder();
        indice.AppendLine("# INDEX - giro screenshot TestLoom su schermo piccolo");
        indice.AppendLine();
        indice.AppendLine("Generato da `ScreenshotTourSchermoPiccoloTests` (categoria Tour). Dati: demo completa sintetica, utente Master.");
        indice.AppendLine("PNG: finestra intera (per i dialog, la finestra del dialog). `MANCA_*.png` = passo non riuscito, con motivo.");
        indice.AppendLine("Diagnostica da UI Automation: scorrimento della pagina, pulsanti tagliati, stato dei pulsanti azione.");
        indice.AppendLine();
        foreach (var f in Directory.GetFiles(radice, "_diagnostica.txt", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            indice.Append(File.ReadAllText(f, Encoding.UTF8));
        File.WriteAllText(Path.Combine(radice, "INDEX.md"), indice.ToString(), Encoding.UTF8);
    }

    // ------------------------------------------------------------------ utilita'

    private static string RadiceWorktree()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TestLoom.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("radice del worktree (TestLoom.slnx) non trovata da " + AppContext.BaseDirectory);
    }

    private static string Riga(Exception ex)
    {
        var primaRiga = (ex.Message ?? string.Empty).Split('\n')[0].Trim();
        return $"{ex.GetType().Name}: {primaRiga}";
    }

    private static string SafeId(AutomationElement e)
    {
        try { return e.Properties.AutomationId.ValueOrDefault ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafeName(AutomationElement e)
    {
        try { return e.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static ControlType? SafeControlType(AutomationElement e)
    {
        try { return e.ControlType; }
        catch { return null; }
    }

    private static string Etichetta(AutomationElement e)
    {
        var nome = SafeName(e);
        var id = SafeId(e);
        return $"'{(nome.Length == 0 ? "(senza nome)" : nome)}' [{(id.Length == 0 ? "senza id" : id)}]";
    }
}
