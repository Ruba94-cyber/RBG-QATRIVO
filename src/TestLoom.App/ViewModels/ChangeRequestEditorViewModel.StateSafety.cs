using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

public partial class ChangeRequestEditorViewModel
{
    /// <summary>
    /// Variante usata dalla finestra secondaria editabile: il callback è la chiusura della finestra proprietaria,
    /// quindi sia Salva sia Annulla terminano davvero quell'editor invece di lasciare un form senza lock a video.
    /// </summary>
    public TestCaseEditorViewModel CreaEditorTestValidazione(string testCaseId, Action tornaAllaFinestraProprietaria) =>
        new(_state, testCaseId, tornaAllaFinestraProprietaria, _ => { });
}
