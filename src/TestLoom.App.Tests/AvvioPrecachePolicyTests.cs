using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Non far competere il precache differito con una pagina in uso,
/// il refresh iniziale o il login: la velocita' di avvio viene prima del JIT delle pagine secondarie.</summary>
public sealed class AvvioPrecachePolicyTests
{
    private static bool Valuta(
        bool cartella = true, bool bootstrap = true, bool primoAvvio = false,
        bool onboarding = false, bool caricamento = false, bool aggiornamento = false,
        bool editorConModifiche = false, bool paginaVisibile = true) =>
        AvvioPrecachePolicy.PuoScaldare(cartella, bootstrap, primoAvvio,
            onboarding, caricamento, aggiornamento, editorConModifiche, paginaVisibile);

    [Fact]
    public void CodaPrecache_IncludeTutteLePagineDellaSidebarEccettoLaPrima()
    {
        var voci = new[] { "home", "test", "anomalie", "wustatus", "guasto", "report", "audit", "impostazioni" };
        Assert.Equal(new[] { "test", "anomalie", "wustatus", "guasto", "report", "audit", "impostazioni" },
            AvvioPrecachePolicy.CreaCodaPagine(voci, "home"));

        // Un operatore puo' aver riordinato la barra: la prima destinazione
        // resta esclusa indipendentemente dal suo ID.
        Assert.Equal(new[] { "home", "test", "wustatus", "guasto", "report", "audit", "impostazioni" },
            AvvioPrecachePolicy.CreaCodaPagine(voci, "anomalie"));
    }

    [Fact]
    public void CodaPrecache_NonDuplicaLeDestinazioniEdESicuraConScelteVuote()
    {
        Assert.Equal(new[] { "test", "report" },
            AvvioPrecachePolicy.CreaCodaPagine(new[] { "home", "test", "TEST", "report", "  " }, "home"));
        Assert.Empty(AvvioPrecachePolicy.CreaCodaPagine(Array.Empty<string>(), "home"));
    }

    [Fact]
    public void Precache_PermessoSoloConPaginaDisponibileEWorkspacePronto()
    {
        Assert.True(Valuta());
        Assert.False(Valuta(cartella: false));
        Assert.False(Valuta(bootstrap: false));
        Assert.False(Valuta(primoAvvio: true));
        Assert.False(Valuta(onboarding: true));
        Assert.False(Valuta(caricamento: true));
        Assert.False(Valuta(aggiornamento: true));
        Assert.False(Valuta(editorConModifiche: true));
        Assert.False(Valuta(paginaVisibile: false));
    }

    [Fact]
    public void NavigazioneInCorso_EEditorSporco_NonAvvianoNuovaPagina()
    {
        // Mentre la pagina viene caricata, o l'utente modifica un editor,
        // le pagine rimanenti non devono essere preparate.
        Assert.False(Valuta(paginaVisibile: false));
        Assert.False(Valuta(caricamento: true));
        Assert.False(Valuta(editorConModifiche: true));
        // Quando la navigazione termina, puo' proseguire anche fuori dalla Home.
        Assert.True(Valuta());
    }
}
