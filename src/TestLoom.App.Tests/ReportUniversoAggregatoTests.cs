using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Il conteggio aggregato dell'universo "Non testato" (un contatore per cella, nessuna combinazione materializzata) deve dare
/// esattamente il risultato del percorso materializzato: stesse celle, stessi conteggi per stato, stessi totali di colonna, riga e
/// generale, con e senza filtri di vista e marcature N.A.</summary>
public sealed class ReportUniversoAggregatoTests
{
    private static readonly List<Veicolo> Veicoli =
    [
        new() { Nome = "V1", GammaNome = "G1" },
        new() { Nome = "V2", GammaNome = "G1" },
        new() { Nome = "V3", GammaNome = "G2" },
        new() { Nome = "V4", GammaNome = "G2" },
    ];

    private static readonly List<Componente> CatalogoEcu =
    [
        new() { Nome = "E1", VeicoliAssociati = ["V1"] },
        new() { Nome = "E2", VeicoliAssociati = ["V1", "V2"] },   // piu' veicoli: puo' generare "da decidere"
        new() { Nome = "E3", VeicoliAssociati = [] },              // senza veicolo
        new() { Nome = "E4", VeicoliAssociati = ["V3", "V4"] },
    ];

    private static readonly List<Funzionalita> Funzioni =
    [
        new() { Nome = "F1", Categorie = ["CatA", "CatB"] },
        new() { Nome = "F2", Categorie = ["CatA"], TutteLeEcu = false, EcuAssociate = ["E1", "E2"] },
        new() { Nome = "F3", Categorie = [] },
        new() { Nome = "F4", Categorie = ["CatC", "CatD"] },
        new() { Nome = "F5", Categorie = ["CatB"] },
    ];

    private static TestCase Test(string id, string componente, string veicolo, string funzionalita, DateTime data, Esito esito) =>
        new()
        {
            Id = id,
            Componente = new EcuSelection([componente]),
            Veicoli = [veicolo],
            Funzionalita = [funzionalita],
            Esecuzioni =
            [
                new Esecuzione
                {
                    Data = data,
                    Condizioni = [new Condizione { Funzionalita = funzionalita, Esito = esito, Componente = new EcuSelection([componente]) }],
                },
            ],
        };

    private static List<TestCase> Test() =>
    [
        Test("T-1", "E1", "V1", "F1", new DateTime(2026, 1, 1), Esito.Conforme),
        Test("T-2", "E2", "V1", "F1", new DateTime(2026, 1, 2), Esito.NonConforme),
        Test("T-3", "E2", "V2", "F1", new DateTime(2026, 1, 3), Esito.Conforme),
        Test("T-4", "E2", "V2", "F5", new DateTime(2026, 1, 4), Esito.Conforme),   // (F5,E2,V1) non testato con sibling eseguito
        Test("T-5", "E4", "V3", "F3", new DateTime(2026, 1, 5), Esito.DaEseguire),
        Test("T-6", "E3", "", "F4", new DateTime(2026, 1, 6), Esito.NonConforme),
        Test("T-7", "E1", "V1", "F2", new DateTime(2026, 1, 7), Esito.Conforme),
    ];

    private static List<StatoFunzionalitaEcuVeicolo> Manuali() =>
    [
        new()
        {
            Funzionalita = "F3", Componente = "E4", Veicolo = "V4", Esito = Esito.Conforme,
            Operatore = "Master", Nota = "correzione", DataDecisione = new DateTime(2026, 1, 8),
        },
    ];

    private static ConfigurazioneMatriceReport Config(IReadOnlyList<string> campiX, IReadOnlyList<string> campiY, ModalitaCellaMatrice modalita,
        params (string Dimensione, string Valore)[] filtri)
    {
        var config = new ConfigurazioneMatriceReport { CampiX = campiX.ToList(), CampiY = campiY.ToList(), ModalitaCella = modalita };
        foreach (var (dimensione, valore) in filtri)
        {
            if (!config.Filtri.TryGetValue(dimensione, out var valori)) config.Filtri[dimensione] = valori = new List<string>();
            valori.Add(valore);
        }
        return config;
    }

    private static string Riassunto(RisultatoMatriceReport r)
    {
        static string Tot(TotaleMatriceReport t) => $"V={t.Valutati} C={t.Conformi} S={t.Stati} SP={t.StatiPercentuale} T={t.Testo}";
        var sb = new StringBuilder();
        sb.Append("COL:").AppendJoin('|', r.Colonne).AppendLine();
        foreach (var riga in r.Righe.OrderBy(x => x.Riga, StringComparer.Ordinal))
        {
            sb.Append("R:").Append(riga.Riga).Append(" TR=").Append(Tot(riga.TotaleRiga)).AppendLine();
            foreach (var c in riga.Celle.OrderBy(x => x.Colonna, StringComparer.Ordinal))
                sb.Append("  ").Append(c.Colonna).Append(" T=").Append(c.Totali).Append(" V=").Append(c.Valutati).Append(" C=").Append(c.Conformi)
                    .Append(" S=").Append(c.Stati).Append(" SP=").Append(c.StatiPercentuale).Append(" DD=").Append(c.StatiPercentuale.DaDecidere)
                    .Append(" N=").Append(c.NonApplicabile).Append(" TX=").Append(c.Testo).AppendLine();
        }
        for (var i = 0; i < r.TotaliColonna.Count; i++) sb.Append("TC:").Append(r.Colonne[i]).Append(' ').Append(Tot(r.TotaliColonna[i])).AppendLine();
        sb.Append("TG:").Append(Tot(r.TotaleGenerale));
        return sb.ToString();
    }

    public static IEnumerable<object[]> Casi()
    {
        yield return [0];   // componente x Categoria, Stato %
        yield return [1];   // componente x Categoria, Stato
        yield return [2];   // Sottoveicolo x Funzionalita, Stato %
        yield return [3];   // Gamma x Esito, Stato
        yield return [4];   // Categoria x componente, Stato
        yield return [5];   // filtro Sottoveicolo, Stato %
        yield return [6];   // filtro Esito non testato, Stato
        yield return [7];   // componente x Funzionalita con filtro componente, Stato
        yield return [8];   // filtro Categoria e Funzionalita, Stato %
    }

    private static ConfigurazioneMatriceReport CasoConfigurazione(int caso) => caso switch
    {
        0 => Config(["componente"], ["categoria-funzionalita"], ModalitaCellaMatrice.Percentuale),
        1 => Config(["componente"], ["categoria-funzionalita"], ModalitaCellaMatrice.Stato),
        2 => Config(["sottoveicolo"], ["funzionalita"], ModalitaCellaMatrice.Percentuale),
        3 => Config(["gamma"], ["esito"], ModalitaCellaMatrice.Stato),
        4 => Config(["categoria-funzionalita"], ["componente"], ModalitaCellaMatrice.Stato),
        5 => Config(["componente"], ["categoria-funzionalita"], ModalitaCellaMatrice.Percentuale, ("sottoveicolo", "V1")),
        6 => Config(["componente"], ["categoria-funzionalita"], ModalitaCellaMatrice.Stato, ("esito", "Non testato")),
        7 => Config(["componente"], ["funzionalita"], ModalitaCellaMatrice.Stato, ("componente", "E2")),
        _ => Config(["componente"], ["categoria-funzionalita"], ModalitaCellaMatrice.Percentuale, ("categoria-funzionalita", "CatA")),
    };

    [Theory]
    [MemberData(nameof(Casi))]
    public void Aggregato_UgualeAlMaterializzato_SenzaENconPNA(int caso)
    {
        var config = CasoConfigurazione(caso);
        var ecuCatalogo = CatalogoEcu;
        var manuali = Manuali();
        RisultatoMatriceReport Calcola(bool aggrega, List<CellaNonApplicabile>? marcature) =>
            ReportMatriceService.Prepara(Test(), config, Veicoli, Funzioni, [], ecuCatalogo, manuali, aggrega)
                .Completa(marcature);

        var marcatureNA = new List<CellaNonApplicabile>
        {
            new() { ValoreAsseX = "E2", ValoreAsseY = "CatA" },
            new() { ValoreAsseX = "E4", ValoreAsseY = "CatC" },
        };
        foreach (var marcature in new List<CellaNonApplicabile>?[] { null, marcatureNA })
        {
            var materializzato = Riassunto(Calcola(false, marcature));
            var aggregato = Riassunto(Calcola(true, marcature));
            Assert.Equal(materializzato, aggregato);
        }
    }

    [Fact]
    public void Aggregato_UnaSolaCellaDettaglio_ConLeStesseCombinazioni()
    {
        var config = Config(["componente"], ["categoria-funzionalita"], ModalitaCellaMatrice.Percentuale);
        var aggregato = ReportMatriceService.Prepara(Test(), config, Veicoli, Funzioni, [], CatalogoEcu, Manuali(), true);
        var materializzato = ReportMatriceService.Prepara(Test(), config, Veicoli, Funzioni, [], CatalogoEcu, Manuali(), false);
        var r1 = aggregato.Completa();
        var r2 = materializzato.Completa();
        foreach (var riga in r2.Righe)
            foreach (var cella in riga.Celle)
            {
                var attese = materializzato.CombinazioniDi(riga.Riga, cella.Colonna).Select(c => $"{c.Funzionalita}|{c.Componente}|{c.Veicolo}|{c.Stato}").OrderBy(s => s, StringComparer.Ordinal).ToList();
                var ottenute = aggregato.CombinazioniDi(riga.Riga, cella.Colonna).Select(c => $"{c.Funzionalita}|{c.Componente}|{c.Veicolo}|{c.Stato}").OrderBy(s => s, StringComparer.Ordinal).ToList();
                Assert.Equal(attese, ottenute);
            }
        Assert.Equal(Riassunto(r2), Riassunto(r1));
    }

    /// <summary>Forma comune del conteggio aggregato (componente x Categoria, un veicolo per componente, nessun filtro): stessi risultati del
    /// percorso materializzato, con e senza N.A., in Stato % e in Stato.</summary>
    [Fact]
    public void Aggregato_EcuCategoria_UnVeicoloPerEcu_UgualeAlMaterializzato()
    {
        var unVeicoloPerEcu = new List<Componente>
        {
            new() { Nome = "E1", VeicoliAssociati = ["V1"] },
            new() { Nome = "E2", VeicoliAssociati = ["V2"] },
            new() { Nome = "E3", VeicoliAssociati = [] },
            new() { Nome = "E4", VeicoliAssociati = ["V3"] },
        };
        var manuali = Manuali();
        foreach (var modalita in new[] { ModalitaCellaMatrice.Percentuale, ModalitaCellaMatrice.Stato })
        {
            var config = Config(["componente"], ["categoria-funzionalita"], modalita);
            RisultatoMatriceReport Calcola(bool aggrega, List<CellaNonApplicabile>? marcature) =>
                ReportMatriceService.Prepara(Test(), config, Veicoli, Funzioni, [], unVeicoloPerEcu, manuali, aggrega).Completa(marcature);
            var marcatureNA = new List<CellaNonApplicabile> { new() { ValoreAsseX = "E2", ValoreAsseY = "CatA" } };
            foreach (var marcature in new List<CellaNonApplicabile>?[] { null, marcatureNA })
                Assert.Equal(Riassunto(Calcola(false, marcature)), Riassunto(Calcola(true, marcature)));
        }
    }

    /// <summary>Funzionalita' del fixture con restrizioni per componente (Funzionalita.VeicoliPerEcu): F1 solo V2 su E2; F2 nessun veicolo su E2 e solo
    /// V1 su E1; F3 solo V4 su E4; F5 solo V1 su E2 (V9 non esiste nel fixture: nessun effetto); F4 senza restrizioni.</summary>
    private static List<Funzionalita> FunzioniRistrette() =>
    [
        new() { Nome = "F1", Categorie = ["CatA", "CatB"], VeicoliPerEcu = [new() { Componente = "E2", Veicoli = ["V2"] }] },
        new() { Nome = "F2", Categorie = ["CatA"], TutteLeEcu = false, EcuAssociate = ["E1", "E2"],
            VeicoliPerEcu = [new() { Componente = "E1", Veicoli = ["V1"] }, new() { Componente = "E2", Veicoli = [] }] },
        new() { Nome = "F3", Categorie = [], VeicoliPerEcu = [new() { Componente = "E4", Veicoli = ["V4"] }] },
        new() { Nome = "F4", Categorie = ["CatC", "CatD"] },
        new() { Nome = "F5", Categorie = ["CatB"], VeicoliPerEcu = [new() { Componente = "E2", Veicoli = ["V1", "V9"] }] },
    ];

    /// <summary>Come Funzioni, ma F5 limitata al solo veicolo indicato su E2 (null = nessuna restrizione).</summary>
    private static List<Funzionalita> FunzioniConF5Su(string? veicolo) =>
        Funzioni.Select(f => f.Nome == "F5" && veicolo != null
            ? new Funzionalita { Nome = f.Nome, Categorie = f.Categorie, VeicoliPerEcu = [new() { Componente = "E2", Veicoli = [veicolo] }] }
            : f).ToList();

    [Theory]
    [MemberData(nameof(Casi))]
    public void Aggregato_ConRestrizioniPerEcu_UgualeAlMaterializzato(int caso)
    {
        var config = CasoConfigurazione(caso);
        var funzioni = FunzioniRistrette();
        RisultatoMatriceReport Calcola(bool aggrega, List<CellaNonApplicabile>? marcature) =>
            ReportMatriceService.Prepara(Test(), config, Veicoli, funzioni, [], CatalogoEcu, Manuali(), aggrega).Completa(marcature);

        var marcatureNA = new List<CellaNonApplicabile>
        {
            new() { ValoreAsseX = "E2", ValoreAsseY = "CatA" },
            new() { ValoreAsseX = "E4", ValoreAsseY = "CatC" },
        };
        foreach (var marcature in new List<CellaNonApplicabile>?[] { null, marcatureNA })
            Assert.Equal(Riassunto(Calcola(false, marcature)), Riassunto(Calcola(true, marcature)));
    }

    /// <summary>Restrizione per componente: la cella E2 x F5 conta solo i veicoli ammessi. Con F5 solo su V1, V1 resta non testato e la sorella V2
    /// (testata ma fuori restrizione) non lo rende "da decidere"; con F5 solo su V2 il non testato sparisce. Vale per entrambi i percorsi.</summary>
    [Theory]
    [InlineData(null, 2, 0, 1)]   // nessuna restrizione: V1 non testato, sorella V2 eseguita -> da decidere
    [InlineData("V1", 2, 1, 0)]   // V1 non testato, nessuna sorella ammessa eseguita -> non testato
    [InlineData("V2", 1, 0, 0)]   // solo V2, gia' testato
    public void Restrizione_CellaE2xF5_SegueLaRegolaDiValeSu(string? veicoloAmmesso, int totali, int nonTestati, int daDecidere)
    {
        var config = Config(["componente"], ["funzionalita"], ModalitaCellaMatrice.Stato);
        var funzioni = FunzioniConF5Su(veicoloAmmesso);
        foreach (var aggrega in new[] { false, true })
        {
            // Con componente come asse X (colonne) e funzionalita' come asse Y (righe): la cella e' riga F5 x colonna E2.
            var riga = ReportMatriceService.Prepara(Test(), config, Veicoli, funzioni, [], CatalogoEcu, Manuali(), aggrega)
                .Completa().Righe.Single(r => r.Riga == "F5");
            var cella = riga.Celle.Single(c => c.Colonna == "E2");
            Assert.Equal(totali, cella.Totali);
            Assert.Equal(nonTestati, cella.Stati.NonTestato);
            Assert.Equal(daDecidere, cella.Stati.DaDecidere);
        }
    }

    /// <summary>Decisione sul caso componente senza veicoli: la combinazione senza veicolo resta anche con una restrizione (non c'e' un veicolo
    /// su cui applicarla), come Funzionalita.ValeSu(componente, "") che ignora la restrizione.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restrizione_EcuSenzaVeicoli_ConservaLaCombinazioneSenzaVeicolo(bool aggrega)
    {
        var funzioni = new List<Funzionalita>
        {
            new() { Nome = "F1", VeicoliPerEcu = [new() { Componente = "E3", Veicoli = ["V1"] }] },
        };
        var config = Config(["componente"], ["funzionalita"], ModalitaCellaMatrice.Stato);
        var riga = ReportMatriceService.Prepara(Test(), config, Veicoli, funzioni, [], CatalogoEcu, Manuali(), aggrega)
            .Completa().Righe.Single(r => r.Riga == "F1");
        var cella = riga.Celle.Single(c => c.Colonna == "E3");
        Assert.Equal(1, cella.Totali);
        Assert.Equal(1, cella.Stati.NonTestato);
    }

    /// <summary>Forma comune (componente x Categoria, un veicolo per componente) con restrizioni: la correzione per coppia (funzionalita', componente) dà gli
    /// stessi risultati del percorso materializzato, con e senza N.A., in Stato % e in Stato.</summary>
    [Fact]
    public void Aggregato_EcuCategoria_ConRestrizioni_UgualeAlMaterializzato()
    {
        var unVeicoloPerEcu = new List<Componente>
        {
            new() { Nome = "E1", VeicoliAssociati = ["V1"] },
            new() { Nome = "E2", VeicoliAssociati = ["V2"] },
            new() { Nome = "E3", VeicoliAssociati = [] },
            new() { Nome = "E4", VeicoliAssociati = ["V3"] },
        };
        var funzioni = new List<Funzionalita>
        {
            new() { Nome = "F1", Categorie = ["CatA", "CatB"], VeicoliPerEcu = [new() { Componente = "E4", Veicoli = [] }, new() { Componente = "E2", Veicoli = ["V2", "V1"] }] },
            new() { Nome = "F2", Categorie = ["CatA"], TutteLeEcu = false, EcuAssociate = ["E1", "E2"], VeicoliPerEcu = [new() { Componente = "E2", Veicoli = [] }] },
            new() { Nome = "F3", Categorie = [] },
            new() { Nome = "F4", Categorie = ["CatC", "CatD"], VeicoliPerEcu = [new() { Componente = "E3", Veicoli = ["V1"] }] },
            new() { Nome = "F5", Categorie = ["CatB"], VeicoliPerEcu = [new() { Componente = "E1", Veicoli = ["V1"] }] },
        };
        var manuali = Manuali();
        foreach (var modalita in new[] { ModalitaCellaMatrice.Percentuale, ModalitaCellaMatrice.Stato })
        {
            var config = Config(["componente"], ["categoria-funzionalita"], modalita);
            RisultatoMatriceReport Calcola(bool aggrega, List<CellaNonApplicabile>? marcature) =>
                ReportMatriceService.Prepara(Test(), config, Veicoli, funzioni, [], unVeicoloPerEcu, manuali, aggrega).Completa(marcature);
            var marcatureNA = new List<CellaNonApplicabile> { new() { ValoreAsseX = "E2", ValoreAsseY = "CatA" } };
            foreach (var marcature in new List<CellaNonApplicabile>?[] { null, marcatureNA })
                Assert.Equal(Riassunto(Calcola(false, marcature)), Riassunto(Calcola(true, marcature)));
        }
    }
}
