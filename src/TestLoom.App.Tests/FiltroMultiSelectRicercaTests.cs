using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copre la logica di ricerca/selezione estratta da FiltroMultiSelectFlyoutHelper per il nuovo filtro
/// virtualizzato (Addendum performance 2026-09-25), indipendente dalla UI: ricerca case-insensitive su TUTTE le
/// M voci (mai troncata), selezione preservata cambiando ricerca, "seleziona tutto" con/senza ricerca attiva,
/// nessuna perdita di selezioni pregresse, valori speciali (etichette vuote o con testo tipo "Test senza componente
/// (da correggere)").</summary>
public class FiltroMultiSelectRicercaTests
{
    private static FiltroMultiSelect CreaFiltro(params string[] etichette)
    {
        var filtro = new FiltroMultiSelect();
        filtro.Popola(etichette.Select(e => (Valore: e, Etichetta: e)));
        return filtro;
    }

    [Fact]
    public void SenzaRicerca_MostraTutteLeVociNellOrdineOriginale()
    {
        var filtro = CreaFiltro("Alfa", "Beta", "Gamma");
        var ricerca = new FiltroMultiSelectRicerca(filtro);

        var visibili = ricerca.VociVisibili();

        Assert.Equal(new[] { "Alfa", "Beta", "Gamma" }, visibili.Select(v => v.Etichetta));
        Assert.Equal("3", ricerca.TestoConteggio());
    }

    [Fact]
    public void Ricerca_CaseInsensitive_SuTutteLeMVoci()
    {
        // 1000 voci: la ricerca deve trovarne una qualunque, anche fuori dalla prima schermata (Addendum
        // performance: mai limitare/troncare la lista che la ricerca ispeziona).
        var etichette = Enumerable.Range(0, 1000).Select(i => $"Voce {i:D4}").ToArray();
        var filtro = CreaFiltro(etichette);
        var ricerca = new FiltroMultiSelectRicerca(filtro) { Ricerca = "voce 0999" };

        var visibili = ricerca.VociVisibili();

        Assert.Single(visibili);
        Assert.Equal("Voce 0999", visibili[0].Etichetta);
        Assert.Equal("1 di 1000", ricerca.TestoConteggio());
    }

    [Fact]
    public void CambiareRicerca_NonPerdeLeSelezioniPregresse()
    {
        var filtro = CreaFiltro("Alfa", "Beta", "Gamma");
        filtro.Opzioni.First(o => o.Etichetta == "Beta").Selezionato = true;
        var ricerca = new FiltroMultiSelectRicerca(filtro);

        ricerca.Ricerca = "Al"; // nasconde Beta, ma la selezione resta nel modello, non nel controllo
        Assert.DoesNotContain(ricerca.VociVisibili(), v => v.Etichetta == "Beta");

        ricerca.Ricerca = ""; // tornando a mostrare tutto, Beta e' ancora selezionata
        var beta = ricerca.VociVisibili().Single(v => v.Etichetta == "Beta");
        Assert.True(beta.Selezionato);
    }

    [Fact]
    public void SelezionaTutto_AgisceSulleVociVisibiliEPreservaSelezioniNascoste()
    {
        var filtro = CreaFiltro("Alfa", "Beta", "Gamma");
        filtro.Opzioni.Single(o => o.Etichetta == "Gamma").Selezionato = true;
        var ricerca = new FiltroMultiSelectRicerca(filtro) { Ricerca = "Al" }; // solo "Alfa" visibile

        ricerca.SelezionaTutto();

        Assert.True(filtro.Opzioni.Single(o => o.Etichetta == "Alfa").Selezionato);
        Assert.False(filtro.Opzioni.Single(o => o.Etichetta == "Beta").Selezionato);
        Assert.True(filtro.Opzioni.Single(o => o.Etichetta == "Gamma").Selezionato);
    }

    [Fact]
    public void DeselezionaTutto_AgisceSulleVociVisibiliEPreservaSelezioniNascoste()
    {
        var filtro = CreaFiltro("Alfa", "Beta", "Gamma");
        filtro.SelezionaTutto();
        var ricerca = new FiltroMultiSelectRicerca(filtro) { Ricerca = "Al" };

        ricerca.DeselezionaTutto();

        Assert.False(filtro.Opzioni.Single(o => o.Etichetta == "Alfa").Selezionato);
        Assert.True(filtro.Opzioni.Single(o => o.Etichetta == "Beta").Selezionato);
        Assert.True(filtro.Opzioni.Single(o => o.Etichetta == "Gamma").Selezionato);
    }

    [Fact]
    public void SelezionaTutto_ConsenteDiTogliereUnaVoceSenzaCambiareLeAltre()
    {
        var filtro = CreaFiltro("Alfa", "Altro", "Beta");
        filtro.Opzioni.Single(o => o.Etichetta == "Beta").Selezionato = true;
        var ricerca = new FiltroMultiSelectRicerca(filtro) { Ricerca = "Al" };

        ricerca.SelezionaTutto();
        filtro.Opzioni.Single(o => o.Etichetta == "Altro").Selezionato = false;

        Assert.True(filtro.Opzioni.Single(o => o.Etichetta == "Alfa").Selezionato);
        Assert.False(filtro.Opzioni.Single(o => o.Etichetta == "Altro").Selezionato);
        Assert.True(filtro.Opzioni.Single(o => o.Etichetta == "Beta").Selezionato);
    }

    [Fact]
    public void ValoriSpeciali_EtichetteConParentesiEVuote_SonoRicercabili()
    {
        var filtro = CreaFiltro("Test senza componente (da correggere)", "", "  ", "Componente-1234");
        var ricerca = new FiltroMultiSelectRicerca(filtro) { Ricerca = "senza componente" };

        var visibili = ricerca.VociVisibili();

        Assert.Single(visibili);
        Assert.Equal("Test senza componente (da correggere)", visibili[0].Etichetta);
    }

    [Fact]
    public void RicercaVuotaOSpazi_NonNascondeNulla()
    {
        var filtro = CreaFiltro("Alfa", "Beta");
        var ricerca = new FiltroMultiSelectRicerca(filtro) { Ricerca = "   " };

        Assert.Equal(2, ricerca.VociVisibili().Count);
        Assert.Equal("2", ricerca.TestoConteggio());
    }

    [Fact]
    public void FiltroSenzaVoci_ConteggioEListaVuoti()
    {
        var filtro = CreaFiltro();
        var ricerca = new FiltroMultiSelectRicerca(filtro);

        Assert.Empty(ricerca.VociVisibili());
        Assert.Equal("0", ricerca.TestoConteggio());
    }

    /// <summary>Addendum prestazioni 2026-09-25 (Home a 100k): quando i valori letti sono IDENTICI (stesso
    /// contenuto, stesso ordine) a quelli gia' mostrati, Ripopola non deve ricreare le FiltroOpzione — altrimenti
    /// un Clear+Add da ~1000 voci per 7 filtri si ripete a ogni rivisita anche a dati invariati. Qui si verifica
    /// tramite l'identita' per-riferimento delle FiltroOpzione, non solo il contenuto risultante.</summary>
    [Fact]
    public void Ripopola_ContenutoIdentico_NonRicreaLeOpzioni()
    {
        var filtro = CreaFiltro("Alfa", "Beta", "Gamma");
        var opzionePrima = filtro.Opzioni.First(o => o.Etichetta == "Beta");
        opzionePrima.Selezionato = true;

        filtro.Ripopola(new[] { "Alfa", "Beta", "Gamma" }.Select(x => (x, x)));

        Assert.Same(opzionePrima, filtro.Opzioni.First(o => o.Etichetta == "Beta"));
        Assert.True(filtro.Opzioni.First(o => o.Etichetta == "Beta").Selezionato);
        Assert.Equal(new[] { "Alfa", "Beta", "Gamma" }, filtro.Opzioni.Select(o => o.Etichetta));
    }

    /// <summary>Contenuto realmente cambiato (una voce nuova dall'archivio, es. una seconda postazione che ha
    /// scritto un nuovo valore): Ripopola deve comunque aggiornare la lista e preservare la selezione pregressa,
    /// esattamente come prima dell'ottimizzazione del confronto di contenuto.</summary>
    [Fact]
    public void Ripopola_ContenutoDiverso_AggiornaEPreservaLaSelezione()
    {
        var filtro = CreaFiltro("Alfa", "Beta");
        filtro.Opzioni.First(o => o.Etichetta == "Beta").Selezionato = true;

        filtro.Ripopola(new[] { "Alfa", "Beta", "Gamma" }.Select(x => (x, x)));

        Assert.Equal(new[] { "Alfa", "Beta", "Gamma" }, filtro.Opzioni.Select(o => o.Etichetta));
        Assert.True(filtro.Opzioni.First(o => o.Etichetta == "Beta").Selezionato);
    }

    /// <summary>Stesso insieme di valori ma un'etichetta cambiata (es. rinomina di un catalogo): non e' un
    /// contenuto identico, la lista va comunque rigenerata.</summary>
    [Fact]
    public void Ripopola_EtichettaCambiata_NonEConsiderataContenutoIdentico()
    {
        var filtro = CreaFiltro("Alfa", "Beta");

        filtro.Ripopola(new[] { ("Alfa", "Alfa"), ("Beta", "Beta rinominata") });

        Assert.Equal("Beta rinominata", filtro.Opzioni.Single(o => o.Valore == "Beta").Etichetta);
    }
}
