using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Ordinamento per colonna della tabella Change Request: sull'intero risultato, deterministico a parita' di chiave.</summary>
public class ChangeRequestOrdinamentoTests
{
    private static ChangeRequest Cr(string id, DateTime creata, DateTime? implementata = null, DateTime? chiusa = null, params string[] veicoli) => new()
    {
        Id = id, Titolo = "T " + id, DataCreazione = creata, ImplementataIl = implementata, ChiusaIl = chiusa,
        VeicoliImpattati = veicoli.ToList(),
    };

    private static IReadOnlyList<string> Ordina(IReadOnlyList<ChangeRequest> crs, ChangeRequestOrdinamentoChiave chiave, bool decrescente)
    {
        var filtro = new NonConformitaViewModel.FiltroChangeRequest(null, null, null, null, null, null, false, null, null, null, null, null, null, 1,
            Ordinamento: new ChangeRequestOrdinamento(chiave, decrescente));
        return NonConformitaViewModel.CalcolaChangeRequestsFiltrate(crs, filtro, null).Select(c => c.Id).ToList();
    }

    private static readonly List<ChangeRequest> Dati = new()
    {
        Cr("CR-1", new DateTime(2026, 1, 3), null, null, "Daily", "Bus"),
        Cr("CR-2", new DateTime(2026, 1, 1), new DateTime(2026, 2, 1), new DateTime(2026, 3, 1), "Crafter"),
        Cr("CR-3", new DateTime(2026, 1, 2), new DateTime(2026, 2, 5)),
        Cr("CR-4", new DateTime(2026, 1, 2), null, new DateTime(2026, 3, 9), "Atlas"),
    };

    [Fact]
    public void PredefinitoPerAperturaDecrescente_ConSpareggioDeterministico()
    {
        // CR-3 e CR-4 hanno la stessa apertura: a parita' decide l'Id, nella stessa direzione dell'ordinamento.
        Assert.Equal(new[] { "CR-1", "CR-4", "CR-3", "CR-2" }, Ordina(Dati, ChangeRequestOrdinamentoChiave.Apertura, true));
        Assert.Equal(new[] { "CR-2", "CR-3", "CR-4", "CR-1" }, Ordina(Dati, ChangeRequestOrdinamentoChiave.Apertura, false));
    }

    [Fact]
    public void PerVeicolo_UsaIlPrimoInOrdineAlfabeticoENessunVeicoloVienePrima()
    {
        // CR-1 ha {Bus, Daily}: il primo e' Bus. Nessun veicolo (CR-3) precede tutti in ordine crescente.
        Assert.Equal(new[] { "CR-3", "CR-4", "CR-1", "CR-2" }, Ordina(Dati, ChangeRequestOrdinamentoChiave.Veicolo, false));
    }

    [Fact]
    public void PerDateOpzionali_LeCrSenzaDataStannoInFondoInOrdineDecrescente()
    {
        // CR-1 e CR-4 non hanno la data: a parita' decide l'Id, nella stessa direzione.
        Assert.Equal(new[] { "CR-3", "CR-2", "CR-4", "CR-1" }, Ordina(Dati, ChangeRequestOrdinamentoChiave.Implementazione, true));
        Assert.Equal(new[] { "CR-4", "CR-2", "CR-3", "CR-1" }, Ordina(Dati, ChangeRequestOrdinamentoChiave.Chiusura, true));
    }

    [Fact]
    public void ElencoVeicoli_PrimoPiuConteggio()
    {
        var c = VeicoliSommarioConverter.Instance;
        Assert.Equal("Bus +1", c.Convert(new List<string> { "Daily", "Bus" }, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Atlas", c.Convert(new List<string> { "Atlas" }, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("", c.Convert(new List<string>(), typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>Ordinamento delle Anomalie per le chiavi che dipendono dal Test collegato (non presenti nell'indice SQLite).</summary>
public class AnomalieOrdinamentoConTestTests
{
    private static (NonConformita, TestCase?, string) Riga(string id, string testId, string? breve, params string[] veicoli) =>
        (new NonConformita { Id = id, DescrizioneBreveSegnalata = breve ?? "" }, new TestCase { Id = testId, Veicoli = veicoli.ToList() }, testId);

    private static List<string> Ordina(AnomalieOrdinamentoChiave chiave, bool decrescente, params (NonConformita Nc, TestCase? Test, string TestId)[] righe)
    {
        var l = righe.ToList();
        l.Sort(AnomalieOrdinamentoConTestComparer.Crea(new AnomalieOrdinamento(chiave, decrescente)));
        return l.Select(r => r.Nc.Id).ToList();
    }

    [Fact]
    public void PerVeicolo_UsaIlPrimoInOrdineAlfabeticoEPareggiaPerId()
    {
        var a = Riga("NC-2", "T1", null, "Daily", "Bus");
        var b = Riga("NC-1", "T2", null, "Bus");
        var c = Riga("NC-3", "T3", null);
        Assert.Equal(new[] { "NC-3", "NC-1", "NC-2" }, Ordina(AnomalieOrdinamentoChiave.Veicolo, false, a, b, c));
        Assert.Equal(new[] { "NC-2", "NC-1", "NC-3" }, Ordina(AnomalieOrdinamentoChiave.Veicolo, true, a, b, c));
    }

    [Fact]
    public void VeicoliDellaRete_VisibiliEOrdinatiAncheConTestCollegato()
    {
        var nc = new NonConformita { Id = "NC-1", DaSegnalazioneRete = true, VeicoliSegnalati = ["Daily", "Bus"] };
        var senzaTest = new NcRiga { Nc = nc };
        Assert.Equal("Bus +1", senzaTest.VeicoliTesto);
        Assert.Equal(string.Join(Environment.NewLine, "Bus", "Daily"), senzaTest.VeicoliElencoTooltip);

        var test = new TestCase { Veicoli = ["bus", "Eurocargo"] };
        var collegata = new NcRiga { Nc = nc, TestCase = test };
        Assert.Equal("Bus +2", collegata.VeicoliTesto);
        Assert.Equal(string.Join(Environment.NewLine, "Bus", "Daily", "Eurocargo"), collegata.VeicoliElencoTooltip);
        var altra = Riga("NC-2", "T2", null, "Daily");
        Assert.Equal(new[] { "NC-1", "NC-2" }, Ordina(AnomalieOrdinamentoChiave.Veicolo, false, (nc, test, "T1"), altra));
    }

    [Fact]
    public void PerTestCollegato_EPerDescrizioneBreve()
    {
        var a = Riga("NC-1", "T9", "zeta");
        var b = Riga("NC-2", "T1", "Alfa");
        Assert.Equal(new[] { "NC-2", "NC-1" }, Ordina(AnomalieOrdinamentoChiave.TestCollegato, false, a, b));
        Assert.Equal(new[] { "NC-2", "NC-1" }, Ordina(AnomalieOrdinamentoChiave.DescrizioneBreve, false, a, b));
    }

    [Fact]
    public void ChiaviCheRichiedonoIlTest_SonoQuattro()
    {
        Assert.Equal(4, Enum.GetValues<AnomalieOrdinamentoChiave>().Count(AnomalieOrdinamentoChiaveEstensioni.RichiedeTest));
    }
}
