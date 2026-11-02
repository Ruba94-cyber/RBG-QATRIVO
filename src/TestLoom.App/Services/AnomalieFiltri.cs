using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Colonne per cui la pagina Anomalie supporta l'ordinamento. Le prime otto hanno una colonna dedicata
/// nell'indice SQLite locale (LocalNonConformitaIndexStore): l'ordinamento avviene sull'intero risultato
/// filtrato, non sulla sola pagina visibile. Le ultime quattro (Test collegato, Descrizione breve, Ticket, Veicolo) dipendono dal Test collegato e non hanno colonna
/// nell'indice: si ordinano sempre in memoria (vedi <see cref="AnomalieOrdinamentoChiaveEstensioni.RichiedeTest"/>).</summary>
public enum AnomalieOrdinamentoChiave
{
    Apertura, Id, Stato, Descrizione, Assegnatario, Priorita, Gravita, Chiusura,
    TestCollegato, DescrizioneBreve, Ticket, Veicolo
}

public static class AnomalieOrdinamentoChiaveEstensioni
{
    public static bool RichiedeTest(AnomalieOrdinamentoChiave chiave) =>
        chiave is AnomalieOrdinamentoChiave.TestCollegato or AnomalieOrdinamentoChiave.DescrizioneBreve
            or AnomalieOrdinamentoChiave.Ticket or AnomalieOrdinamentoChiave.Veicolo;
}

/// <summary>Comparatore per le chiavi che dipendono dal Test collegato: stessi testi mostrati in tabella (descrizione breve della
/// segnalazione o del Test, ticket Ticketing della NC o del Test, primo veicolo in ordine alfabetico), spareggio per Id e Uid.</summary>
public static class AnomalieOrdinamentoConTestComparer
{
    public static IComparer<(NonConformita Nc, TestCase? Test, string TestId)> Crea(AnomalieOrdinamento ord)
    {
        var dir = ord.Decrescente ? -1 : 1;
        return Comparer<(NonConformita Nc, TestCase? Test, string TestId)>.Create((a, b) =>
        {
            var c = AnomalieOrdinamentoComparer.ConfrontaNoCase(Testo(a, ord.Chiave), Testo(b, ord.Chiave));
            if (c == 0) c = AnomalieOrdinamentoComparer.ConfrontaNoCase(a.Nc.Id ?? "", b.Nc.Id ?? "");
            if (c == 0) c = string.CompareOrdinal(a.Nc.Uid.ToString("N"), b.Nc.Uid.ToString("N"));
            return c * dir;
        });
    }

    private static string Testo((NonConformita Nc, TestCase? Test, string TestId) r, AnomalieOrdinamentoChiave chiave) => chiave switch
    {
        AnomalieOrdinamentoChiave.TestCollegato => r.TestId ?? "",
        AnomalieOrdinamentoChiave.DescrizioneBreve => !string.IsNullOrWhiteSpace(r.Nc.DescrizioneBreveSegnalata)
            ? r.Nc.DescrizioneBreveSegnalata : r.Test?.DescrizioneBreve ?? "",
        AnomalieOrdinamentoChiave.Ticket => (r.Nc.CollegamentiPolarion.Count > 0
                ? r.Nc.CollegamentiPolarion.Select(t => t.Codice ?? "")
                : r.Test?.TicketCollegati.Select(t => t.Riferimento ?? "") ?? Enumerable.Empty<string>())
            .Where(t => t.Length > 0).OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault() ?? "",
        AnomalieOrdinamentoChiave.Veicolo => VeicoliAssociati(r.Nc, r.Test)
            .Where(v => !string.IsNullOrWhiteSpace(v)).OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault() ?? "",
        _ => "",
    };

    /// <summary>Veicoli dichiarati nella segnalazione e quelli del test collegato, senza duplicati.</summary>
    public static IEnumerable<string> VeicoliAssociati(NonConformita nc, TestCase? test) =>
        (nc.VeicoliSegnalati ?? new List<string>())
            .Concat(test?.VeicoliEffettivi ?? Array.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase);
}

public enum FiltroPresenzaTicketEsta { Tutti, ConTicket, SenzaTicket }

/// <summary>Ordinamento richiesto. A parita' di chiave l'ordine e' sempre deterministico (Id, poi Uid nella
/// stessa direzione) cosi' la paginazione non duplica ne' perde righe fra una pagina e l'altra.</summary>
public readonly record struct AnomalieOrdinamento(AnomalieOrdinamentoChiave Chiave, bool Decrescente)
{
    public static AnomalieOrdinamento Predefinito => new(AnomalieOrdinamentoChiave.Apertura, true);
}

/// <summary>Proiezione di una NC sulle colonne di filtro/ordinamento dell'indice SQLite locale.
/// Calcolata in un solo punto, sia per l'upsert incrementale sia per il backfill della migrazione.</summary>
public readonly record struct AnomalieIndiceRiga(
    int StatoWorkflow, int Stato, int? StatoDettaglio, int? Destinatario, int Priorita, int? Severita,
    string Owner, long AperturaTicks, long? ChiusuraTicks, string Descrizione, string TestoCercabile, bool HaTicketEsta)
{
    public static AnomalieIndiceRiga Da(NonConformita nc) => new(
        (int)nc.StatoWorkflow, (int)nc.Stato,
        nc.StatoDettaglio.HasValue ? (int)nc.StatoDettaglio.Value : null,
        nc.Destinatario.HasValue ? (int)nc.Destinatario.Value : null,
        (int)nc.Priorita,
        nc.Severita.HasValue ? (int)nc.Severita.Value : null,
        nc.Owner ?? "", nc.DataApertura.Ticks, nc.DataChiusura?.Ticks,
        nc.Descrizione ?? "", AnomalieFiltroNc.TestoIndicizzato(nc), nc.CollegamentiPolarion.Count > 0);

    /// <summary>Firma FNV-1a a 64 bit di tutti i valori proiettati (compreso il testo cercabile): serve solo a
    /// evitare riscritture dell'indice per NC invariate; una collisione ritarderebbe l'aggiornamento di una riga
    /// fino alla sua prossima modifica, mai perdendo dati autorevoli.</summary>
    public long Firma()
    {
        unchecked
        {
            var h = 14695981039346656037UL;
            void Mix(long v) { for (var i = 0; i < 8; i++) { h ^= (byte)(v >> (i * 8)); h *= 1099511628211UL; } }
            void MixS(string s) { foreach (var ch in s) { h ^= ch; h *= 1099511628211UL; } h ^= 0xFF; h *= 1099511628211UL; }
            Mix(StatoWorkflow); Mix(Stato); Mix(StatoDettaglio ?? long.MinValue); Mix(Destinatario ?? long.MinValue);
            Mix(Priorita); Mix(Severita ?? long.MinValue); MixS(Owner); Mix(AperturaTicks); Mix(ChiusuraTicks ?? long.MinValue);
            MixS(Descrizione); MixS(TestoCercabile); Mix(HaTicketEsta ? 1 : 0);
            return (long)h;
        }
    }
}

/// <summary>Filtri propri di una Non Conformita' (risolvibili dal solo record NC, quindi interrogabili
/// sull'indice SQLite senza caricare alcun Test). Tutti i criteri sono in AND; dentro una multi-selezione le
/// voci sono in OR; una lista vuota o un valore nullo significano "nessun vincolo". Gli intervalli di data sono
/// INCLUSIVI di entrambi gli estremi, a granularita' di giorno (l'ora e' ignorata): una NC aperta il 15/03
/// alle 17:30 rientra in "A = 15/03". Una NC senza data di chiusura non soddisfa mai un vincolo sulla data di
/// chiusura.</summary>
public enum UnitaDurataNc { Giorni, Settimane, Mesi }

/// <summary>Etichette localizzate (IT/EN/FR) delle unita' di durata: l'interfaccia non mostra mai il nome grezzo dell'enum.</summary>
public static class UnitaDurataEtichette
{
    public static string Etichetta(UnitaDurataNc unita) => unita switch
    {
        UnitaDurataNc.Giorni => LocalizationService.Instance.Scegli("Giorni", "Days", "Jours"),
        UnitaDurataNc.Settimane => LocalizationService.Instance.Scegli("Settimane", "Weeks", "Semaines"),
        UnitaDurataNc.Mesi => LocalizationService.Instance.Scegli("Mesi", "Months", "Mois"),
        _ => unita.ToString()
    };
}

/// <summary>Converte (unita', Lingua) nell'etichetta localizzata; il secondo valore serve solo a rinfrescare il testo al cambio lingua.</summary>
public sealed class UnitaDurataEtichettaConverter : Avalonia.Data.Converters.IMultiValueConverter
{
    public static readonly UnitaDurataEtichettaConverter Instance = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        values.Count > 0 && values[0] is UnitaDurataNc u ? UnitaDurataEtichette.Etichetta(u) : values.FirstOrDefault()?.ToString();
}

public sealed class AnomalieFiltroNc
{
    public StatoNonConformita? Stato { get; init; }
    public StatoDettaglioNc? StatoDettaglio { get; init; }
    public DestinatarioSegnalazione? Destinatario { get; init; }
    public IReadOnlyList<StatoAnomalia> StatiWorkflow { get; init; } = Array.Empty<StatoAnomalia>();
    public IReadOnlyList<PrioritaOperativa> Priorita { get; init; } = Array.Empty<PrioritaOperativa>();
    public IReadOnlyList<SeveritaAnomalia> Gravita { get; init; } = Array.Empty<SeveritaAnomalia>();
    /// <summary>Confronto esatto (ordinale, case-sensitive) come il vecchio filtro Owner a valore singolo.</summary>
    public IReadOnlyList<string> Assegnatari { get; init; } = Array.Empty<string>();
    public DateTime? AperturaDa { get; init; }
    public DateTime? AperturaA { get; init; }
    public DateTime? ChiusuraDa { get; init; }
    public DateTime? ChiusuraA { get; init; }
    /// <summary>Soglia esclusiva; -1 indica un input non valido.</summary>
    public int? DurataMinima { get; init; }
    public UnitaDurataNc UnitaDurata { get; init; }
    /// <summary>Presenza di un ticket TICKET collegato direttamente alla NC; null non filtra.</summary>
    public bool? HaTicketEsta { get; init; }
    /// <summary>Ricerca libera (Contains, senza distinzione di maiuscole) sui campi di <see cref="TestoCorrisponde"/>.</summary>
    public string? Testo { get; init; }

    public bool HaTesto => !string.IsNullOrWhiteSpace(Testo);

    public AnomalieFiltroNc SenzaTesto() => new()
    {
        Stato = Stato, StatoDettaglio = StatoDettaglio, Destinatario = Destinatario, StatiWorkflow = StatiWorkflow,
        Priorita = Priorita, Gravita = Gravita, Assegnatari = Assegnatari, AperturaDa = AperturaDa, AperturaA = AperturaA,
        ChiusuraDa = ChiusuraDa, ChiusuraA = ChiusuraA, DurataMinima = DurataMinima, UnitaDurata = UnitaDurata, HaTicketEsta = HaTicketEsta, Testo = null
    };

    /// <summary>Primo istante (incluso) dell'intervallo di apertura, o null.</summary>
    public long? AperturaDaTicks => AperturaDa?.Date.Ticks;
    /// <summary>Primo istante ESCLUSO oltre l'intervallo di apertura (giorno successivo a "A"), o null.</summary>
    public long? AperturaAEsclusoTicks => AperturaA?.Date.AddDays(1).Ticks;
    public long? ChiusuraDaTicks => ChiusuraDa?.Date.Ticks;
    public long? ChiusuraAEsclusoTicks => ChiusuraA?.Date.AddDays(1).Ticks;

    /// <summary>Implementazione di riferimento in memoria. L'indice SQLite deve produrre esattamente lo stesso
    /// insieme (verificato da test di equivalenza); e' anche il percorso di ripiego quando l'indice non e'
    /// utilizzabile. <paramref name="testIdVisualizzato"/> e' l'id del Test corrente quando e' stato risolto,
    /// altrimenti il TestCaseId grezzo.</summary>
    public bool Corrisponde(NonConformita nc, string? testIdVisualizzato = null, bool applicaTesto = true)
    {
        if (HaTicketEsta.HasValue && (nc.CollegamentiPolarion.Count > 0) != HaTicketEsta.Value) return false;
        if (Stato.HasValue && nc.Stato != Stato.Value) return false;
        if (StatoDettaglio.HasValue && nc.StatoDettaglio != StatoDettaglio) return false;
        if (Destinatario.HasValue && nc.Destinatario != Destinatario) return false;
        if (StatiWorkflow.Count > 0 && !Contiene(StatiWorkflow, nc.StatoWorkflow)) return false;
        if (Priorita.Count > 0 && !Contiene(Priorita, nc.Priorita)) return false;
        if (Gravita.Count > 0 && (!nc.Severita.HasValue || !Contiene(Gravita, nc.Severita.Value))) return false;
        if (Assegnatari.Count > 0 && !Contiene(Assegnatari, nc.Owner)) return false;
        if (!NelIntervallo(nc.DataApertura, AperturaDaTicks, AperturaAEsclusoTicks)) return false;
        if (DurataMinima is { } soglia && !SuperaDurata(nc, soglia, UnitaDurata)) return false;
        if (ChiusuraDa.HasValue || ChiusuraA.HasValue)
        {
            if (!nc.DataChiusura.HasValue) return false;
            if (!NelIntervallo(nc.DataChiusura.Value, ChiusuraDaTicks, ChiusuraAEsclusoTicks)) return false;
        }
        if (applicaTesto && HaTesto && !TestoCorrisponde(nc, Testo!.Trim(), testIdVisualizzato ?? nc.TestCaseId)) return false;
        return true;
    }

    private static bool NelIntervallo(DateTime valore, long? daTicks, long? aEsclusoTicks) =>
        (!daTicks.HasValue || valore.Ticks >= daTicks.Value) && (!aEsclusoTicks.HasValue || valore.Ticks < aEsclusoTicks.Value);

    private static bool SuperaDurata(NonConformita nc, int soglia, UnitaDurataNc unita)
    {
        if (soglia < 0 || !nc.DataChiusura.HasValue) return false;
        try
        {
            // Durata di calendario a granularita' di giorno (come ExportPivotService.GiorniDiVita): si confrontano le date, non gli orari.
            // Soglia esclusiva ("maggiore di"): con apertura 31/01 e soglia 1 mese il limite e' il 28/02 (AddMonths), quindi 28/02 non supera, 01/03 si'.
            var apertura = nc.DataApertura.Date;
            var limite = unita switch
            {
                UnitaDurataNc.Giorni => apertura.AddDays(soglia),
                UnitaDurataNc.Settimane => apertura.AddDays((long)soglia * 7),
                UnitaDurataNc.Mesi => apertura.AddMonths(soglia),
                _ => DateTime.MaxValue
            };
            return nc.DataChiusura.Value.Date > limite;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static bool Contiene<T>(IReadOnlyList<T> lista, T valore) where T : notnull
    {
        for (var i = 0; i < lista.Count; i++)
            if (EqualityComparer<T>.Default.Equals(lista[i], valore)) return true;
        return false;
    }

    /// <summary>Un solo elenco di campi cercati dalla ricerca libera. L'identificativo del Test e' un parametro
    /// perche' il percorso con filtri d'ambito usa l'id del Test corrente (che puo' arrivare da ProveCollegate).
    /// <see cref="TestoIndicizzato"/> deve restare allineato a questo elenco.</summary>
    public static bool TestoCorrisponde(NonConformita nc, string q, string testIdVisualizzato) =>
        nc.Descrizione?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
        || nc.Owner?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
        || nc.Id?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
        || testIdVisualizzato?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
        || nc.Azione?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
        || nc.DescrizioneBreveSegnalata?.Contains(q, StringComparison.OrdinalIgnoreCase) == true
        || ContieneTesto(nc.EcuSegnalate, q)
        || ContieneTesto(nc.VeicoliSegnalati, q);

    private static bool ContieneTesto(IReadOnlyList<string>? valori, string q)
    {
        if (valori == null) return false;
        for (var i = 0; i < valori.Count; i++)
            if (valori[i]?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) return true;
        return false;
    }

    private const char Separatore = '\u001F';

    /// <summary>Testo cercabile per l'indice: gli stessi campi di <see cref="TestoCorrisponde"/> (con il
    /// TestCaseId grezzo), in minuscolo e separati da un carattere di controllo che un operatore non puo'
    /// digitare, cosi' una sottostringa non puo' "attraversare" due campi.</summary>
    public static string TestoIndicizzato(NonConformita nc)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(nc.Descrizione).Append(Separatore).Append(nc.Owner).Append(Separatore).Append(nc.Id).Append(Separatore)
          .Append(nc.TestCaseId).Append(Separatore).Append(nc.Azione).Append(Separatore).Append(nc.DescrizioneBreveSegnalata);
        foreach (var e in nc.EcuSegnalate ?? new EcuSelection()) sb.Append(Separatore).Append(e);
        foreach (var v in nc.VeicoliSegnalati ?? new List<string>()) sb.Append(Separatore).Append(v);
        return sb.ToString().ToUpperInvariant();
    }

    /// <summary>Chiave normalizzata per l'interrogazione dell'indice (stessa normalizzazione di <see cref="TestoIndicizzato"/>).</summary>
    public static string TestoRicerca(string q) => q.Trim().ToUpperInvariant();
}

/// <summary>Filtri che richiedono la risoluzione del Test collegato (ambito componente/veicolo/gamma/funzionalita' e
/// ticket): non sono interamente contenuti nel record NC, quindi non vengono delegati all'indice SQLite ma
/// applicati in memoria sui soli candidati che hanno gia' superato i filtri propri della NC.</summary>
public sealed class AnomalieFiltroAmbito
{
    public HashSet<string>? Veicoli { get; init; }
    public HashSet<string>? Componente { get; init; }
    public HashSet<string>? Gamma { get; init; }
    public HashSet<string>? Funzionalita { get; init; }
    public string? Ticket { get; init; }
    public IReadOnlyDictionary<string, string>? GammaDiVeicolo { get; init; }

    public bool Attivo => Veicoli != null || Componente != null || Gamma != null || Funzionalita != null
        || !string.IsNullOrWhiteSpace(Ticket);

    /// <summary>L'ambito di una NC non vive soltanto sul Test collegato: una segnalazione di rete lo porta su
    /// EcuSegnalate/VeicoliSegnalati/FunzionalitaSegnalate. Unione delle due fonti, non alternativa.</summary>
    public bool Corrisponde(NonConformita nc, TestCase? tc)
    {
        if (!string.IsNullOrWhiteSpace(Ticket))
        {
            var q = Ticket!.Trim();
            var match = nc.CollegamentiPolarion.Any(t => t.Codice.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (tc != null && tc.TicketCollegati.Any(t => t.Riferimento.Contains(q, StringComparison.OrdinalIgnoreCase)));
            if (!match) return false;
        }
        if (Veicoli != null && !Contiene(tc?.VeicoliEffettivi, nc.VeicoliSegnalati, Veicoli)) return false;
        if (Componente != null && !Contiene(tc?.Componente, nc.EcuSegnalate, Componente)) return false;
        if (Gamma != null && !ContieneGamma(tc?.VeicoliEffettivi, nc.VeicoliSegnalati)) return false;
        if (Funzionalita != null && !Contiene(tc?.Funzionalita, nc.FunzionalitaSegnalate, Funzionalita)) return false;
        return true;
    }

    /// <summary>Accesso indicizzato, non foreach: corpo del ciclo percorso per ogni candidato a 100k NC (un
    /// foreach su IReadOnlyList boxa l'enumeratore a ogni chiamata anche per liste vuote).</summary>
    private static bool Contiene(IReadOnlyList<string>? daTest, IReadOnlyList<string>? daSegnalazione, HashSet<string> cercati)
    {
        if (daTest != null)
            for (var i = 0; i < daTest.Count; i++)
                if (cercati.Contains(daTest[i])) return true;
        if (daSegnalazione != null)
            for (var i = 0; i < daSegnalazione.Count; i++)
                if (cercati.Contains(daSegnalazione[i])) return true;
        return false;
    }

    private bool ContieneGamma(IReadOnlyList<string>? daTest, IReadOnlyList<string>? daSegnalazione)
    {
        if (daTest != null)
            for (var i = 0; i < daTest.Count; i++)
                if (GammaCorrisponde(daTest[i])) return true;
        if (daSegnalazione != null)
            for (var i = 0; i < daSegnalazione.Count; i++)
                if (GammaCorrisponde(daSegnalazione[i])) return true;
        return false;
    }

    private bool GammaCorrisponde(string veicolo) =>
        GammaDiVeicolo != null && GammaDiVeicolo.TryGetValue(veicolo, out var g) && g != null && Gamma!.Contains(g);
}

/// <summary>Comparatore in memoria equivalente all'ORDER BY dell'indice SQLite (ripiego e test di equivalenza).
/// Chiavi nulle (Gravita/Chiusura non impostate) precedono sempre quelle valorizzate in ordine crescente, come
/// in SQLite. Le stringhe si confrontano come COLLATE NOCASE (vedi ConfrontaNoCase).</summary>
public static class AnomalieOrdinamentoComparer
{
    public static IComparer<NonConformita> Crea(AnomalieOrdinamento ord)
    {
        var dir = ord.Decrescente ? -1 : 1;
        return Comparer<NonConformita>.Create((a, b) =>
        {
            var c = ConfrontaChiave(a, b, ord.Chiave);
            if (c == 0) c = ConfrontaNoCase(a.Id ?? "", b.Id ?? "");
            if (c == 0) c = string.CompareOrdinal(a.Uid.ToString("N"), b.Uid.ToString("N"));
            return c * dir;
        });
    }

    /// <summary>Stessa semantica del COLLATE NOCASE di SQLite: solo le lettere ASCII si piegano in minuscolo,
    /// poi confronto ordinale (un prefisso precede la stringa piu' lunga). NON equivale a OrdinalIgnoreCase,
    /// che piega in maiuscolo e ordina diversamente i caratteri fra 'Z' e 'a' (es. '_').</summary>
    public static int ConfrontaNoCase(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var x = a[i]; var y = b[i];
            if (x is >= 'A' and <= 'Z') x = (char)(x + 32);
            if (y is >= 'A' and <= 'Z') y = (char)(y + 32);
            if (x != y) return x < y ? -1 : 1;
        }
        return a.Length.CompareTo(b.Length);
    }

    private static int ConfrontaChiave(NonConformita a, NonConformita b, AnomalieOrdinamentoChiave chiave) => chiave switch
    {
        AnomalieOrdinamentoChiave.Apertura => a.DataApertura.Ticks.CompareTo(b.DataApertura.Ticks),
        AnomalieOrdinamentoChiave.Id => ConfrontaNoCase(a.Id ?? "", b.Id ?? ""),
        AnomalieOrdinamentoChiave.Stato => ((int)a.StatoWorkflow).CompareTo((int)b.StatoWorkflow),
        AnomalieOrdinamentoChiave.Descrizione => ConfrontaNoCase(a.Descrizione ?? "", b.Descrizione ?? ""),
        AnomalieOrdinamentoChiave.Assegnatario => ConfrontaNoCase(a.Owner ?? "", b.Owner ?? ""),
        AnomalieOrdinamentoChiave.Priorita => ((int)a.Priorita).CompareTo((int)b.Priorita),
        AnomalieOrdinamentoChiave.Gravita => (a.Severita.HasValue ? (long)a.Severita.Value : long.MinValue)
            .CompareTo(b.Severita.HasValue ? (long)b.Severita.Value : long.MinValue),
        AnomalieOrdinamentoChiave.Chiusura => (a.DataChiusura?.Ticks ?? long.MinValue).CompareTo(b.DataChiusura?.Ticks ?? long.MinValue),
        _ => 0
    };
}
