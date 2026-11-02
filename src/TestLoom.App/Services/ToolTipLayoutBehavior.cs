using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;

namespace TestLoom.App.Services;

/// <summary>Fa sì che tutti i tooltip dell'app si riposizionino o si riducano quando il puntatore è vicino
/// ai bordi del monitor, invece di lasciare parte del testo fuori dall'area visibile.</summary>
public static class ToolTipLayoutBehavior
{
    private static readonly ConditionalWeakTable<Control, StatoTooltip> Configurati = new();
    private static bool _registrato;

    public static void Configure()
    {
        if (_registrato) return;
        _registrato = true;
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is not null) Configura(control);
        });
    }

    private static void Configura(Control control)
    {
        var stato = new StatoTooltip();
        if (!Configurati.TryAdd(control, stato)) return;
        control.PointerMoved += (_, e) => stato.PosizionePuntatore = e.GetPosition(control);
        ToolTip.SetCustomPopupPlacementCallback(control, posizione => MantieniEntroSchermo(posizione, control, stato));
        ToolTip.SetPlacement(control, PlacementMode.Custom);
    }

    private static void MantieniEntroSchermo(CustomPopupPlacement posizione, Control control, StatoTooltip stato)
    {
        // Il custom placement riceve la geometria del target ma non il puntatore. Conserviamo l'ultimo
        // punto locale ricevuto così il tooltip resta vicino al mouse e il positioner può spostarlo
        // dentro lo schermo quando si trova vicino a un bordo.
        var puntoLocale = stato.PosizionePuntatore ?? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        var topLevel = TopLevel.GetTopLevel(control);
        var punto = topLevel is null
            ? puntoLocale
            : control.TranslatePoint(puntoLocale, topLevel) ?? puntoLocale;
        posizione.AnchorRectangle = new Rect(punto, new Size(1, 1));
        posizione.Anchor = PopupAnchor.TopLeft;
        posizione.Gravity = PopupGravity.BottomRight;
        posizione.Offset = new Point(12, 18);
        posizione.ConstraintAdjustment = PopupPositionerConstraintAdjustment.All;
    }

    private sealed class StatoTooltip
    {
        public Point? PosizionePuntatore { get; set; }
    }
}
