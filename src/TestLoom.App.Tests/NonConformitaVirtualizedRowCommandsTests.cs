using System.Text.RegularExpressions;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata al FAIL critico della review 2026-09-25 sul ramo perf/6.4.0-ui-riuso-view:
/// NonConformitaView.axaml.cs ricablava con una sola passata (InstradaComandiStateSafety, su
/// AttachedToVisualTree/DataContextChanged) i pulsanti grezzi verso le varianti "Sicure". Con la lista Righe
/// virtualizzata (ListBox + VirtualizingStackPanel, Height fissa) i container creati DOPO quella passata
/// (scroll, riciclo, riga espansa con editor lazy) restavano agganciati ai comandi grezzi — chiusura/riapertura/
/// eliminazione anomalia e classificazione/esclusione Segnalazione potevano bypassare i percorsi fail-closed.
///
/// La correzione lega i pulsanti DIRETTAMENTE nello XAML ai comandi Sicuri (ChiudiManualmenteSicuroCommand,
/// RiapriAnomaliaSicuroCommand, EliminaAnomaliaSicuroCommand, ClassificaSegnalazioneSicuroCommand,
/// EscludiSegnalazioneSicuroCommand) ed elimina il ricablaggio a runtime: ogni container del DataTemplate,
/// realizzato subito o dopo scroll/riciclo, nasce già con il comando sicuro, senza bisogno di alcuna passata
/// successiva. Questo test blocca sia una regressione al ricablaggio runtime sia il ritorno accidentale di un
/// Command="{Binding ...}" verso uno dei cinque comandi grezzi in questa vista.</summary>
public class NonConformitaVirtualizedRowCommandsTests
{
    private static string ViewsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "TestLoom.App", "Views");
            if (Directory.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir, "src", "TestLoom.App", "Views");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Cartella Views non trovata a partire da " + AppContext.BaseDirectory);
    }

    private static readonly string[] ComandiGrezzi =
    {
        "ChiudiManualmenteCommand",
        "RiapriAnomaliaCommand",
        "EliminaAnomaliaCommand",
        "ClassificaSegnalazioneCommand",
        "EscludiSegnalazioneCommand",
    };

    /// <summary>Comandi ancora legati direttamente nella lista della pagina (righe Segnalazioni). Chiudi/Riapri/
    /// Elimina di una NC sono ora nel dialogo Dettaglio (AnomaliaDettaglioViewModel), non piu' in un editor
    /// inline espandibile: la riga NC ha solo il bottone 'Dettaglio' (altezza fissa, niente scatti di scroll).</summary>
    private static readonly string[] ComandiSicuri =
    {
        "ClassificaSegnalazioneSicuroCommand",
        "EscludiSegnalazioneSicuroCommand",
    };

    private static readonly string[] ComandiDelDialogo =
    {
        "ChiudiCommand", "RiapriCommand", "EliminaCommand", "SalvaCommand", "AnnullaCommand",
    };

    /// <summary>Ogni container prodotto dal DataTemplate della ListBox Righe (o dell'ItemsControl Segnalazioni)
    /// — incluso uno realizzato per la prima volta dopo uno scroll oltre le righe iniziali, grazie alla
    /// virtualizzazione — deve legare i cinque pulsanti di workflow SOLO ai comandi Sicuri: essendo un binding
    /// XAML statico dentro il DataTemplate, non dipende da quando/quante volte il container viene creato.</summary>
    [Fact]
    public void NonConformitaView_PulsantiDiRiga_LeganoSoloIComandiSicuri()
    {
        var file = Path.Combine(ViewsDir(), "NonConformitaView.axaml");
        var contenuto = File.ReadAllText(file);

        foreach (var sicuro in ComandiSicuri)
            Assert.True(contenuto.Contains("Command=\"{Binding #Root.((vm:NonConformitaViewModel)DataContext)." + sicuro + "}\""),
                $"Atteso un binding diretto a {sicuro} nello XAML (nessun ricablaggio a runtime ammesso).");

        // Nessun binding Command deve puntare ESATTAMENTE a un comando grezzo (accoppiato a un word boundary,
        // cosi' "ChiudiManualmenteCommand" non intercetta se stesso dentro "ChiudiManualmenteSicuroCommand").
        foreach (var grezzo in ComandiGrezzi)
        {
            var pattern = new Regex(@"Command=""\{Binding[^""]*\b" + Regex.Escape(grezzo) + @"\}""");
            Assert.False(pattern.IsMatch(contenuto),
                $"Trovato un binding Command residuo verso il comando grezzo {grezzo} in NonConformitaView.axaml: " +
                "con la lista virtualizzata un container creato dopo il primo giro (scroll/riciclo) resterebbe " +
                "agganciato a un'azione non fail-safe.");
        }
    }

    /// <summary>La riga NC e' ad altezza fissa: nessun editor espandibile (Espandi/EditorContent), solo il bottone
    /// 'Dettaglio' che apre il dialogo; i comandi di workflow vivono nel dialogo con binding diretti.</summary>
    [Fact]
    public void NonConformitaView_RigaNc_HaIlBottoneDettaglio_NonPiuLEditorEspandibile()
    {
        var pagina = File.ReadAllText(Path.Combine(ViewsDir(), "NonConformitaView.axaml"));
        Assert.Contains("Anomalie_Riga_Dettaglio_Button", pagina);
        Assert.DoesNotContain("Anomalie_Riga_Espandi_Button", pagina);
        Assert.DoesNotContain("EditorContent", pagina);
        Assert.DoesNotContain("ToggleEspansioneRigaCommand", pagina);

        var dialogo = File.ReadAllText(Path.Combine(ViewsDir(), "AnomaliaDettaglioWindow.axaml"));
        foreach (var cmd in ComandiDelDialogo)
            Assert.Contains("Command=\"{Binding " + cmd + "}\"", dialogo);
        foreach (var id in new[] { "Stato_Combo", "Severita_Combo", "Priorita_Combo", "Origine_Combo", "StatoTest_Combo", "Owner_TextBox",
                     "TicketRiferimento_TextBox", "TicketUrl_TextBox", "MotivoChiusura_TextBox", "Salva_Button", "Chiudi_Button",
                     "Riapri_Button", "Elimina_Button", "Annulla_Button", "Errore_Text" })
            Assert.Contains("AnomaliaDettaglio_" + id, dialogo);
        Assert.Contains("CanResize=\"True\"", dialogo);
    }

    [Fact]
    public void NonConformitaView_FiltriCrESegnalazioni_HannoAzioniEsplicite()
    {
        var pagina = File.ReadAllText(Path.Combine(ViewsDir(), "NonConformitaView.axaml"));
        foreach (var automationId in new[]
        {
            "Anomalie_Cr_FiltroVeicolo_Button", "Anomalie_Cr_FiltroComponente_Button",
            "Anomalie_Cr_FiltroGamma_Button", "Anomalie_Cr_FiltroFunzionalita_Button",
            "Anomalie_Cr_FiltroAssegnatario_Button", "Anomalie_Cr_FiltroCreatore_Button",
            "Anomalie_Cr_FiltroAssegnatiAMe_ToggleButton", "Anomalie_Cr_FiltroCreatiDaMe_ToggleButton",
        })
            Assert.Contains(automationId, pagina);

        // Apri e Modifica rapida sono le due azioni di riga NC (la modifica della segnalazione sta nella finestra rapida).
        Assert.Contains("AutomationProperties.AutomationId=\"Anomalie_Riga_Dettaglio_Button\"", pagina);
        Assert.Contains("AutomationProperties.AutomationId=\"Anomalie_Riga_ModificaRapida_Button\"", pagina);
        Assert.Contains("AutomationProperties.AutomationId=\"Anomalie_Cr_RimuoviFiltri_Button\"", pagina);
        Assert.Contains("IsVisible=\"{Binding HaFiltriCrAttivi}\"", pagina);
        Assert.DoesNotContain("Content=\"⚑\"", pagina);
        Assert.DoesNotContain("Content=\"ⓘ\"", pagina);
    }

    [Fact]
    public void SegnalazioneReteView_MostraIndicatoreEFileDuranteIlCaricamentoAllegato()
    {
        var pagina = File.ReadAllText(Path.Combine(ViewsDir(), "SegnalazioneReteEditorView.axaml"));
        Assert.Contains("Segnalazione_Allegato_Caricamento_Indicatore", pagina);
        Assert.Contains("Segnalazione_Allegato_Caricamento_Nome_Text", pagina);
        Assert.Contains("IsVisible=\"{Binding CaricamentoAllegatoInCorso}\"", pagina);
        Assert.Contains("ConverterParameter=SegEdAllegatoCaricamento}", pagina);
        Assert.Contains("IsEnabled=\"{Binding PuoAggiungereAllegati}\"", pagina);
        Assert.Contains("IsEnabled=\"{Binding PuoSalvare}\"", pagina);
        Assert.Contains("IsEnabled=\"{Binding PuoAnnullare}\"", pagina);
    }

    /// <summary>Il ricablaggio a runtime (il vecchio metodo che ricablava i comandi, rieseguito una sola volta
    /// su AttachedToVisualTree/DataContextChanged) era la causa strutturale del bug: coi container virtualizzati
    /// creati DOPO quella passata restavano sui comandi grezzi. Va rimosso, non reso "più aggressivo": i
    /// binding XAML diretti (verificati sopra) bastano e valgono per ogni container, sempre. Cerca la
    /// dichiarazione/chiamata del metodo (non il nome nudo, che può restare legittimamente in un commento
    /// esplicativo) e l'uso di GetVisualDescendants, entrambi spariti col fix.</summary>
    [Fact]
    public void NonConformitaView_NonHaPiuIlRicablaggioRuntimeDeiComandi()
    {
        var file = Path.Combine(ViewsDir(), "NonConformitaView.axaml.cs");
        var contenuto = File.ReadAllText(file);

        Assert.DoesNotMatch(new Regex(@"(void|\bthis\.)\s*InstradaComandiStateSafety\s*\("), contenuto);
        Assert.DoesNotContain("GetVisualDescendants", contenuto);
    }
}
