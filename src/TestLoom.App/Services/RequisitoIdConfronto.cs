namespace TestLoom.App.Services;

/// <summary>Confronto unico degli ID requisito: maiuscole e minuscole non contano. Un Test e il Master possono riportare la
/// stessa sigla con grafia diversa, e il confronto ordinale la lasciava fuori da copertura, punteggi e Report.</summary>
public static class RequisitoIdConfronto
{
    public static bool Uguali(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static bool Contiene(IEnumerable<string> requisitoIds, string requisitoId) =>
        requisitoIds.Contains(requisitoId, StringComparer.OrdinalIgnoreCase);
}
