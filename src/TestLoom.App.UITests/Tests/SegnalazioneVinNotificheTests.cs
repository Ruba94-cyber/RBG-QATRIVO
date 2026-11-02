using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Feedback utente 6.4.x: matricola/ticket/licenza digitati ma non "aggiunti" bloccavano il Salva, i banner
/// restavano fissi e il Salva era raggiungibile solo in fondo alla pagina.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class SegnalazioneVinNotificheTests : UiTestBase
{
    public SegnalazioneVinNotificheTests() : base(TestWorkspace.CreateLoggedIn(nameof(SegnalazioneVinNotificheTests))) { }

    [Fact]
    public void Segnalazione_VinSenzaInvio_SalvaDaHeader_NonDaErroreVin()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var editor = shell.GoToAnomalie().NuovaSegnalazioneRete();
            Wait.ForCondition(() => editor.HaSalvaHeader, "il pulsante Salva e' presente nell'intestazione");

            editor.DigitaNumeroPraticaSenzaInvio("PRATICA-QA-0002");
            editor.DigitaVinSenzaInvio("ZFA0000000000QA02");
            editor.DigitaLicenzaSenzaInvio("LIC-QA-0002");
            editor.SetDescrizioneBreve("Segnalazione QA - matricola senza Invio");
            editor.SetDescrizione("La matricola e' digitato ma non aggiunto con Invio.");
            editor.SetSeverita("Medium");
            editor.SetPriorita("Normal");
            editor.SetStato("InVerifica");
            editor.AggiungiEcu(App, TestWorkspace.DefaultEcuName);
            editor.SalvaDaHeader();

            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"),
                "salvataggio riuscito (torna alla lista Anomalie/CR) senza errore sul matricola");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_VinSenzaInvio_SalvaDaHeader_NonDaErroreVin), ex);
        }
    }

    [Fact]
    public void Segnalazione_BannerErrore_SiChiudeConLaX()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var editor = shell.GoToAnomalie().NuovaSegnalazioneRete();

            editor.SalvaDaHeader(); // campi mancanti: errore di validazione
            Wait.ForCondition(() => editor.HaBannerErrore, "banner di errore con pulsante di chiusura");
            // Il popup "Campi obbligatori mancanti" puo' coprire la pagina: si chiude con Esc se presente.
            window.Focus();
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
            Wait.ForCondition(() => editor.HaBannerErrore, "banner ancora presente dopo chiusura popup");
            editor.ChiudiBannerErrore();
            Wait.ForCondition(() => !editor.HaBannerErrore, "il banner di errore sparisce dopo la X");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_BannerErrore_SiChiudeConLaX), ex);
        }
    }
}
