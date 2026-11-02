namespace TestLoom.App.Services;

/// <summary>Regola unica del filtro "Data a" (Home, Report, Excel, PDF, Lista Test): la data scelta è inclusa per intero.
/// Il limite superiore è quindi l'inizio del giorno successivo, da confrontare in modo stretto (<c>&lt;</c>). Un valore a
/// mezzanotte, come quello di un selettore di data, non deve escludere i test creati durante il giorno scelto.</summary>
public static class DataAInclusiva
{
    /// <summary>Primo istante escluso dopo il giorno di "Data a": un test rientra nel filtro se è creato strettamente prima.</summary>
    public static DateTime FineEsclusa(DateTime dataA) => dataA.Date.AddDays(1);
}
