namespace TestLoom.App.Services;

/// <summary>Confronta due stringhe trattando ogni sequenza di cifre consecutive come un numero intero invece
/// che carattere per carattere (feedback utente 2026-08-26: "vedo solo fino alla riga ST99 nonostante
/// arrivino a 1700+"). Un ordinamento alfabetico puro (StringComparer.OrdinalIgnoreCase, usato prima qui e
/// altrove) mette "ST-100" prima di "ST-99" perché confronta cifra per cifra ('1' &lt; '9'): con ID come
/// ST-1..ST-1700+ il risultato sembra "rotto" o troncato, anche se nessuna riga manca davvero — sono solo
/// mescolate in un ordine che non corrisponde all'intuizione numerica dell'utente. Stesso principio usato da
/// Esplora File di Windows per ordinare i nomi dei file (1, 2, ..., 10, 11, ..., non 1, 10, 11, ..., 2).</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x == null || y == null) return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
            {
                var sx = ix; while (sx < x.Length && char.IsDigit(x[sx])) sx++;
                var sy = iy; while (sy < y.Length && char.IsDigit(y[sy])) sy++;

                var numX = x[ix..sx].TrimStart('0');
                var numY = y[iy..sy].TrimStart('0');
                if (numX.Length == 0) numX = "0";
                if (numY.Length == 0) numY = "0";

                var confrontoLunghezza = numX.Length.CompareTo(numY.Length);
                if (confrontoLunghezza != 0) return confrontoLunghezza;
                var confrontoValore = string.CompareOrdinal(numX, numY);
                if (confrontoValore != 0) return confrontoValore;

                ix = sx; iy = sy;
            }
            else
            {
                var cx = char.ToUpperInvariant(x[ix]);
                var cy = char.ToUpperInvariant(y[iy]);
                if (cx != cy) return cx.CompareTo(cy);
                ix++; iy++;
            }
        }
        return (x.Length - ix).CompareTo(y.Length - iy);
    }
}
