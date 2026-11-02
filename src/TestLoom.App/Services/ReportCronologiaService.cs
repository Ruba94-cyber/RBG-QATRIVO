using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Aggiorna lo storico locale dei report esportati con "Salva con nome" (Addendum: grill-me
/// 2026-08-18, follow-up — il percorso può essere ovunque sul PC, non solo nella cartella condivisa, quindi
/// non si può più ricostruire la Cronologia scansionando una cartella fissa). Pura, senza I/O: il chiamante
/// (ReportViewModel) si occupa di persistere il risultato in AppConfig.</summary>
public static class ReportCronologiaService
{
    private const int MassimoVoci = 50;

    public static List<ReportSalvato> Aggiungi(List<ReportSalvato> cronologia, string percorso, DateTime quando)
    {
        var aggiornata = new List<ReportSalvato> { new() { Percorso = percorso, Data = quando } };
        aggiornata.AddRange(cronologia);
        return aggiornata.Take(MassimoVoci).ToList();
    }
}
