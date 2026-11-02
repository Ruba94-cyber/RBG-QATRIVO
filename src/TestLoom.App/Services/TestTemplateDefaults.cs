using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Genera i template automotive di partenza (Validazione Banco/Veicolo/UnitLogger/UnitLogger Performance) e il tipo RAS UnitLogger,
/// equivalenti al vecchio enum fisso TipoTest ma come TestTemplate modificabili da un Master. Usato solo per
/// popolare test-template.json la primissima volta che una cartella condivisa non ce l'ha ancora (stesso
/// pattern di CompatibilitaVersioneService per version-manifest.json) — dopo la prima creazione, il contenuto
/// vive nella cartella condivisa e viene gestito dall'editor, non più rigenerato da qui.</summary>
public static class TestTemplateDefaults
{
    public static List<TestTemplate> Seed()
    {
        var banco = new TestTemplate { Nome = "Validazione Banco", Icona = "🛠", Ordine = 0 };
        var veicolo = new TestTemplate { Nome = "Validazione Veicolo", Icona = "🚚", Ordine = 1 };
        var unitlogger = new TestTemplate { Nome = "UnitLogger", Icona = "📋", Ordine = 2 };
        var udtPerf = new TestTemplate { Nome = "UnitLogger Performance", Icona = "📈", Ordine = 3 };

        foreach (var t in new[] { banco, veicolo, unitlogger, udtPerf })
        {
            t.Sezioni.Add(SezioneDettagliStandard());
            t.CampiPianificazione = CampiPianificazioneStandard();
            t.CampiRiferimenti = CampiRiferimentiStandard();
            // Senza questo, la tabella "Tipi di test" mostrava "01/01/0001 00:00" in Ultima modifica (bug
            // trovato in collaudo live 2026-08-17) — stessa convenzione già usata per gli altri master data
            // seminati da questo file/da SeedDemoDataService (UltimaModifica/ModificatoDa="Setup").
            t.UltimaModifica = DateTime.Now;
            t.ModificatoDa = "Setup";
        }

        banco.StrumentiPreselezionati = new List<string> { "ValidationTool" };
        veicolo.StrumentiPreselezionati = new List<string> { "ValidationTool", "Breakout Box" };
        unitlogger.StrumentiPreselezionati = new List<string> { "UnitLogger" };
        // Km/ore/usura vivono in uno strumento a sé ("Componente Usato"), preselezionato solo su UnitLogger
        // Performance: su un UnitLogger semplice non devono comparire (feedback utente 2026-08-18, punto 1).
        udtPerf.StrumentiPreselezionati = new List<string> { "UnitLogger", "Componente Usato" };

        // Non applicato per default (era già così prima: "obbligatorio" solo nel commento, mai davvero
        // validato). Il Master può attivare Obbligatorio in seguito se lo vuole davvero.
        unitlogger.VincoloCollegamento = new VincoloCollegamentoTemplate
        {
            Obbligatorio = false,
            Etichetta = "Test collegato",
            TemplateAmmessiUid = new List<Guid> { banco.Uid, veicolo.Uid },
        };
        udtPerf.VincoloCollegamento = new VincoloCollegamentoTemplate
        {
            Obbligatorio = false,
            Etichetta = "Test collegato",
            TemplateAmmessiUid = new List<Guid> { banco.Uid, veicolo.Uid },
        };

        banco.FogliReport.Add(FoglioTestOnBench());
        banco.FogliReport.Add(FoglioDtcInjection());
        veicolo.FogliReport.Add(FoglioTestVeicolo());
        unitlogger.FogliReport.Add(FoglioUdt());
        udtPerf.FogliReport.Add(FoglioUdt());

        // RAS UnitLogger (Test type, release 6.6.1): stesso modello UnitLogger con identità fissa, vedi CreaRasUdt. Ordine dopo
        // gli standard esistenti; VincoloCollegamento ammette banco e veicolo di questo stesso seed.
        var rasUdt = CreaRasUdt(ordine: 4, templateAmmessiUid: new List<Guid> { banco.Uid, veicolo.Uid });

        return new List<TestTemplate> { banco, veicolo, unitlogger, udtPerf, rasUdt };
    }

    /// <summary>Identità fissa del Test type "RAS UnitLogger" (remote assistance, release 6.6.1). Uguale su tutte le postazioni
    /// e in ogni cartella dati: la migrazione idempotente (CatalogMigrationService) e la deduplica si appoggiano a questo
    /// Uid, mai a un Guid casuale.</summary>
    public static readonly Guid RasUdtUid = new("30b69bac-4151-4b13-97c3-79423fb75474");

    /// <summary>Nome del template RAS UnitLogger. La deduplica in migrazione è case-insensitive e considera anche le righe archiviate.</summary>
    public const string NomeRasUdt = "RAS UnitLogger";

    /// <summary>Nomi dei template ammessi dal vincolo collegamento del RAS UnitLogger (stessi del seed).</summary>
    public const string NomeTemplateBanco = "Validazione Banco";
    public const string NomeTemplateVeicolo = "Validazione Veicolo";

    /// <summary>Data fissa, non DateTime.Now: la riga è byte-identica su ogni postazione che la aggiunge, così due append
    /// concorrenti dello stesso template convergono senza conflitto (vedi PhaseDMasterRepositoryEngine.Save).</summary>
    private static readonly DateTime RasUdtUltimaModifica = new(2026, 10, 12, 0, 0, 0);

    /// <summary>Definizione RAS UnitLogger = definizione UnitLogger del seed: stesse sezioni Dettagli/campi, stessi campi Pianificazione
    /// e Riferimenti, stesso foglio report (FoglioUdt) e strumento preselezionato "UnitLogger" (il tool UnitLogger esistente, invariato).
    /// Vincolo collegamento identico (non obbligatorio, "Test collegato"): i template ammessi sono passati dal chiamante
    /// perché i loro Uid dipendono dal catalogo in cui la riga viene creata.</summary>
    public static TestTemplate CreaRasUdt(int ordine, IReadOnlyList<Guid> templateAmmessiUid)
    {
        ArgumentNullException.ThrowIfNull(templateAmmessiUid);
        var rasUdt = new TestTemplate
        {
            Uid = RasUdtUid,
            Nome = NomeRasUdt,
            Icona = "🛰",
            Ordine = ordine,
            UltimaModifica = RasUdtUltimaModifica,
            ModificatoDa = "Setup",
            StrumentiPreselezionati = new List<string> { "UnitLogger" },
            VincoloCollegamento = new VincoloCollegamentoTemplate
            {
                Obbligatorio = false,
                Etichetta = "Test collegato",
                TemplateAmmessiUid = templateAmmessiUid.ToList(),
            },
        };
        rasUdt.Sezioni.Add(SezioneDettagliStandard());
        rasUdt.CampiPianificazione = CampiPianificazioneStandard();
        rasUdt.CampiRiferimenti = CampiRiferimentiStandard();
        rasUdt.FogliReport.Add(FoglioUdt());
        AssegnaUidNidificatiDeterministici(rasUdt);
        return rasUdt;
    }

    /// <summary>Uid di sezioni, campi, fogli e colonne derivati dal percorso strutturale: il payload JSON del RAS UnitLogger è lo
    /// stesso su ogni postazione, senza Guid casuali annidati.</summary>
    private static void AssegnaUidNidificatiDeterministici(TestTemplate t)
    {
        for (var s = 0; s < t.Sezioni.Count; s++)
        {
            t.Sezioni[s].Uid = UidDeterministico($"sezione/{s}");
            for (var c = 0; c < t.Sezioni[s].Campi.Count; c++)
                t.Sezioni[s].Campi[c].Uid = UidDeterministico($"sezione/{s}/campo/{c}");
        }
        for (var c = 0; c < t.CampiPianificazione.Count; c++)
            t.CampiPianificazione[c].Uid = UidDeterministico($"pianificazione/{c}");
        for (var c = 0; c < t.CampiRiferimenti.Count; c++)
            t.CampiRiferimenti[c].Uid = UidDeterministico($"riferimenti/{c}");
        for (var f = 0; f < t.FogliReport.Count; f++)
        {
            t.FogliReport[f].Uid = UidDeterministico($"foglio/{f}");
            for (var k = 0; k < t.FogliReport[f].Colonne.Count; k++)
                t.FogliReport[f].Colonne[k].Uid = UidDeterministico($"foglio/{f}/colonna/{k}");
        }
    }

    private static Guid UidDeterministico(string percorso)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("testloom-ras-unitlogger-v1|" + percorso));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        // Bit di versione/variante UUID, come MasterEntityIdentityService.DeterministicGuid.
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static SezioneTemplate SezioneDettagliStandard()
    {
        var sezione = new SezioneTemplate { Nome = "Dettagli", Ordine = 0 };
        var ordine = 0;
        foreach (var chiave in CampiSistema.Chiavi)
        {
            var campo = new CampoTemplate
            {
                Tipo = TipoCampoTemplate.CampoSistema,
                CampoSistemaChiave = chiave,
                Nome = CampiSistema.LabelDefault(chiave),
                Ordine = ordine++,
            };
            // Generalizza il vecchio "Descrizione problema visibile solo su esito problematico" (era fisso nel
            // codice, TestCaseEditorViewModel.MostraDescrizioneProblema): stesso comportamento di default, ora
            // configurabile per qualunque campo (Addendum: collaudo live 2026-08-17).
            if (chiave == CampiSistema.DescrizioneProblema)
                campo.CondizioneEsito = new List<Esito> { Esito.NonConforme, Esito.ConformeConDeviazioni, Esito.DaRivedere };
            // Riproduce gli obbligatori fissi di prima (Componente/Veicolo/Descrizione sempre; matricola solo se non
            // Pianificato, perché il veicolo/banco reale può non essere ancora stato scelto), ora dati
            // configurabili invece di controlli incisi in Salva() (Addendum: collaudo live 2026-08-17).
            if (chiave is CampiSistema.Componente or CampiSistema.Veicolo or CampiSistema.Descrizione)
                campo.Obbligatorio = true;
            if (chiave == CampiSistema.Matricola)
            {
                campo.Obbligatorio = true;
                campo.StatiObbligatorio = Enum.GetValues(typeof(StatoLavoroTest)).Cast<StatoLavoroTest>().Where(s => s != StatoLavoroTest.Pianificato).ToList();
            }
            sezione.Campi.Add(campo);
        }
        return sezione;
    }

    private static List<CampoTemplate> CampiPianificazioneStandard()
    {
        var ordine = 0;
        return CampiSistema.ChiaviPianificazione.Select(chiave =>
        {
            var campo = new CampoTemplate
            {
                Tipo = TipoCampoTemplate.CampoSistema,
                CampoSistemaChiave = chiave,
                Nome = CampiSistema.LabelDefault(chiave),
                Ordine = ordine++,
            };
            // Assegnatario era obbligatorio sempre (controllo fisso in Salva()); riprodotto qui come default
            // configurabile, non più inciso nel codice (Addendum: collaudo live 2026-08-17).
            if (chiave == CampiSistema.Assegnatario) campo.Obbligatorio = true;
            return campo;
        }).ToList();
    }

    private static List<CampoTemplate> CampiRiferimentiStandard()
    {
        var ordine = 0;
        return CampiSistema.ChiaviRiferimenti.Select(chiave => new CampoTemplate
        {
            Tipo = TipoCampoTemplate.CampoSistema,
            CampoSistemaChiave = chiave,
            Nome = CampiSistema.LabelDefault(chiave),
            Ordine = ordine++,
        }).ToList();
    }

    private static ColonnaReportTemplate Sistema(int ordine, string intestazione, string chiave) => new()
    {
        Ordine = ordine, Intestazione = intestazione, Origine = OrigineColonnaReport.CampoSistema, Chiave = chiave,
    };

    private static ColonnaReportTemplate Strumento(int ordine, string intestazione, string chiave) => new()
    {
        Ordine = ordine, Intestazione = intestazione, Origine = OrigineColonnaReport.CampoStrumento, Chiave = chiave,
    };

    private static FoglioReportTemplate FoglioTestOnBench() => new()
    {
        NomeFoglio = "Test On Bench",
        Ordine = 0,
        Colonne = new List<ColonnaReportTemplate>
        {
            Sistema(0, "Veicolo", CampiSistema.Veicolo),
            Sistema(1, "Componente", CampiSistema.Componente),
            Sistema(2, "Baseline SW", CampiSistema.BaselineSw),
            Sistema(3, "Baseline HW", CampiSistema.BaselineHw),
            Strumento(4, "Comando (Service)", "ValidationTool::Comando (Service)"),
            Strumento(5, "ID / Sub-funzione", "ValidationTool::ID/Sub-funzione"),
            Strumento(6, "Risposta attesa/ottenuta", "ValidationTool::Risposta attesa/ottenuta"),
            Strumento(7, "ADA", "ValidationTool::ADA"),
            Strumento(8, "SGW", "ValidationTool::SGW"),
            Strumento(9, "Componente Status", "ValidationTool::componente Status"),
            Strumento(10, "Diagnostic Session", "ValidationTool::Diagnostic Session"),
            Strumento(11, "Description ID", "ValidationTool::Description ID"),
        },
    };

    private static FoglioReportTemplate FoglioDtcInjection() => new()
    {
        NomeFoglio = "Guasto Injection",
        Ordine = 1,
        Colonne = new List<ColonnaReportTemplate>
        {
            Sistema(0, "Veicolo", CampiSistema.Veicolo),
            Sistema(1, "Componente", CampiSistema.Componente),
            Strumento(2, "Operazione", "Guasto Injection::guasto - Operazione"),
            Strumento(3, "Stato chiave", "Guasto Injection::guasto - Stato chiave/nottolino"),
            Strumento(4, "Scenario", "Guasto Injection::guasto - Scenario"),
            Strumento(5, "Transizioni", "Guasto Injection::guasto - Transizioni"),
            Strumento(6, "Guasto atteso", "Guasto Injection::guasto - Atteso"),
            Strumento(7, "Parametri correlati", "Guasto Injection::guasto - Parametri correlati"),
        },
    };

    private static FoglioReportTemplate FoglioTestVeicolo() => new()
    {
        NomeFoglio = "Test a Veicolo",
        Ordine = 0,
        Colonne = new List<ColonnaReportTemplate>
        {
            Sistema(0, "Veicolo", CampiSistema.Veicolo),
            Sistema(1, "Matricola", CampiSistema.Matricola),
            Sistema(2, "Componente", CampiSistema.Componente),
            Strumento(3, "Azione da fare a veicolo", "Breakout Box::Azione veicolo"),
            Strumento(4, "Pin", "Breakout Box::PIN"),
            Strumento(5, "Segnale inviato", "Breakout Box::Segnale inviato"),
            Strumento(6, "Segnali letti", "Breakout Box::Segnali letti"),
            Strumento(7, "Risultato atteso", "Breakout Box::Risultato atteso"),
            Strumento(8, "Stato veicolo", "Breakout Box::Stato veicolo"),
            Strumento(9, "Reazione osservata", "Breakout Box::Reazione osservata"),
        },
    };

    private static FoglioReportTemplate FoglioUdt() => new()
    {
        NomeFoglio = "UnitLogger",
        Ordine = 0,
        Colonne = new List<ColonnaReportTemplate>
        {
            Strumento(0, "Nome pacchetto", "UnitLogger::Nome pacchetto"),
            Strumento(1, "Versione pacchetto", "UnitLogger::Versione pacchetto"),
            Strumento(2, "Licenza", "UnitLogger::Licenza"),
            Strumento(3, "Versione UnitLogger", "UnitLogger::Versione UnitLogger"),
            Strumento(4, "Lingua", "UnitLogger::Lingua"),
            Strumento(5, "Pacchetto", "UnitLogger::Pacchetto"),
            Strumento(6, "Versione Pacchetto", "UnitLogger::Versione Pacchetto"),
        },
    };
}
