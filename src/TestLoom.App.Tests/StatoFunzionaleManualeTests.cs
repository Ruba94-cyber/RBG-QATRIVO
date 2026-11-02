using System.Security.Cryptography;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Correzione manuale dello stato del Report (6.6.1, fase 2): precedenza fra valore manuale ed esiti dei Test ("vince il
/// piu' recente"), "!" per le combinazioni propagabili non decise (propaga, valore a mano, resta "!" se ignorato), permessi
/// (solo Master), nota obbligatoria, risoluzione delle collisioni fra postazioni e additivita' del catalogo per un client precedente.</summary>
public sealed class StatoFunzionaleManualeTests : IDisposable
{
    private const string Funz = "Lettura guasto";
    private const string Componente = "BCM";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_StatoFunz_" + Guid.NewGuid().ToString("N"));
    private readonly DataPaths _paths;

    public StatoFunzionaleManualeTests()
    {
        _paths = new DataPaths(_dir);
        _paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private MasterDataRepository<StatoFunzionalitaEcuVeicolo> Repo() => new(_paths.StatiFunzionalitaEcuVeicoloFile);

    private static TestCase Test(string id, string veicolo, Esito esito, DateTime data)
    {
        var tc = new TestCase { Id = id, Componente = new EcuSelection([Componente]), Veicoli = [veicolo], Funzionalita = [Funz] };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = data,
            Condizioni = [new Condizione { Funzionalita = Funz, Esito = esito, Componente = new EcuSelection([Componente]) }],
        });
        return tc;
    }

    private static StatoFunzionalitaEcuVeicolo Manuale(string veicolo, Esito esito, DateTime data, string nota = "motivazione") => new()
    {
        Funzionalita = Funz, Componente = Componente, Veicolo = veicolo, Esito = esito, Origine = OrigineStatoFunzionalita.Manuale,
        Nota = nota, Operatore = "Master", DataDecisione = data, UltimaModifica = data,
        Uid = Guid.NewGuid(),
    };

    private static ConfigurazioneMatriceReport ConfigStato() => new()
    {
        CampiX = [DimensioniMatrice.Componente],
        CampiY = [DimensioniMatrice.Funzionalita],
        ModalitaCella = ModalitaCellaMatrice.Stato,
    };

    private static Componente CatalogoBcm(params string[] veicoli) => new() { Nome = Componente, VeicoliAssociati = veicoli.ToList() };

    private static Veicolo V(string nome) => new() { Nome = nome, GammaNome = "LCV" };

    private static Funzionalita F() => new() { Nome = Funz, Categorie = ["Diagnostica"] };

    private static CellaMatriceReport Cella(RisultatoMatriceReport r) => r.Righe.Single().Celle.Single();

    [Fact]
    public void Precedenza_manualePiuRecenteDiUnNonConformeVince()
    {
        var tests = new[] { Test("T-1", "Daily A", Esito.NonConforme, new DateTime(2026, 1, 1)) };
        var manuali = new[] { Manuale("Daily A", Esito.Conforme, new DateTime(2026, 1, 10)) };
        var cella = Cella(ReportMatriceService.Genera(tests, ConfigStato(), [V("Daily A")], [F()], [], null, null, manuali));
        Assert.Equal(StatoCellaMatrice.Conforme, cella.CodiceStato);
        Assert.Equal(1, cella.Manuali);
        Assert.Equal(0, cella.ManualiSuperati);
    }

    [Fact]
    public void Precedenza_testPiuRecenteSuperaIlManuale()
    {
        var tests = new[]
        {
            Test("T-1", "Daily A", Esito.Conforme, new DateTime(2026, 2, 1)),
        };
        var manuali = new[] { Manuale("Daily A", Esito.NonConforme, new DateTime(2026, 1, 2)) };
        var cella = Cella(ReportMatriceService.Genera(tests, ConfigStato(), [V("Daily A")], [F()], [], null, null, manuali));
        Assert.Equal(StatoCellaMatrice.Conforme, cella.CodiceStato);
        Assert.Equal(0, cella.Manuali);
        Assert.Equal(1, cella.ManualiSuperati);
        Assert.Contains("superato", cella.TestoStato);
    }

    [Fact]
    public void Precedenza_aPariDataVinceIlManuale()
    {
        var data = new DateTime(2026, 3, 1, 9, 0, 0);
        var tests = new[] { Test("T-1", "Daily A", Esito.Conforme, data) };
        var manuali = new[] { Manuale("Daily A", Esito.NonConforme, data) };
        var cella = Cella(ReportMatriceService.Genera(tests, ConfigStato(), [V("Daily A")], [F()], [], null, null, manuali));
        Assert.Equal(StatoCellaMatrice.NonConforme, cella.CodiceStato);
        Assert.Equal(1, cella.Manuali);
    }

    [Fact]
    public void Precedenza_daEseguirePiuRecenteNonSovrascriveIlManuale()
    {
        var tests = new[] { Test("T-2", "Daily A", Esito.DaEseguire, new DateTime(2026, 4, 1)) };
        var manuali = new[] { Manuale("Daily A", Esito.Conforme, new DateTime(2026, 3, 1)) };
        var cella = Cella(ReportMatriceService.Genera(tests, ConfigStato(), [V("Daily A")], [F()], [], null, null, manuali));
        Assert.Equal(StatoCellaMatrice.Conforme, cella.CodiceStato);
        Assert.Equal(1, cella.Manuali);
    }

    [Fact]
    public void Punto_propagabileSenzaDecisioneEMostratoComeDaDecidere()
    {
        // Esito su Daily A; Daily B della stessa componente non ha Test: il "!" resta finche' un Master non decide.
        var tests = new[] { Test("T-1", "Daily A", Esito.Conforme, new DateTime(2026, 1, 1)) };
        var catalogo = new[] { CatalogoBcm("Daily A", "Daily B") };
        var veicoli = new[] { V("Daily A"), V("Daily B") };
        var r = ReportMatriceService.Genera(tests, ConfigStato(), veicoli, [F()], [], null, catalogo);
        var cella = Cella(r);
        Assert.Equal(1, cella.Stati.Ok);
        Assert.Equal(1, cella.Stati.DaDecidere);
        Assert.Equal(StatoCellaMatrice.Misto, cella.CodiceStato);

        var soloB = ConfigStato();
        soloB.Filtri[DimensioniMatrice.Sottoveicolo] = ["Daily B"];
        Assert.Equal(StatoCellaMatrice.DaDecidere, Cella(ReportMatriceService.Genera(tests, soloB, veicoli, [F()], [], null, catalogo)).CodiceStato);
    }

    [Fact]
    public void Punto_senzaFonteEsitoResteNonTestato()
    {
        // Nessun veicolo della stessa componente ha un esito eseguito: niente "!", resta Non testato.
        var catalogo = new[] { CatalogoBcm("Daily A", "Daily B") };
        var veicoli = new[] { V("Daily A"), V("Daily B") };
        var cella = Cella(ReportMatriceService.Genera([], ConfigStato(), veicoli, [F()], [], null, catalogo));
        Assert.Equal(StatoCellaMatrice.NonTestato, cella.CodiceStato);
        Assert.Equal(0, cella.Stati.DaDecidere);
    }

    [Fact]
    public void Punto_risoltoDaUnValoreManualeNonPiuPunto()
    {
        var tests = new[] { Test("T-1", "Daily A", Esito.Conforme, new DateTime(2026, 1, 1)) };
        var catalogo = new[] { CatalogoBcm("Daily A", "Daily B") };
        var veicoli = new[] { V("Daily A"), V("Daily B") };
        var manuali = new[] { Manuale("Daily B", Esito.NonConforme, new DateTime(2026, 2, 1)) };
        var soloB = ConfigStato();
        soloB.Filtri[DimensioniMatrice.Sottoveicolo] = ["Daily B"];
        var cella = Cella(ReportMatriceService.Genera(tests, soloB, veicoli, [F()], [], null, catalogo, manuali));
        Assert.Equal(StatoCellaMatrice.NonConforme, cella.CodiceStato);
        Assert.Equal(0, cella.Stati.DaDecidere);
    }

    [Fact]
    public void Punto_propagaASottoinsiemeLasciaGliAltriAPunto()
    {
        var repo = Repo();
        var fonte = new FonteStatoPropagato("Daily A", "T-1", Esito.Conforme);
        var ok = StatoFunzionaleManualeService.Propaga(repo, Funz, Componente, [("Daily B", fonte)], "propagato", "Master", true, out var errore);
        Assert.True(ok, errore);
        var valori = repo.Load();
        Assert.Single(valori);
        Assert.Equal(OrigineStatoFunzionalita.Propagata, valori[0].Origine);
        Assert.Equal("Daily A", valori[0].SorgenteVeicolo);
        // Daily C e' propagabile ma non scelto: resta "!".
        var catalogo = new[] { CatalogoBcm("Daily A", "Daily B", "Daily C") };
        var veicoli = new[] { V("Daily A"), V("Daily B"), V("Daily C") };
        var tests = new[] { Test("T-1", "Daily A", Esito.Conforme, new DateTime(2026, 1, 1)) };
        var perC = ConfigStato();
        perC.Filtri[DimensioniMatrice.Sottoveicolo] = ["Daily C"];
        Assert.Equal(StatoCellaMatrice.DaDecidere, Cella(ReportMatriceService.Genera(tests, perC, veicoli, [F()], [], null, catalogo, valori)).CodiceStato);
        var perB = ConfigStato();
        perB.Filtri[DimensioniMatrice.Sottoveicolo] = ["Daily B"];
        Assert.Equal(StatoCellaMatrice.Conforme, Cella(ReportMatriceService.Genera(tests, perB, veicoli, [F()], [], null, catalogo, valori)).CodiceStato);
    }

    [Fact]
    public void Permessi_operatoreNonPuoCorreggere()
    {
        var repo = Repo();
        var fonte = new FonteStatoPropagato("Daily A", "T-1", Esito.Conforme);
        Assert.False(StatoFunzionaleManualeService.ImpostaManuale(repo, Funz, Componente, ["Daily B"], Esito.Conforme, "nota", "Operatore", false, out var e1));
        Assert.Equal(StatoFunzionaleManualeService.ErrorePermessoNegato, e1);
        Assert.False(StatoFunzionaleManualeService.Propaga(repo, Funz, Componente, [("Daily B", fonte)], "nota", "Operatore", false, out var e2));
        Assert.Equal(StatoFunzionaleManualeService.ErrorePermessoNegato, e2);
        Assert.False(StatoFunzionaleManualeService.RimuoviManuale(repo, Funz, Componente, "Daily B", false, out var e3));
        Assert.Equal(StatoFunzionaleManualeService.ErrorePermessoNegato, e3);
        Assert.Empty(repo.Load());
    }

    [Fact]
    public void NotaObbligatoriaPerOgniDecisione()
    {
        var repo = Repo();
        Assert.False(StatoFunzionaleManualeService.ImpostaManuale(repo, Funz, Componente, ["Daily B"], Esito.Conforme, "  ", "Master", true, out var errore));
        Assert.Equal(StatoFunzionaleManualeService.ErroreNotaOppureVeicoli, errore);
        Assert.Empty(repo.Load());
    }

    [Fact]
    public void ValoreManualeDevEssereEseguito()
    {
        var repo = Repo();
        Assert.False(StatoFunzionaleManualeService.ImpostaManuale(repo, Funz, Componente, ["Daily B"], Esito.DaEseguire, "nota", "Master", true, out var errore));
        Assert.Equal(StatoFunzionaleManualeService.ErroreEsitoNonEseguito, errore);
    }

    [Fact]
    public void MultiPc_collisioneRisoltaDalPiuRecente()
    {
        // Due postazioni hanno scritto la stessa combinazione con UID diverse: la piu' recente vince, lo scarto resta nel changelog.
        var vecchia = Manuale("Daily B", Esito.Conforme, new DateTime(2026, 5, 1, 10, 0, 0));
        var recente = Manuale("Daily B", Esito.NonConforme, new DateTime(2026, 5, 2, 10, 0, 0));
        var lista = new List<StatoFunzionalitaEcuVeicolo> { vecchia, recente };
        var rimosse = StatoFunzionaleManualeService.ConsolidaCollisioni(lista, "Master");
        Assert.Equal(1, rimosse);
        Assert.Single(lista);
        Assert.Equal(Esito.NonConforme, lista[0].Esito);
        Assert.Contains(lista[0].Changelog, c => c.Campo.Contains("consolidamento"));
    }

    [Fact]
    public void MultiPc_stessaDataVinceUidMinore()
    {
        var data = new DateTime(2026, 5, 3);
        var a = Manuale("Daily B", Esito.Conforme, data);
        var b = Manuale("Daily B", Esito.NonConforme, data);
        var attesa = string.CompareOrdinal(a.Uid.ToString("N"), b.Uid.ToString("N")) < 0 ? a : b;
        var lista = new List<StatoFunzionalitaEcuVeicolo> { a, b };
        StatoFunzionaleManualeService.ConsolidaCollisioni(lista, "Master");
        Assert.Equal(attesa.Uid, lista[0].Uid);
    }

    [Fact]
    public void ClientPrecedente_nonPerdeNeSovrascriveIlCatalogoAggiunto()
    {
        // Il catalogo nuovo e' un file a parte: salvare un catalogo gia' esistente non deve toccarlo.
        var repo = Repo();
        Assert.True(StatoFunzionaleManualeService.ImpostaManuale(repo, Funz, Componente, ["Daily B"], Esito.Conforme, "nota", "Master", true, out _));
        var prima = Serializza(repo.Load());
        var componente = new MasterDataRepository<Componente>(_paths.EcuFile);
        componente.Save([CatalogoBcm("Daily B")]);
        var catalogoEcu = componente.Load();
        catalogoEcu[0].VeicoliAssociati.Add("Daily C"); // stessa componente (stessa chiave di business): si modifica, non si duplica
        componente.Save(catalogoEcu);
        Assert.Equal(prima, Serializza(repo.Load()));
        Assert.Single(repo.Load());
    }

    [Fact]
    public void Cella_portaLaNotaDelValoreManualeCheDeterminaLoStato()
    {
        var tests = new[] { Test("T-1", "Daily A", Esito.NonConforme, new DateTime(2026, 1, 1)) };
        var manuali = new[] { Manuale("Daily A", Esito.Conforme, new DateTime(2026, 1, 10), "verificato sul banco") };
        var cella = Cella(ReportMatriceService.Genera(tests, ConfigStato(), [V("Daily A")], [F()], [], null, null, manuali));
        Assert.Equal("verificato sul banco", cella.NotaManuale);
    }

    [Fact]
    public void Origine_unaRigaPerValoreManualeConDeterminaLoStato()
    {
        var tests = new[] { Test("T-1", "Daily A", Esito.NonConforme, new DateTime(2026, 1, 1)) };
        var manuali = new[] { Manuale("Daily A", Esito.Conforme, new DateTime(2026, 1, 10), "motivazione prova") };
        var (righe, _, _) = ReportMatriceService.Origine(tests, ConfigStato(), [V("Daily A")], [F()], [], 100, null, manuali);
        var riga = righe.Single(r => r.TestId.StartsWith(ReportMatriceService.ManualePrefisso, StringComparison.Ordinal));
        Assert.Contains("Manuale:", riga.Descrizione);
        Assert.Contains("motivazione prova", riga.Descrizione);
        Assert.True(riga.DeterminaStato);
    }

    private static string Serializza(IReadOnlyList<StatoFunzionalitaEcuVeicolo> valori) =>
        System.Text.Json.JsonSerializer.Serialize(valori.Select(v => new { v.Uid, v.Funzionalita, v.Componente, v.Veicolo, v.Esito, v.Nota, v.Operatore }));
}
