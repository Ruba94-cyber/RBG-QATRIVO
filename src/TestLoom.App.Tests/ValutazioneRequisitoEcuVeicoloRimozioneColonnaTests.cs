using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Rimozione atomica della colonna componente in Audit: solo valutazioni Manuali/Propagate per componente,
/// veicolo e requisiti attivi; le fonti Test/legacy restano inalterate e i valori rimossi restano nello storico.</summary>
public sealed class ValutazioneRequisitoEcuVeicoloRimozioneColonnaTests
{
    private const string EcuTarget = "BCM";
    private const string EcuAltra = "ABS";
    private const string VeicoloTarget = "My24";
    private const string VeicoloAltro = "My26";

    private static Componente EcuDi(string nome, params string[] veicoli) =>
        new() { Nome = nome, VeicoliAssociati = veicoli.ToList() };

    private static Requisito Requisito(string id, bool archiviata = false) =>
        new() { Id = id, Testo = id, Archiviata = archiviata };

    private static TestCase TestDiretto(string id, string componente, string requisitoId, string veicolo) =>
        new()
        {
            Id = id,
            Componente = componente,
            RequisitoId = { requisitoId },
            Veicolo = veicolo,
            Esecuzioni =
            {
                new Esecuzione
                {
                    Data = new DateTime(2026, 10, 15, 10, 0, 0),
                    Esito = Esito.Conforme,
                    Matricola = "Matricola-TEST",
                    PunteggiRequisiti =
                    {
                        new PunteggioRequisito { RequisitoId = requisitoId, Valore = PunteggioConformita.Conforme },
                    },
                },
            },
        };

    private static MasterDataRepository<ValutazioneRequisitoEcuVeicolo> CreaRepository(TempWorkspace workspace) =>
        new(workspace.Paths.ValutazioniRequisitoEcuVeicoloFile);

    [Fact]
    public void RimuoviValutazioniColonnaAudit_RimuoveSoloEcuVeicoloERequisitiAttivi_EConservaFontiEStorico()
    {
        using var workspace = new TempWorkspace();
        var repo = CreaRepository(workspace);
        var componenti = new[] { EcuDi(EcuTarget, VeicoloTarget, VeicoloAltro), EcuDi(EcuAltra, VeicoloTarget) };
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", EcuTarget, new[] { VeicoloTarget }, PunteggioConformita.NonConforme,
            "manuale da rimuovere", "QA", "SW-COL", out var errore), errore);
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", EcuTarget, new[] { VeicoloAltro }, PunteggioConformita.Conforme,
            "altro veicolo", "QA", null, out errore), errore);
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", EcuAltra, new[] { VeicoloTarget }, PunteggioConformita.AmpiamenteConforme,
            "altra componente", "QA", null, out errore), errore);
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R3", EcuTarget, new[] { VeicoloTarget }, PunteggioConformita.ScarsamenteConforme,
            "requisito archiviato", "QA", null, out errore), errore);
        var sorgentePropagazione = new CandidatoPropagazioneRequisito(
            VeicoloAltro, "TC-SOURCE", PunteggioConformita.ModeratamenteConforme, new DateTime(2026, 10, 15, 9, 0, 0));
        Assert.True(ValutazioneRequisitoEcuVeicoloService.Propaga(
            repo, "R2", EcuTarget, new[] { (VeicoloTarget, sorgentePropagazione) },
            "propagata da rimuovere", "QA", out errore), errore);

        var test = TestDiretto("TC-COLUMN-KEEP", EcuTarget, "R1", VeicoloTarget);
        var tests = new[] { test };
        var risoluzione = new RisoluzioneConflittoAudit
        {
            Componente = EcuTarget,
            RequisitoId = "R1",
            PunteggioManuale = PunteggioConformita.ScarsamenteConforme,
            Motivazione = "valore legacy da lasciare intatto",
            UltimaModifica = new DateTime(2026, 10, 15, 8, 0, 0),
            ModificatoDa = "Legacy QA",
        };
        var risoluzioni = new[] { risoluzione };
        var improntaTest = TestCaseFingerprint.Compute(test);
        var improntaLegacy = EntityStateFingerprint.Compute(risoluzione);
        var requisiti = new[] { Requisito("R1"), Requisito("R2"), Requisito("R3", archiviata: true) };

        var anteprima = ValutazioneRequisitoEcuVeicoloService.CatturaRimozioneValutazioniColonnaAudit(
            EcuTarget, VeicoloTarget, componenti, requisiti, tests, risoluzioni, repo.Load());

        Assert.True(anteprima.EcuAssociataAlVeicolo);
        Assert.Equal(2, anteprima.NumeroValutazioni);
        Assert.Contains(anteprima.Valutazioni, v => v.RequisitoId == "R1" && v.Origine == OrigineValutazioneRequisito.Manuale);
        Assert.Contains(anteprima.Valutazioni, v => v.RequisitoId == "R2" && v.Origine == OrigineValutazioneRequisito.Propagata);

        var salvata = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazioniColonnaAudit(
            repo, anteprima, componenti, requisiti, tests, risoluzioni, "QA", out var rimosse, out errore,
            () => new FontiRimozioneValutazioniColonnaAudit(componenti, requisiti, tests, risoluzioni),
            new DateTime(2026, 10, 16, 12, 0, 0));

        Assert.True(salvata, errore);
        Assert.Equal(2, rimosse);
        var righe = repo.Load();
        var manualeTarget = Assert.Single(righe, v => v.Componente == EcuTarget && v.RequisitoId == "R1" && v.Veicolo == VeicoloTarget);
        var propagataTarget = Assert.Single(righe, v => v.Componente == EcuTarget && v.RequisitoId == "R2" && v.Veicolo == VeicoloTarget);
        Assert.True(manualeTarget.IsArchiviata);
        Assert.True(propagataTarget.IsArchiviata);
        Assert.Null(manualeTarget.EventoCorrenteId);
        Assert.Null(propagataTarget.EventoCorrenteId);
        Assert.Contains(manualeTarget.Storico, s => s.Origine == OrigineStoricoValutazioneAudit.Manuale &&
            s.Punteggio == PunteggioConformita.NonConforme && s.Nota == "manuale da rimuovere" && s.BaselineSw == "SW-COL");
        Assert.Contains(propagataTarget.Storico, s => s.Origine == OrigineStoricoValutazioneAudit.Propagata &&
            s.Punteggio == PunteggioConformita.ModeratamenteConforme && s.Nota == "propagata da rimuovere" && s.TestCaseId == "TC-SOURCE");
        Assert.Contains(righe, v => v.Componente == EcuTarget && v.RequisitoId == "R1" && v.Veicolo == VeicoloAltro && !v.IsArchiviata);
        Assert.Contains(righe, v => v.Componente == EcuAltra && v.RequisitoId == "R1" && v.Veicolo == VeicoloTarget && !v.IsArchiviata);
        Assert.Contains(righe, v => v.Componente == EcuTarget && v.RequisitoId == "R3" && v.Veicolo == VeicoloTarget && !v.IsArchiviata);
        Assert.Equal(improntaTest, TestCaseFingerprint.Compute(test));
        Assert.Equal(improntaLegacy, EntityStateFingerprint.Compute(risoluzione));
    }

    [Fact]
    public void RimuoviValutazioniColonnaAudit_FonteTestCambiataDopoAnteprima_FallisceSenzaScrivere()
    {
        using var workspace = new TempWorkspace();
        var repo = CreaRepository(workspace);
        var componenti = new[] { EcuDi(EcuTarget, VeicoloTarget) };
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", EcuTarget, new[] { VeicoloTarget }, PunteggioConformita.Conforme,
            "non rimuovere se cambia il perimetro", "QA", null, out var errore), errore);
        var requisiti = new[] { Requisito("R1") };
        var tests = Array.Empty<TestCase>();
        var risoluzioni = Array.Empty<RisoluzioneConflittoAudit>();
        var anteprima = ValutazioneRequisitoEcuVeicoloService.CatturaRimozioneValutazioniColonnaAudit(
            EcuTarget, VeicoloTarget, componenti, requisiti, tests, risoluzioni, repo.Load());
        var testNuovo = TestDiretto("TC-COLUMN-NEW", EcuTarget, "R1", VeicoloTarget);

        var salvata = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazioniColonnaAudit(
            repo, anteprima, componenti, requisiti, tests, risoluzioni, "QA", out var rimosse, out errore,
            () => new FontiRimozioneValutazioniColonnaAudit(componenti, requisiti, new[] { testNuovo }, risoluzioni));

        Assert.False(salvata);
        Assert.Contains("cambiata", errore, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, rimosse);
        var riga = Assert.Single(repo.Load());
        Assert.False(riga.IsArchiviata);
        Assert.Empty(riga.Storico);
    }

    [Fact]
    public void RimuoviValutazioniColonnaAudit_AssociazioneEcuCambiataDopoAnteprima_FallisceSenzaScrivere()
    {
        using var workspace = new TempWorkspace();
        var repo = CreaRepository(workspace);
        var componenti = new[] { EcuDi(EcuTarget, VeicoloTarget) };
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", EcuTarget, new[] { VeicoloTarget }, PunteggioConformita.Conforme,
            "non rimuovere se cambia l'associazione", "QA", null, out var errore), errore);
        var requisiti = new[] { Requisito("R1") };
        var tests = Array.Empty<TestCase>();
        var risoluzioni = Array.Empty<RisoluzioneConflittoAudit>();
        var anteprima = ValutazioneRequisitoEcuVeicoloService.CatturaRimozioneValutazioniColonnaAudit(
            EcuTarget, VeicoloTarget, componenti, requisiti, tests, risoluzioni, repo.Load());
        var ecusRiassegnate = new[] { EcuDi(EcuTarget, VeicoloAltro) };

        var salvata = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazioniColonnaAudit(
            repo, anteprima, componenti, requisiti, tests, risoluzioni, "QA", out var rimosse, out errore,
            () => new FontiRimozioneValutazioniColonnaAudit(ecusRiassegnate, requisiti, tests, risoluzioni));

        Assert.False(salvata);
        Assert.Contains("cambiata", errore, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, rimosse);
        var riga = Assert.Single(repo.Load());
        Assert.False(riga.IsArchiviata);
        Assert.Empty(riga.Storico);
    }

    [Fact]
    public void RimuoviValutazioniColonnaAudit_ConcorrenzaDuranteCommit_FallisceSenzaScritturaParziale()
    {
        using var workspace = new TempWorkspace();
        var repo = CreaRepository(workspace);
        var componenti = new[] { EcuDi(EcuTarget, VeicoloTarget) };
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R1", EcuTarget, new[] { VeicoloTarget }, PunteggioConformita.Conforme,
            "prima", "QA", null, out var errore), errore);
        Assert.True(ValutazioneRequisitoEcuVeicoloService.ImpostaManuale(
            repo, "R2", EcuTarget, new[] { VeicoloTarget }, PunteggioConformita.NonConforme,
            "seconda", "QA", null, out errore), errore);
        var requisiti = new[] { Requisito("R1"), Requisito("R2") };
        var tests = Array.Empty<TestCase>();
        var risoluzioni = Array.Empty<RisoluzioneConflittoAudit>();
        var anteprima = ValutazioneRequisitoEcuVeicoloService.CatturaRimozioneValutazioniColonnaAudit(
            EcuTarget, VeicoloTarget, componenti, requisiti, tests, risoluzioni, repo.Load());

        var salvata = ValutazioneRequisitoEcuVeicoloService.RimuoviValutazioniColonnaAudit(
            repo, anteprima, componenti, requisiti, tests, risoluzioni, "QA", out var rimosse, out errore,
            () =>
            {
                var concorrente = CreaRepository(workspace);
                var correnti = concorrente.Load();
                var baseline = EntityStateFingerprint.Clone(correnti);
                correnti.Single(v => v.RequisitoId == "R2").Nota = "modificata da altra postazione";
                concorrente.Save(correnti, baseline);
                return new FontiRimozioneValutazioniColonnaAudit(componenti, requisiti, tests, risoluzioni);
            });

        Assert.False(salvata);
        Assert.NotNull(errore);
        Assert.Equal(0, rimosse);
        var dopoConcorrenza = repo.Load();
        Assert.All(dopoConcorrenza, v => Assert.False(v.IsArchiviata));
        Assert.All(dopoConcorrenza, v => Assert.Empty(v.Storico));
        Assert.Equal("modificata da altra postazione", Assert.Single(dopoConcorrenza, v => v.RequisitoId == "R2").Nota);
    }

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "TestLoom_ValReqEcuColumn_" + Guid.NewGuid().ToString("N"));
        public DataPaths Paths { get; }

        public TempWorkspace()
        {
            Paths = new DataPaths(Root);
            Paths.EnsureStructure();
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
