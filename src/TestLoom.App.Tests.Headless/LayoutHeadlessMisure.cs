using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Misure e asserzioni comuni ai test Category=LayoutHeadless (editor, dialoghi, matrice Audit): finestra con la colonna
/// della sidebar, rettangoli nelle coordinate di un riferimento, card a una colonna, workspace di prova con un Test esistente.</summary>
internal static class LayoutHeadlessMisure
{
    /// <summary>Id del Test esistente creato da <see cref="CreaFixtureEditor"/>.</summary>
    internal const string IdTestLayout = "T-LAYOUT";

    /// <summary>Workspace di prova con un operatore Master e un Test esistente a due operazioni nella revisione corrente: la prima
    /// e' quella senza precedenti. Il template di default del Master porta i campi di sistema (Veicolo, componente, matricola, baseline...):
    /// senza, l'editor non li mostra.</summary>
    internal sealed class FixtureEditor : IDisposable
    {
        internal FixtureEditor(AppState state, Guid prima, Guid seconda, string dir)
        {
            State = state;
            Prima = prima;
            Seconda = seconda;
            Dir = dir;
        }

        internal AppState State { get; }
        internal Guid Prima { get; }
        internal Guid Seconda { get; }
        internal string Dir { get; }

        public void Dispose()
        {
            State.Dispose();
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    internal static FixtureEditor CreaFixtureEditor()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_LayoutEditorTest_" + Guid.NewGuid().ToString("N"));
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Layout", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        if (!state.ImpostaOperatore(op.Nome, "1234")) throw new InvalidOperationException("operatore di prova non impostato");
        var prima = new Condizione { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Diagnosi", Esito = Esito.Conforme };
        var seconda = new Condizione { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Lettura", Esito = Esito.Conforme };
        var revisione = new Esecuzione
        {
            Iterazione = 1, Data = new DateTime(2026, 9, 1), Operatore = "Mario", Matricola = "Matricola1",
            Condizioni = { prima, seconda },
        };
        revisione.SincronizzaEsito();
        var template = state.Master.TestTemplate.Load().First(t => !t.IsChangeRequest);
        var tc = new TestCase
        {
            Id = IdTestLayout, TestTemplateUid = template.Uid, Componente = { "BCM" }, Veicolo = "V1", Veicoli = { "V1" },
            Assegnatario = "Luigi", StatoLavoro = StatoLavoroTest.InCorso,
            Esecuzioni = new List<Esecuzione> { revisione },
        };
        state.TestCases.Save(tc, isNew: true);
        return new FixtureEditor(state, prima.Uid, seconda.Uid, dir);
    }

    /// <summary>Mostra la pagina in una finestra della dimensione data. A sinistra c'e' una colonna della larghezza della sidebar
    /// del modo corrente (200 DIP in Compatto): la pagina ha quindi la larghezza che avrebbe davvero sul portatile. Le classi di
    /// modo della finestra sono quelle che MainWindow applica. Va chiamata sul thread UI.</summary>
    internal static Window MostraInSidebar(Control pagina, int larghezza, int altezza)
    {
        LayoutAdattivo.ReimpostaStato();
        LayoutAdattivo.Aggiorna(larghezza, altezza);
        var sidebar = LayoutAdattivo.Token(LayoutAdattivo.ModoCorrente).LarghezzaSidebar;
        var radice = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions { new(sidebar, GridUnitType.Pixel), new(1, GridUnitType.Star) },
        };
        radice.Children.Add(new Border { Background = Brushes.Transparent });
        Grid.SetColumn(pagina, 1);
        radice.Children.Add(pagina);
        var finestra = new Window { Width = larghezza, Height = altezza, Content = radice };
        foreach (var classe in LayoutAdattivo.Classi(LayoutAdattivo.ModoCorrente)) finestra.Classes.Add(classe);
        LayoutHeadlessSessione.Mostra(finestra);
        for (var i = 0; i < 3; i++) LayoutHeadlessSessione.Assesta(finestra);
        return finestra;
    }

    internal static T Trova<T>(Visual radice, string automationId) where T : Control =>
        radice.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    /// <summary>Figli diretti visibili di un pannello, nell'ordine in cui il layout li tratta.</summary>
    internal static List<Control> VisibiliDirettiDi(Panel pannello) =>
        pannello.Children.OfType<Control>().Where(c => c.IsEffectivelyVisible).ToList();

    /// <summary>Rettangolo del controllo nelle coordinate del riferimento (finestra, griglia o pannello).</summary>
    internal static Rect Rettangolo(Control controllo, Visual riferimento)
    {
        var origine = controllo.TranslatePoint(new Point(0, 0), riferimento);
        if (origine == null) throw new InvalidOperationException($"{controllo.GetType().Name} non e' nell'albero di {riferimento.GetType().Name}");
        return new Rect(origine.Value, controllo.Bounds.Size);
    }

    /// <summary>Il controllo sta per intero dentro l'area client della finestra (contenuto non tagliato in basso o a destra).</summary>
    internal static void AsserisciDentroFinestra(Control controllo, Window finestra, string nome)
    {
        var r = Rettangolo(controllo, finestra);
        Assert.True(r.Right <= finestra.ClientSize.Width + 0.5 && r.Bottom <= finestra.ClientSize.Height + 0.5,
            $"{nome} esce dalla finestra: {r.Right:0}x{r.Bottom:0} oltre {finestra.ClientSize.Width:0}x{finestra.ClientSize.Height:0}");
    }

    /// <summary>Una colonna sola: ogni campo occupa tutta la larghezza del pannello e sta sotto il precedente, senza sovrapporsi.</summary>
    internal static void AsserisciUnaColonna(Layoutable pannello, IEnumerable<Control> campi, string nome)
    {
        var lista = campi.ToList();
        var larghezzaPannello = pannello.Bounds.Width;
        var rettangoli = lista.Select(c => Rettangolo(c, pannello)).OrderBy(r => r.Y).ToList();
        var dettaglio = string.Join("; ", lista.Select(c =>
            $"{c.GetType().Name} x={Rettangolo(c, pannello).X:0} larg={Rettangolo(c, pannello).Width:0}"));
        foreach (var r in rettangoli)
            Assert.True(r.X <= 0.5 && Math.Abs(r.Width - larghezzaPannello) <= 1.5,
                $"{nome}: campo non a tutta larghezza (pannello {larghezzaPannello:0}; {dettaglio})");
        for (var i = 1; i < rettangoli.Count; i++)
            Assert.True(rettangoli[i].Y >= rettangoli[i - 1].Bottom - 0.5, $"{nome}: campi sovrapposti in verticale");
    }

    /// <summary>Numero di colonne occupate da un pannello: quante posizioni X distinte hanno i suoi figli visibili.</summary>
    internal static int ColonneOccupate(Visual pannello, IEnumerable<Control> campi) =>
        campi.Select(c => Math.Round(Rettangolo(c, pannello).X)).Distinct().Count();
}
