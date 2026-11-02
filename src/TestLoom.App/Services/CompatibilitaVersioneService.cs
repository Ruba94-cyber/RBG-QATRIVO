using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

public sealed class VersionManifest
{
    public int ManifestSchema { get; set; } = 1;
    public string VersioneCorrente { get; set; } = "6.1.0";
    public string VersioneMinima { get; set; } = CompatibilitaVersioneService.JournalSafetyMinimumVersion;
    public int SchemaDatiMassimo { get; set; } = TestCase.CurrentSchemaVersion;
    public string PacchettoRelativo { get; set; } = "updates/TestLoom-latest.zip";
    public DateTime PubblicatoIl { get; set; } = DateTime.Now;
}

public static class CompatibilitaVersioneService
{
    public static void EnsureTestUidSchema(DataPaths paths)
    {
        var (nodo, schema) = LeggiSchemaDatiManifestSicuro(paths.VersionManifestFile);
        if (nodo == null || schema < 10 || schema > TestCase.CurrentSchemaVersion)
            throw new IOException("Collegamenti TestUid bloccati: il manifest condiviso deve confermare lo schema dati 10 prima della scrittura.");
    }
    /// <summary>
    /// Prima versione che comprende, oltre alle revision/hash guard e ai journal bounded, il cutover dei master
    /// sulla source of truth distribuita Phase D. Un client 6.0 non conosce ready.json/journal master-v2 e dopo
    /// il cutover potrebbe continuare a scrivere i monoliti legacy ormai non autorevoli: deve quindi diventare
    /// sola lettura appena riceve il manifest condiviso 6.1.
    /// </summary>
    public const string JournalSafetyMinimumVersion = "6.1.0";

    /// <summary>
    /// Gate di startup rigorosamente non mutante. Se il manifest non esiste non lo crea: significa soltanto
    /// che il workspace può procedere alla normale prova di scrittura/inizializzazione. Se invece il manifest
    /// esiste ed è futuro/incompatibile, il client entra in sola lettura PRIMA di creare directory/default.
    /// </summary>
    public static EsitoStatoCondivisione VerificaSenzaScrivere(DataPaths paths, EsitoStatoCondivisione stato)
        => ConTettoDiTempo(paths, stato, () => Valuta(paths, stato, clientVersion: null, consentiScrittureManifest: false));

    /// <summary>Override del tetto di tempo della sonda di sola lettura, solo per i test (null = tetto della modalità).</summary>
    internal static TimeSpan? TimeoutSondaOverride { get; set; }

    /// <summary>Gancio dei test: eseguito dentro la sonda prima della lettura, per simulare una cartella di rete lenta.</summary>
    internal static Action? PausaSondaPerTest { get; set; }

    /// <summary>
    /// Single-flight del gate: manifest, alzamento schema e bootstrap Phase D si eseguono una alla volta nel processo,
    /// mai due scritture concorrenti sullo stesso workspace.
    /// </summary>
    private static readonly object GateApplica = new();

    /// <summary>Manifest con alzamento dello schema non riuscito, in attesa del nuovo tentativo della sessione.</summary>
    private static readonly HashSet<string> AlzamentoSchemaSospeso = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tentativi di scrittura del manifest prima di dichiarare fallito l'alzamento (come il marcatore catalogo).</summary>
    private const int TentativiScritturaManifest = 3;

    public static EsitoStatoCondivisione Applica(DataPaths paths, EsitoStatoCondivisione stato)
        => Applica(paths, stato, null);

    /// <summary>
    /// Su rete prima una sonda di sola lettura con tetto di tempo: se la cartella non risponde il workspace resta in sola
    /// lettura. La sonda non scrive mai, quindi un worker abbandonato dal tetto non può scrivere in ritardo (P2.11).
    /// Le scritture avvengono poi in primo piano, senza tetto di tempo: un errore resta esplicito nel ramo che lo produce.
    /// </summary>
    internal static EsitoStatoCondivisione Applica(
        DataPaths paths,
        EsitoStatoCondivisione stato,
        Version? clientVersion)
    {
        lock (GateApplica)
        {
            if (stato.Stato != StatoCondivisione.Operativa) return stato;
            if (ModalitaCondivisioneService.ERete(paths.Root))
            {
                var sonda = ConTettoDiTempo(paths, stato, () => Valuta(paths, stato, clientVersion, consentiScrittureManifest: false));
                if (sonda.Stato != StatoCondivisione.Operativa) return sonda;
            }
            return Valuta(paths, stato, clientVersion, consentiScrittureManifest: true);
        }
    }

    /// <summary>
    /// Il manifest di compatibilità vive sulla cartella condivisa: la sua lettura passa dal tetto di tempo della modalità
    /// (6.6.1, correzione D). <paramref name="operazione"/> deve essere di sola lettura: su scadenza il workspace resta in
    /// sola lettura e il worker abbandonato non può scrivere nulla.
    /// </summary>
    private static EsitoStatoCondivisione ConTettoDiTempo(DataPaths paths, EsitoStatoCondivisione stato, Func<EsitoStatoCondivisione> operazione)
    {
        if (stato.Stato != StatoCondivisione.Operativa) return stato;
        // Decisione B: per OneDrive/locale nessun thread di appoggio e nessuna scadenza, come prima della 6.6.1.
        if (!ModalitaCondivisioneService.ERete(paths.Root)) return operazione();
        var fallback = new EsitoStatoCondivisione
        {
            Stato = StatoCondivisione.SolaLettura,
            Messaggio = "Manifest di compatibilità non verificabile entro il tempo previsto: sola lettura finché la cartella non risponde.",
            VerificatoIl = DateTime.Now,
        };
        return IoTimeoutGuard.Esegui(() => { PausaSondaPerTest?.Invoke(); return operazione(); },
            TimeoutSondaOverride ?? TimeoutCondivisione.Per(paths.Root), fallback) ?? fallback;
    }

    private static EsitoStatoCondivisione Valuta(
        DataPaths paths,
        EsitoStatoCondivisione stato,
        Version? clientVersion,
        bool consentiScrittureManifest)
    {
        if (stato.Stato != StatoCondivisione.Operativa) return stato;
        VersionManifest? manifest;
        if (!File.Exists(paths.VersionManifestFile))
        {
            if (!consentiScrittureManifest) return stato;
            manifest = new VersionManifest();
            try { JsonIo.WriteAtomic(paths.VersionManifestFile, manifest); }
            catch (Exception ex)
            {
                CrashLogService.Registra("Creazione manifest versione", ex);
                return new EsitoStatoCondivisione
                {
                    Stato = StatoCondivisione.SolaLettura,
                    Messaggio = "Impossibile creare il gate di compatibilità della cartella condivisa.",
                    VerificatoIl = DateTime.Now
                };
            }
        }
        else manifest = JsonIo.TryRead<VersionManifest>(paths.VersionManifestFile, out _);
        if (manifest == null) return new EsitoStatoCondivisione { Stato = StatoCondivisione.SolaLettura, Messaggio = "Manifest versione non leggibile.", VerificatoIl = DateTime.Now };

        if (!Version.TryParse(manifest.VersioneMinima, out var minimaManifest) ||
            !Version.TryParse(JournalSafetyMinimumVersion, out var minimaSicura))
        {
            return new EsitoStatoCondivisione { Stato = StatoCondivisione.SolaLettura, Messaggio = "Versione minima nel manifest non valida.", VerificatoIl = DateTime.Now };
        }

        // La compatibilità FUTURA va controllata prima di qualunque migrazione del manifest. In particolare,
        // nessun client che non capisce lo schema dichiarato deve scrivere anche solo il gate stesso.
        var corrente = clientVersion ?? Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        if (corrente < minimaManifest)
            return new EsitoStatoCondivisione { Stato = StatoCondivisione.SolaLettura, Messaggio = $"Aggiornamento richiesto: versione minima {minimaManifest}.", VerificatoIl = DateTime.Now };
        if (manifest.SchemaDatiMassimo > TestCase.CurrentSchemaVersion)
            return new EsitoStatoCondivisione { Stato = StatoCondivisione.SolaLettura, Messaggio = "Schema dati più recente dell'app: aggiorna TestLoom prima di modificare.", VerificatoIl = DateTime.Now };

        // Migrazione non distruttiva del solo gate di compatibilità. Il preflight non mutante non esegue mai
        // questo ramo: viene effettuato soltanto dopo che il workspace è già stato dichiarato scrivibile.
        if (minimaManifest < minimaSicura)
        {
            if (!consentiScrittureManifest)
                return stato;

            manifest.VersioneMinima = JournalSafetyMinimumVersion;
            manifest.VersioneCorrente = CurrentProductVersion();
            manifest.SchemaDatiMassimo = Math.Max(manifest.SchemaDatiMassimo, TestCase.CurrentSchemaVersion);
            manifest.PubblicatoIl = DateTime.Now;
            try { JsonIo.WriteAtomic(paths.VersionManifestFile, manifest); }
            catch (Exception ex)
            {
                CrashLogService.Registra("Aggiornamento manifest versione minima", ex);
                return new EsitoStatoCondivisione
                {
                    Stato = StatoCondivisione.SolaLettura,
                    Messaggio = "Impossibile aggiornare il gate di compatibilità della cartella condivisa.",
                    VerificatoIl = DateTime.Now
                };
            }
        }

        // 6.5.1: il primo client >= 6.5.1 che trova un manifest con schemaDatiMassimo < TestCase.CurrentSchemaVersion
        // lo alza. Il workspace e' qui gia' dichiarato Operativo/scrivibile (Valuta esce prima altrimenti) e il
        // ramo e' raggiungibile solo da Applica (mai dal preflight). Best effort: un fallimento NON blocca l'app.
        if (consentiScrittureManifest)
            AllineaSchemaDatiManifest(paths, corrente);

        // Phase D viene attivata nello stesso punto in cui abbiamo già dimostrato che il workspace è
        // compatibile e realmente scrivibile. Il preflight read-only non deve mai creare manifest/journal.
        // ready.json è il confine di autorità: fino alla sua pubblicazione i monoliti legacy restano la source
        // of truth; dopo la pubblicazione tutti i MasterDataRepository passano automaticamente al journal
        // per-device. Un bootstrap parziale/ambiguo non può quindi degradare silenziosamente a un dual-write:
        // il workspace viene aperto in sola lettura e l'operatore conserva intatti sia legacy sia staging.
        if (consentiScrittureManifest)
        {
            try
            {
                PhaseDMasterBootstrapService.EnsureReady(paths);
            }
            catch (Exception ex)
            {
                CrashLogService.Registra("Bootstrap master Phase D", ex);
                return new EsitoStatoCondivisione
                {
                    Stato = StatoCondivisione.SolaLettura,
                    Messaggio = $"Migrazione storage master Phase D non completata in sicurezza: {ex.Message}",
                    VerificatoIl = DateTime.Now
                };
            }
        }

        return stato;
    }

    /// <summary>Prima versione client che alza lo schemaDatiMassimo del manifest condiviso.</summary>
    public const string SchemaRaiseMinimumClientVersion = "6.5.1";

    internal enum EsitoAllineamentoSchema
    {
        ClientTroppoVecchio, GiaAggiornato, Alzato, ManifestAssente, ManifestNonLeggibile, ScritturaFallita
    }

    /// <summary>
    /// Alza SOLO <c>schemaDatiMassimo</c> a <see cref="TestCase.CurrentSchemaVersion"/> quando il manifest condiviso e'
    /// piu' basso e il client e' &gt;= 6.5.1. Monotono (mai al ribasso: il valore viene riletto dal disco subito prima
    /// di scrivere e si scrive solo se ancora inferiore), idempotente, scrittura atomica tmp+replace via JsonIo.
    /// Modifica il documento JSON in place, quindi campi sconosciuti e gli altri campi (versioneCorrente,
    /// pacchettoRelativo, pubblicatoIl: gestiti dal packaging di release) restano intatti. Due PC simultanei
    /// scrivono lo stesso valore: l'ultimo replace e' equivalente. Dopo la scrittura rilegge e riprova (max 3) se un
    /// writer concorrente piu' vecchio ha riportato il valore sotto il target. Non solleva mai eccezioni.
    /// </summary>
    /// <summary>
    /// Ritenta nel corso della sessione l'alzamento dello schema rimasto in sospeso, come
    /// <c>CatalogMigrationService.RiprovaMarcatore</c>: senza nulla in sospeso non fa nulla. Non legge né riscrive altro.
    /// </summary>
    public static bool RiprovaAlzamentoSchema(DataPaths paths)
    {
        lock (AlzamentoSchemaSospeso)
        {
            if (!AlzamentoSchemaSospeso.Contains(paths.VersionManifestFile)) return true;
        }
        lock (GateApplica)
        {
            var esito = AllineaSchemaDatiManifest(paths, Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));
            return esito is EsitoAllineamentoSchema.Alzato or EsitoAllineamentoSchema.GiaAggiornato;
        }
    }

    /// <summary>Vero finché l'alzamento dello schema di questo manifest non è riuscito (diagnostica e test).</summary>
    internal static bool AlzamentoSchemaInSospeso(DataPaths paths)
    {
        lock (AlzamentoSchemaSospeso) return AlzamentoSchemaSospeso.Contains(paths.VersionManifestFile);
    }

    /// <summary>
    /// Scrittura atomica del manifest con pochi tentativi e attesa crescente, come il marcatore del catalogo: l'ultimo
    /// errore torna al chiamante, che lo registra.
    /// </summary>
    private static void ScriviManifestConRiprova(string path, byte[] bytes)
    {
        for (var tentativo = 1; ; tentativo++)
        {
            try
            {
                JsonIo.WriteBytesAtomic(path, bytes);
                return;
            }
            catch (Exception ex) when (tentativo < TentativiScritturaManifest)
            {
                DiagnosticTraceService.Registra("Compatibilita.AlzamentoSchema",
                    $"tentativo {tentativo} di scrittura del manifest fallito ({ex.GetType().Name}): nuovo tentativo.");
                Thread.Sleep(TimeSpan.FromMilliseconds(150 * tentativo));
            }
        }
    }

    /// <summary>
    /// Esito visibile dell'alzamento (P2.12): un fallimento va in diagnostica e resta in sospeso per il nuovo tentativo
    /// (<see cref="RiprovaAlzamentoSchema"/>). Il workspace resta operativo: il contratto del 6.5.1 è non bloccante.
    /// </summary>
    internal static EsitoAllineamentoSchema AllineaSchemaDatiManifest(DataPaths paths, Version client)
    {
        var esito = AllineaSchemaDatiManifestCore(paths, client);
        lock (AlzamentoSchemaSospeso)
        {
            if (esito == EsitoAllineamentoSchema.ScritturaFallita)
            {
                if (AlzamentoSchemaSospeso.Add(paths.VersionManifestFile))
                    DiagnosticTraceService.Registra("Compatibilita.AlzamentoSchema",
                        "Schema dati del manifest condiviso non alzato: nuovo tentativo nel corso della sessione e all'avvio.");
            }
            else if (esito is EsitoAllineamentoSchema.Alzato or EsitoAllineamentoSchema.GiaAggiornato)
            {
                AlzamentoSchemaSospeso.Remove(paths.VersionManifestFile);
            }
        }
        return esito;
    }

    private static EsitoAllineamentoSchema AllineaSchemaDatiManifestCore(DataPaths paths, Version client)
    {
        var target = TestCase.CurrentSchemaVersion;
        try
        {
            var minimoRaise = Version.Parse(SchemaRaiseMinimumClientVersion);
            var normalizzato = new Version(client.Major, client.Minor, Math.Max(0, client.Build));
            if (normalizzato < minimoRaise) return EsitoAllineamentoSchema.ClientTroppoVecchio;

            for (var tentativo = 0; tentativo < 3; tentativo++)
            {
                if (!File.Exists(paths.VersionManifestFile)) return EsitoAllineamentoSchema.ManifestAssente;
                var (nodo, valore) = LeggiSchemaDatiMassimo(paths.VersionManifestFile);
                if (nodo == null)
                {
                    CrashLogService.Registra("Allineamento schema manifest versione",
                        new InvalidDataException("Manifest versione non leggibile: schemaDatiMassimo non alzato."));
                    return EsitoAllineamentoSchema.ManifestNonLeggibile;
                }
                if (valore >= target) return tentativo == 0 ? EsitoAllineamentoSchema.GiaAggiornato : EsitoAllineamentoSchema.Alzato;

                // Il lettore dell'app (JsonIo.Options) e' case-sensitive e legge/scrive PascalCase: si aggiornano
                // tutte le chiavi equivalenti sotto il target e, se manca, si aggiunge quella che l'app legge.
                var chiavi = nodo.Select(kv => kv.Key)
                    .Where(k => string.Equals(k, "schemaDatiMassimo", StringComparison.OrdinalIgnoreCase)).ToList();
                if (chiavi.Count == 0) chiavi.Add("SchemaDatiMassimo");
                foreach (var chiave in chiavi)
                    if (!(nodo[chiave] is JsonValue v && v.TryGetValue<int>(out var attuale) && attuale >= target))
                        nodo[chiave] = target;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(nodo, new JsonSerializerOptions { WriteIndented = true });
                ScriviManifestConRiprova(paths.VersionManifestFile, bytes);
            }
            var (_, finale) = LeggiSchemaDatiManifestSicuro(paths.VersionManifestFile);
            return finale >= target ? EsitoAllineamentoSchema.Alzato : EsitoAllineamentoSchema.ScritturaFallita;
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Allineamento schema manifest versione (non bloccante)", ex);
            return EsitoAllineamentoSchema.ScritturaFallita;
        }
    }

    private static (JsonObject? Nodo, int Valore) LeggiSchemaDatiManifestSicuro(string path)
    {
        try { return LeggiSchemaDatiMassimo(path); } catch { return (null, 0); }
    }

    private static (JsonObject? Nodo, int Valore) LeggiSchemaDatiMassimo(string path)
    {
        var bytes = JsonIo.ReadAllBytesShared(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
        if (JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not JsonObject obj)
            return (null, 0);
        // Valore effettivo = il minimo tra le chiavi equivalenti (0 se assente o non numerico): basta una chiave
        // sotto il target per giustificare la scrittura, e nessuna scrittura puo' mai abbassare un valore.
        int? minimo = null;
        foreach (var kv in obj)
        {
            if (!string.Equals(kv.Key, "schemaDatiMassimo", StringComparison.OrdinalIgnoreCase)) continue;
            var n = kv.Value is JsonValue v && v.TryGetValue<int>(out var x) ? x : 0;
            minimo = minimo is null ? n : Math.Min(minimo.Value, n);
        }
        return (obj, minimo ?? 0);
    }

    private static string CurrentProductVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(6, 1, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}
