using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata per il feedback utente 2026-08-26: la matrice Audit ordinava i Requisito.Id
/// con StringComparer.OrdinalIgnoreCase, mettendo "ST-100" prima di "ST-99" — con ID che arrivano oltre
/// ST-1700 il risultato sembrava troncato ("vedo solo fino alla riga ST99"), anche se nessuna riga mancava
/// davvero: erano solo mescolate in un ordine non numerico.</summary>
public class NaturalStringComparerTests
{
    [Fact]
    public void Ordina_IdConSuffissoNumerico_InOrdineNumericoNonAlfabetico()
    {
        var ids = new[] { "ST-1", "ST-2", "ST-9", "ST-10", "ST-11", "ST-50", "ST-99", "ST-100", "ST-101", "ST-1699", "ST-1700" };
        var mescolati = new[] { "ST-100", "ST-1700", "ST-2", "ST-9", "ST-1", "ST-99", "ST-1699", "ST-11", "ST-101", "ST-10", "ST-50" };

        var ordinati = mescolati.OrderBy(x => x, NaturalStringComparer.Instance).ToArray();

        Assert.Equal(ids, ordinati);
    }

    [Fact]
    public void Ordina_IdSenzaCifre_ComeUnConfrontoAlfabeticoNormale()
    {
        var ordinati = new[] { "Beta", "alpha", "Gamma" }.OrderBy(x => x, NaturalStringComparer.Instance).ToArray();
        Assert.Equal(new[] { "alpha", "Beta", "Gamma" }, ordinati);
    }

    [Fact]
    public void Ordina_ZeriIniziali_NonAlteranoIlValoreNumerico()
    {
        // ST-009 e ST-9 valgono numericamente lo stesso (9, lo zero iniziale è solo padding): a parità di
        // valore l'ordine tra i due non è definito da questo confronto, ma ST-10 deve venire dopo entrambi.
        var ordinati = new[] { "ST-10", "ST-009", "ST-9" }.OrderBy(x => x, NaturalStringComparer.Instance).ToArray();
        Assert.Equal("ST-10", ordinati[2]);
        Assert.Equal(0, NaturalStringComparer.Instance.Compare("ST-009", "ST-9"));
    }
}
