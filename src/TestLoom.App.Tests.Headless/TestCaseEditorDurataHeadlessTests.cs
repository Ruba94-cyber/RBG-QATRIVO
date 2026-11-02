using Avalonia.Controls;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>TIME (6.6.1) nell'editor del Test in headless: ogni operazione mostra il campo durata con i pulsanti -5 e +5, il testo
/// digitato aggiorna il valore dell'operazione, il meno da zero non scende sotto zero e un testo non valido non cambia il valore.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class TestCaseEditorDurataHeadlessTests
{
    /// <summary>Esegue il comando legato al pulsante: il binding deve esistere (controllo della vista, non solo del modello).</summary>
    private static void Pulsa(Button pulsante)
    {
        Assert.NotNull(pulsante.Command);
        Assert.True(pulsante.Command!.CanExecute(pulsante.CommandParameter));
        pulsante.Command.Execute(pulsante.CommandParameter);
    }

    [Fact]
    public void EditorDurata_PulsantiPiuMenoCinqueECampoTesto_AggiornanoLOperazione() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = LayoutHeadlessMisure.CreaFixtureEditor();
            var vm = new TestCaseEditorViewModel(fixture.State, LayoutHeadlessMisure.IdTestLayout, () => { }, _ => { }) { ModoEditorRapido = false };
            var editor = new TestCaseEditorView { DataContext = vm };
            var finestra = LayoutHeadlessMisure.MostraInSidebar(editor, 1280, 672);
            try
            {
                LayoutHeadlessSessione.Assesta(finestra);
                var prima = vm.RevisioneCorrente!.Condizioni.First(c => c.Uid == fixture.Prima);
                var campo = LayoutHeadlessMisure.Trova<TextBox>(editor, $"Editor_Condizione_{fixture.Prima}_Durata_TextBox");
                var piu = LayoutHeadlessMisure.Trova<Button>(editor, $"Editor_Condizione_{fixture.Prima}_DurataPiu5_Button");
                var meno = LayoutHeadlessMisure.Trova<Button>(editor, $"Editor_Condizione_{fixture.Prima}_DurataMeno5_Button");
                Assert.Equal(string.Empty, campo.Text);      // durata non nota: il campo e' vuoto, mai uno zero inventato

                Pulsa(piu);
                Assert.Equal(5, prima.DurataMinuti);
                Assert.Equal("5", campo.Text);

                Pulsa(piu);
                Assert.Equal("10", campo.Text);

                Pulsa(meno);
                Pulsa(meno);
                Pulsa(meno);
                Assert.Equal(0, prima.DurataMinuti);         // zero noto: mai negativo
                Assert.Equal("0", campo.Text);

                campo.Text = "-3";                          // testo non valido: il valore resta zero
                Assert.Equal(0, prima.DurataMinuti);

                campo.Text = "42";
                Assert.Equal(42, prima.DurataMinuti);
            }
            finally
            {
                finestra.Hide();
            }
        });

    [Fact]
    public void EditorDurata_TestoNonValido_MostraErroreEBloccaIlSalvataggio() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = LayoutHeadlessMisure.CreaFixtureEditor();
            var vm = new TestCaseEditorViewModel(fixture.State, LayoutHeadlessMisure.IdTestLayout, () => { }, _ => { }) { ModoEditorRapido = false };
            var editor = new TestCaseEditorView { DataContext = vm };
            var finestra = LayoutHeadlessMisure.MostraInSidebar(editor, 1280, 672);
            try
            {
                LayoutHeadlessSessione.Assesta(finestra);
                var prima = vm.RevisioneCorrente!.Condizioni.First(c => c.Uid == fixture.Prima);
                var campo = LayoutHeadlessMisure.Trova<TextBox>(editor, $"Editor_Condizione_{fixture.Prima}_Durata_TextBox");
                var errore = LayoutHeadlessMisure.Trova<TextBlock>(editor, $"Editor_Condizione_{fixture.Prima}_Durata_Errore");
                var messaggio = LocalizationService.Instance.Traduci("DurataNonValidaMsg");
                Assert.False(errore.IsVisible);

                campo.Text = "12,5";                         // decimale: non e' un numero intero di minuti
                Assert.Null(prima.DurataMinuti);              // il modello non assorbe il testo sbagliato
                Assert.True(errore.IsVisible);
                Assert.Equal(messaggio, errore.Text);

                vm.SalvaCommand.Execute(null);
                Assert.Contains(vm.CampiObbligatoriMancanti, c => c.Messaggio == messaggio);   // il salvataggio e' bloccato

                campo.Text = "12";
                Assert.Equal(12, prima.DurataMinuti);
                Assert.False(errore.IsVisible);
            }
            finally
            {
                finestra.Hide();
            }
        });
}
