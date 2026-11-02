using System.Security.Cryptography;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;

namespace TestLoom.App.UITests.Helpers;

/// <summary>
/// Semina e legge il workspace sintetico per le suite di copertura Test (Test*CoverageUiTests). La semina avviene
/// SEMPRE prima di avviare TestLoom.exe (stesso schema di TestEditorOperazioniTests), mai pilotando la UI da qui; la
/// lettura usa gli stessi JsonIo/repository dell'app, sul disco del workspace isolato di ciascun test.
/// </summary>
public static class TestCoverageSeed
{
    public const string NomeTemplateSecondario = "QA Template Secondo";

    public static Guid UidTemplatePrincipale(AppState state) =>
        state.Master.TestTemplate.Load().Single(t => t.Nome == TestWorkspace.DefaultTemplateName).Uid;

    /// <summary>Secondo tipo test selezionabile (stessi campi minimi del principale), per filtro Tipo test e creazione
    /// con un template diverso. Non archivia nulla: il principale resta selezionabile.</summary>
    public static Guid AggiungiTemplateSecondario(AppState state)
    {
        var sezione = new SezioneTemplate
        {
            Nome = "Dettagli",
            Campi = new List<CampoTemplate>
            {
                new() { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.DescrizioneBreve, Obbligatorio = true },
                new() { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.Componente, Obbligatorio = true },
            },
        };
        var templates = state.Master.TestTemplate.Load();
        var secondo = new TestTemplate { Nome = NomeTemplateSecondario, Icona = "🔬", Sezioni = new List<SezioneTemplate> { sezione }, ModificatoDa = "UITests" };
        templates.Add(secondo);
        state.Master.TestTemplate.Save(templates);
        return secondo.Uid;
    }

    /// <summary>Test con una sola operazione (esito dato) salvato come da storage reale, via TestCaseRepository.</summary>
    public static TestCase SeedTest(AppState state, string id, string descrizione, Guid templateUid, string? componente, Esito esito,
        StatoLavoroTest stato = StatoLavoroTest.Pianificato, string? assegnatario = null, string creatoDa = "QA Master")
    {
        var tc = new TestCase
        {
            Id = id,
            DescrizioneBreve = descrizione,
            TestTemplateUid = templateUid,
            CreatoDa = creatoDa,
            StatoLavoro = stato,
            Assegnatario = assegnatario,
            ModificatoDa = "UITests",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        };
        if (!string.IsNullOrEmpty(componente)) tc.Componente.Add(componente);
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1,
            Data = DateTime.Now,
            Operatore = "UITests",
            Esito = esito,
            Condizioni = new List<Condizione> { new() { Esito = esito } },
        });
        state.TestCases.Save(tc, true);
        return tc;
    }

    /// <summary>Anomalia (NC) aperta, candidata al collegamento da una segnalazione del Test.</summary>
    public static void SeedNonConformitaAperta(AppState state, string id, string descrizione)
    {
        state.NonConformita.Save(new NonConformita
        {
            Id = id,
            Descrizione = descrizione,
            Owner = TestWorkspace.DefaultOperatorName,
            DataApertura = DateTime.Now,
            StatoWorkflow = StatoAnomalia.InVerifica,
            Severita = SeveritaAnomalia.Medium,
        });
    }

    public static string CartellaTestCases(TestWorkspace ws) => Path.Combine(ws.WorkspaceDir, "testcases");

    public static IReadOnlyList<string> FileTestCase(TestWorkspace ws) =>
        Directory.Exists(CartellaTestCases(ws))
            ? Directory.EnumerateFiles(CartellaTestCases(ws), "testcase_*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList()
            : Array.Empty<string>();

    public static IReadOnlyList<string> FileNonConformita(TestWorkspace ws)
    {
        var cartella = Path.Combine(ws.WorkspaceDir, "nonconformita");
        return Directory.Exists(cartella)
            ? Directory.EnumerateFiles(cartella, "nc_*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal).ToList()
            : Array.Empty<string>();
    }

    public static IReadOnlyList<string> FileSegnalazioni(TestWorkspace ws)
    {
        var cartella = Path.Combine(ws.WorkspaceDir, "segnalazioni");
        return Directory.Exists(cartella)
            ? Directory.EnumerateFiles(cartella, "segnalazione_*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal).ToList()
            : Array.Empty<string>();
    }

    /// <summary>File presenti sotto il workspace con quel nome, a qualsiasi profondita' (allegati e recovery).</summary>
    public static IReadOnlyList<string> FileConNome(TestWorkspace ws, string nomeFile) =>
        Directory.Exists(ws.WorkspaceDir)
            ? Directory.EnumerateFiles(ws.WorkspaceDir, nomeFile, SearchOption.AllDirectories).ToList()
            : Array.Empty<string>();

    public static TestCase LeggiTestCase(string percorso) => Leggi<TestCase>(percorso);

    public static NonConformita LeggiNonConformita(string percorso) => Leggi<NonConformita>(percorso);

    public static SegnalazioneDaClassificare LeggiSegnalazione(string percorso) => Leggi<SegnalazioneDaClassificare>(percorso);

    /// <summary>Test salvato con quell'Id (qualunque sia la cartella), o null. Un Test archiviato non e' piu' un
    /// TestCase leggibile (il file diventa un tombstone): viene saltato.</summary>
    public static TestCase? TrovaTestCase(TestWorkspace ws, string id) =>
        FileTestCase(ws).Where(f => !ETombstone(f)).Select(LeggiTestCase).FirstOrDefault(t => t.Id == id);

    /// <summary>File di un Test cancellato logicamente: JsonIo lo segnala come "Tombstone TestLoom" (vedi JsonIo.cs).</summary>
    public static bool ETombstone(string percorso)
    {
        var valore = JsonIo.TryRead<TestCase>(percorso, out var errore);
        return valore == null && errore != null && errore.ToString()!.StartsWith("Tombstone TestLoom", StringComparison.Ordinal);
    }

    /// <summary>Vero se il Test con quell'Id risulta archiviato (tombstone) nel workspace.</summary>
    public static bool EArchiviato(TestWorkspace ws, string id) =>
        FileTestCase(ws).Any(f => Path.GetFileName(f).Contains(id, StringComparison.Ordinal) && ETombstone(f));

    /// <summary>Copia del contenuto del Test nel cestino (archiviato, ripristinabile). La cancellazione definitiva la rimuove;
    /// il tombstone in testcases resta per design (marcatore _ditmDeleted per la sincronizzazione).</summary>
    public static bool InCestino(TestWorkspace ws, string id) =>
        File.Exists(Path.Combine(ws.WorkspaceDir, "cestino", $"testcase_{id}.json"));

    /// <summary>Vero se del Test non resta alcun contenuto: niente copia nel cestino e nessun TestCase leggibile.</summary>
    public static bool NessunContenutoDelTest(TestWorkspace ws, string id) =>
        !InCestino(ws, id) && TrovaTestCase(ws, id) == null;

    /// <summary>File di prova sintetico nella cartella temporanea del test, mai nella cartella condivisa.</summary>
    public static string CreaFileDiProva(TestWorkspace ws, string nomeFile, string contenuto)
    {
        var cartella = Path.Combine(ws.RootDir, "fixture");
        Directory.CreateDirectory(cartella);
        var percorso = Path.Combine(cartella, nomeFile);
        File.WriteAllText(percorso, contenuto);
        return percorso;
    }

    public static string HashFile(string percorso) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(percorso)));

    private static T Leggi<T>(string percorso) where T : class
    {
        var valore = JsonIo.TryRead<T>(percorso, out var errore);
        if (valore == null || errore != null)
            throw new InvalidOperationException($"Lettura JSON fallita per '{Path.GetFileName(percorso)}': {errore}");
        return valore;
    }
}
