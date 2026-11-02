using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Equivalenza della proiezione Audit/Report (LocalTestArchiveQueryService.QueryAllAnalisi) con il percorso completo (QueryAll):
/// stesso indice, stessi filtri, stesso ordine. Il dataset e' ricco (revisioni, condizioni con guasto e ripetizioni, punteggi per
/// requisito, cestino, campi custom, componente con maiuscole diverse e spazi, veicoli multipli) e contiene anche i campi pesanti che la
/// proiezione non legge (Changelog, Dettagli, valori strumenti): il risultato di matrice Report, celle Audit e anteprima deve essere
/// identico, e i campi proiettati devono coincidere uno a uno.
/// </summary>
public sealed class ProiezioneAnaliticaEquivalenzaTests : IClassFixture<ProiezioneAnaliticaEquivalenzaTests.DatasetProiezione>
{
    private readonly DatasetProiezione _dataset;

    public ProiezioneAnaliticaEquivalenzaTests(DatasetProiezione dataset) => _dataset = dataset;

    public static IEnumerable<object[]> Filtri() => new[]
    {
        new object[] { "tutti" },
        new object[] { "componente-a" },
        new object[] { "componente-selettiva" },
        new object[] { "componente-rara" },
        new object[] { "veicolo" },
        new object[] { "template" },
        new object[] { "campo-custom" },
        new object[] { "cestino" },
        new object[] { "data" },
    };

    [Theory]
    [MemberData(nameof(Filtri))]
    public void Proiezione_CampiLetti_UgualiAlTestCompleto(string filtro)
    {
        var servizio = new LocalTestArchiveQueryService(_dataset.DatabasePath);
        var query = QueryDi(filtro, _dataset.TemplateUid);

        var completi = servizio.QueryAll(query);
        var proiettati = servizio.QueryAllAnalisi(query);

        Assert.True(completi.Count > 0, $"Il filtro '{filtro}' non deve essere vuoto nel dataset di prova.");
        Assert.Equal(completi.Count, proiettati.Count);
        for (var i = 0; i < completi.Count; i++)
            Assert.Equal(FirmaCampi(completi[i]), FirmaCampi(proiettati[i]));

        // La proiezione e' davvero piu' leggera: i campi non letti restano vuoti, mentre il percorso completo li carica.
        Assert.All(proiettati, tc => Assert.Empty(tc.Changelog));
        Assert.Contains(completi, tc => tc.Changelog.Count > 0);
    }

    [Theory]
    [MemberData(nameof(Filtri))]
    public void Report_MatriceEAnteprima_UgualiConProiezioneEPercorsoCompleto(string filtro)
    {
        var servizio = new LocalTestArchiveQueryService(_dataset.DatabasePath);
        var query = QueryDi(filtro, _dataset.TemplateUid);
        var completi = servizio.QueryAll(query);
        var proiettati = servizio.QueryAllAnalisi(query);

        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Funzionalita };
        var veicoli = new List<Veicolo>();
        var funzionalita = new List<Funzionalita>();
        var templates = new List<TestTemplate>();

        var matriceCompleta = ReportMatriceService.Genera(completi, config, veicoli, funzionalita, templates);
        var matriceProiettata = ReportMatriceService.Genera(proiettati, config, veicoli, funzionalita, templates);
        // Righe, colonne, conteggi (conformi, valutati, totali), N.A. e totali di riga/colonna/generale.
        Assert.Equal(Serializza(matriceCompleta), Serializza(matriceProiettata));

        // Anteprima: stesso ordine di ApplicaAnteprima (ultima modifica decrescente, a parita' l'ordine della query).
        Assert.Equal(
            completi.OrderByDescending(tc => tc.UltimaModifica).Select(tc => tc.Id),
            proiettati.OrderByDescending(tc => tc.UltimaModifica).Select(tc => tc.Id));
    }

    [Theory]
    [InlineData("Componente-A")]        // componente comune: una frazione grande dell'archivio
    [InlineData("Componente-RARA")]     // componente rara: percorso selettivo sull'indice (proiezione)
    public void Audit_CelleUgualiConProiezioneEPercorsoCompleto(string componente)
    {
        var servizio = new LocalTestArchiveQueryService(_dataset.DatabasePath);
        var tutteLeEcu = _dataset.CatalogoEcu;
        var requisiti = _dataset.Requisiti;
        const string veicolo = "V2";

        var completi = servizio.QueryAll(new LocalTestArchiveQuery { AllowedEcus = new[] { componente } });
        var proiettati = servizio.QueryAllAnalisi(new LocalTestArchiveQuery { AllowedEcus = new[] { componente } });
        Assert.Equal(completi.Select(tc => tc.Uid), proiettati.Select(tc => tc.Uid));

        var celleCompleto = CelleAudit(veicolo, tutteLeEcu, requisiti, completi);
        var celleProiettato = CelleAudit(veicolo, tutteLeEcu, requisiti, proiettati);
        Assert.NotEmpty(celleCompleto);
        Assert.Equal(Serializza(celleCompleto), Serializza(celleProiettato));

        // Percorso Audit di produzione (AuditSelezioneTestPerEcu.Leggi): stessi Test e stesse celle, qualunque percorso scelga.
        var snapshot = servizio.QueryAll(new LocalTestArchiveQuery());
        var letti = AuditSelezioneTestPerEcu.Leggi(servizio, snapshot, new[] { componente });
        Assert.Equal(Serializza(celleCompleto), Serializza(CelleAudit(veicolo, tutteLeEcu, requisiti, letti)));
    }

    private static List<CellaMatriceAudit> CelleAudit(
        string veicolo, IReadOnlyList<Componente> tutteLeEcu, IReadOnlyList<Requisito> requisiti, IReadOnlyList<TestCase> testCases) =>
        AuditMatriceService.Matrice(
            veicolo, tutteLeEcu, requisiti, testCases,
            new List<RisoluzioneConflittoAudit>(),
            AuditMatriceService.IndicizzaTestPerEcuERequisito(testCases),
            new List<ValutazioneRequisitoEcuVeicolo>());

    private static LocalTestArchiveQuery QueryDi(string filtro, Guid templateUid) => filtro switch
    {
        "componente-a" => new LocalTestArchiveQuery { Componente = "Componente-A", MatchStringsOrdinal = true },
        "componente-selettiva" => new LocalTestArchiveQuery { AllowedEcus = new[] { "Componente-D" } },
        "componente-rara" => new LocalTestArchiveQuery { Componente = "Componente-RARA", MatchStringsOrdinal = true },
        "veicolo" => new LocalTestArchiveQuery { Vehicle = "V2", MatchStringsOrdinal = true },
        "template" => new LocalTestArchiveQuery { TemplateUids = new[] { templateUid } },
        "campo-custom" => new LocalTestArchiveQuery
        {
            CustomFieldSelections = new Dictionary<string, IReadOnlyCollection<string>> { ["Campo1"] = new[] { "x" } },
        },
        "cestino" => new LocalTestArchiveQuery { DeletionScope = LocalTestDeletionScope.All },
        "data" => new LocalTestArchiveQuery { CreatedAtOrAfter = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
        _ => new LocalTestArchiveQuery(),
    };

    /// <summary>Firma dei campi che Audit e Report leggono (non dei campi pesanti non proiettati): deve coincidere uno a uno.</summary>
    private static string FirmaCampi(TestCase tc)
    {
        var sb = new StringBuilder();
        sb.Append(FormattableString.Invariant(
            $"{tc.Uid:N}|{tc.Id}|{tc.DataCreazione.Ticks}|{tc.UltimaModifica.Ticks}|{tc.Eliminato}|{tc.Veicolo}|{Unisci(tc.VeicoliEffettivi)}|{tc.TestTemplateUid:N}|{tc.Assegnatario}|"));
        sb.Append(Unisci(tc.Componente)).Append('|')
            .Append(Unisci(tc.RequisitoId)).Append('|')
            .Append(Unisci(tc.Funzionalita)).Append('|')
            .Append(Unisci(tc.CampiCustom.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value)))
            .Append('|').Append(tc.StatoCorrente?.ToString() ?? "-");
        foreach (var e in tc.Esecuzioni)
        {
            sb.Append(FormattableString.Invariant(
                $"[rev:{e.Iterazione}|{e.Data.Ticks}|{e.Operatore}|{e.Origine}|{e.Esito}|{e.EsitoSovrascritto}"));
            foreach (var c in e.CondizioniEffettive)
                sb.Append(FormattableString.Invariant(
                    $"|cond:{c.Uid:N}|{c.OperazioneOrigineUid:N}|{c.Guasto}|{Unisci(c.Componente)}|{c.Funzionalita}|{c.Esito}|{c.RiguardaDtc}"));
            foreach (var p in e.PunteggiRequisiti)
                sb.Append(FormattableString.Invariant($"|pun:{p.RequisitoId}|{p.Valore}|{p.Sovrascritto}"));
            sb.Append(']');
        }
        return sb.ToString();
    }

    private static string Serializza<T>(T valore) => JsonSerializer.Serialize(valore, JsonIo.CompactOptions);

    private static string Unisci(IEnumerable<string> valori) => string.Join(",", valori);

    /// <summary>Dataset deterministico (seme fisso): un indice SQLite con Test ricchi, costruito una volta per classe di test.</summary>
    public sealed class DatasetProiezione : IDisposable
    {
        public const int Quanti = 400;

        private static readonly string[] EcuBase = { "Componente-A", "Componente-B", "componente-b", "Componente-C", " componente-D " };
        private static readonly string[] VeicoliBase = { "V1", "V2", "v2", "V3", "V4" };
        private static readonly string[] RequisitiBase = { "R1", "R2", "R3", "R4", "R5", "R6" };
        private static readonly string[] FunzionalitaBase = { "F1", "F2", "F3" };
        private static readonly Guid[] TemplateBase =
        {
            new("11111111-1111-1111-1111-111111111111"),
            new("22222222-2222-2222-2222-222222222222"),
            new("33333333-3333-3333-3333-333333333333"),
        };
        private static readonly Esito[] EsitiBase =
        {
            Esito.Conforme, Esito.NonConforme, Esito.ConformeConDeviazioni,
            Esito.NonApplicabile, Esito.DaRivedere, Esito.DaEseguire,
        };

        private readonly string _root;
        private readonly string _local;

        public DatasetProiezione()
        {
            _root = Path.Combine(Path.GetTempPath(), "TestLoom_ProiezioneAnalitica_" + Guid.NewGuid().ToString("N"));
            _local = Path.Combine(Path.GetTempPath(), "TestLoom_ProiezioneAnaliticaLocal_" + Guid.NewGuid().ToString("N"));
            var store = new LocalTestIndexStore(_root, _local);
            var casuale = new Random(20261011);
            var mutazioni = new List<LocalIndexMutation>(Quanti);
            for (var i = 0; i < Quanti; i++)
            {
                var tc = CreaTest(casuale, i);
                mutazioni.Add(LocalIndexMutation.Upsert(Path.Combine(_root, $"proiezione_{i:D6}.json"), tc.UltimaModifica, 100, tc));
            }
            store.ApplyBatch(mutazioni);
            DatabasePath = store.DatabasePath;
        }

        public string DatabasePath { get; }

        public Guid TemplateUid => TemplateBase[0];

        /// <summary>Catalogo componente con i veicoli associati (la matrice Audit risolve i componenti del sottoveicolo da qui).</summary>
        public List<Componente> CatalogoEcu { get; } = new()
        {
            new Componente { Nome = "Componente-A", VeicoliAssociati = new List<string> { "V1", "V2" } },
            new Componente { Nome = "Componente-B", VeicoliAssociati = new List<string> { "V2", "V3" } },
            new Componente { Nome = "Componente-C", VeicoliAssociati = new List<string> { "V3", "V4" } },
            new Componente { Nome = "Componente-D", VeicoliAssociati = new List<string> { "V2" } },
            new Componente { Nome = "Componente-RARA", VeicoliAssociati = new List<string> { "V2" } },
        };

        public List<Requisito> Requisiti { get; } = RequisitiBase
            .Select(id => new Requisito { Id = id, Testo = "Requisito " + id })
            .ToList();

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var cartella in new[] { _root, _local })
                try { if (Directory.Exists(cartella)) Directory.Delete(cartella, recursive: true); } catch { /* best-effort */ }
        }

        private static TestCase CreaTest(Random r, int i)
        {
            var uidBytes = new byte[16];
            BitConverter.TryWriteBytes(uidBytes, i + 1);
            var base0 = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);
            var componente = Campioni(r, EcuBase, 1, 3).ToList();
            if (i % 80 == 0) componente.Add("Componente-RARA");   // poche decine di Test: percorso selettivo
            var tc = new TestCase
            {
                Uid = new Guid(uidBytes),
                Id = (i % 7 == 0 ? "tc-" : "TC-") + i.ToString("D4"),
                DataCreazione = base0.AddDays(-r.Next(1, 400)),
                UltimaModifica = base0.AddMinutes(r.Next(0, 3000)),       // istanti ripetuti: legami nell'ordine
                Eliminato = r.Next(25) == 0,
                Componente = new EcuSelection(componente),
                Veicoli = Campioni(r, VeicoliBase, 0, 2).ToList(),
                TestTemplateUid = TemplateBase[r.Next(TemplateBase.Length)],
                RequisitoId = Campioni(r, RequisitiBase, 0, 3).ToList(),
                Funzionalita = Campioni(r, FunzionalitaBase, 0, 2).ToList(),
                Assegnatario = r.Next(2) == 0 ? null : "Op A",
                CreatoDa = "Paolo",
                DescrizioneBreve = "Test " + i,
                Descrizione = "Descrizione del test " + i,
                CampiCustom = r.Next(3) == 0
                    ? new Dictionary<string, string> { ["Campo1"] = r.Next(2) == 0 ? "x" : "y" }
                    : new Dictionary<string, string>(),
                // Campi pesanti non proiettati: non devono cambiare il risultato di Audit e Report.
                Changelog = { new ChangelogEntry { Data = base0, Operatore = "Paolo", Campo = "(creazione)", ValoreNuovo = "setup" } },
                ValoriStrumentiTest = new Dictionary<string, string> { ["Strumento"] = "ValidationTool" },
            };
            if (r.Next(3) == 0) tc.Veicolo = tc.Veicoli.FirstOrDefault() ?? "";
            else tc.Veicolo = VeicoliBase[r.Next(VeicoliBase.Length)];

            var revisioni = r.Next(4);
            for (var k = 0; k < revisioni; k++)
            {
                var esecuzione = new Esecuzione
                {
                    Iterazione = k + 1,
                    Data = base0.AddDays(-r.Next(1, 300)).AddHours(k),
                    Operatore = r.Next(2) == 0 ? "Op A" : "Op B",
                    Origine = OrigineRevisione.Operatore,
                    Esito = EsitiBase[r.Next(EsitiBase.Length)],
                    EsitoSovrascritto = r.Next(10) == 0,
                    Note = "Nota revisione " + k,
                    Matricola = "Matricola" + i,
                    BaselineSw = "3.4",
                    Dettagli = new DettagliTestSnapshot { Descrizione = "fotografia " + k, Veicolo = "V1" },
                    ValoriStrumenti = new Dictionary<string, string> { ["ValidationTool::Canale"] = "1" },
                };
                var operazioni = 1 + r.Next(3);
                var primaUid = Guid.Empty;
                for (var c = 0; c < operazioni; c++)
                {
                    var condizione = new Condizione
                    {
                        Esito = EsitiBase[r.Next(EsitiBase.Length)],
                        Componente = new EcuSelection(Campioni(r, EcuBase, 0, 2)),
                        Funzionalita = FunzionalitaBase[r.Next(FunzionalitaBase.Length)],
                        Guasto = r.Next(6) == 0 ? "0x1234" : null,
                        Nota = "nota operazione",
                        DescrizioneProblema = "problema",
                        Strumento = "ValidationTool",
                    };
                    if (c == 0) primaUid = condizione.Uid;
                    else if (r.Next(2) == 0) condizione.OperazioneOrigineUid = primaUid;   // ripetizione della stessa operazione
                    esecuzione.Condizioni.Add(condizione);
                }
                foreach (var requisito in tc.RequisitoId)
                {
                    if (r.Next(4) == 0) continue;
                    esecuzione.PunteggiRequisiti.Add(new PunteggioRequisito
                    {
                        RequisitoId = requisito,
                        Valore = r.Next(2) == 0 ? PunteggioConformita.Conforme : PunteggioConformita.NonConforme,
                        Sovrascritto = r.Next(6) == 0,
                    });
                }
                tc.Esecuzioni.Add(esecuzione);
            }
            return tc;
        }

        private static string[] Campioni(Random r, string[] base0, int minimo, int massimo)
        {
            var n = minimo + r.Next(massimo - minimo + 1);
            return Enumerable.Range(0, base0.Length).OrderBy(_ => r.Next()).Take(n).Select(k => base0[k]).ToArray();
        }
    }
}
