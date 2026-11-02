using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Il totale generale calcolato senza costruire la griglia (MatricePreparata.CalcolaTotaleGenerale, usato dalla distribuzione
/// di conformita' della Home) deve coincidere con il totale generale di Genera, con e senza marcature N.A., filtri di vista e valori
/// manuali. La dimensione Esito deve dare "Non testato" solo se esiste una combinazione del catalogo non coperta dai Test.</summary>
public sealed class ReportTotaleGeneraleTests
{
    private static readonly List<Veicolo> Veicoli =
    [
        new() { Nome = "V1", GammaNome = "G1" },
        new() { Nome = "V2", GammaNome = "G1" },
        new() { Nome = "V3", GammaNome = "G2" },
    ];

    private static readonly List<Componente> CatalogoEcu =
    [
        new() { Nome = "E1", VeicoliAssociati = ["V1"] },
        new() { Nome = "E2", VeicoliAssociati = ["V1", "V2"] },   // piu' veicoli: puo' generare "da decidere"
        new() { Nome = "E3", VeicoliAssociati = [] },              // senza veicolo
        new() { Nome = "E4", VeicoliAssociati = ["V3"] },
    ];

    private static readonly List<Funzionalita> Funzioni =
    [
        new() { Nome = "F1", Categorie = ["CatA", "CatB"] },
        new() { Nome = "F2", Categorie = ["CatA"], TutteLeEcu = false, EcuAssociate = ["E1", "E2"] },
        new() { Nome = "F3", Categorie = [] },
        new() { Nome = "F4", Categorie = ["CatC"] },
    ];

    private static TestCase Test(string id, string componente, string veicolo, string funzionalita, DateTime data, Esito esito) =>
        new()
        {
            Id = id,
            Componente = new EcuSelection([componente]),
            Veicoli = string.IsNullOrEmpty(veicolo) ? [] : [veicolo],
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
        Test("T-4", "E2", "V2", "F4", new DateTime(2026, 1, 4), Esito.Conforme),
        Test("T-5", "E4", "V3", "F3", new DateTime(2026, 1, 5), Esito.DaEseguire),
        Test("T-6", "E3", "", "F4", new DateTime(2026, 1, 6), Esito.NonConforme),
        Test("T-7", "E1", "V1", "F2", new DateTime(2026, 1, 7), Esito.Conforme),
    ];

    private static List<StatoFunzionalitaEcuVeicolo> Manuali() =>
    [
        new()
        {
            Funzionalita = "F3", Componente = "E4", Veicolo = "V3", Esito = Esito.Conforme,
            Operatore = "Master", Nota = "correzione", DataDecisione = new DateTime(2026, 1, 8),
        },
    ];

    private static string Riepilogo(TotaleMatriceReport t) => $"V={t.Valutati} C={t.Conformi} S={t.Stati} SP={t.StatiPercentuale} T={t.Testo}";

    public static IEnumerable<object[]> Casi() => Enumerable.Range(0, 7).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Casi))]
    public void TotaleGenerale_SenzaGriglia_UgualeAGenera(int caso)
    {
        var config = new ConfigurazioneMatriceReport
        {
            CampiX = [DimensioniMatrice.Componente],
            CampiY = [DimensioniMatrice.Funzionalita],
            ModalitaCella = ModalitaCellaMatrice.Stato,
        };
        var manuali = Manuali();
        var marcature = new List<CellaNonApplicabile>
        {
            new() { ValoreAsseX = "E2", ValoreAsseY = "F1" },
            new() { ValoreAsseX = "E4", ValoreAsseY = "F3" },
        };
        List<CellaNonApplicabile>? marcatureCaso = null;
        List<StatoFunzionalitaEcuVeicolo>? manualiCaso = null;
        switch (caso)
        {
            case 1: marcatureCaso = marcature; manualiCaso = manuali; break;
            case 2: config.CampiY = [DimensioniMatrice.CategoriaFunzionalita]; break;
            case 3: config.CampiY = [DimensioniMatrice.CategoriaFunzionalita]; marcatureCaso = marcature; manualiCaso = manuali; break;
            case 4: config.Filtri[DimensioniMatrice.Componente] = ["E2"]; break;
            case 5: config.CampiX = [DimensioniMatrice.Funzionalita]; config.CampiY = [DimensioniMatrice.Componente]; marcatureCaso = marcature; break;
            case 6: config.ModalitaCella = ModalitaCellaMatrice.TestPercentuale; marcatureCaso = marcature; break;
        }

        var totale = ReportMatriceService.Prepara(Test(), config, Veicoli, Funzioni, [], CatalogoEcu, manualiCaso)
            .CalcolaTotaleGenerale(marcatureCaso);
        var atteso = ReportMatriceService.Genera(Test(), config, Veicoli, Funzioni, [], marcatureCaso, CatalogoEcu, manualiCaso)
            .TotaleGenerale;

        Assert.Equal(Riepilogo(atteso), Riepilogo(totale));
    }

    [Fact]
    public void Esito_NonTestatoSoloSeUnaCombinazioneDelCatalogoNonEVerificata()
    {
        var configurazione = new ConfigurazioneMatriceReport
        {
            CampiX = [DimensioniMatrice.Componente],
            CampiY = [DimensioniMatrice.Funzionalita],
            ModalitaCella = ModalitaCellaMatrice.Stato,
        };
        var componente = new List<Componente> { new() { Nome = "E1", VeicoliAssociati = ["V1"] } };
        var testCoperto = new List<TestCase> { Test("T-1", "E1", "V1", "F1", new DateTime(2026, 1, 1), Esito.Conforme) };
        var funzionalitaCoperta = new List<Funzionalita> { new() { Nome = "F1" } };
        var funzionalitaConUnaNonTestata = new List<Funzionalita> { new() { Nome = "F1" }, new() { Nome = "F9" } };

        // Unica combinazione del catalogo (E1 x F1 x V1), coperta dall'unico Test eseguito: nessun "Non testato" dall'universo.
        var coperto = ReportMatriceService.ValoriDisponibiliPerDimensioni([DimensioniMatrice.Esito], testCoperto, Veicoli,
            funzionalitaCoperta, [], configurazione, componente);
        Assert.DoesNotContain("Non testato", coperto[DimensioniMatrice.Esito]);

        // Aggiungendo F9 (non testata) compare "Non testato".
        var scoperto = ReportMatriceService.ValoriDisponibiliPerDimensioni([DimensioniMatrice.Esito], testCoperto, Veicoli,
            funzionalitaConUnaNonTestata, [], configurazione, componente);
        Assert.Contains("Non testato", scoperto[DimensioniMatrice.Esito]);
    }
}
