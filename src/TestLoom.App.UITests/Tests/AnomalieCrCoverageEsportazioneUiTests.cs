using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Esportazione Excel delle Non Conformita' dalla pagina Anomalie/CR (pulsante "Esporta Excel"). Il comando
/// apre il dialogo di salvataggio NATIVO di Windows (StorageProvider.SaveFilePickerAsync): il test lo pilota via UI
/// Automation, sceglie un percorso sotto la cartella temporanea del workspace di test e verifica il file .xlsx
/// prodotto (contenitore ZIP valido e Id delle NC presenti nel foglio). Se il dialogo non e' quello atteso, il
/// messaggio d'errore riporta l'albero dei controlli trovato.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class AnomalieCrCoverageEsportazioneUiTests : UiTestBase
{
    private const string NcUno = "NC-QA-0001";
    private const string NcDue = "NC-QA-0002";

    public AnomalieCrCoverageEsportazioneUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(AnomalieCrCoverageEsportazioneUiTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        state.NonConformita.Save(new NonConformita
        {
            Id = NcUno, DataApertura = DateTime.UtcNow.AddMinutes(-2), Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.InVerifica, Priorita = PrioritaOperativa.Critical,
            Descrizione = "NC da esportare in Excel", Owner = "Lucia",
        });
        state.NonConformita.Save(new NonConformita
        {
            Id = NcDue, DataApertura = DateTime.UtcNow.AddMinutes(-1), Severita = SeveritaAnomalia.High,
            StatoWorkflow = StatoAnomalia.Confermata, Priorita = PrioritaOperativa.Major,
            Descrizione = "Seconda NC da esportare", Owner = "Mario",
        });
        return workspace;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>Copia il testo negli appunti di Windows (formato Unicode) per incollarlo nel campo del dialogo.</summary>
    private static void ImpostaAppunti(string testo)
    {
        var byteTotali = checked((testo.Length + 1) * 2);
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)byteTotali);
        if (hMem == IntPtr.Zero) throw new InvalidOperationException("Allocazione degli appunti non riuscita.");
        var ptr = GlobalLock(hMem);
        Marshal.Copy(testo.ToCharArray(), 0, ptr, testo.Length);
        Marshal.WriteInt16(ptr, testo.Length * 2, 0);
        GlobalUnlock(hMem);
        if (!OpenClipboard(IntPtr.Zero)) throw new InvalidOperationException("Appunti non disponibili.");
        try
        {
            EmptyClipboard();
            if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
                throw new InvalidOperationException("Impostazione degli appunti non riuscita.");
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Descrive i controlli del dialogo nativo, per il messaggio d'errore quando non e' quello atteso.</summary>
    private static string Descrivi(AutomationElement finestra)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"finestra: titolo='{finestra.Name}' class='{finestra.ClassName}' automationId='{finestra.Properties.AutomationId.ValueOrDefault}'");
        foreach (var el in finestra.FindAllDescendants().Take(120))
        {
            sb.AppendLine($"  {el.ControlType} name='{el.Name}' id='{el.Properties.AutomationId.ValueOrDefault}' class='{el.ClassName}'");
        }
        return sb.ToString();
    }

    /// <summary>Il dialogo di salvataggio nativo e' annidato nella finestra principale TestLoom nell'albero UIA (non e' un
    /// figlio del desktop): si cerca tra i discendenti della finestra quello con il campo 'Nome file' (AutomationId 1001).</summary>
    private static Window AttendiDialogoNativo(Window finestraPrincipale)
    {
        return Wait.Until<Window>(
            () => finestraPrincipale.FindAllDescendants(cf => cf.ByControlType(ControlType.Window))
                .Select(e => e.AsWindow())
                .FirstOrDefault(w => w.FindFirstDescendant(cf => cf.ByAutomationId("1001")) != null),
            w => w != null,
            TimeSpan.FromSeconds(20),
            "dialogo di salvataggio nativo di Esporta Excel");
    }

    [Fact]
    public void EsportaExcel_SalvaUnFileXlsxNelPercorsoScelto_ConLeNcNelFoglio()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 2,
                "le due NC seminate compaiono nella lista");

            var cartella = Path.Combine(Workspace.RootDir, "esportazione");
            Directory.CreateDirectory(cartella);
            var destinazione = Path.Combine(cartella, "TestLoom_NonConformita_QA.xlsx");

            ElementFinders.WaitButtonById(window, "Anomalie_EsportaExcel_Button").SafeInvoke();
            // Dialogo unico di export: si conferma con le impostazioni di partenza, poi compare il Salva con nome.
            var opzioniExport = App.WaitForWindowByContent("EsportaOpzioni_Esporta");
            ElementFinders.WaitButtonById(opzioniExport, "EsportaOpzioni_Esporta").SafeInvoke();
            var dialogo = AttendiDialogoNativo(window);

            var nomeFile = dialogo.FindFirstDescendant(cf => cf.ByAutomationId("1001"))
                ?? dialogo.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
            if (nomeFile == null)
                throw new InvalidOperationException("Campo 'Nome file' del dialogo di salvataggio non trovato.\n" + Descrivi(dialogo));
            // Il percorso si incolla dagli appunti: digitarlo carattere per carattere nel combo 'Nome file' perde lettere
            // (es. 'esportazione' diventava 'esportazine') e il campo non espone un ValuePattern utilizzabile. Il pulsante
            // Salva si invoca tramite Invoke pattern, senza clic a coordinate.
            ImpostaAppunti(destinazione);
            dialogo.Focus();
            nomeFile.Focus();
            FlaUI.Core.Input.Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            FlaUI.Core.Input.Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
            var salva = dialogo.FindFirstDescendant(cf => cf.ByAutomationId("1").And(cf.ByControlType(ControlType.Button)))
                ?? throw new InvalidOperationException("Pulsante Salva del dialogo non trovato.\n" + Descrivi(dialogo));
            if (!salva.Patterns.Invoke.TryGetPattern(out var invoke))
                throw new InvalidOperationException("Il pulsante Salva non espone il pattern Invoke.\n" + Descrivi(dialogo));
            invoke.Invoke();

            Wait.ForCondition(() => File.Exists(destinazione) && new FileInfo(destinazione).Length > 0,
                $"il salvataggio crea il file .xlsx nel percorso scelto (campo Nome file = '{nomeFile.AsTextBox().Text}')");

            var bytes = File.ReadAllBytes(destinazione);
            Assert.True(bytes.Length > 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K',
                "il file esportato e' un contenitore ZIP (formato .xlsx)");
            using var zip = ZipFile.OpenRead(destinazione);
            var testo = string.Concat(zip.Entries
                .Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .Select(e => new StreamReader(e.Open()).ReadToEnd()));
            Assert.Contains(NcUno, testo);
            Assert.Contains(NcDue, testo);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(EsportaExcel_SalvaUnFileXlsxNelPercorsoScelto_ConLeNcNelFoglio), ex);
        }
    }
}
