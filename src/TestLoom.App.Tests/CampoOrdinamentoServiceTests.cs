using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class CampoOrdinamentoServiceTests
{
    private class Voce : IOrdinabile
    {
        public string Nome { get; set; } = "";
        public int Ordine { get; set; }
        public override string ToString() => Nome;
    }

    private static List<Voce> Lista(params string[] nomi) => nomi.Select(n => new Voce { Nome = n }).ToList();

    private static IEnumerable<string> Visuale(IEnumerable<Voce> lista) =>
        CampoOrdinamentoService.OrdinePerVisualizzazione(lista).Select(v => v.Nome);

    // ----- OrdinePerVisualizzazione: lettura pura, mai una mutazione (revisione F2) -----------------------

    [Fact]
    public void OrdinePerVisualizzazione_ConOrdineTuttoAZero_MantieneLOrdineDiProvenienza()
    {
        // Dati legacy (creati prima di questa funzionalità, o mai riordinati): Ordine=0 per tutti è il
        // default di un int non impostato. OrderBy è uno stable sort: a parità di chiave l'ordine relativo
        // della sorgente resta invariato, quindi la vista è già corretta senza alcuna migrazione scritta.
        var lista = Lista("A", "B", "C");

        Assert.Equal(new[] { "A", "B", "C" }, Visuale(lista));
        Assert.All(lista, v => Assert.Equal(0, v.Ordine)); // nessuna mutazione: la sola lettura non scrive nulla
    }

    [Fact]
    public void OrdinePerVisualizzazione_NonMutaGliElementiNéLaListaSorgente()
    {
        var lista = Lista("A", "B", "C");
        foreach (var v in lista) v.Ordine = 7;

        CampoOrdinamentoService.OrdinePerVisualizzazione(lista);

        Assert.All(lista, v => Assert.Equal(7, v.Ordine));
        Assert.Equal(new[] { "A", "B", "C" }, lista.Select(v => v.Nome)); // lista fisica intatta
    }

    [Fact]
    public void OrdinePerVisualizzazione_RispettaOrdineCrescente()
    {
        var lista = Lista("A", "B", "C");
        lista[0].Ordine = 5; lista[1].Ordine = 1; lista[2].Ordine = 3;

        Assert.Equal(new[] { "B", "C", "A" }, Visuale(lista));
    }

    // ----- SpostaSu/SpostaGiu: azione utente esplicita, opera sull'ordine di visualizzazione ---------------

    [Fact]
    public void SpostaSu_ScambiaConIlPrecedenteNellOrdineDiVisualizzazione()
    {
        var lista = Lista("A", "B", "C");

        var risultato = CampoOrdinamentoService.SpostaSu(lista, lista[1]);

        Assert.True(risultato);
        Assert.Equal(new[] { "B", "A", "C" }, Visuale(lista));
    }

    [Fact]
    public void SpostaSu_NessunEffettoSeGiaInCima()
    {
        var lista = Lista("A", "B", "C");

        var risultato = CampoOrdinamentoService.SpostaSu(lista, lista[0]);

        Assert.False(risultato);
        Assert.Equal(new[] { "A", "B", "C" }, Visuale(lista));
    }

    [Fact]
    public void SpostaSu_NessunEffettoSeElementoNonInLista()
    {
        var lista = Lista("A", "B");
        var estraneo = new Voce { Nome = "X" };

        var risultato = CampoOrdinamentoService.SpostaSu(lista, estraneo);

        Assert.False(risultato);
        Assert.Equal(new[] { "A", "B" }, Visuale(lista));
    }

    [Fact]
    public void SpostaSu_FunzionaAncheConOrdineLegacyTuttoAZero()
    {
        // Il caso che la versione precedente (basata sulla posizione fisica) gestiva già, ma che una
        // versione ingenua basata solo su Ordine romperebbe: più elementi con la stessa chiave (0,0,0).
        var lista = Lista("A", "B", "C");

        CampoOrdinamentoService.SpostaSu(lista, lista[1]); // "B" su, sopra "A"

        Assert.Equal(new[] { "B", "A", "C" }, Visuale(lista));
    }

    [Fact]
    public void SpostaGiu_ScambiaConIlSuccessivo()
    {
        var lista = Lista("A", "B", "C");

        var risultato = CampoOrdinamentoService.SpostaGiu(lista, lista[0]);

        Assert.True(risultato);
        Assert.Equal(new[] { "B", "A", "C" }, Visuale(lista));
    }

    [Fact]
    public void SpostaGiu_NessunEffettoSeGiaInFondo()
    {
        var lista = Lista("A", "B", "C");

        var risultato = CampoOrdinamentoService.SpostaGiu(lista, lista[2]);

        Assert.False(risultato);
        Assert.Equal(new[] { "A", "B", "C" }, Visuale(lista));
    }

    [Fact]
    public void SpostaSu_NonAlteraLaPosizioneFisicaNellaListaSorgente()
    {
        // Revisione F2: Su/Giu mutano solo Ordine, mai la posizione fisica nella List<T> — così un consumer
        // che (per errore o per scelta) iteri ancora la lista fisica invece di OrdinePerVisualizzazione non
        // vede un cambiamento silenzioso di quell'ordine.
        var lista = Lista("A", "B", "C");

        CampoOrdinamentoService.SpostaSu(lista, lista[1]);

        Assert.Equal(new[] { "A", "B", "C" }, lista.Select(v => v.Nome));
    }

    // ----- InserisciInPosizione ------------------------------------------------------------------------

    [Fact]
    public void InserisciInPosizione_SenzaParametri_VaInFondo()
    {
        var lista = Lista("A", "B");
        var nuovo = new Voce { Nome = "C" };

        CampoOrdinamentoService.InserisciInPosizione(lista, nuovo);

        Assert.Equal(new[] { "A", "B", "C" }, Visuale(lista));
    }

    [Fact]
    public void InserisciInPosizione_Inizio_VaInCima()
    {
        var lista = Lista("A", "B");
        var nuovo = new Voce { Nome = "Z" };

        CampoOrdinamentoService.InserisciInPosizione(lista, nuovo, inizio: true);

        Assert.Equal(new[] { "Z", "A", "B" }, Visuale(lista));
    }

    [Fact]
    public void InserisciInPosizione_Dopo_VaSubitoDopoLElementoIndicato()
    {
        var lista = Lista("A", "B", "C");
        var nuovo = new Voce { Nome = "X" };

        CampoOrdinamentoService.InserisciInPosizione(lista, nuovo, dopo: lista[0]);

        Assert.Equal(new[] { "A", "X", "B", "C" }, Visuale(lista));
    }

    [Fact]
    public void InserisciInPosizione_DopoElementoNonInLista_VaInFondo()
    {
        var lista = Lista("A", "B");
        var nuovo = new Voce { Nome = "X" };
        var estraneo = new Voce { Nome = "Estraneo" };

        CampoOrdinamentoService.InserisciInPosizione(lista, nuovo, dopo: estraneo);

        Assert.Equal(new[] { "A", "B", "X" }, Visuale(lista));
    }

    [Fact]
    public void InserisciInPosizione_AggiungeLElementoAllaListaFisicaSorgente()
    {
        var lista = Lista("A", "B");
        var nuovo = new Voce { Nome = "X" };

        CampoOrdinamentoService.InserisciInPosizione(lista, nuovo, dopo: lista[0]);

        Assert.Contains(nuovo, lista);
    }

    // ----- Normalizza ------------------------------------------------------------------------------------

    [Fact]
    public void Normalizza_DatiSenzaOrdine_LoRiallineaAllaVistaAttualeSenzaSpostareNulla()
    {
        var lista = Lista("A", "B", "C");

        CampoOrdinamentoService.Normalizza(lista);

        Assert.Equal(new[] { "A", "B", "C" }, Visuale(lista));
        Assert.Equal(new[] { 0, 1, 2 }, lista.Select(v => v.Ordine));
    }

    [Fact]
    public void Normalizza_EIdempotente()
    {
        var lista = Lista("A", "B", "C");
        CampoOrdinamentoService.Normalizza(lista);
        var primaPassata = lista.Select(v => v.Ordine).ToArray();

        CampoOrdinamentoService.Normalizza(lista);

        Assert.Equal(primaPassata, lista.Select(v => v.Ordine));
    }
}
