using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Chiusura di un'anomalia cambiando gli esiti del test collegato: nasce una REV n+1 con Origine
/// (Rete / RieseguitoDaNoi), la REV n resta intatta, Test e NC si salvano in modo atomico.</summary>
[Collection("Localization")]
public sealed class AnomaliaChiusuraNuovaRevTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_AnomChiusura_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Adesso = new(2026, 10, 6, 12, 0, 0);

    public void Dispose() { if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } } }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    private static (Guid Cond1, Guid Cond2) CreaTestENc(AppState state)
    {
        var c1 = new Condizione { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Diagnosi", Esito = Esito.NonConforme };
        var c2 = new Condizione { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Lettura", Esito = Esito.Conforme };
        var rev = new Esecuzione
        {
            Iterazione = 1, Data = new DateTime(2026, 9, 1), Operatore = "Mario", Matricola = "Matricola1",
            Condizioni = { c1, c2 }
        };
        rev.SincronizzaEsito();
        var tc = new TestCase
        {
            Id = "T-1", Componente = { "BCM" }, Veicolo = "V1", Veicoli = { "V1" },
            Assegnatario = "Luigi", StatoLavoro = StatoLavoroTest.InCorso,
            Esecuzioni = new List<Esecuzione> { rev }
        };
        state.TestCases.Save(tc, isNew: true);
        var nc = new NonConformita
        {
            Id = "NC-0001", TestCaseId = "T-1", EsecuzioneIterazione = 1, Owner = "Mario", Descrizione = "KO",
            ProveCollegate = { new RiferimentoProva { TestId = "T-1", EsecuzioneIterazione = 1, CondizioneUid = c1.Uid } }
        };
        state.NonConformita.Save(nc);
        return (c1.Uid, c2.Uid);
    }

    private static string Json(object o) => JsonSerializer.Serialize(o, JsonIo.CompactOptions);

    [Theory]
    [InlineData(OrigineRevisione.Rete)]
    [InlineData(OrigineRevisione.RieseguitoDaNoi)]
    public void ChiudiESalva_CreaRevNPiuUnoConOrigine_StoricaIdentica_AssegnatarioInvariato(OrigineRevisione origine)
    {
        var state = CreaStato();
        var (c1, _) = CreaTestENc(state);
        var rev1Prima = Json(state.TestCases.GetById("T-1")!.Esecuzioni[0]);

        var r = AnomaliaChiusuraService.ChiudiESalva(state, "T-1", "NC-0001",
            new Dictionary<Guid, Esito> { [c1] = Esito.Conforme }, StatoLavoroTest.Concluso,
            origine, "Op Prova", Adesso, "Risolto");

        Assert.NotNull(r.NuovaRev);
        var tc = state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!;
        Assert.Equal(2, tc.Esecuzioni.Count);
        Assert.Equal(rev1Prima, Json(tc.Esecuzioni[0]));
        var rev2 = tc.Esecuzioni[1];
        Assert.Equal(2, rev2.Iterazione);
        Assert.Equal(origine, rev2.Origine);
        Assert.Equal("NC-0001", rev2.NonConformitaOrigineId);
        Assert.Equal("Op Prova", rev2.Operatore);
        Assert.Equal(Adesso, rev2.Data);
        Assert.Equal(Esito.Conforme, rev2.Esito);              // esito ricalcolato (era NonConforme)
        Assert.Equal(Esito.NonConforme, tc.Esecuzioni[0].Esito);
        Assert.Equal(3, rev2.Condizioni.Count);                // nuovo tentativo, nulla mutato
        Assert.Equal(c1, rev2.Condizioni[0].Uid);
        Assert.Equal(Esito.NonConforme, rev2.Condizioni[0].Esito);
        Assert.Equal(c1, rev2.Condizioni[1].OperazioneOrigineUid);
        Assert.Equal("Luigi", tc.Assegnatario);
        Assert.Equal(StatoLavoroTest.Concluso, tc.StatoLavoro);
        Assert.Equal(origine != OrigineRevisione.Rete, rev2.EsecuzioneDaOperatore);

        var nc = state.NonConformita.GetById("NC-0001")!;
        Assert.Equal(StatoAnomalia.Conclusa, nc.StatoWorkflow);
        Assert.Equal("Risolto", nc.MotivoChiusura);
        Assert.Contains(nc.Changelog, c => c.Campo == "Chiusura");
    }

    [Fact]
    public void NessunEsitoNeStatoCambiato_NessunaRev_SoloChiusuraNc()
    {
        var state = CreaStato();
        var (c1, c2) = CreaTestENc(state);
        var revTest = state.TestCases.GetById("T-1")!.RevisioneDati;

        var r = AnomaliaChiusuraService.ChiudiESalva(state, "T-1", "NC-0001",
            new Dictionary<Guid, Esito> { [c1] = Esito.NonConforme, [c2] = Esito.Conforme }, StatoLavoroTest.InCorso,
            OrigineRevisione.Rete, "Op Prova", Adesso, "Chiusa senza modifiche");

        Assert.Null(r.NuovaRev);
        var tc = state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!;
        Assert.Single(tc.Esecuzioni);
        Assert.Equal(revTest, tc.RevisioneDati);
        Assert.Equal(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
    }

    [Fact]
    public void MotivoObbligatorio_NulloSalvatoENcApertaResta()
    {
        var state = CreaStato();
        var (c1, _) = CreaTestENc(state);

        Assert.Throws<ArgumentException>(() => AnomaliaChiusuraService.ChiudiESalva(state, "T-1", "NC-0001",
            new Dictionary<Guid, Esito> { [c1] = Esito.Conforme }, null, OrigineRevisione.Rete, "Op Prova", Adesso, "  "));

        Assert.Single(state.TestCases.GetById("T-1")!.Esecuzioni);
        Assert.NotEqual(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
    }

    [Fact]
    public void ConflittoSuTest_NcNonChiusa()
    {
        var state = CreaStato();
        var (c1, _) = CreaTestENc(state);
        var revVista = state.TestCases.GetById("T-1")!.RevisioneDati;
        var tc = state.TestCases.GetById("T-1")!;
        tc.ModificatoDa = "toccato";
        state.TestCases.Save(tc, false, revVista); // qualcun altro avanza la RevisioneDati

        Assert.Throws<ConflittoRevisioneException>(() => AnomaliaChiusuraService.ChiudiESalva(state, "T-1", "NC-0001",
            new Dictionary<Guid, Esito> { [c1] = Esito.Conforme }, null, OrigineRevisione.Rete, "Op Prova", Adesso, "x",
            revisioneTestAttesa: revVista));

        Assert.Single(state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!.Esecuzioni);
        Assert.NotEqual(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-0001")!.StatoWorkflow);
    }

    [Fact]
    public void ConflittoSuNcDopoSaveTest_RollbackDelTest()
    {
        var stato = CreaStato();
        CreaTestENc(stato);
        var c1 = stato.TestCases.GetById("T-1")!.Esecuzioni[0].Condizioni[0].Uid;

        // Un altro PC (AppState indipendente, stessa cartella) avanza la NC: la cache dello stato "stale" non lo sa.
        var altro = TestStateFactory.Create();
        altro.InizializzaCartella(_dir);
        var ncAltro = altro.NonConformita.GetById("NC-0001")!;
        ncAltro.Descrizione = "modificata altrove";
        altro.NonConformita.Save(ncAltro, ncAltro.RevisioneDati);

        var ex = Assert.ThrowsAny<IOException>(() => AnomaliaChiusuraService.ChiudiESalva(stato, "T-1", "NC-0001",
            new Dictionary<Guid, Esito> { [c1] = Esito.Conforme }, StatoLavoroTest.Concluso,
            OrigineRevisione.Rete, "Op Prova", Adesso, "Risolto"));

        Assert.IsType<ConflittoRevisioneException>(ex);
        // Verifica su disco con uno stato nuovo: Test ripristinato (1 REV, stato lavoro originale), NC non chiusa.
        var verifica = TestStateFactory.Create();
        verifica.InizializzaCartella(_dir);
        var file = Directory.EnumerateFiles(_dir, "testcase_T-1.json", SearchOption.AllDirectories).Single();
        var daDisco = JsonSerializer.Deserialize<TestCase>(File.ReadAllText(file), JsonIo.Options)!;
        Assert.Single(daDisco.Esecuzioni);
        Assert.Single(stato.TestCases.GetById("T-1")!.Esecuzioni); // cache in-process allineata al rollback
        var tc = verifica.TestCases.GetById("T-1")!;
        Assert.Single(tc.Esecuzioni);
        Assert.Equal(StatoLavoroTest.InCorso, tc.StatoLavoro);
        Assert.NotEqual(StatoAnomalia.Conclusa, verifica.NonConformita.GetById("NC-0001")!.StatoWorkflow);
    }

    [Fact]
    public void ModificaInPlaceDellOrigineDiUnaRevStorica_Bloccata()
    {
        var state = CreaStato();
        var (c1, _) = CreaTestENc(state);
        AnomaliaChiusuraService.ChiudiESalva(state, "T-1", "NC-0001",
            new Dictionary<Guid, Esito> { [c1] = Esito.Conforme }, null, OrigineRevisione.Rete, "Op Prova", Adesso, "ok");

        var copia = EntityStateFingerprint.Clone(state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!);
        copia.Esecuzioni[1].Origine = OrigineRevisione.Operatore;
        copia.Esecuzioni.Add(new Esecuzione { Iterazione = 3 });

        Assert.Throws<InvalidDataException>(() => state.TestCases.Save(copia, false, copia.RevisioneDati));
    }

    [Fact]
    public void ModificaInPlaceDellOrigineDellaRevCorrente_CreaNuovaRevPreservandoLaStorica()
    {
        var persisted = new TestCase { Id = "T", Esecuzioni = new() { new Esecuzione { Iterazione = 1, Origine = OrigineRevisione.Rete } } };
        var candidate = EntityStateFingerprint.Clone(persisted);
        candidate.Esecuzioni[0].Origine = OrigineRevisione.Operatore;

        Assert.True(TestRevisionSafety.PreserveImmutableHistory(persisted, candidate, Adesso, "Op"));
        Assert.Equal(2, candidate.Esecuzioni.Count);
        Assert.Equal(OrigineRevisione.Rete, candidate.Esecuzioni[0].Origine);
    }

    [Fact]
    public void JsonLegacySenzaOrigine_LeggeOperatore_RoundTripConserva()
    {
        var legacy = "{\"Iterazione\":2,\"Operatore\":\"Mario\",\"Condizioni\":[]}";
        var rev = JsonSerializer.Deserialize<Esecuzione>(legacy, JsonIo.Options)!;
        Assert.Equal(OrigineRevisione.Operatore, rev.Origine);
        Assert.Equal("", rev.NonConformitaOrigineId);
        Assert.True(rev.EsecuzioneDaOperatore);

        rev.Origine = OrigineRevisione.RieseguitoDaNoi;
        rev.NonConformitaOrigineId = "NC-0009";
        var json = JsonSerializer.Serialize(rev, JsonIo.Options);
        Assert.Contains("\"RieseguitoDaNoi\"", json);
        var back = JsonSerializer.Deserialize<Esecuzione>(json, JsonIo.Options)!;
        Assert.Equal(OrigineRevisione.RieseguitoDaNoi, back.Origine);
        Assert.Equal("NC-0009", back.NonConformitaOrigineId);
    }

    [Fact]
    public async Task RevConOverride_LaNuovaRevRicalcolaEsitoEAzzeraOverride_EditorPuoSalvare()
    {
        var state = CreaStato();
        var c1 = new Condizione { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Diagnosi", Esito = Esito.NonConforme, DescrizioneProblema = "KO originale" };
        var c2 = new Condizione { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Lettura", Esito = Esito.Conforme };
        var rev = new Esecuzione
        {
            Iterazione = 1, Data = new DateTime(2026, 9, 1), Operatore = "Mario", Matricola = "Matricola1",
            Condizioni = { c1, c2 },
            EsitoSovrascritto = true, Esito = Esito.Conforme, MotivazioneOverride = "forzato", Deviazioni = "dev", Note = "nota rev", NonConformitaId = "NC-0001",
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.Conforme, Sovrascritto = true, MotivazioneOverride = "x" } }
        };
        var tc = new TestCase
        {
            Id = "T-1", Componente = { "BCM" }, Veicolo = "V1", Veicoli = { "V1" }, Assegnatario = "Luigi", StatoLavoro = StatoLavoroTest.InCorso,
            Funzionalita = { "Diagnosi", "Lettura" }, DescrizioneBreve = "d", Descrizione = "d", RequisitoId = { "R1" },
            Esecuzioni = new List<Esecuzione> { rev }
        };
        state.TestCases.Save(tc, isNew: true);
        state.NonConformita.Save(new NonConformita { Id = "NC-0001", TestCaseId = "T-1", EsecuzioneIterazione = 1, Owner = "Mario", Descrizione = "KO",
            ProveCollegate = { new RiferimentoProva { TestId = "T-1", EsecuzioneIterazione = 1, CondizioneUid = c1.Uid } } });

        // c1 passa a N.A. e c2 a Conforme con deviazioni: due nuovi tentativi con esiti che richiedono campi dedicati.
        AnomaliaChiusuraService.ChiudiESalva(state, "T-1", "NC-0001",
            new Dictionary<Guid, Esito> { [c1.Uid] = Esito.NonApplicabile, [c2.Uid] = Esito.ConformeConDeviazioni }, null,
            OrigineRevisione.Rete, "Op Prova", Adesso, "Risolto in rete");

        var salvato = state.TestCases.LoadCurrent(state.TestCases.GetById("T-1")!)!;
        var rev2 = salvato.Esecuzioni[1];
        Assert.False(rev2.EsitoSovrascritto);
        Assert.Equal("", rev2.MotivazioneOverride);
        Assert.Equal("", rev2.Deviazioni);
        Assert.Equal("", rev2.Note);
        Assert.Null(rev2.NonConformitaId);
        Assert.All(rev2.PunteggiRequisiti, p => Assert.False(p.Sovrascritto));
        Assert.Equal(Esito.ConformeConDeviazioni, rev2.Esito); // ricalcolato dalle condizioni, non ereditato (Conforme forzato)
        var nuovi = rev2.Condizioni.Where(c => c.OperazioneOrigineUid != null).ToList();
        Assert.Equal(2, nuovi.Count);
        Assert.All(nuovi, c => Assert.Contains("NC-0001", c.Nota));
        Assert.False(string.IsNullOrWhiteSpace(nuovi.Single(c => c.Esito == Esito.NonApplicabile).MotivazioneNonApplicabile));
        Assert.False(string.IsNullOrWhiteSpace(nuovi.Single(c => c.Esito == Esito.ConformeConDeviazioni).DescrizioneDeviazione));
        // La REV 1 storica non e toccata (override incluso).
        Assert.True(salvato.Esecuzioni[0].EsitoSovrascritto);

        // Validazione reale dell'editor: nessun blocco per-operazione sul primo Salva.
        var vm = new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.CampiObbligatoriMancanti, c => c.Chiave == CampiSistema.OperazioneCondizione);
    }

    [Fact]
    public void NonConformitaRepository_RefreshPaths_RiallineaCacheAlFileRipristinato()
    {
        var state = CreaStato();
        CreaTestENc(state);
        var nc = state.NonConformita.GetById("NC-0001")!;
        var percorso = state.NonConformita.FilePathOf(nc);
        var contenutoOriginale = File.ReadAllBytes(percorso);
        var revOriginale = nc.RevisioneDati;

        // Simula Save in cache + rollback del file (come fa la Unit of Work): cache avanti, disco indietro.
        var copia = EntityStateFingerprint.Clone(nc);
        copia.Descrizione = "scritta e poi annullata";
        state.NonConformita.Save(copia, revOriginale);
        Assert.Equal("scritta e poi annullata", state.NonConformita.GetById("NC-0001")!.Descrizione);
        File.WriteAllBytes(percorso, contenutoOriginale);

        state.NonConformita.RefreshPaths(new[] { percorso });

        var dopo = state.NonConformita.GetById("NC-0001")!;
        Assert.Equal("KO", dopo.Descrizione);
        Assert.Equal(revOriginale, dopo.RevisioneDati);
        // Nessun falso conflitto sul salvataggio successivo.
        var successiva = EntityStateFingerprint.Clone(dopo);
        successiva.Descrizione = "ok";
        state.NonConformita.Save(successiva, dopo.RevisioneDati);
    }
}
