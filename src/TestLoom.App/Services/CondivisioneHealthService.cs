using TestLoom.App.Models;

namespace TestLoom.App.Services;

public static class CondivisioneHealthService
{
    /// <summary>Verifiche della radice condivisa. Ogni accesso alla radice (esistenza, attributi, prova di scrittura) ha un
    /// tetto di tempo dalla modalità (<see cref="TimeoutCondivisione"/>, 6.6.1): una cartella di rete in stallo non blocca
    /// il chiamante. Scaduto il tetto la cartella vale non disponibile e nulla viene creato. Il controllo periodico non
    /// crea né cancella probe di scrittura sulla radice; la prova di scrittura completa avviene solo al recupero.</summary>

    /// <summary>
    /// Preflight di startup rigorosamente non mutante. Serve PRIMA del gate versione: un client vecchio che
    /// entra per errore in un workspace futuro deve poter capire che la cartella esiste senza creare probe,
    /// directory o file di default prima di aver letto il manifest di compatibilità.
    /// </summary>
    public static EsitoStatoCondivisione VerificaDisponibilitaSenzaScrivere(string? root)
    {
        var now = DateTime.Now;
        if (string.IsNullOrWhiteSpace(root))
            return new() { Stato = StatoCondivisione.NonDisponibile, Messaggio = "Cartella condivisa non disponibile.", VerificatoIl = now };

        // 6.6.1 (correzione 1): la sonda della radice ha un tetto di tempo. Su cartella di rete in stallo il chiamante non
        // resta bloccato: scaduto il tetto la cartella vale non disponibile, e nulla viene creato o seminato.
        var esito = RadiceCondivisaSonda.Verifica(root);
        if (esito == RadiceCondivisaSonda.Esito.Assente)
            return new() { Stato = StatoCondivisione.NonDisponibile, Messaggio = "Cartella condivisa non disponibile.", VerificatoIl = now };
        if (esito == RadiceCondivisaSonda.Esito.Lenta)
            return new() { Stato = StatoCondivisione.NonDisponibile,
                Messaggio = $"Cartella condivisa non risponde entro {TimeoutCondivisione.Per(root).TotalSeconds:0} secondi: " +
                    "nessuna apertura né creazione di strutture finché non risponde.",
                VerificatoIl = now };

        // Anche una replica locale scrivibile puo avere la sincronizzazione cloud sospesa.
        // Il blocco esplicito dell'utente prevale sul successo del probe del filesystem.
        if (WorkspaceReadOnlyService.IsEnabled(root))
            return new() { Stato = StatoCondivisione.SolaLettura,
                Messaggio = "Modalità visualizzatore attivata su questo PC. Nessuna nuova modifica condivisa.",
                VerificatoIl = now };

        if (esito == RadiceCondivisaSonda.Esito.Inaccessibile)
            return new() { Stato = StatoCondivisione.SolaLettura, Messaggio = "Cartella non verificabile: attributi non leggibili.", VerificatoIl = now };
        if (esito == RadiceCondivisaSonda.Esito.Offline)
            return new() { Stato = StatoCondivisione.SolaLettura, Messaggio = "Cartella disponibile solo offline: modifiche sospese.", VerificatoIl = now };
        return new() { Stato = StatoCondivisione.Operativa, Messaggio = "Cartella condivisa disponibile; scrittura non ancora verificata.", VerificatoIl = now };
    }

    /// <summary>
    /// Prova di scrittura nelle sottocartelle che TestLoom scrive davvero (6.6.1, correzione C). Una radice che risponde non basta
    /// per riaprire la scrittura: una sottocartella (testcases, nonconformita, change-requests, segnalazioni, allegati) può
    /// essere negata. Ogni probe ha il tetto di tempo della modalità; una sottocartella non ancora creata non viene provata.
    /// </summary>
    public static bool ProvaSottocartelleScrittura(DataPaths paths) => SottocartelleNonScrivibili(paths).Count == 0;

    /// <summary>Verifica della radice usata dal monitor di scrittura in background (seam di test: sostituibile con un esito fisso o bloccante).</summary>
    internal static Func<string?, EsitoStatoCondivisione> Sonda { get; set; } = Verifica;

    /// <summary>Probe di scrittura di una sottocartella (seam di test: sostituibile con una che rifiuta sempre).</summary>
    internal static Func<string, bool> ProbeSottocartella { get; set; } =
        dir => IoTimeoutGuard.Esegui(() => ScriviEEliminaProbe(dir), TimeoutCondivisione.Per(dir), false);

    /// <summary>
    /// Sottocartelle di TestLoom che rifiutano la prova di scrittura. Ogni percorso è valutato da solo (6.6.1, correzione 4): una
    /// sottocartella che rifiuta non tiene la radice in sola lettura; il chiamante lo registra come avviso per quel percorso.
    /// </summary>
    public static IReadOnlyList<string> SottocartelleNonScrivibili(DataPaths paths)
    {
        var rifiutate = new List<string>();
        foreach (var dir in new[] { paths.TestCasesDir, paths.NonConformitaDir, paths.ChangeRequestDir, paths.SegnalazioniDir, paths.AllegatiRootDir })
        {
            var esito = RadiceCondivisaSonda.Verifica(dir);
            if (esito == RadiceCondivisaSonda.Esito.Assente) continue;
            if (esito is RadiceCondivisaSonda.Esito.Lenta or RadiceCondivisaSonda.Esito.Inaccessibile || !ProbeSottocartella(dir))
                rifiutate.Add(dir);
        }
        return rifiutate;
    }

    private static bool ScriviEEliminaProbe(string dir)
    {
        var probe = Path.Combine(dir, ".testloom-write-probe-" + Guid.NewGuid().ToString("N")[..8]);
        using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Flush(flushToDisk: true);
        File.Delete(probe);
        return true;
    }

    public static EsitoStatoCondivisione Verifica(string? root)
    {
        var now = DateTime.Now;
        if (string.IsNullOrWhiteSpace(root))
            return new() { Stato = StatoCondivisione.NonDisponibile, Messaggio = "Cartella condivisa non disponibile.", VerificatoIl = now };

        // 6.6.1 (correzione 1): esistenza con tetto di tempo, come nel preflight. Su scadenza nessuna prova di scrittura parte.
        var esitoRadice = RadiceCondivisaSonda.Verifica(root);
        if (esitoRadice == RadiceCondivisaSonda.Esito.Assente)
            return new() { Stato = StatoCondivisione.NonDisponibile, Messaggio = "Cartella condivisa non disponibile.", VerificatoIl = now };
        if (esitoRadice == RadiceCondivisaSonda.Esito.Lenta)
            return new() { Stato = StatoCondivisione.SolaLettura, Messaggio = "Cartella condivisa lenta a rispondere: nuovo tentativo al prossimo giro.", VerificatoIl = now, Incerto = true };

        if (WorkspaceReadOnlyService.IsEnabled(root))
            return new() { Stato = StatoCondivisione.SolaLettura,
                Messaggio = "Modalità visualizzatore attivata su questo PC. Nessuna nuova modifica condivisa.",
                VerificatoIl = now };

        var fallbackLento = new EsitoStatoCondivisione { Stato = StatoCondivisione.SolaLettura, Messaggio = "Cartella condivisa lenta a rispondere: nuovo tentativo al prossimo giro.", VerificatoIl = now, Incerto = true };
        try
        {
            return IoTimeoutGuard.Esegui(() => VerificaInterna(root, now), TimeoutCondivisione.Per(root), fallbackLento)!;
        }
        catch (Exception ex)
        {
            return new() { Stato = StatoCondivisione.SolaLettura, Messaggio = $"Scrittura non disponibile: {ex.Message}", VerificatoIl = now };
        }
    }

    private static EsitoStatoCondivisione VerificaInterna(string root, DateTime now)
    {
        try
        {
            var attrs = File.GetAttributes(root);
            if (attrs.HasFlag(FileAttributes.Offline))
                return new() { Stato = StatoCondivisione.SolaLettura, Messaggio = "Cartella disponibile solo offline: modifiche sospese.", VerificatoIl = now };

            // Niente controllo generico "c'è una scheda di rete attiva": un client OneDrive/SharePoint
            // sincronizzato scrive benissimo su un file locale anche a Wi-Fi spento (la sincronizzazione
            // avviene dopo, in background) e la riconciliazione dei conflitti esiste già altrove. Un
            // controllo sulla sola connettività, non sulla cartella specifica, bloccava in sola lettura
            // anche una cartella locale perfettamente valida — inclusa la primissima configurazione su un
            // PC nuovo eventualmente isolato — senza che nulla fosse davvero irraggiungibile. La prova di
            // scrittura reale qui sotto è l'unico segnale che conta davvero.
            var probe = Path.Combine(root, ".testloom-write-probe");
            using (var stream = new FileStream(probe, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                stream.Flush(true);
            File.Delete(probe);
            return new() { Stato = StatoCondivisione.Operativa, Messaggio = "Cartella condivisa raggiungibile e scrivibile.", VerificatoIl = now };
        }
        catch (Exception ex)
        {
            return new() { Stato = StatoCondivisione.SolaLettura, Messaggio = $"Scrittura non disponibile: {ex.Message}", VerificatoIl = now };
        }
    }
}
