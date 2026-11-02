using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Centralizza la struttura di cartelle della cartella condivisa (§3.2), così che nessun'altra
/// classe debba conoscere i nomi di file/cartella hard-coded.</summary>
public class DataPaths
{
    public string Root { get; }

    public DataPaths(string root) => Root = root;

    public string RequisitiFile => Path.Combine(Root, "requisiti.json");
    public string FunzionalitaFile => Path.Combine(Root, "funzionalita.json");
    public string EcuFile => Path.Combine(Root, "componente.json");
    public string GammaFile => Path.Combine(Root, "gamma.json");
    public string CategorieFile => Path.Combine(Root, "categorie.json");
    public string VeicoliFile => Path.Combine(Root, "veicoli.json");
    public string StrumentiFile => Path.Combine(Root, "strumenti.json");
    public string OperatoriFile => Path.Combine(Root, "operatori.json");
    public string TestTemplateFile => Path.Combine(Root, "test-template.json");
    public string CampiSistemaPianificazioneFile => Path.Combine(Root, "campi-sistema-pianificazione.json");
    public string CampiSistemaDettagliFile => Path.Combine(Root, "campi-sistema-dettagli.json");
    public string RisoluzioniConflittoAuditFile => Path.Combine(Root, "audit-risoluzioni-conflitto.json");
    public string RevisioniUdtFile => Path.Combine(Root, "revisioni-unitlogger.json");
    public string ValutazioniRequisitoEcuVeicoloFile => Path.Combine(Root, "audit-valutazioni-requisito-componente-veicolo.json");
    public string StatiFunzionalitaEcuVeicoloFile => Path.Combine(Root, "report-stato-funzionalita-componente-veicolo.json");

    public string TestCasesDir => Path.Combine(Root, "testcases");
    public string TestCaseDir(string componente) => Path.Combine(TestCasesDir, SanitizeFolderName(componente));
    public string TestCaseFile(string componente, string id) => Path.Combine(TestCaseDir(componente), $"testcase_{id}.json");

    /// <summary>Nuova collocazione fisica gerarchica, senza cambiare la semantica del TestCase:
    /// Gamma / gruppo di Sottoveicoli / gruppo componente. Le selezioni multiple restano un solo test e vengono
    /// rappresentate da un segmento deterministico, ordinato e limitato in lunghezza.</summary>
    public string TestCaseFileGerarchico(TestCase tc, IReadOnlyList<Veicolo> catalogoVeicoli)
    {
        var gammaPerVeicolo = catalogoVeicoli.ToDictionary(v => v.Nome, v => v.GammaNome, StringComparer.OrdinalIgnoreCase);
        var gamme = tc.VeicoliEffettivi.Select(v => gammaPerVeicolo.GetValueOrDefault(v))
            .Where(g => !string.IsNullOrWhiteSpace(g)).Cast<string>();
        var dir = Path.Combine(TestCasesDir, SegmentoGruppo(gamme, "_SENZA_GAMMA"),
            SegmentoGruppo(tc.VeicoliEffettivi, "_SENZA_SOTTOVEICOLO"),
            SegmentoGruppo(tc.Componente, "_SENZA_componente"));
        return Path.Combine(dir, $"testcase_{tc.Id}.json");
    }

    private static string SegmentoGruppo(IEnumerable<string> valori, string fallback)
    {
        var distinti = valori.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        if (distinti.Count == 0) return fallback;
        var originale = string.Join(" + ", distinti);
        var pulito = SanitizeFolderName(originale);
        if (pulito.Length <= 80) return pulito;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(originale)))[..10];
        return pulito[..65].TrimEnd() + "_" + hash;
    }

    /// <summary>
    /// Unica radice allegati condivisa, separata dai Test JSON così OneDrive può applicare Files On-Demand ai
    /// binari senza toccare i record operativi. I nuovi allegati NON dipendono più da componente/ID: ogni entità usa
    /// il proprio UID immutabile. Questo evita spostamenti dei binari quando cambiano componente, veicolo, gamma o ID
    /// e mantiene le directory abbastanza piccole da non degradare SharePoint con una cartella piatta enorme.
    /// </summary>
    public string AllegatiRootDir => Path.Combine(Root, "allegati");
    public string AllegatiTestRootDir => Path.Combine(AllegatiRootDir, "test");
    public string AllegatiChangeRequestRootDir => Path.Combine(AllegatiRootDir, "cr");
    public string AllegatiNonConformitaRootDir => Path.Combine(AllegatiRootDir, "nc");

    public string AllegatiDir(TestCase tc) => Path.Combine(AllegatiTestRootDir, tc.Uid.ToString("N"));
    public string AllegatiDirChangeRequest(ChangeRequest cr) => Path.Combine(AllegatiChangeRequestRootDir, cr.Uid.ToString("N"));
    public string AllegatiDirNonConformita(NonConformita nc) => Path.Combine(AllegatiNonConformitaRootDir, nc.Uid.ToString("N"));

    /// <summary>Firma legacy mantenuta per codice/test meno recenti; non viene usata per nuovi upload.</summary>
    public string AllegatiDir(string componente, string id) => Path.Combine(AllegatiRootDir, SanitizeFolderName(componente), SanitizeFolderName(id));

    /// <summary>Compatibilità con il layout immediatamente precedente: allegati/&lt;gruppo-componente&gt;/&lt;id&gt;.</summary>
    public string AllegatiDirLegacyCurrent(TestCase tc) =>
        Path.Combine(AllegatiRootDir, SegmentoGruppo(tc.Componente, "_SENZA_componente"), SanitizeFolderName(tc.Id));

    /// <summary>Compatibilità con le vecchie Change Request: allegati/ChangeRequest/&lt;id&gt;.</summary>
    public string AllegatiDirChangeRequestLegacy(string id) =>
        Path.Combine(AllegatiRootDir, "ChangeRequest", SanitizeFolderName(id));

    /// <summary>Vecchissima posizione degli allegati dentro testcases/&lt;componente&gt;/allegati/&lt;id&gt;.</summary>
    public string AllegatiDirLegacy(string componente, string id) => Path.Combine(TestCaseDir(componente), "allegati", SanitizeFolderName(id));

    public string CestinoDir => Path.Combine(Root, "cestino");
    public string CestinoFile(string id) => Path.Combine(CestinoDir, $"testcase_{id}.json");

    public string NonConformitaDir => Path.Combine(Root, "nonconformita");
    public string NonConformitaFile(string id) => Path.Combine(NonConformitaDir, $"nc_{id}.json");
    public string ChangeRequestDir => Path.Combine(Root, "change-requests");
    public string ChangeRequestFile(string id) => Path.Combine(ChangeRequestDir, $"cr_{SanitizeFolderName(id)}.json");

    public string SegnalazioniDir => Path.Combine(Root, "segnalazioni");
    public string SegnalazioneFile(Guid uid) => Path.Combine(SegnalazioniDir, $"segnalazione_{uid:N}.json");
    public string NotificheDir => Path.Combine(Root, "notifiche");

    /// <summary>Vecchia cartella backup CONDIVISA (fino alla 6.4.1). Dalla 6.4.2 gli snapshot ZIP sono SOLO
    /// locali al PC (vedi <see cref="WorkspaceLocalPaths.GetBackupDirectory"/> e <see cref="BackupService"/>):
    /// questa proprietà resta solo per riconoscere/escludere eventuali zip condivisi lasciati da versioni
    /// precedenti, che l'app non cancella né più scrive.</summary>
    public string BackupDir => Path.Combine(Root, "backup");

    /// <summary>
    /// Cestino tecnico di dominio condiviso. È volutamente FUORI da _ditm e dagli allegati: gli envelope JSON
    /// vengono quindi inclusi nei normali snapshot ZIP e non hanno retention automatica.
    /// </summary>
    public string RecoveryBinDir => Path.Combine(Root, "recovery-bin");
    public string VersionManifestFile => Path.Combine(Root, "version-manifest.json");
    public string PolarionConfigFile => Path.Combine(Root, "ticketing-config.json");
    public string PolarionApiConfigFile => Path.Combine(Root, "ticketing-api-config.json");
    public string PolarionSnapshotDir => Path.Combine(Root, "ticketing-snapshots");
    public string PolarionHistoryDir => Path.Combine(Root, "ticketing-history");

    /// <summary>Modalità di accesso (con/senza PIN), impostazione unica per l'intera cartella dati (piano
    /// 6.4.2, §4b). Vedi AccessModeConfigService per il motivo del file dedicato invece di un campo su un
    /// file master esistente.</summary>
    public string AccessoConfigFile => Path.Combine(Root, "accesso-config.json");

    /// <summary>Marcatore condiviso delle migrazioni idempotenti del catalogo (es. RAS UnitLogger, vedi CatalogMigrationService).
    /// Singleton piccolo come ticketing-config.json/accesso-config.json: contiene solo gli id già applicati.</summary>
    public string CatalogMigrationsFile => Path.Combine(Root, "catalog-migrations.json");

    /// <summary>Politica condivisa della durata delle operazioni (TIME, 6.6.1): un solo record per l'intera cartella dati,
    /// modificabile solo dal Master. Non e' in AppConfig per PC e non e' legata alla sicurezza di accesso: vedi
    /// PoliticaDurataService.</summary>
    public string PoliticaDurataFile => Path.Combine(Root, "politica-durata-operazioni.json");

    public string ReportDir => Path.Combine(Root, "Report");

    /// <summary>Crea soltanto la struttura di cartelle "dumb". Deliberatamente NON attiva Phase D qui: farlo
    /// renderebbe questo metodo pericoloso da chiamare da un punto non ancora passato dal gate di compatibilità
    /// versione (CompatibilitaVersioneService.Applica, l'unico chiamante autorizzato di
    /// PhaseDMasterBootstrapService.EnsureReady) — un client incompatibile con uno schema futuro deve scoprirlo
    /// PRIMA di creare probe, directory, manifest o journal, non dopo.</summary>
    public void EnsureStructure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(TestCasesDir);
        Directory.CreateDirectory(AllegatiRootDir);
        Directory.CreateDirectory(AllegatiTestRootDir);
        Directory.CreateDirectory(AllegatiChangeRequestRootDir);
        Directory.CreateDirectory(AllegatiNonConformitaRootDir);
        Directory.CreateDirectory(CestinoDir);
        Directory.CreateDirectory(NonConformitaDir);
        Directory.CreateDirectory(ChangeRequestDir);
        Directory.CreateDirectory(SegnalazioniDir);
        Directory.CreateDirectory(NotificheDir);
        // Dalla 6.4.2 BackupDir (cartella CONDIVISA) non viene più creata qui: gli snapshot ZIP sono SOLO
        // locali al PC (vedi BackupService/WorkspaceLocalPaths). Creare comunque una cartella "backup/" vuota
        // in ogni workspace condiviso non avrebbe più alcuno scopo.
        Directory.CreateDirectory(RecoveryBinDir);
        Directory.CreateDirectory(ReportDir);
    }

    private static string SanitizeFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "_SENZA_componente";
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "_SENZA_componente" : clean;
    }
}
