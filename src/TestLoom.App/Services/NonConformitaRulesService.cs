using TestLoom.App.Models;

namespace TestLoom.App.Services;

public enum AzioneNc { NessunaAzione, AperturaAutomatica, ChiediApertura, ChiusuraAutomatica }

/// <summary>Regole di apertura/chiusura NC (§5.1). L'auto-chiusura resta sempre scoped alla stessa istanza di
/// test case: non chiude mai automaticamente una NC su un test case diverso anche se correlato (Addendum §16.4)
/// — la risoluzione "via test correlato" richiede sempre una chiusura manuale esplicita.</summary>
public static class NonConformitaRulesService
{
    public static AzioneNc ValutaAzione(Esito nuovoEsito, bool testCaseHaNcAperta)
    {
        return nuovoEsito switch
        {
            Esito.NonConforme => AzioneNc.AperturaAutomatica,
            Esito.ConformeConDeviazioni => AzioneNc.ChiediApertura,
            Esito.Conforme when testCaseHaNcAperta => AzioneNc.ChiusuraAutomatica,
            _ => AzioneNc.NessunaAzione
        };
    }

    public const int LunghezzaMassimaSintesiProblema = 80;

    /// <summary>Titolo/descrizione breve di un'anomalia creata da un'operazione di test (feedback utente 6.4.x:
    /// "il titolo non deve prendere il dettaglio di esecuzione"): funzionalita' (+ componente) e sintesi del PROBLEMA
    /// (DescrizioneProblema dell'operazione, prima riga, spazi normalizzati, troncata), mai la Nota/dettaglio di
    /// esecuzione. Senza funzionalita' si usa <paramref name="etichettaFallback"/> (es. "Operazione 2").</summary>
    public static string TitoloDaOperazione(string? funzionalita, string? componente, string? descrizioneProblema, string etichettaFallback)
    {
        var titolo = string.IsNullOrWhiteSpace(funzionalita) ? etichettaFallback : funzionalita.Trim();
        if (!string.IsNullOrWhiteSpace(componente)) titolo += $" ({componente.Trim()})";
        var problema = SintesiProblema(descrizioneProblema);
        return problema.Length == 0 ? titolo : $"{titolo}: {problema}";
    }

    private static string SintesiProblema(string? testo)
    {
        if (string.IsNullOrWhiteSpace(testo)) return "";
        var primaRiga = testo.Split('\n', '\r').Select(r => r.Trim()).FirstOrDefault(r => r.Length > 0) ?? "";
        primaRiga = System.Text.RegularExpressions.Regex.Replace(primaRiga, @"\s+", " ");
        if (primaRiga.Length <= LunghezzaMassimaSintesiProblema) return primaRiga;
        var taglio = primaRiga.LastIndexOf(' ', LunghezzaMassimaSintesiProblema - 1);
        if (taglio < LunghezzaMassimaSintesiProblema / 2) taglio = LunghezzaMassimaSintesiProblema - 1;
        return primaRiga[..taglio].TrimEnd(' ', ',', ';', ':', '.') + "…";
    }

    public static NonConformita CreaNuova(string id, string testCaseId, int iterazione, string owner, string descrizione)
    {
        return new NonConformita
        {
            Id = id,
            TestCaseId = testCaseId,
            EsecuzioneIterazione = iterazione,
            DataApertura = DateTime.Now,
            Owner = owner,
            Descrizione = descrizione
        };
    }

    public static void Chiudi(NonConformita nc)
    {
        nc.StatoWorkflow = StatoAnomalia.Conclusa;
        nc.DataChiusura = DateTime.Now;
    }

    /// <summary>Riporta un'anomalia chiusa per errore allo stato aperto (feedback utente 2026-08-21, punto
    /// 9/chiudi: la chiusura non era reversibile). Pulisce data/motivo di chiusura invece di lasciarli come
    /// residuo fuorviante su un'anomalia di nuovo aperta.</summary>
    public static void Riapri(NonConformita nc)
    {
        nc.StatoWorkflow = StatoAnomalia.Confermata;
        nc.DataChiusura = null;
        nc.MotivoChiusura = "";
    }
}
