using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class CampiClassiciOrdineServiceTests
{
    private static readonly string[] Chiavi = { "A", "B", "C" };

    // ----- CalcolaRango --------------------------------------------------------------------------------

    [Fact]
    public void CalcolaRango_UsaOrdineTemplateFalso_RestituisceLOrdineDiDefault()
    {
        // Prima della migrazione (flag falso) il rango è sempre quello fisso storico, indipendentemente da
        // qualunque Ordine presente nel modello — nessun cambio visibile finché l'utente non riordina.
        var rango = CampiClassiciOrdineService.CalcolaRango(Chiavi, chiave => chiave == "A" ? 99 : 0, usaOrdineTemplate: false);

        Assert.Equal(0, rango["A"]);
        Assert.Equal(1, rango["B"]);
        Assert.Equal(2, rango["C"]);
    }

    [Fact]
    public void CalcolaRango_UsaOrdineTemplateVero_OrdinaPerOrdineDelCampo()
    {
        var ordini = new Dictionary<string, int?> { ["A"] = 2, ["B"] = 0, ["C"] = 1 };
        var rango = CampiClassiciOrdineService.CalcolaRango(Chiavi, k => ordini[k], usaOrdineTemplate: true);

        Assert.Equal(0, rango["B"]);
        Assert.Equal(1, rango["C"]);
        Assert.Equal(2, rango["A"]);
    }

    [Fact]
    public void CalcolaRango_ChiaveSenzaCampoTrovato_VaInFondoNellOrdineDiDefault()
    {
        // Template incompleto/dati di test: una chiave del gruppo senza CampoTemplate corrispondente non deve
        // sollevare un'eccezione, va semplicemente in fondo nell'ordine di default.
        var ordini = new Dictionary<string, int?> { ["A"] = 5, ["B"] = null, ["C"] = 1 };
        var rango = CampiClassiciOrdineService.CalcolaRango(Chiavi, k => ordini[k], usaOrdineTemplate: true);

        Assert.Equal(0, rango["C"]);
        Assert.Equal(1, rango["A"]);
        Assert.Equal(2, rango["B"]);
    }

    [Fact]
    public void CalcolaRango_ParitaDiOrdine_OrdinamentoStabile()
    {
        var rango = CampiClassiciOrdineService.CalcolaRango(Chiavi, _ => 0, usaOrdineTemplate: true);

        Assert.Equal(0, rango["A"]);
        Assert.Equal(1, rango["B"]);
        Assert.Equal(2, rango["C"]);
    }

    // ----- CorreggiCompatibilitaVeicoloEcu --------------------------------------------------------------

    private static TestTemplate TemplateConDettagli(int ordineEcu, int ordineVeicolo, bool giaRiordinato = false)
    {
        var template = new TestTemplate { CampiClassiciDettagliRiordinati = giaRiordinato };
        var sezione = new SezioneTemplate { Nome = "Dettagli" };
        sezione.Campi.Add(new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.Componente, Ordine = ordineEcu });
        sezione.Campi.Add(new CampoTemplate { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = CampiSistema.Veicolo, Ordine = ordineVeicolo });
        template.Sezioni.Add(sezione);
        return template;
    }

    [Fact]
    public void CorreggiCompatibilitaVeicoloEcu_OrdineStoricoEcuPrimaDiVeicolo_LiScambia()
    {
        // Stato di ogni template mai toccato dalla 6.3.1 (seed 6.3.0: Componente Ordine=0, Veicolo Ordine=1).
        var template = TemplateConDettagli(ordineEcu: 0, ordineVeicolo: 1);

        CampiClassiciOrdineService.CorreggiCompatibilitaVeicoloEcu(template);

        var componente = template.Sezioni[0].Campi.First(c => c.CampoSistemaChiave == CampiSistema.Componente);
        var veicolo = template.Sezioni[0].Campi.First(c => c.CampoSistemaChiave == CampiSistema.Veicolo);
        Assert.True(veicolo.Ordine < componente.Ordine);
        Assert.True(template.CampiClassiciDettagliRiordinati);
    }

    [Fact]
    public void CorreggiCompatibilitaVeicoloEcu_Idempotente_SecondaChiamataNonCambiaNulla()
    {
        var template = TemplateConDettagli(ordineEcu: 0, ordineVeicolo: 1);
        CampiClassiciOrdineService.CorreggiCompatibilitaVeicoloEcu(template);
        var veicoloOrdineDopoPrima = template.Sezioni[0].Campi.First(c => c.CampoSistemaChiave == CampiSistema.Veicolo).Ordine;
        var ecuOrdineDopoPrima = template.Sezioni[0].Campi.First(c => c.CampoSistemaChiave == CampiSistema.Componente).Ordine;

        CampiClassiciOrdineService.CorreggiCompatibilitaVeicoloEcu(template);

        Assert.Equal(veicoloOrdineDopoPrima, template.Sezioni[0].Campi.First(c => c.CampoSistemaChiave == CampiSistema.Veicolo).Ordine);
        Assert.Equal(ecuOrdineDopoPrima, template.Sezioni[0].Campi.First(c => c.CampoSistemaChiave == CampiSistema.Componente).Ordine);
    }

    [Fact]
    public void CorreggiCompatibilitaVeicoloEcu_FlagGiaVero_NonToccaOrdineAncheSeEcuPrimaDiVeicolo()
    {
        // Un utente che ha già riordinato esplicitamente (flag vero) e scelto Componente prima di Veicolo: la
        // correzione una tantum non deve mai ribaltare una scelta esplicita successiva alla migrazione.
        var template = TemplateConDettagli(ordineEcu: 0, ordineVeicolo: 1, giaRiordinato: true);

        CampiClassiciOrdineService.CorreggiCompatibilitaVeicoloEcu(template);

        var componente = template.Sezioni[0].Campi.First(c => c.CampoSistemaChiave == CampiSistema.Componente);
        var veicolo = template.Sezioni[0].Campi.First(c => c.CampoSistemaChiave == CampiSistema.Veicolo);
        Assert.True(componente.Ordine < veicolo.Ordine); // invariato
    }

    [Fact]
    public void CorreggiCompatibilitaVeicoloEcu_VeicoloGiaPrimaDiEcu_NonliTocca()
    {
        var template = TemplateConDettagli(ordineEcu: 5, ordineVeicolo: 1);

        CampiClassiciOrdineService.CorreggiCompatibilitaVeicoloEcu(template);

        var componente = template.Sezioni[0].Campi.First(c => c.CampoSistemaChiave == CampiSistema.Componente);
        var veicolo = template.Sezioni[0].Campi.First(c => c.CampoSistemaChiave == CampiSistema.Veicolo);
        Assert.Equal(1, veicolo.Ordine);
        Assert.Equal(5, componente.Ordine);
        Assert.True(template.CampiClassiciDettagliRiordinati);
    }

    [Fact]
    public void CorreggiCompatibilitaVeicoloEcu_SezioneSenzaEcuOVeicolo_NonSolleveEccezioni()
    {
        var template = new TestTemplate();
        template.Sezioni.Add(new SezioneTemplate { Nome = "Dettagli" });

        var eccezione = Record.Exception(() => CampiClassiciOrdineService.CorreggiCompatibilitaVeicoloEcu(template));

        Assert.Null(eccezione);
        Assert.True(template.CampiClassiciDettagliRiordinati);
    }
}
