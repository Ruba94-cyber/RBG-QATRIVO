using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.VisualTree;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Layout della shell reale in headless (Category=LayoutHeadless, meno di 30 s in totale). La finestra principale
/// viene mostrata a 1280x672, 1366x768 e 1920x1080; 1024x600 copre il modo Minimo, che nessuna delle tre dimensioni
/// raggiunge. Verifica modo calcolato, token applicati, pulsanti e campi alti almeno 32 px, sidebar senza testi tagliati in
/// minimo, overlay entro l'altezza della finestra e sfondo opaco.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class LayoutShellHeadlessTests
{
    [Theory]
    [InlineData(1280, 672, ModoLayout.Compatto)]  // caso peggiore: FHD al 150% meno barra delle applicazioni
    [InlineData(1366, 768, ModoLayout.Standard)]
    [InlineData(1920, 1080, ModoLayout.Ampio)]
    [InlineData(1024, 600, ModoLayout.Minimo)]    // sidebar a 64 px: nessun testo tagliato
    public void Shell_ModoTokenSidebarOverlayESfondoSecondoLaDimensione(int larghezza, int altezza, ModoLayout atteso) =>
        LayoutHeadlessSessione.Esegui(() => VerificaShell(larghezza, altezza, atteso));

    private static void VerificaShell(int larghezza, int altezza, ModoLayout atteso)
    {
        LayoutAdattivo.ReimpostaStato();
        var vm = new MainViewModel(TestStateFactory.Create());
        // Senza cartella condivisa valida la shell parte in primo avvio, con la sidebar nascosta: qui serve la shell.
        vm.MostraPrimoAvvio = false;
        var finestra = LayoutHeadlessSessione.CreaFinestraPrincipale(vm, larghezza, altezza);
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);

            Assert.Equal(atteso, LayoutAdattivo.ModoCorrente);
            AsserisciClassi(finestra, atteso);
            AsserisciTokenApplicati(atteso);

            var sidebar = finestra.FindControl<Border>("SidebarBordo")!;
            Assert.Equal(LayoutAdattivo.Token(atteso).LarghezzaSidebar, sidebar.Bounds.Width, 1);
            AsserisciSfondoOpaco(finestra);

            // Un pannello di prova dentro la shell eredita stili e classi della finestra: stessi token, stesse altezze.
            var prova = CreaPannelloDiProva();
            finestra.FindControl<ContentControl>("PaginaCorrenteHost")!.Content = prova.Radice;
            LayoutHeadlessSessione.Assesta(finestra);
            AsserisciStiliDiPagina(prova, atteso);

            AsserisciIndietroLeggibile(finestra);
            AsserisciTooltipVociNavigazione(finestra);
            if (atteso == ModoLayout.Minimo) AsserisciSidebarSenzaTroncamenti(sidebar);
            AsserisciOverlayEntroAltezza(finestra, vm);
        }
        finally
        {
            finestra.Hide();
        }
    }

    private static void AsserisciClassi(Window finestra, ModoLayout modo)
    {
        var attese = LayoutAdattivo.Classi(modo);
        foreach (var classe in attese) Assert.Contains(classe, finestra.Classes);
        foreach (var classe in LayoutAdattivo.TutteLeClassi.Except(attese)) Assert.DoesNotContain(classe, finestra.Classes);
    }

    private static void AsserisciTokenApplicati(ModoLayout modo)
    {
        var t = LayoutAdattivo.Token(modo);
        var risorse = Application.Current!.Resources;
        Assert.Equal(new Thickness(t.PagePadding), risorse["PagePadding"]);
        Assert.Equal(new Thickness(t.CardPadding), risorse["CardPadding"]);
        Assert.Equal(t.SpaziaturaSezioni, (double)risorse["SpaziaturaSezioni"]!, 6);
        Assert.Equal(t.TitoloPagina, (double)risorse["TitoloPagina"]!, 6);
        Assert.Equal(t.KpiNumero, (double)risorse["KpiNumero"]!, 6);
        Assert.Equal(t.KpiNumeroPiccolo, (double)risorse["KpiNumeroPiccolo"]!, 6);
        Assert.Equal(t.LarghezzaSidebar, (double)risorse["LarghezzaSidebar"]!, 6);
        Assert.Equal(t.MaxLarghezzaPagina, (double)risorse["MaxLarghezzaPagina"]!, 6);
        Assert.Equal(t.AltezzaMinimaTabella, (double)risorse["AltezzaMinimaTabella"]!, 6);
    }

    private static void AsserisciSfondoOpaco(Window finestra)
    {
        var sfondo = Assert.IsAssignableFrom<ISolidColorBrush>(finestra.Background);
        Assert.Equal(255, sfondo.Color.A);
        Assert.Equal(Color.Parse("#F8FAFC"), sfondo.Color);
    }

    /// <summary>Il "Indietro" della sidebar e' disabilitato senza storia di navigazione: deve restare leggibile (grigio, non
    /// attenuato al 50% come il Link normale), secondo lo stile SidebarLink.</summary>
    private static void AsserisciIndietroLeggibile(Window finestra)
    {
        var indietro = finestra.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetAutomationId(b) == "Main_Indietro_Button");
        Assert.False(indietro.IsEnabled);
        Assert.Equal("#64748B", Colore(indietro.Foreground));
        indietro.ApplyTemplate();
        var contenuto = indietro.GetVisualDescendants().OfType<ContentPresenter>().First();
        Assert.Equal(1, contenuto.Opacity, 3);
    }

    /// <summary>Ogni voce di navigazione porta il nome completo nel tooltip: in minimo e' l'unico testo che resta.</summary>
    private static void AsserisciTooltipVociNavigazione(Window finestra)
    {
        var navigazione = finestra.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("SidebarNav"));
        var voci = navigazione.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Assert.NotEmpty(voci);
        foreach (var voce in voci)
        {
            var conTooltip = voce.GetVisualDescendants().OfType<Control>().Any(c => ToolTip.GetTip(c) is string s && s.Length > 0);
            Assert.True(conTooltip, "voce di navigazione senza tooltip");
        }
    }

    private static string Colore(IBrush? pennello) =>
        pennello is ISolidColorBrush s ? $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : "?";

    private sealed record PannelloDiProva(StackPanel Radice, TextBlock Titolo, Border Card, Button Primario, TextBox Campo);

    private static PannelloDiProva CreaPannelloDiProva()
    {
        var titolo = new TextBlock { Text = "Titolo pagina", Classes = { "PageTitle" } };
        var card = new Border { Classes = { "Card" }, Child = new TextBlock { Text = "contenuto" } };
        var primario = new Button { Content = "Salva", Classes = { "Primary" } };
        var campo = new TextBox { Classes = { "Field" }, Text = "valore" };
        var radice = new StackPanel { Children = { titolo, card, primario, campo } };
        return new PannelloDiProva(radice, titolo, card, primario, campo);
    }

    private static void AsserisciStiliDiPagina(PannelloDiProva p, ModoLayout modo)
    {
        var t = LayoutAdattivo.Token(modo);
        Assert.Equal(t.TitoloPagina, p.Titolo.FontSize, 6);
        Assert.Equal(new Thickness(t.CardPadding), p.Card.Padding);
        var compatto = modo is ModoLayout.Compatto or ModoLayout.Minimo;
        Assert.Equal(compatto ? new Thickness(12, 6) : new Thickness(16, 8), p.Primario.Padding);
        Assert.Equal(compatto ? new Thickness(8, 5) : new Thickness(8, 6), p.Campo.Padding);
        if (compatto)
        {
            // Padding ridotto ma altezza minima di 32 px per pulsanti e campi cliccabili, misurata dal layout reale.
            Assert.True(p.Primario.Bounds.Height >= 32 - 0.5, $"pulsante alto {p.Primario.Bounds.Height} px");
            Assert.True(p.Campo.Bounds.Height >= 32 - 0.5, $"campo alto {p.Campo.Bounds.Height} px");
        }
    }

    private static void AsserisciSidebarSenzaTroncamenti(Border sidebar)
    {
        var visibili = sidebar.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible).ToList();

        // In minimo nessun testo della sidebar resta visibile: al suo posto ci sono solo icone con tooltip.
        var testiVisibili = visibili.OfType<TextBlock>().Where(t => t.Classes.Contains("SidebarTesto")).Select(t => t.Text).ToList();
        Assert.Empty(testiVisibili);

        // Ogni testo visibile sta per intero nello spazio assegnato: misurato senza vincoli, non supera la sua larghezza.
        foreach (var testo in visibili.OfType<TextBlock>())
        {
            testo.Measure(Size.Infinity);
            Assert.True(testo.Bounds.Width + 0.5 >= testo.DesiredSize.Width,
                $"testo troncato nella sidebar: '{testo.Text}' ({testo.Bounds.Width} px assegnati, {testo.DesiredSize.Width} px necessari)");
        }

        // Ogni controllo visibile sta dentro la larghezza della sidebar; fuori dalla lista di navigazione, anche in altezza.
        foreach (var controllo in visibili)
        {
            var origine = controllo.TranslatePoint(new Point(0, 0), sidebar);
            Assert.NotNull(origine);
            var o = origine!.Value;
            Assert.True(o.X >= -0.5 && o.X + controllo.Bounds.Width <= sidebar.Bounds.Width + 0.5,
                $"{controllo.GetType().Name} esce dalla larghezza della sidebar");
            if (controllo.FindAncestorOfType<ListBox>() == null)
                Assert.True(o.Y >= -0.5 && o.Y + controllo.Bounds.Height <= sidebar.Bounds.Height + 0.5,
                    $"{controllo.GetType().Name} esce dall'altezza della sidebar");
        }
    }

    private static void AsserisciOverlayEntroAltezza(MainWindow finestra, MainViewModel vm)
    {
        var overlay = new (string Nome, Action<bool> Imposta)[]
        {
            ("OverlayRicerca", v => vm.MostraRicercaGlobale = v),
            ("OverlayAiuto", v => vm.MostraAiuto = v),
            ("OverlayNotifiche", v => vm.MostraNotifiche = v),
            ("OverlayTour", v => vm.MostraOnboarding = v),
        };
        foreach (var (nome, imposta) in overlay)
        {
            imposta(true);
            LayoutHeadlessSessione.Assesta(finestra);
            var bordo = finestra.FindControl<Border>(nome)!;
            Assert.True(bordo.IsEffectivelyVisible, $"{nome} non visibile");
            var origine = bordo.TranslatePoint(new Point(0, 0), finestra);
            Assert.NotNull(origine);
            var o = origine!.Value;
            Assert.True(o.Y >= -0.5, $"{nome} sale oltre il bordo superiore");
            Assert.True(o.Y + bordo.Bounds.Height <= finestra.ClientSize.Height + 0.5,
                $"{nome} esce dall'altezza della finestra ({o.Y + bordo.Bounds.Height} > {finestra.ClientSize.Height})");
            imposta(false);
            LayoutHeadlessSessione.Assesta(finestra);
        }
    }
}

/// <summary>Colonne dei KPI di Home e Audit in headless: stesso binding e stesso convertitore dei file XAML, con la larghezza
/// misurata dal layout. In standard restano su una riga come oggi; in compatto Audit diventa 4 + 3, mai 6 + 1.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class LayoutGriglieHeadlessTests
{
    [Theory]
    [InlineData(1600, 6, 6)]   // Ampio: Home su una riga
    [InlineData(1600, 7, 7)]   // Ampio: Audit su una riga
    [InlineData(1070, 6, 6)]   // Standard: Home resta su una riga
    [InlineData(1070, 7, 7)]   // Standard: Audit resta su una riga
    [InlineData(1048, 7, 4)]   // Compatto: Audit 4 + 3
    [InlineData(700, 6, 3)]    // quattro colonne entrano: Home 3 + 3
    public void GrigliaKpi_ColonneDalLayoutReale(int larghezza, int elementi, int atteso) =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var griglia = new UniformGrid();
            griglia.Bind(UniformGrid.ColumnsProperty, new Binding
            {
                Path = "Bounds.Width",
                RelativeSource = new RelativeSource(RelativeSourceMode.Self),
                Converter = ColonneDaLarghezzaConverter.Instance,
                ConverterParameter = $"150|{elementi}",
            });
            for (var i = 0; i < elementi; i++)
                griglia.Children.Add(new Border { Child = new TextBlock { Text = $"{i + 1}" } });
            var finestra = new Window { Width = larghezza, Height = 300, Content = griglia };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                Assert.Equal(atteso, griglia.Columns);
            }
            finally
            {
                finestra.Hide();
            }
        });
}

/// <summary>Dialoghi in headless (Category=LayoutHeadless): sfondo opaco, classi del modo corrente, finestre di contenuto
/// massimizzate in compatto e limitate al tetto dell'area di lavoro in standard.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class LayoutDialoghiHeadlessTests
{
    private static string Colore(IBrush? pennello) =>
        pennello is ISolidColorBrush s ? $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : "?";

    [Fact]
    public void Dialoghi_HannoSfondoOpacoEClassiDelModoCorrente() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            LayoutAdattivo.Aggiorna(1280, 672);   // Compatto
            var dialoghi = new Window[]
            {
                new ConfirmDialog(),
                new CampiTemplateEditorWindow(),
                new CampoSistemaCatalogoWindow(),
                new MergeConflittoDialog(),
                new TestFormPreviewWindow(),
            };
            foreach (var dialogo in dialoghi)
            {
                try
                {
                    LayoutHeadlessSessione.Mostra(dialogo);
                    var nome = dialogo.GetType().Name;
                    // Lo sfondo deve essere il token WindowBackgroundBrush, non un default del tema: confronto esatto del colore.
                    var colore = Colore(dialogo.Background);
                    Assert.True(colore == "#F8FAFC", $"{nome}: sfondo {colore}, atteso #F8FAFC (token WindowBackgroundBrush)");
                    Assert.Contains("compatto", dialogo.Classes);
                }
                finally
                {
                    dialogo.Hide();
                }
            }
        });

    [Fact]
    public void FinestreDiContenuto_SiMassimizzanoInCompattoELimitanoIlTettoInStandard() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            LayoutAdattivo.Aggiorna(1280, 672);   // Compatto: la finestra di contenuto si apre massimizzata
            var massimizzata = new CampiTemplateEditorWindow();
            try
            {
                LayoutHeadlessSessione.Mostra(massimizzata);
                Assert.Equal(WindowState.Maximized, massimizzata.WindowState);
            }
            finally
            {
                massimizzata.Hide();
            }

            LayoutAdattivo.ReimpostaStato();
            LayoutAdattivo.Aggiorna(1366, 768);   // Standard: nessuna massimizzazione, solo il tetto dell'area di lavoro
            var dimensionata = new CampiTemplateEditorWindow();
            try
            {
                LayoutHeadlessSessione.Mostra(dimensionata);
                Assert.NotEqual(WindowState.Maximized, dimensionata.WindowState);
                var schermo = dimensionata.Screens.ScreenFromWindow(dimensionata) ?? dimensionata.Screens.Primary!;
                var tetto = schermo.WorkingArea.Height / schermo.Scaling - AdattamentoDialogo.Margine;
                Assert.Equal(tetto, dimensionata.MaxHeight, 3);
            }
            finally
            {
                dimensionata.Hide();
            }
        });
}
