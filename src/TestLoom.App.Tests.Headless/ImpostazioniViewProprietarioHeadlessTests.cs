using Avalonia.Controls;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>P4 (7): il dialogo di Impostazioni ("Esamina bozze") deve rileggere il proprio proprietario dopo l'attesa della
/// verifica di rete. Un proprietario nascosto (riduzione nel tray) non e' piu' un proprietario valido per ShowDialog.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class ImpostazioniViewProprietarioHeadlessTests
{
    [Fact]
    public void ProprietarioVisibile_SeLaFinestraSiNascondeDopoLAttesa_NonRestituisceUnProprietarioNascosto() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var finestra = new Window { Width = 800, Height = 600 };
            var vista = new ImpostazioniView();
            finestra.Content = vista;
            LayoutHeadlessSessione.Mostra(finestra);
            try
            {
                Assert.Same(finestra, vista.ProprietarioVisibile());

                // Durante la verifica della radice la finestra puo' essere nascosta: non va usata come proprietario.
                finestra.Hide();
                Assert.Null(vista.ProprietarioVisibile());
            }
            finally
            {
                finestra.Close();
            }
        });
}
