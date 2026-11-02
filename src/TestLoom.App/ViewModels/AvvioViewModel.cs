using CommunityToolkit.Mvvm.ComponentModel;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// ViewModel della schermata di avvio (AvvioWindow). Riceve gli aggiornamenti di <see cref="BootstrapProgress"/>
/// tramite un <see cref="Progress{T}"/> costruito sul thread UI da App.axaml.cs: quel wrapper marshalla già
/// ogni invocazione sul thread giusto, quindi <see cref="Aggiorna"/> può limitarsi ad aggiornare le proprietà
/// osservabili senza occuparsi di Dispatcher.
///
/// Niente percentuale finta: finché <see cref="TotaleConosciuto"/> è false la UI deve mostrare una barra
/// indeterminata e "totale non ancora noto", mai un numero inventato (feedback utente: "dovrebbe dirmi anche
/// quanto tempo manca alla fine" — ma solo quando è davvero calcolabile dal ritmo osservato).
/// </summary>
public partial class AvvioViewModel : ViewModelBase
{
    [ObservableProperty] private string messaggio = "Avvio dell'applicazione…";
    [ObservableProperty] private bool totaleConosciuto;
    [ObservableProperty] private int elaborati;
    [ObservableProperty] private int totale;
    [ObservableProperty] private double percentuale;
    [ObservableProperty] private string trascorsoTesto = "0s";
    [ObservableProperty] private string stimaTesto = "";

    public void Aggiorna(BootstrapProgress valore)
    {
        Messaggio = valore.Messaggio;
        if (valore.Elaborati.HasValue && valore.Totale.HasValue && valore.Totale.Value > 0)
        {
            TotaleConosciuto = true;
            Elaborati = valore.Elaborati.Value;
            Totale = valore.Totale.Value;
            Percentuale = Math.Clamp(100.0 * valore.Elaborati.Value / valore.Totale.Value, 0, 100);
        }
        else
        {
            TotaleConosciuto = false;
            Elaborati = 0;
            Totale = 0;
            Percentuale = 0;
        }

        TrascorsoTesto = FormattaDurata(valore.Trascorso);
        StimaTesto = valore.StimaRimanente.HasValue
            ? string.Format(LocalizationService.Instance.Traduci("AvvioTempoRimanenteFmt"), FormattaDurata(valore.StimaRimanente.Value))
            : (TotaleConosciuto ? LocalizationService.Instance.Traduci("AvvioCalcoloStimaTesto") : LocalizationService.Instance.Traduci("AvvioTotaleSconosciutoTesto"));
    }

    private static string FormattaDurata(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s";
        return $"{Math.Max(0, (int)ts.TotalSeconds)}s";
    }
}
