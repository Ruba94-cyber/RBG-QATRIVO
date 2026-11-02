using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Accettazione delle due modalità della matrice descritte dall'utente, sullo stesso fixture: 1 componente (BCM), una categoria
/// "Diagnostica" con 4 funzionalità (F1 OK; F2 OK poi NOK, quindi NOK; F3 mai testata; F4 NOK poi correzione manuale OK più recente).
/// "Stato %": colonne componente, righe categoria, distribuzione per cella sul totale delle combinazioni incluse le non testate, correzioni
/// manuali per precedenza della più recente. "Stato": colonne componente › funzionalità annidate, righe stato; ogni funzionalità compare una
/// sola volta, sotto il suo stato corrente, e gli stati storici non compaiono mai. Ogni numero è verificato sul modello e sull'export
/// Excel ("Matrice configurabile"), che legge gli stessi cataloghi e gli stessi Test del modello.</summary>
public sealed class ReportModalitaAccettazioneTests : IDisposable
{
    private const char Sep = ReportMatriceService.SepLivello;
    private readonly string _dir;
    private readonly AppState _state;

    public ReportModalitaAccettazioneTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ModalitaAccettazione_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_dir);
        _state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1", GammaNome = "LCV" } });
        _state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["V1"] } });
        _state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "F1", Categorie = ["Diagnostica"] },
            new() { Nome = "F2", Categorie = ["Diagnostica"] },
            new() { Nome = "F3", Categorie = ["Diagnostica"] },
            new() { Nome = "F4", Categorie = ["Diagnostica"] },
        });
        SalvaTest("T-F1", "F1", Esito.Conforme, new DateTime(2026, 1, 1));
        SalvaTest("T-F2-OK", "F2", Esito.Conforme, new DateTime(2026, 1, 1));
        SalvaTest("T-F2-NOK", "F2", Esito.NonConforme, new DateTime(2026, 1, 2));
        // F3 non ha alcun Test: e' la combinazione del catalogo mai testata (universo "Non testato"). Un Test pianificato senza
        // esecuzioni, invece, conta come "da eseguire" e non come non testato.
        SalvaTest("T-F4", "F4", Esito.NonConforme, new DateTime(2026, 1, 2));
        // Correzione manuale del Master su F4, più recente del test NOK: vince sul test.
        _state.Master.StatiFunzionalitaEcuVeicolo.Save(new List<StatoFunzionalitaEcuVeicolo>
        {
            new()
            {
                Funzionalita = "F4", Componente = "BCM", Veicolo = "V1", Esito = Esito.Conforme,
                Operatore = "Master", Nota = "correzione manuale", DataDecisione = new DateTime(2026, 1, 5),
            },
        });
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private void SalvaTest(string id, string funzionalita, Esito esito, DateTime data)
    {
        var tc = new TestCase { Id = id, Componente = new EcuSelection(["BCM"]), Veicoli = ["V1"], Funzionalita = [funzionalita] };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = data,
            Condizioni = [new Condizione { Funzionalita = funzionalita, Esito = esito, Componente = new EcuSelection(["BCM"]) }],
        });
        _state.TestCases.Save(tc, isNew: true);
    }

    private RisultatoMatriceReport Modello(ConfigurazioneMatriceReport config) =>
        ReportMatriceService.Genera(_state.TestCases.GetAll(), config, _state.Master.Veicoli.Load(),
            _state.Master.Funzionalita.Load(), _state.Master.TestTemplate.Load(), null, _state.Master.Componente.Load(),
            config.ModalitaCella != ModalitaCellaMatrice.TestPercentuale ? _state.Master.StatiFunzionalitaEcuVeicolo.Load() : null);

    private XLWorkbook Excel(ConfigurazioneMatriceReport config) =>
        new(new MemoryStream(ReportService.GeneraExcel(new ReportFilter(), _state, "Operatore Prova Accettazione", config)));

    private static ConfigurazioneMatriceReport StatoPercentuale() => new()
    {
        CampiX = [DimensioniMatrice.Componente],
        CampiY = [DimensioniMatrice.CategoriaFunzionalita],
        ModalitaCella = ModalitaCellaMatrice.Percentuale,
    };

    private static ConfigurazioneMatriceReport StatoNidificato() => new()
    {
        CampiX = [DimensioniMatrice.Componente, DimensioniMatrice.Funzionalita],
        CampiY = [DimensioniMatrice.Esito],
        ModalitaCella = ModalitaCellaMatrice.Stato,
    };

    [Fact]
    public void StatoPercentuale_ModelloEExcel_50ConformePer25NonConformePer25NonTestato()
    {
        var risultato = Modello(StatoPercentuale());

        var riga = Assert.Single(risultato.Righe);
        Assert.Equal("Diagnostica", riga.Riga);
        var cella = Assert.Single(riga.Celle);
        Assert.Equal("BCM", cella.Colonna);
        // 4 combinazioni (4 funzionalità × 1 componente × 1 veicolo): OK = F1 e F4 (correzione manuale più recente), NOK = F2, non testata = F3.
        Assert.Equal(new StatiMatrice(2, 1, 0, 0, 1, 0), cella.StatiPercentuale);
        Assert.Equal(4, cella.StatiPercentuale.Totale);
        Assert.Equal(50d, cella.Percentuale);
        Assert.Contains("OK 50%", cella.Testo);
        Assert.Contains("NOK 25%", cella.Testo);
        Assert.Contains("non test. 25%", cella.Testo);
        // Totali coerenti: colonna, riga e generale hanno la stessa distribuzione della cella.
        Assert.Equal(cella.StatiPercentuale, Assert.Single(risultato.TotaliColonna).StatiPercentuale);
        Assert.Equal(cella.StatiPercentuale, riga.TotaleRiga.StatiPercentuale);
        Assert.Equal(cella.StatiPercentuale, risultato.TotaleGenerale.StatiPercentuale);

        using var wb = Excel(StatoPercentuale());
        var ws = wb.Worksheet("Matrice configurabile");
        Assert.Equal("BCM", ws.Cell(4, 2).GetString().Split('\n')[0]);
        Assert.Equal("Diagnostica", ws.Cell(5, 1).GetString().Split('\n')[0]);
        // Stessi numeri del modello: il testo della cella Excel è quello della cella del modello.
        Assert.Equal(cella.TestoVisualizzato, ws.Cell(5, 2).GetString());
    }

    [Fact]
    public void Stato_ModelloEExcel_OgniFunzionalitaUnaVoltaSottoloStatoCorrente()
    {
        var risultato = Modello(StatoNidificato());

        var attese = new Dictionary<string, string>
        {
            ["F1"] = Esito.Conforme.Label(),
            ["F2"] = Esito.NonConforme.Label(),
            ["F3"] = Traduci("MatriceStatoNonTestato"),
            ["F4"] = Esito.Conforme.Label(),
        };
        foreach (var (funzionalita, statoAtteso) in attese)
        {
            var colonna = "BCM" + Sep + funzionalita;
            var valorizzate = risultato.Righe
                .SelectMany(r => r.Celle.Where(c => c.Colonna == colonna && c.Stati.Totale > 0).Select(c => (Stato: r.Riga, c.TestoStato)))
                .ToList();
            var unica = Assert.Single(valorizzate);
            Assert.Equal(statoAtteso, unica.Stato);
        }

        using var wb = Excel(StatoNidificato());
        var ws = wb.Worksheet("Matrice configurabile");
        var ultimaRiga = ws.LastRowUsed()!.RowNumber();
        var ultimaColonna = ws.LastColumnUsed()!.ColumnNumber();
        var nelFoglio = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var colonna = 2; colonna <= ultimaColonna; colonna++)
        {
            var funzionalita = PrimaRiga(ws.Cell(5, colonna).GetString());
            for (var riga = 6; riga <= ultimaRiga; riga++)
            {
                if (string.IsNullOrWhiteSpace(ws.Cell(riga, colonna).GetString())) continue;
                if (!nelFoglio.TryGetValue(funzionalita, out var stati)) nelFoglio[funzionalita] = stati = new List<string>();
                stati.Add(PrimaRiga(ws.Cell(riga, 1).GetString()));
            }
        }
        // Ogni funzionalità compare una sola volta nel foglio, sotto lo stato corrente.
        Assert.Equal(attese.Keys.OrderBy(k => k), nelFoglio.Keys.OrderBy(k => k));
        foreach (var (funzionalita, statoAtteso) in attese)
            Assert.Equal(new[] { statoAtteso }, nelFoglio[funzionalita]);
    }

    private static string Traduci(string chiave) => LocalizationService.Instance.Traduci(chiave);

    /// <summary>Prima riga di un'intestazione o di un'etichetta Excel (la seconda riga, se c'è, riporta le percentuali di colonna).</summary>
    private static string PrimaRiga(string testo) => testo.Split('\n')[0].Trim();
}
