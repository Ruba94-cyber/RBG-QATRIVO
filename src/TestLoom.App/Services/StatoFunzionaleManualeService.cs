using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Esito di una combinazione propagato da un altro veicolo della stessa componente (fonte della propagazione).</summary>
public sealed record FonteStatoPropagato(string VeicoloSorgente, string TestIdSorgente, Esito EsitoSorgente);

/// <summary>Decisione per una singola triade: manuale oppure propagazione selezionata dal Master.</summary>
public sealed record DecisioneStatoFunzionale(string Funzionalita, string Componente, string Veicolo,
    Esito? EsitoManuale = null, FonteStatoPropagato? Fonte = null);

/// <summary>Correzione manuale dello stato delle combinazioni funzionalità × componente × veicolo nel Report (6.6.1, fase 2).
/// Stessa architettura dell'Audit (<see cref="ValutazioneRequisitoEcuVeicoloService"/>): catalogo master condiviso con Phase D,
/// scrittura con verifica dello stato atteso (concorrenza ottimistica), consolidamento delle collisioni di chiave prima di ogni
/// mutazione (vince la modifica più recente, a parità l'UID ordinale minore). SOLO Master: il controllo è anche qui, non solo
/// nell'interfaccia. Nota obbligatoria per ogni decisione. Il valore manuale entra nella riduzione "ultimo esito" del Report
/// con la sua DataDecisione (vedi ReportMatriceService.CreaTestManuale).</summary>
public static class StatoFunzionaleManualeService
{
    public const string ErrorePermessoNegato = "Solo un Master puo' correggere lo stato delle combinazioni.";
    public const string ErroreNotaOppureVeicoli = "Nessun veicolo scelto o nota mancante.";
    public const string ErroreEsitoNonEseguito = "Un valore manuale deve essere un esito eseguito (Conforme, Non conforme, Conforme con deviazioni).";

    /// <summary>Chiave di business del catalogo (come PhaseDMasterBusinessKeyService): funzionalità, componente e veicolo.</summary>
    public static string ChiaveCombinazione(StatoFunzionalitaEcuVeicolo v) => v.Funzionalita + "|" + v.Componente + "|" + v.Veicolo;

    private static bool Corrisponde(StatoFunzionalitaEcuVeicolo v, string funzionalita, string componente, string veicolo) =>
        v.Funzionalita.Equals(funzionalita, StringComparison.OrdinalIgnoreCase)
        && v.Componente.Equals(componente, StringComparison.OrdinalIgnoreCase)
        && v.Veicolo.Equals(veicolo, StringComparison.OrdinalIgnoreCase);

    /// <summary>Inserisce o corregge a mano l'esito di una o più combinazioni (stessa funzionalità ed componente, veicoli scelti).
    /// Ogni veicolo ha un solo valore attivo: la scelta sostituisce il precedente, lo storico resta nel journal Phase D.</summary>
    public static bool ImpostaManuale(MasterDataRepository<StatoFunzionalitaEcuVeicolo> repo, string funzionalita, string componente,
        IEnumerable<string> veicoli, Esito esito, string nota, string operatore, bool isMaster, out string? errore)
    {
        if (!isMaster) { errore = ErrorePermessoNegato; return false; }
        var listaVeicoli = veicoli.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (listaVeicoli.Count == 0 || string.IsNullOrWhiteSpace(nota)) { errore = ErroreNotaOppureVeicoli; return false; }
        if (!esito.EEseguito()) { errore = ErroreEsitoNonEseguito; return false; }
        return ProvaAggiorna(repo, valori =>
        {
            ConsolidaCollisioni(valori, operatore);
            var adesso = DateTime.Now;
            foreach (var veicolo in listaVeicoli)
            {
                valori.RemoveAll(v => Corrisponde(v, funzionalita, componente, veicolo));
                valori.Add(new StatoFunzionalitaEcuVeicolo
                {
                    Funzionalita = funzionalita, Componente = componente, Veicolo = veicolo, Esito = esito,
                    Origine = OrigineStatoFunzionalita.Manuale, Nota = nota.Trim(), Operatore = operatore,
                    DataDecisione = adesso, UltimaModifica = adesso, ModificatoDa = operatore, CreatoDa = operatore,
                });
            }
            return true;
        }, out errore);
    }

    /// <summary>Conferma la propagazione dell'esito di un altro veicolo a uno o più veicoli scelti (decisione del Master sul "!").
    /// Il valore diventa un valore manuale di origine Propagata, con fonte e nota.</summary>
    public static bool Propaga(MasterDataRepository<StatoFunzionalitaEcuVeicolo> repo, string funzionalita, string componente,
        IEnumerable<(string Veicolo, FonteStatoPropagato Fonte)> voci, string nota, string operatore, bool isMaster, out string? errore)
    {
        if (!isMaster) { errore = ErrorePermessoNegato; return false; }
        var listaVoci = voci.ToList();
        if (listaVoci.Count == 0 || string.IsNullOrWhiteSpace(nota)) { errore = ErroreNotaOppureVeicoli; return false; }
        return ProvaAggiorna(repo, valori =>
        {
            ConsolidaCollisioni(valori, operatore);
            var adesso = DateTime.Now;
            foreach (var (veicolo, fonte) in listaVoci)
            {
                valori.RemoveAll(v => Corrisponde(v, funzionalita, componente, veicolo));
                valori.Add(new StatoFunzionalitaEcuVeicolo
                {
                    Funzionalita = funzionalita, Componente = componente, Veicolo = veicolo, Esito = fonte.EsitoSorgente,
                    Origine = OrigineStatoFunzionalita.Propagata, SorgenteVeicolo = fonte.VeicoloSorgente,
                    SorgenteTestId = fonte.TestIdSorgente, SorgenteEsito = fonte.EsitoSorgente,
                    Nota = nota.Trim(), Operatore = operatore, DataDecisione = adesso,
                    UltimaModifica = adesso, ModificatoDa = operatore, CreatoDa = operatore,
                });
            }
            return true;
        }, out errore);
    }

    /// <summary>Scrive tutte le decisioni di un pannello in UNA SOLA Save del catalogo Phase D.
    /// La singola append del journal pubblica un batch unitario: a differenza di più chiamate
    /// separate, un guasto sulla seconda combinazione non può lasciare confermata solo la prima.
    /// La fonte viene verificata dal ViewModel immediatamente prima della chiamata.</summary>
    public static bool ApplicaDecisioni(MasterDataRepository<StatoFunzionalitaEcuVeicolo> repo,
        IEnumerable<DecisioneStatoFunzionale> richieste, string nota, string operatore, bool isMaster,
        out string? errore)
    {
        if (!isMaster) { errore = ErrorePermessoNegato; return false; }
        var decisioni = richieste.ToList();
        if (decisioni.Count == 0 || string.IsNullOrWhiteSpace(nota))
        {
            errore = ErroreNotaOppureVeicoli;
            return false;
        }
        var chiavi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var richiesta in decisioni)
        {
            if (string.IsNullOrWhiteSpace(richiesta.Funzionalita) || string.IsNullOrWhiteSpace(richiesta.Componente)
                || string.IsNullOrWhiteSpace(richiesta.Veicolo)
                || (richiesta.EsitoManuale.HasValue == (richiesta.Fonte != null))
                || (richiesta.EsitoManuale is { } esito && !esito.EEseguito())
                || (richiesta.Fonte is { } fonte && (!fonte.EsitoSorgente.EEseguito()
                    || string.IsNullOrWhiteSpace(fonte.VeicoloSorgente)
                    || string.IsNullOrWhiteSpace(fonte.TestIdSorgente)
                    || string.Equals(fonte.VeicoloSorgente, richiesta.Veicolo, StringComparison.OrdinalIgnoreCase)))
                || !chiavi.Add(ChiaveStabile(richiesta.Funzionalita, richiesta.Componente, richiesta.Veicolo)))
            {
                errore = "Decisioni duplicate o non valide: selezionare un solo esito o una fonte esterna per ogni triade.";
                return false;
            }
        }

        return ProvaAggiorna(repo, valori =>
        {
            ConsolidaCollisioni(valori, operatore);
            var adesso = DateTime.Now;
            foreach (var richiesta in decisioni)
            {
                valori.RemoveAll(v => Corrisponde(v, richiesta.Funzionalita, richiesta.Componente, richiesta.Veicolo));
                var fonte = richiesta.Fonte;
                var propagata = fonte != null;
                valori.Add(new StatoFunzionalitaEcuVeicolo
                {
                    Funzionalita = richiesta.Funzionalita, Componente = richiesta.Componente, Veicolo = richiesta.Veicolo,
                    Esito = fonte?.EsitoSorgente ?? richiesta.EsitoManuale!.Value,
                    Origine = propagata ? OrigineStatoFunzionalita.Propagata : OrigineStatoFunzionalita.Manuale,
                    SorgenteVeicolo = fonte?.VeicoloSorgente ?? "",
                    SorgenteTestId = fonte?.TestIdSorgente ?? "",
                    SorgenteEsito = fonte?.EsitoSorgente,
                    Nota = nota.Trim(), Operatore = operatore, DataDecisione = adesso,
                    UltimaModifica = adesso, ModificatoDa = operatore, CreatoDa = operatore,
                });
            }
            return true;
        }, out errore);
    }

    private static string ChiaveStabile(string funzionalita, string componente, string veicolo) =>
        string.Concat(funzionalita.Length, ":", funzionalita,
            componente.Length, ":", componente, veicolo.Length, ":", veicolo);

    /// <summary>Elimina in un solo batch del catalogo gli stati manuali richiesti; se uno non
    /// esiste più, non viene scritta alcuna rimozione.</summary>
    public static bool RimuoviManuali(MasterDataRepository<StatoFunzionalitaEcuVeicolo> repo,
        IEnumerable<(string Funzionalita, string Componente, string Veicolo)> richieste, bool isMaster, out string? errore)
    {
        if (!isMaster) { errore = ErrorePermessoNegato; return false; }
        var lista = richieste.ToList();
        if (lista.Count == 0) { errore = null; return true; }
        if (lista.Any(x => string.IsNullOrWhiteSpace(x.Funzionalita) || string.IsNullOrWhiteSpace(x.Componente)
                || string.IsNullOrWhiteSpace(x.Veicolo)))
        {
            errore = "Una o più combinazioni da rimuovere non sono valide.";
            return false;
        }
        var riuscita = ProvaAggiorna(repo, valori =>
        {
            if (lista.Any(r => !valori.Any(v => Corrisponde(v, r.Funzionalita, r.Componente, r.Veicolo))))
                return false;
            foreach (var r in lista)
                valori.RemoveAll(v => Corrisponde(v, r.Funzionalita, r.Componente, r.Veicolo));
            return true;
        }, out errore);
        if (!riuscita && errore == null) errore = "Uno o più valori manuali non sono più presenti: aggiornare il Report.";
        return riuscita;
    }

    /// <summary>Rimuove il valore manuale di UNA combinazione: torna a valere solo l'esito dei Test (o il "!" se propagabile).</summary>
    public static bool RimuoviManuale(MasterDataRepository<StatoFunzionalitaEcuVeicolo> repo, string funzionalita, string componente,
        string veicolo, bool isMaster, out string? errore)
    {
        if (!isMaster) { errore = ErrorePermessoNegato; return false; }
        return ProvaAggiorna(repo, valori => valori.RemoveAll(v => Corrisponde(v, funzionalita, componente, veicolo)) > 0, out errore);
    }

    /// <summary>Riparazione deterministica delle collisioni di chiave (stessa funzionalità×componente×veicolo, UID diverse): vince
    /// l'UltimaModifica più recente, a parità l'UID ordinale minore; lo scarto resta annotato nel changelog della vincitrice.
    /// La mutazione dipende solo dai dati, così due postazioni che riparano la stessa collisione producono lo stesso valore.</summary>
    public static int ConsolidaCollisioni(List<StatoFunzionalitaEcuVeicolo> valori, string operatore)
    {
        var gruppi = valori.GroupBy(ChiaveCombinazione, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        var rimosse = 0;
        foreach (var gruppo in gruppi)
        {
            var ordinate = gruppo.OrderByDescending(v => v.UltimaModifica).ThenBy(v => v.Uid.ToString("N"), StringComparer.Ordinal).ToList();
            var vincitrice = ordinate[0];
            foreach (var scartata in ordinate.Skip(1))
            {
                var autore = string.IsNullOrWhiteSpace(scartata.Operatore) ? "operatore non registrato" : scartata.Operatore;
                vincitrice.Changelog.Add(new ChangelogEntry
                {
                    Data = scartata.UltimaModifica,
                    Operatore = "(riparazione automatica)",
                    Campo = "(consolidamento collisione combinazione)",
                    ValorePrecedente = $"valore di {autore} del {scartata.UltimaModifica:dd/MM/yyyy} ({scartata.Esito})",
                    ValoreNuovo = "consolidato in questo valore (modifica piu' recente)",
                });
                valori.Remove(scartata);
                rimosse++;
            }
        }
        return rimosse;
    }

    /// <summary>Stesso schema di ValutazioneRequisitoEcuVeicoloService.ProvaAggiorna: carica, muta, salva verificando lo stato atteso.</summary>
    private static bool ProvaAggiorna(MasterDataRepository<StatoFunzionalitaEcuVeicolo> repo, Func<List<StatoFunzionalitaEcuVeicolo>, bool> mutazione,
        out string? errore)
    {
        var valori = repo.Load();
        var atteso = EntityStateFingerprint.Clone(valori);
        if (!mutazione(valori)) { errore = null; return false; }
        try
        {
            repo.Save(valori, atteso);
            errore = null;
            return true;
        }
        catch (Exception ex)
        {
            errore = ex.Message;
            return false;
        }
    }
}
