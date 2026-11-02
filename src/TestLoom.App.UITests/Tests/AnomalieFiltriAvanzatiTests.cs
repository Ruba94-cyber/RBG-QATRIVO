using System.Linq;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Pagina Anomalie 6.5.0: filtri multipli in AND (priorita', gravita', stato, assegnatario, funzionalita',
/// intervalli di data), azzeramento con un clic, ordinamento per colonna, colonne personalizzabili, persistenza
/// dopo riavvio. Semina le NC direttamente via repository (stessa tecnica di NonConformitaWorkflowTests).
/// Etichette in italiano (lingua di default del workspace di prova).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
public sealed class AnomalieFiltriAvanzatiTests : UiTestBase
{
    public AnomalieFiltriAvanzatiTests() : base(CreaWorkspace()) { }

    private static NonConformita Nc(string n, PrioritaOperativa p, SeveritaAnomalia s, string owner, DateTime apertura,
        StatoAnomalia w = StatoAnomalia.Confermata, DateTime? chiusura = null, string? funzionalita = null)
    {
        var nc = new NonConformita
        {
            Id = $"NC-QA-{n}", Priorita = p, Severita = s, Owner = owner, DataApertura = apertura, StatoWorkflow = w,
            DataChiusura = chiusura, Descrizione = $"Anomalia di prova {n}", TestCaseId = ""
        };
        if (funzionalita != null) nc.FunzionalitaSegnalate.Add(funzionalita);
        return nc;
    }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(AnomalieFiltriAvanzatiTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Climate" }, new() { Nome = "Radio" } });
        state.NonConformita.Save(Nc("01", PrioritaOperativa.Critical, SeveritaAnomalia.High, "Mario", new DateTime(2026, 3, 10)));
        state.NonConformita.Save(Nc("02", PrioritaOperativa.Major, SeveritaAnomalia.High, "Lucia", new DateTime(2026, 3, 15, 17, 0, 0), funzionalita: "Climate"));
        state.NonConformita.Save(Nc("03", PrioritaOperativa.Minor, SeveritaAnomalia.Low, "Mario", new DateTime(2026, 4, 1)));
        state.NonConformita.Save(Nc("04", PrioritaOperativa.Critical, SeveritaAnomalia.Low, "Lucia", new DateTime(2026, 5, 1), StatoAnomalia.InVerifica));
        state.NonConformita.Save(Nc("05", PrioritaOperativa.Normal, SeveritaAnomalia.Medium, "Giorgio", new DateTime(2026, 6, 1),
            StatoAnomalia.Conclusa, new DateTime(2026, 6, 10)));
        return workspace;
    }

    private static string[] Ids(params string[] numeri) => numeri.Select(n => $"NC-QA-{n}").ToArray();

    private static void AttendiIds(NonConformitaPage p, string descrizione, params string[] numeri) =>
        Wait.ForCondition(() => p.IdRighe().OrderBy(x => x).SequenceEqual(Ids(numeri).OrderBy(x => x)), descrizione);

    [Fact]
    public void FiltriMultipli_SiCumulanoInAnd_ESiAzzeranoConUnClic()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            AttendiIds(anomalie, "default: solo le aperte", "01", "02", "03", "04");

            anomalie.SelezionaVoci(App, "Priorita", "Critical", "Major");
            AttendiIds(anomalie, "priorita' Critical o Major", "01", "02", "04");

            anomalie.SelezionaVoci(App, "Gravita", "Alta");
            AttendiIds(anomalie, "AND con gravita' Alta", "01", "02");

            anomalie.SelezionaVoci(App, "Assegnatario", "Lucia");
            AttendiIds(anomalie, "AND con assegnatario Lucia", "02");
            Assert.Equal(1, anomalie.TotaleFiltratoStabile(tentativiMax: 6, pausaMs: 300));

            anomalie.AzzeraFiltri();
            AttendiIds(anomalie, "dopo l'azzeramento tornano tutte (anche la chiusa)", "01", "02", "03", "04", "05");
            Wait.ForCondition(() => !anomalie.AzzeraFiltriVisibile, "il link di azzeramento sparisce");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltriMultipli_SiCumulanoInAnd_ESiAzzeranoConUnClic), ex); }
    }

    [Fact]
    public void PannelloAltriFiltri_ParteCollassato_ConContatore_EIFiltriRestanoApplicatiDaCollassato()
    {
        try
        {
            var anomalie = new MainShellPage(App.WaitForMainWindow()).GoToAnomalie();
            Assert.False(anomalie.FiltriEspansi, "all'avvio il pannello e' collassato");
            Assert.Equal("Altri filtri", anomalie.RiepilogoFiltri());
            // I filtri principali (ricerca, Stato, Assegnatario) sono visibili anche da collassato.
            AttendiIds(anomalie, "default", "01", "02", "03", "04");
            anomalie.SelezionaVoci(App, "Assegnatario", "Mario");
            AttendiIds(anomalie, "Assegnatario Mario (filtro principale, senza espandere)", "01", "03");
            Assert.Equal("Altri filtri", anomalie.RiepilogoFiltri()); // i principali non entrano nel contatore

            anomalie.EspandiFiltri();
            Assert.True(anomalie.FiltriEspansi);
            anomalie.SelezionaVoci(App, "Priorita", "Minor");
            anomalie.ImpostaData("DataAperturaDa", "01/03/2026");
            Wait.ForCondition(() => anomalie.RiepilogoFiltri() == "Altri filtri (2 attivi)", "contatore con 2 filtri nel pannello");
            AttendiIds(anomalie, "Mario + Minor + dal 01/03", "03");

            anomalie.CollassaFiltri();
            Assert.Equal("Altri filtri (2 attivi)", anomalie.RiepilogoFiltri());
            AttendiIds(anomalie, "i filtri restano applicati da collassato", "03");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(PannelloAltriFiltri_ParteCollassato_ConContatore_EIFiltriRestanoApplicatiDaCollassato), ex); }
    }

    [Fact]
    public void StatoAnomaliaEFunzionalita_FiltranoLeRighe()
    {
        try
        {
            var anomalie = new MainShellPage(App.WaitForMainWindow()).GoToAnomalie();
            AttendiIds(anomalie, "default", "01", "02", "03", "04");
            anomalie.SelezionaVoci(App, "StatoAnomalia", "In verifica");
            AttendiIds(anomalie, "stato 'In verifica'", "04");
            anomalie.AzzeraFiltri();
            anomalie.SelezionaVoci(App, "Funzionalita", "Climate");
            AttendiIds(anomalie, "funzionalita' Climate (ambito proprio della NC)", "02");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(StatoAnomaliaEFunzionalita_FiltranoLeRighe), ex); }
    }

    [Fact]
    public void IntervalloDiApertura_EInclusivoDegliEstremi_EUnaDataNonValidaNonFiltra()
    {
        try
        {
            var anomalie = new MainShellPage(App.WaitForMainWindow()).GoToAnomalie();
            AttendiIds(anomalie, "default", "01", "02", "03", "04");

            anomalie.ImpostaData("DataAperturaDa", "10/03/2026");
            anomalie.ImpostaData("DataAperturaA", "15/03/2026");
            AttendiIds(anomalie, "10/03-15/03 inclusivo (la NC del 15/03 ore 17 rientra)", "01", "02");

            anomalie.ImpostaData("DataAperturaA", "14/03/2026");
            AttendiIds(anomalie, "fino al 14/03", "01");

            anomalie.ImpostaData("DataAperturaA", "14/03/20"); // incompleta: non applica alcun vincolo, il campo la segnala
            Wait.ForCondition(() => anomalie.MessaggioDataNonValidaVisibile, "messaggio 'data non valida' sotto i filtri");
            AttendiIds(anomalie, "una data incompleta non applica alcun vincolo", "01", "02", "03", "04");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(IntervalloDiApertura_EInclusivoDegliEstremi_EUnaDataNonValidaNonFiltra), ex); }
    }

    [Fact]
    public void IntervalloDiApertura_SceltoDalCalendario_FiltraComeLaDataDigitata()
    {
        try
        {
            var anomalie = new MainShellPage(App.WaitForMainWindow()).GoToAnomalie();
            AttendiIds(anomalie, "default", "01", "02", "03", "04");

            // La fine si scrive a mano: il calendario poi si apre sul mese gia' impostato (marzo 2026).
            anomalie.ImpostaData("DataAperturaDa", "10/03/2026");
            anomalie.ImpostaData("DataAperturaA", "15/03/2026");
            AttendiIds(anomalie, "10/03-15/03 digitato", "01", "02");

            anomalie.ScegliDataDalCalendario(App, "DataAperturaA", 14);
            AttendiIds(anomalie, "fino al 14/03 scelto dal calendario, come con il testo", "01");

            anomalie.ScegliDataDalCalendario(App, "DataAperturaA", 16);
            AttendiIds(anomalie, "fino al 16/03 con il calendario riaperto", "01", "02");

            // Campo svuotato: resta solo il vincolo "dal 10/03", quindi tornano tutte le NC dal 10/03 in poi.
            anomalie.ImpostaData("DataAperturaA", "");
            AttendiIds(anomalie, "campo 'al' svuotato: tornano le NC dal 10/03 in poi", "01", "02", "03", "04");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(IntervalloDiApertura_SceltoDalCalendario_FiltraComeLaDataDigitata), ex); }
    }

    [Fact]
    public void IntervalloDiChiusura_MostraSoloLeNcChiuseNelRange()
    {
        try
        {
            var anomalie = new MainShellPage(App.WaitForMainWindow()).GoToAnomalie();
            anomalie.AzzeraFiltri(); // include anche le chiuse
            AttendiIds(anomalie, "tutte", "01", "02", "03", "04", "05");
            anomalie.ImpostaData("DataChiusuraDa", "10/06/2026");
            anomalie.ImpostaData("DataChiusuraA", "10/06/2026");
            AttendiIds(anomalie, "chiusa il 10/06 (estremi inclusivi)", "05");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(IntervalloDiChiusura_MostraSoloLeNcChiuseNelRange), ex); }
    }

    [Fact]
    public void OrdinamentoPerColonna_SuIntero_Elenco_SecondoClicInvertePer()
    {
        try
        {
            var anomalie = new MainShellPage(App.WaitForMainWindow()).GoToAnomalie();
            AttendiIds(anomalie, "default", "01", "02", "03", "04");

            anomalie.OrdinaPerColonna("Priorità");
            Wait.ForCondition(() => anomalie.IdRighe().SequenceEqual(Ids("03", "02", "01", "04")), "priorita' crescente: Minor, Major, Critical(01,04)");
            anomalie.OrdinaPerColonna("Priorità");
            Wait.ForCondition(() => anomalie.IdRighe().SequenceEqual(Ids("04", "01", "02", "03")), "priorita' decrescente");
            anomalie.OrdinaPerColonna("ID");
            Wait.ForCondition(() => anomalie.IdRighe().SequenceEqual(Ids("01", "02", "03", "04")), "per ID crescente");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(OrdinamentoPerColonna_SuIntero_Elenco_SecondoClicInvertePer), ex); }
    }

    [Fact]
    public void ColonnePersonalizzabili_NascondiERimostra()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            AttendiIds(anomalie, "default", "01", "02", "03", "04");
            Wait.ForCondition(() => anomalie.TitoliColonneVisibili().Any(t => t.StartsWith("Gravità")), "colonna Gravità visibile");

            anomalie.CommutaColonna(window, "Gravità");
            Wait.ForCondition(() => !anomalie.TitoliColonneVisibili().Any(t => t.StartsWith("Gravità")), "colonna Gravità nascosta");
            anomalie.CommutaColonna(window, "Gravità");
            Wait.ForCondition(() => anomalie.TitoliColonneVisibili().Any(t => t.StartsWith("Gravità")), "colonna Gravità di nuovo visibile");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ColonnePersonalizzabili_NascondiERimostra), ex); }
    }

    [Fact]
    public void FiltriEOrdinamento_SopravvivonoAlRiavvio()
    {
        try
        {
            var anomalie = new MainShellPage(App.WaitForMainWindow()).GoToAnomalie();
            anomalie.SelezionaVoci(App, "Priorita", "Minor");
            AttendiIds(anomalie, "priorita' Minor", "03");
            Assert.Equal("Minor", anomalie.TestoFiltro("Priorita"));

            App.Dispose();
            using var app2 = TestLoomApplication.Launch(Workspace);
            var anomalie2 = new MainShellPage(app2.WaitForMainWindow()).GoToAnomalie();
            // Il pannello riparte SEMPRE collassato, ma i valori dei filtri sono stati ripristinati e il
            // riepilogo dice che la lista e' filtrata.
            Assert.False(anomalie2.FiltriEspansi, "il pannello Altri filtri riparte collassato");
            Wait.ForCondition(() => anomalie2.RiepilogoFiltri() == "Altri filtri (1 attivi)", "riepilogo dei filtri ripristinati");
            AttendiIds(anomalie2, "dopo il riavvio solo la NC Minor (filtro applicato da collassato)", "03");
            Assert.Equal("Minor", anomalie2.TestoFiltro("Priorita"));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(FiltriEOrdinamento_SopravvivonoAlRiavvio), ex); }
    }
}
