using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Flows;

/// <summary>
/// Compone Lista Test + Editor + dialog "Scegli componente" nell'unica sequenza reale per creare un Test Case dalla
/// UI con il template minimo seminato da <see cref="TestWorkspace"/> (solo Descrizione breve + componente
/// obbligatori): passo 1 del wizard (scelta template) → scheda "Dettagli" → compila → scheda "Riferimenti"
/// (dove compare Salva, Addendum §18) → Salva. Usato da più test (creazione, persistenza dopo riavvio) per
/// non duplicare la stessa sequenza di 7 passi in ognuno.
///
/// TestCaseEditorViewModel.Salva() torna sempre alla Lista Test al termine di un salvataggio RIUSCITO (mai in
/// caso di errore di validazione, dove l'editor resta aperto con il banner errori) — per questo il flow
/// ritorna una TestListPage, non l'editor.
/// </summary>
public static class CreateTestCaseFlow
{
    public const int TabDettagli = 1;
    public const int TabRiferimenti = 4;

    public static TestListPage Run(TestLoomApplication app, MainShellPage shell, string descrizioneBreve, string ecuName)
    {
        var testList = shell.GoToTestList();
        var editor = testList.ClickNew();

        editor.ChooseTemplate();
        editor.GoToTab(TabDettagli);
        editor.SetDescrizioneBreve(descrizioneBreve);
        editor.ChooseEcu(app, ecuName);
        editor.GoToTab(TabRiferimenti);
        editor.Save();

        return new TestListPage(app.WaitForMainWindow(), app.WaitForMainWindow());
    }
}
