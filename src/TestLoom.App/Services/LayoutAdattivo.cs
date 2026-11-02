namespace TestLoom.App.Services;

/// <summary>Modo di layout della finestra principale (6.6.1), dal piu' piccolo al piu' ampio.</summary>
public enum ModoLayout
{
    Minimo = 0,
    Compatto = 1,
    Standard = 2,
    Ampio = 3,
}

/// <summary>Livello raggiunto da larghezza e altezza client (0..3). Il modo e' il livello piu' basso dei due:
/// Ampio solo se entrambe le dimensioni sono al livello 3, Minimo se una delle due e' al livello 0.</summary>
public readonly record struct StatoLayout(int LivelloLarghezza, int LivelloAltezza)
{
    public ModoLayout Modo => (ModoLayout)Math.Min(LivelloLarghezza, LivelloAltezza);
}

/// <summary>Valori dei token DynamicResource (App.axaml) per un modo. Solo numeri (DIP): la classe resta testabile senza Avalonia.</summary>
public readonly record struct TokenLayout(
    double PagePadding,
    double CardPadding,
    double SpaziaturaSezioni,
    double TitoloPagina,
    double KpiNumero,
    double KpiNumeroPiccolo,
    double LarghezzaSidebar,
    double MaxLarghezzaPagina,
    double AltezzaMinimaTabella);

/// <summary>Layout adattivo della finestra principale (6.6.1). Il modo dipende dalla dimensione client reale:
/// Ampio: larghezza &gt;= 1600 e altezza &gt;= 900; Standard: larghezza &gt;= 1366 e altezza &gt;= 760; Minimo: larghezza &lt; 1100
/// o altezza &lt; 640; Compatto nei casi restanti. Isteresi di 16 px: per uscire da un livello la dimensione deve scendere
/// di 16 px sotto la soglia, cosi' i bordi non fanno sfarfallare il layout durante il ridimensionamento.
/// Parte pura (<see cref="Calcola"/>, <see cref="Token"/>, <see cref="Classi"/>) piu' lo stato del modo corrente, che i dialoghi leggono.</summary>
public static class LayoutAdattivo
{
    /// <summary>Isteresi in DIP: margine oltre la soglia richiesto per tornare a un livello piu' piccolo.</summary>
    public const double Isteresi = 16;

    /// <summary>Tutte le classi di modo: MainWindow le rimuove prima di applicare quelle del nuovo modo.</summary>
    public static readonly string[] TutteLeClassi = { "ampio", "standard", "compatto", "minimo" };

    // Soglie per livello (indice = livello raggiunto); il livello 0 e' sotto la prima soglia utile.
    private static readonly double[] SogliaLarghezza = { 0, 1100, 1366, 1600 };
    private static readonly double[] SogliaAltezza = { 0, 640, 760, 900 };

    private static StatoLayout? _stato;

    /// <summary>Modo corrente della finestra principale. I dialoghi lo leggono all'apertura; prima della prima misura vale Standard.</summary>
    public static ModoLayout ModoCorrente { get; private set; } = ModoLayout.Standard;

    /// <summary>Calcolo puro. Senza stato precedente valgono le soglie esatte; con lo stato precedente un livello resta
    /// attivo finche' la dimensione non scende di <see cref="Isteresi"/> px sotto la sua soglia.</summary>
    public static StatoLayout Calcola(double larghezza, double altezza, StatoLayout? precedente = null) =>
        new(Livello(larghezza, SogliaLarghezza, precedente?.LivelloLarghezza),
            Livello(altezza, SogliaAltezza, precedente?.LivelloAltezza));

    /// <summary>Modo senza stato precedente (soglie esatte).</summary>
    public static ModoLayout Modo(double larghezza, double altezza) => Calcola(larghezza, altezza).Modo;

    /// <summary>Aggiorna il modo corrente con la dimensione client della finestra principale (solo thread UI).
    /// Restituisce null se la dimensione non e' ancora valida (finestra non misurata).</summary>
    public static ModoLayout? Aggiorna(double larghezza, double altezza)
    {
        if (!(larghezza > 0) || !(altezza > 0) || double.IsInfinity(larghezza) || double.IsInfinity(altezza)) return null;
        _stato = Calcola(larghezza, altezza, _stato);
        ModoCorrente = _stato.Value.Modo;
        return ModoCorrente;
    }

    /// <summary>Riporta lo stato alla prima misura. Serve solo ai test headless: l'isteresi dipende dall'ultimo modo.</summary>
    internal static void ReimpostaStato()
    {
        _stato = null;
        ModoCorrente = ModoLayout.Standard;
    }

    /// <summary>Classi da applicare alla Window per il modo dato. Minimo implica anche Compatto.</summary>
    public static IReadOnlyList<string> Classi(ModoLayout modo) => modo switch
    {
        ModoLayout.Ampio => new[] { "ampio" },
        ModoLayout.Standard => new[] { "standard" },
        ModoLayout.Compatto => new[] { "compatto" },
        _ => new[] { "compatto", "minimo" },
    };

    /// <summary>Valori dei token per il modo dato. Standard e Ampio mantengono l'aspetto attuale (padding 28, titoli 22,
    /// KPI 26/22, sidebar 240); Ampio allarga soltanto la larghezza massima delle pagine.</summary>
    public static TokenLayout Token(ModoLayout modo) => modo switch
    {
        ModoLayout.Ampio => new(PagePadding: 28, CardPadding: 16, SpaziaturaSezioni: 20, TitoloPagina: 22, KpiNumero: 26,
            KpiNumeroPiccolo: 22, LarghezzaSidebar: 240, MaxLarghezzaPagina: 1600, AltezzaMinimaTabella: 200),
        ModoLayout.Standard => new(PagePadding: 28, CardPadding: 16, SpaziaturaSezioni: 20, TitoloPagina: 22, KpiNumero: 26,
            KpiNumeroPiccolo: 22, LarghezzaSidebar: 240, MaxLarghezzaPagina: 1180, AltezzaMinimaTabella: 200),
        ModoLayout.Compatto => new(PagePadding: 16, CardPadding: 12, SpaziaturaSezioni: 12, TitoloPagina: 18, KpiNumero: 20,
            KpiNumeroPiccolo: 20, LarghezzaSidebar: 200, MaxLarghezzaPagina: 1180, AltezzaMinimaTabella: 200),
        _ => new(PagePadding: 12, CardPadding: 10, SpaziaturaSezioni: 10, TitoloPagina: 18, KpiNumero: 20,
            KpiNumeroPiccolo: 20, LarghezzaSidebar: 64, MaxLarghezzaPagina: 1180, AltezzaMinimaTabella: 200),
    };

    private static int Livello(double valore, double[] soglie, int? precedente)
    {
        if (precedente is not { } livello) return LivelloEsatto(valore, soglie);
        // Salita: si entra nel livello superiore alla soglia esatta.
        while (livello < soglie.Length - 1 && valore >= soglie[livello + 1]) livello++;
        // Discesa: si lascia il livello corrente solo quando la dimensione e' Isteresi px sotto la sua soglia.
        while (livello > 0 && valore < soglie[livello] - Isteresi) livello--;
        return livello;
    }

    private static int LivelloEsatto(double valore, double[] soglie)
    {
        var livello = 0;
        while (livello < soglie.Length - 1 && valore >= soglie[livello + 1]) livello++;
        return livello;
    }
}
