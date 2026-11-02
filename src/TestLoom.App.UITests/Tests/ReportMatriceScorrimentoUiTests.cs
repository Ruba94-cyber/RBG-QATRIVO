using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Tests;

[Collection("TestLoom UI")]
[Trait("Category", "Report")]
public sealed class ReportMatriceScorrimentoUiTests : UiTestBase
{
    private const string Veicolo = "Veicolo-Scroll";
    private const int NumeroEcu = 10;
    private const int NumeroCategorie = 14;

    public ReportMatriceScorrimentoUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(ReportMatriceScorrimentoUiTests), seedTestFixtures: false);
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);

        state.Master.Veicoli.Save([new Veicolo { Nome = Veicolo, GammaNome = "LCV", ModificatoDa = "UITests" }]);
        var componenti = Enumerable.Range(1, NumeroEcu).Select(i => $"Componente-Scroll-{i:00}").ToList();
        state.Master.Componente.Save(componenti.Select(nome => new Componente
        {
            Nome = nome,
            VeicoliAssociati = { Veicolo },
            ModificatoDa = "UITests",
        }).ToList());

        var funzionalita = Enumerable.Range(1, NumeroCategorie).Select(i => new Funzionalita
        {
            Nome = $"Funzionalita-Scroll-{i:00}",
            Categorie = { $"Categoria-Scroll-{i:00}" },
            ModificatoDa = "UITests",
        }).ToList();
        state.Master.Funzionalita.Save(funzionalita);

        var test = new TestCase
        {
            Id = "T-Scroll-Matrice",
            Componente = new EcuSelection(componenti),
            Veicoli = [Veicolo],
            Funzionalita = funzionalita.Select(f => f.Nome).ToList(),
            ModificatoDa = "UITests",
        };
        test.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(2026, 1, 1),
            Esito = Esito.Conforme,
            Condizioni = funzionalita.Select(f => new Condizione
            {
                Funzionalita = f.Nome,
                Componente = new EcuSelection(componenti),
                Esito = Esito.Conforme,
            }).ToList(),
        });
        state.TestCases.Save(test, isNew: true);
        return workspace;
    }

    [Fact]
    public void BarreScorrimentoNaviganoRigheEColonneOltreLaFinestraVisibile()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window, App).GoToReport();

            var matrice = ElementFinders.WaitById(window, "Report_Matrice_Contenuto_ScrollViewer");
            var barraRighe = ElementFinders.WaitById(window, "Report_Matrice_ScrollRighe_ScrollBar");
            var barraColonne = ElementFinders.WaitById(window, "Report_Matrice_ScrollColonne_ScrollBar");
            var scorrimentoRighe = barraRighe.Patterns.RangeValue.PatternOrDefault
                ?? throw new InvalidOperationException("La barra righe non espone RangeValue.");
            var scorrimentoColonne = barraColonne.Patterns.RangeValue.PatternOrDefault
                ?? throw new InvalidOperationException("La barra colonne non espone RangeValue.");

            Wait.ForCondition(() => scorrimentoRighe.Maximum > scorrimentoRighe.Minimum
                && scorrimentoColonne.Maximum > scorrimentoColonne.Minimum,
                "gli assi superano la finestra e attivano entrambe le barre");

            scorrimentoRighe.SetValue(scorrimentoRighe.Maximum);
            Wait.ForCondition(() => ContieneTesto(matrice, "Categoria-Scroll-14"),
                "la barra verticale raggiunge l'ultima categoria dell'asse");

            scorrimentoColonne.SetValue(scorrimentoColonne.Maximum);
            Wait.ForCondition(() => ContieneTesto(matrice, "Componente-Scroll-10"),
                "la barra orizzontale raggiunge l'ultima componente dell'asse");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(BarreScorrimentoNaviganoRigheEColonneOltreLaFinestraVisibile), ex);
        }
    }

    private static bool ContieneTesto(AutomationElement root, string testo) =>
        root.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
            .Any(elemento => elemento.BoundingRectangle.Height > 0
                && string.Equals(elemento.Name, testo, StringComparison.Ordinal));
}
