using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Grafici della Home: la registrazione degli export vive nella Cronologia export, come quella degli altri export.
/// Esportazione e copia di ogni card sono in <see cref="ChartRenderizzato"/>.</summary>
public partial class HomeViewModel
{
    /// <summary>Registra un file esportato (PNG di una card o di una finestra) nella Cronologia export.</summary>
    public void RegistraEsportazioneGrafico(string percorso)
    {
        _state.Config.CronologiaReport = ReportCronologiaService.Aggiungi(_state.Config.CronologiaReport, percorso, DateTime.Now);
        _state.ConfigService.Save(_state.Config);
    }
}
