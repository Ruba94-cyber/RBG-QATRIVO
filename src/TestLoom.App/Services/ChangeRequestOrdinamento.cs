using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Colonne per cui la tabella Change Request supporta l'ordinamento. L'ordinamento avviene sull'intero risultato filtrato,
/// non sulla sola pagina visibile.</summary>
public enum ChangeRequestOrdinamentoChiave
{
    Apertura, Id, Titolo, DescrizioneBreve, Descrizione, Ticket, Stato, Veicolo, Implementazione, Chiusura
}

/// <summary>Ordinamento richiesto. A parita' di chiave l'ordine e' deterministico (Id, poi Uid nella stessa direzione), cosi' la
/// paginazione non duplica ne' perde righe fra una pagina e l'altra.</summary>
public readonly record struct ChangeRequestOrdinamento(ChangeRequestOrdinamentoChiave Chiave, bool Decrescente)
{
    public static ChangeRequestOrdinamento Predefinito => new(ChangeRequestOrdinamentoChiave.Apertura, true);
}

public static class ChangeRequestOrdinamentoComparer
{
    public static IComparer<ChangeRequest> Crea(ChangeRequestOrdinamento ord)
    {
        var segno = ord.Decrescente ? -1 : 1;
        return Comparer<ChangeRequest>.Create((a, b) =>
        {
            var c = ConfrontaChiave(a, b, ord.Chiave);
            if (c == 0) c = ConfrontaNoCase(a.Id ?? "", b.Id ?? "");
            if (c == 0) c = string.CompareOrdinal(a.Uid.ToString(), b.Uid.ToString());
            return c * segno;
        });
    }

    /// <summary>Veicoli in ordine alfabetico: il primo e' quello mostrato nella colonna e usato per ordinare.</summary>
    public static IReadOnlyList<string> VeicoliOrdinati(ChangeRequest cr) =>
        cr.VeicoliImpattati.Where(v => !string.IsNullOrWhiteSpace(v)).OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase).ToList();

    private static int ConfrontaChiave(ChangeRequest a, ChangeRequest b, ChangeRequestOrdinamentoChiave chiave) => chiave switch
    {
        ChangeRequestOrdinamentoChiave.Apertura => a.DataCreazione.Ticks.CompareTo(b.DataCreazione.Ticks),
        ChangeRequestOrdinamentoChiave.Id => ConfrontaNoCase(a.Id ?? "", b.Id ?? ""),
        ChangeRequestOrdinamentoChiave.Titolo => ConfrontaNoCase(a.Titolo ?? "", b.Titolo ?? ""),
        ChangeRequestOrdinamentoChiave.DescrizioneBreve => ConfrontaNoCase(a.DescrizioneBreve ?? "", b.DescrizioneBreve ?? ""),
        ChangeRequestOrdinamentoChiave.Descrizione => ConfrontaNoCase(a.Descrizione ?? "", b.Descrizione ?? ""),
        ChangeRequestOrdinamentoChiave.Ticket => ConfrontaNoCase(PrimoTicket(a), PrimoTicket(b)),
        ChangeRequestOrdinamentoChiave.Stato => ((int)a.Stato).CompareTo((int)b.Stato),
        ChangeRequestOrdinamentoChiave.Veicolo => ConfrontaNoCase(PrimoVeicolo(a), PrimoVeicolo(b)),
        ChangeRequestOrdinamentoChiave.Implementazione => (a.ImplementataIl?.Ticks ?? long.MinValue).CompareTo(b.ImplementataIl?.Ticks ?? long.MinValue),
        ChangeRequestOrdinamentoChiave.Chiusura => (a.ChiusaIl?.Ticks ?? long.MinValue).CompareTo(b.ChiusaIl?.Ticks ?? long.MinValue),
        _ => 0,
    };

    private static string PrimoVeicolo(ChangeRequest cr) => VeicoliOrdinati(cr).FirstOrDefault() ?? "";

    private static string PrimoTicket(ChangeRequest cr) =>
        cr.TicketCollegati.Select(t => t.Riferimento ?? "").Where(t => t.Length > 0).OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault() ?? "";

    private static int ConfrontaNoCase(string a, string b) => string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
}
