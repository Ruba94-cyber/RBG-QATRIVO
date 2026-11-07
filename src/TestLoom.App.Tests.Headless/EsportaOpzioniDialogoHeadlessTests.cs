using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Dialogo unico dell'export: sezioni e colonne scelte, filtri a video modificabili ma applicati solo alla conferma, ultima
/// selezione ricordata e configurazioni salvate per pagina (massimo sei).</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class EsportaOpzioniDialogoHeadlessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_EsportaDlg_" + Guid.NewGuid().ToString("N"));
    private readonly AppState _state;
    private string? _veicolo;
    private DateTime? _da;
    private readonly FiltroMultiSelect _gamma = new();

    public EsportaOpzioniDialogoHeadlessTests()
    {
        Directory.CreateDirectory(_dir);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_dir);
        _gamma.Popola(new[] { ("LCV", "LCV"), ("HCV", "HCV") });
        _gamma.Opzioni[0].Selezionato = true; // filtro gia' attivo a video
        _veicolo = "Daily";
        _da = new DateTime(2026, 1, 1);
    }

    public void Dispose() { _state.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private ExportDescrittore Descrittore() => new("test-pagina", "Esporta test",
    [
        new ExportSezione("vista", "Vista", [new ExportColonna("id", "ID"), new ExportColonna("stato", "Stato"), new ExportColonna("veic", "Veicoli")]),
        new ExportSezione("dati", "Dati", PredefinitaAttiva: true),
        new ExportSezione("pivot", "Pivot", PredefinitaAttiva: false),
    ], "Gamma: LCV",
    [
        new ExportFiltroMultiplo("gamma", "Gamma", _gamma),
        new ExportFiltroSingolo("veicolo", "Veicolo", ["Daily", "Bus"], () => _veicolo, v => _veicolo = v),
        new ExportFiltroData("da", "Da", () => _da, d => _da = d),
    ], [new ExportFontePivot("fonte", "Fonte", [new ExportColonna("componente", "Componente"), new ExportColonna("stato", "Stato"), new ExportColonna("durata", "Durata", Numerica: true)])]);

    /// <summary>Seleziona la voce con quell'etichetta: gli elementi del filtro singolo sono record Scelta (Id + Etichetta), con "Tutti" in testa.</summary>
    private static void ScegliVoce(ComboBox combo, string etichetta) =>
        combo.SelectedItem = combo.ItemsSource!.Cast<object>().First(o => o.ToString() == etichetta);

    private static T Trova<T>(Control radice, string id) where T : Control
    {
        var schede = radice.GetVisualDescendants().OfType<TabControl>().Single();
        for (var i = 0; i < 3; i++)
        {
            schede.SelectedIndex = i;
            LayoutHeadlessSessione.Assesta((Window)radice);
            foreach (var e in radice.GetVisualDescendants().OfType<Expander>().ToList()) e.IsExpanded = true;
            LayoutHeadlessSessione.Assesta((Window)radice);
            var trovato = radice.GetVisualDescendants().OfType<T>().FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == id);
            if (trovato != null) return trovato;
        }
        throw new InvalidOperationException("Controllo non trovato: " + id);
    }

    private static void Clic(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Mostra(EsportaOpzioniDialogo d)
    {
        d.Finestra.Show();
        LayoutHeadlessSessione.Assesta(d.Finestra);
        // Le colonne e le voci dei filtri stanno in sezioni a tendina: si aprono per poterle raggiungere.
        foreach (var e in d.Finestra.GetVisualDescendants().OfType<Expander>().ToList()) e.IsExpanded = true;
        LayoutHeadlessSessione.Assesta(d.Finestra);
    }

    [Fact]
    public void PartonoDaiDatiAVideo_ISoloLaConfermaApplicaIFiltri() => LayoutHeadlessSessione.Esegui(() =>
    {
        var d = new EsportaOpzioniDialogo(_state, Descrittore());
        Mostra(d);
        try
        {
            Assert.True(Trova<CheckBox>(d.Finestra, "EsportaOpzioni_Sezione_vista").IsChecked);
            Assert.False(Trova<CheckBox>(d.Finestra, "EsportaOpzioni_Sezione_pivot").IsChecked);
            Assert.Equal("Daily", Trova<ComboBox>(d.Finestra, "EsportaOpzioni_Filtro_veicolo").SelectedItem?.ToString()); // la voce e' una Scelta (Id/Etichetta), non la stringa nuda

            ScegliVoce(Trova<ComboBox>(d.Finestra, "EsportaOpzioni_Filtro_veicolo"), "Bus");
            Trova<CheckBox>(d.Finestra, "EsportaOpzioni_Colonna_vista_stato").IsChecked = false;
            Trova<CheckBox>(d.Finestra, "EsportaOpzioni_Sezione_pivot").IsChecked = true;
            Trova<ComboBox>(d.Finestra, "EsportaOpzioni_Pivot_Percentuale").SelectedIndex = 2;
            Trova<ComboBox>(d.Finestra, "EsportaOpzioni_Pivot_Metrica").SelectedIndex = 2;
            Assert.Equal("Daily", _veicolo); // finche' non si conferma nulla cambia nella pagina
            Assert.True(_gamma.Opzioni[0].Selezionato);

            Clic(Trova<Button>(d.Finestra, "EsportaOpzioni_Esporta"));
            var scelta = d.Risultato.GetAwaiter().GetResult();

            Assert.NotNull(scelta);
            Assert.Equal(new[] { "vista", "dati", "pivot" }, scelta!.Sezioni);
            Assert.Equal(new[] { "id", "veic" }, scelta.ColonneDi("vista"));
            Assert.Equal("fonte", scelta.PivotFonte);
            Assert.Equal("componente", scelta.PivotRighe);
            Assert.Equal("stato", scelta.PivotColonne);
            Assert.Equal("colonna", scelta.PivotPercentuale);
            Assert.Equal("media", scelta.PivotMetrica);
            Assert.Equal("durata", scelta.PivotCampoValore);
            Assert.Equal("Bus", _veicolo);
        }
        finally { d.Finestra.Close(); }
    });

    [Fact]
    public void Annulla_NonCambiaNienteERicordaLUltimaSelezioneSoloSeConfermata() => LayoutHeadlessSessione.Esegui(() =>
    {
        var d = new EsportaOpzioniDialogo(_state, Descrittore());
        Mostra(d);
        ScegliVoce(Trova<ComboBox>(d.Finestra, "EsportaOpzioni_Filtro_veicolo"), "Bus");
        Clic(Trova<Button>(d.Finestra, "EsportaOpzioni_Annulla"));
        Assert.Null(d.Risultato.GetAwaiter().GetResult());
        Assert.Equal("Daily", _veicolo);
        Assert.Null(ExportConfigurazioniService.CaricaUltimaSelezione(_state, "test-pagina"));

        var d2 = new EsportaOpzioniDialogo(_state, Descrittore());
        Mostra(d2);
        Trova<CheckBox>(d2.Finestra, "EsportaOpzioni_Sezione_dati").IsChecked = false;
        Clic(Trova<Button>(d2.Finestra, "EsportaOpzioni_Esporta"));
        d2.Risultato.GetAwaiter().GetResult();

        var d3 = new EsportaOpzioniDialogo(_state, Descrittore());
        Mostra(d3);
        try
        {
            Assert.False(Trova<CheckBox>(d3.Finestra, "EsportaOpzioni_Sezione_dati").IsChecked);
            Assert.True(Trova<CheckBox>(d3.Finestra, "EsportaOpzioni_Sezione_vista").IsChecked);
        }
        finally { d3.Finestra.Close(); }
    });

    [Fact]
    public void PivotMediaSenzaCampoNumerico_NonConferma() => LayoutHeadlessSessione.Esegui(() =>
    {
        var descrittore = Descrittore() with { FontiPivot = [new ExportFontePivot("testo", "Testo",
            [new ExportColonna("componente", "Componente"), new ExportColonna("stato", "Stato")])] };
        var d = new EsportaOpzioniDialogo(_state, descrittore);
        Mostra(d);
        try
        {
            Trova<CheckBox>(d.Finestra, "EsportaOpzioni_Sezione_pivot").IsChecked = true;
            Trova<ComboBox>(d.Finestra, "EsportaOpzioni_Pivot_Metrica").SelectedIndex = 2;
            Clic(Trova<Button>(d.Finestra, "EsportaOpzioni_Esporta"));
            Assert.False(d.Risultato.IsCompleted);
        }
        finally { d.Finestra.Close(); }
    });

    [Fact]
    public void PivotConAssiUguali_NonConferma() => LayoutHeadlessSessione.Esegui(() =>
    {
        var d = new EsportaOpzioniDialogo(_state, Descrittore());
        Mostra(d);
        try
        {
            Trova<CheckBox>(d.Finestra, "EsportaOpzioni_Sezione_pivot").IsChecked = true;
            Trova<ComboBox>(d.Finestra, "EsportaOpzioni_Pivot_Colonne").SelectedIndex = 0;
            Clic(Trova<Button>(d.Finestra, "EsportaOpzioni_Esporta"));
            Assert.False(d.Risultato.IsCompleted);
            Trova<ComboBox>(d.Finestra, "EsportaOpzioni_Pivot_Colonne").SelectedIndex = 1;
            Clic(Trova<Button>(d.Finestra, "EsportaOpzioni_Esporta"));
            Assert.NotNull(d.Risultato.GetAwaiter().GetResult());
        }
        finally { d.Finestra.Close(); }
    });

    [Fact]
    public void NessunaColonnaSelezionata_NonConfermaComeSeFosseroTutte() => LayoutHeadlessSessione.Esegui(() =>
    {
        var d = new EsportaOpzioniDialogo(_state, Descrittore());
        Mostra(d);
        try
        {
            foreach (var id in new[] { "id", "stato", "veic" })
                Trova<CheckBox>(d.Finestra, "EsportaOpzioni_Colonna_vista_" + id).IsChecked = false;
            Clic(Trova<Button>(d.Finestra, "EsportaOpzioni_Esporta"));
            Assert.False(d.Risultato.IsCompleted);
        }
        finally { d.Finestra.Close(); }
    });

    [Fact]
    public void SalvaUnaConfigurazioneEOltreLeSeiLaRifiuta() => LayoutHeadlessSessione.Esegui(() =>
    {
        for (var i = 1; i <= ExportConfigurazioniService.Massimo; i++)
            ExportConfigurazioniService.Salva(_state, "test-pagina", new ExportConfigurazione { Nome = "C" + i, Sezioni = ["vista"] });

        var d = new EsportaOpzioniDialogo(_state, Descrittore());
        Mostra(d);
        try
        {
            var nome = Trova<TextBox>(d.Finestra, "EsportaOpzioni_Nome");
            nome.Text = "Settima";
            Clic(Trova<Button>(d.Finestra, "EsportaOpzioni_Salva"));
            Assert.Equal(ExportConfigurazioniService.Massimo, ExportConfigurazioniService.Carica(_state, "test-pagina").Configurazioni.Count);

            nome.Text = "C2";
            Trova<CheckBox>(d.Finestra, "EsportaOpzioni_Sezione_dati").IsChecked = true;
            Clic(Trova<Button>(d.Finestra, "EsportaOpzioni_Salva"));
            var c2 = ExportConfigurazioniService.Carica(_state, "test-pagina").Configurazioni.Single(c => c.Nome == "C2");
            Assert.True(c2.HaSezione("dati"), "sezioni C2=[" + string.Join(",", c2.Sezioni) + "] nome=[" + nome.Text + "] tutte=[" + string.Join(",", ExportConfigurazioniService.Carica(_state, "test-pagina").Configurazioni.Select(c => c.Nome + ":" + string.Join("/", c.Sezioni))) + "]");
        }
        finally { d.Finestra.Close(); }
    });
    [Fact]
    public void RicercaColonne_MantieneSelezioniNascoste_EPermetteDeselezioneDeiSoliRisultati() =>
        LayoutHeadlessSessione.Esegui(() =>
    {
        var campi = Enumerable.Range(1, 12)
            .Select(i => new ExportColonna("K" + i.ToString("D2"),
                i == 5 ? "UnitLogger · Versione" : "Campo strumento " + i)).ToList();
        var descrittore = Descrittore() with
        {
            Sezioni = [new ExportSezione("vista", "Vista", campi)]
        };
        var dialogo = new EsportaOpzioniDialogo(_state, descrittore);
        Mostra(dialogo);
        try
        {
            var ricerca = Trova<TextBox>(dialogo.Finestra, "EsportaOpzioni_CercaColonne_vista");
            ricerca.Text = "Versione";
            LayoutHeadlessSessione.Assesta(dialogo.Finestra);

            var versione = Trova<CheckBox>(dialogo.Finestra, "EsportaOpzioni_Colonna_vista_K05");
            var altro = Trova<CheckBox>(dialogo.Finestra, "EsportaOpzioni_Colonna_vista_K01");
            Assert.True(versione.IsVisible);
            Assert.False(altro.IsVisible);
            Clic(Trova<Button>(dialogo.Finestra, "EsportaOpzioni_NessunaVisibile_vista"));
            Assert.False(versione.IsChecked == true);
            Assert.True(altro.IsChecked == true); // la ricerca non modifica i campi nascosti

            var scelte = dialogo.Corrente().ColonneDi("vista");
            Assert.NotNull(scelte);
            Assert.Equal(11, scelte!.Count);
            Assert.DoesNotContain("K05", scelte);
            Assert.Contains("K01", scelte);

            ricerca.Text = "";
            LayoutHeadlessSessione.Assesta(dialogo.Finestra);
            Assert.True(altro.IsVisible);
            Assert.False(versione.IsChecked == true); // selezione preservata anche dopo reset

            Clic(Trova<Button>(dialogo.Finestra, "EsportaOpzioni_TutteVisibili_vista"));
            Assert.Null(dialogo.Corrente().ColonneDi("vista")); // null = tutte le colonne
        }
        finally { dialogo.Finestra.Close(); }
    });

}
