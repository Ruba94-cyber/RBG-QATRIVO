using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>P4 (9): il pulsante "Opzioni" di un campo di sistema di tipo Lista si rilocalizza al cambio lingua. La vista resta in
/// cache alla navigazione: un binding che dipende solo dalle opzioni non rivaluta il testo quando cambia la lingua.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class CampoSistemaOpzioniLinguaHeadlessTests
{
    [Fact]
    public void PulsanteOpzioni_SegueIlCambioLingua() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var linguaIniziale = LocalizationService.Instance.Lingua;
            var stato = MasterHeadless.StatoConCartella(out var dir);
            try
            {
                var vm = new CampoSistemaDefinizioneViewModel(stato, GruppoCampoSistema.Pianificazione);
                vm.ItemsAttivi.Add(new CampoSistemaDefinizione
                {
                    Uid = Guid.NewGuid(), Chiave = "CAMPO_LISTA", Etichetta = "Campo lista",
                    Gruppo = GruppoCampoSistema.Pianificazione, Tipo = TipoCampoStrumento.Lista
                });
                var vista = new CampoSistemaDefinizioneView { DataContext = vm };
                var finestra = new Window { Width = 1280, Height = 672, Content = vista };
                LayoutHeadlessSessione.Mostra(finestra);
                try
                {
                    var bottone = vista.GetVisualDescendants().OfType<Button>()
                        .Single(b => AutomationProperties.GetAutomationId(b) == "CampoSist_Opzioni_CAMPO_LISTA");

                    LocalizationService.Instance.Lingua = "it";
                    LayoutHeadlessSessione.Assesta(finestra);
                    Assert.Equal("Opzioni", bottone.Content as string);

                    LocalizationService.Instance.Lingua = "en";
                    LayoutHeadlessSessione.Assesta(finestra);
                    Assert.Equal("Options", bottone.Content as string);
                }
                finally
                {
                    finestra.Close();
                }
            }
            finally
            {
                LocalizationService.Instance.Lingua = linguaIniziale;
                MasterHeadless.Pulisci(dir);
            }
        });
}
