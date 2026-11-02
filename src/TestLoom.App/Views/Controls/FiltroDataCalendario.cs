using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace TestLoom.App.Views.Controls;

/// <summary>Collega un CalendarDatePicker di filtro di data al ViewModel. Un testo digitato non valido non viene scartato
/// in silenzio: <paramref name="nonValida"/> lo segnala (il ViewModel azzera il vincolo e mostra il messaggio).
/// <paramref name="azzera"/> toglie la segnalazione prima di ogni nuovo tentativo (tasto premuto o uscita dal campo),
/// cosi' il messaggio resta solo finche' il valore non valido e' ancora quello in attesa di correzione.</summary>
public static class FiltroDataCalendario
{
    public static void Collega(CalendarDatePicker picker, Action nonValida, Action azzera)
    {
        // ThrowException resta false: la segnalazione e' gestita qui, senza eccezioni.
        picker.DateValidationError += (_, e) =>
        {
            e.ThrowException = false;
            nonValida();
        };
        // Tunnel: la segnalazione viene tolta PRIMA del commit (Invio o uscita dal campo), che puo' rimetterla.
        picker.AddHandler(InputElement.KeyDownEvent, (_, _) => azzera(), RoutingStrategies.Tunnel);
        picker.AddHandler(InputElement.LostFocusEvent, (_, _) => azzera(), RoutingStrategies.Tunnel);
    }
}
