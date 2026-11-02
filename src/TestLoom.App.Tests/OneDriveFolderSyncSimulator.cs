using System.Security.Cryptography;

namespace TestLoom.App.Tests;

/// <summary>
/// Simulatore di sincronizzazione OneDrive/SharePoint fra due cartelle FISICHE distinte ("A" e "B"), usato dagli
/// scenari end-to-end in <see cref="MultiPcSyncScenarios"/>. Ogni "PC" (un processo TestLoom.App.MultiPcHost
/// reale) legge/scrive solo sulla PROPRIA cartella locale; questo simulatore gioca il ruolo del client OneDrive.
///
/// Modello (come il servizio reale): esiste una copia CLOUD autorevole; ogni PC ricorda, per ogni file, l'ultima
/// versione sincronizzata con il cloud. A ogni giro ogni PC:
///  1. CARICA le proprie modifiche locali. Se il cloud non è cambiato dall'ultima sincronizzazione del PC, la
///     versione locale diventa quella del cloud. Se invece il cloud è cambiato nel frattempo (un altro PC ha
///     modificato lo stesso file), c'è un conflitto: la versione locale viene rinominata in una copia di conflitto
///     (`nome-PC.ext`, caricata a sua volta) e il file canonico locale riprende la versione del cloud — nessun
///     contenuto viene mai perso e, dopo il conflitto, i due PC tornano ad avere lo STESSO file canonico.
///  2. SCARICA le modifiche del cloud (nuovi file, aggiornamenti, cancellazioni) che non ha ancora.
/// Condizioni realistiche sulle consegne in download: ritardo (in tick), ordine arbitrario, scritture "a metà"
/// (il file appare troncato fino al completamento e nel frattempo non viene mai ricaricato), file bloccato
/// temporaneamente (upload e download rimandati), cancellazioni ritardate.
///
/// La propagazione avviene per TICK ESPLICITI (<see cref="Propaga"/>), mai su un thread di sfondo con sleep
/// reali: timing deterministico e ripetibile.
/// </summary>
public sealed class OneDriveFolderSyncSimulator
{
    public string CartellaA { get; }
    public string CartellaB { get; }
    private readonly Random _random;

    /// <summary>Ritardo di default (in tick) applicato a un download che non ha un ritardo esplicito: 0 =
    /// consegnato nello stesso Propaga() in cui il cambiamento arriva sul cloud.</summary>
    public int RitardoDiDefaultTick { get; set; }

    /// <summary>Quando true, l'ordine di consegna dei download pronti in questo giro viene mescolato invece di
    /// seguire l'ordine di scoperta (simula "NC prima del Test e viceversa").</summary>
    public bool OrdineArbitrario { get; set; }

    private sealed class Dispositivo
    {
        public required string Cartella { get; init; }
        public required string Nome { get; init; }
        /// <summary>Hash dell'ultima versione sincronizzata con il cloud, per path relativo.</summary>
        public Dictionary<string, string> UltimoSincronizzato { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Path in scrittura "a metà" su questo PC: mai ricaricati finché non completati.</summary>
        public HashSet<string> InScritturaAMeta { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record Download(Dispositivo Destinazione, string PercorsoRelativo, byte[]? Contenuto, string? Hash,
        int TickConsegna, int? ScritturaAMetaBytes)
    {
        public bool Cancellazione => Contenuto == null;
    }

    private readonly Dispositivo _a;
    private readonly Dispositivo _b;
    private readonly Dictionary<string, byte[]> _cloud = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Download> _pendenti = new();
    private readonly Dictionary<string, int> _bloccatiFinoAlTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Dispositivo Pc, string Relativo, string Path, byte[] Resto)> _scrittureAMetaInSospeso = new();
    private int _tickCorrente;

    public OneDriveFolderSyncSimulator(string cartellaA, string cartellaB, int? seed = null)
    {
        CartellaA = cartellaA;
        CartellaB = cartellaB;
        Directory.CreateDirectory(cartellaA);
        Directory.CreateDirectory(cartellaB);
        _a = new Dispositivo { Cartella = cartellaA, Nome = new DirectoryInfo(cartellaA).Name };
        _b = new Dispositivo { Cartella = cartellaB, Nome = new DirectoryInfo(cartellaB).Name };
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    /// <summary>Blocca temporaneamente un percorso relativo (upload e download, su entrambi i PC) per
    /// <paramref name="tick"/> tick, come un file aperto da un altro processo/antivirus. Si sblocca da solo.</summary>
    public void BloccaTemporaneamente(string percorsoRelativo, int tick) =>
        _bloccatiFinoAlTick[percorsoRelativo] = _tickCorrente + tick;

    /// <summary>Un giro del client OneDrive su entrambi i PC: upload delle modifiche locali, poi consegna dei
    /// download maturi. Ogni chiamata avanza il tick simulato di 1.</summary>
    public void Propaga(int? ritardoTickPerNuoviCambiamenti = null, int? scritturaAMetaBytesPerNuoviCambiamenti = null)
    {
        _tickCorrente++;
        var ordine = OrdineArbitrario && _random.Next(2) == 0 ? new[] { _b, _a } : new[] { _a, _b };
        foreach (var pc in ordine) CaricaModificheLocali(pc);
        foreach (var pc in ordine) AccodaDownload(pc, ritardoTickPerNuoviCambiamenti ?? RitardoDiDefaultTick, scritturaAMetaBytesPerNuoviCambiamenti);
        ConsegnaMaturi();
    }

    /// <summary>Continua a chiamare Propaga() finché non ci sono più cambiamenti da caricare né download pendenti
    /// (o si supera <paramref name="maxTick"/>).</summary>
    public void PropagaFinoAConvergenza(int maxTick = 50)
    {
        for (var i = 0; i < maxTick; i++)
        {
            Propaga();
            if (_pendenti.Count == 0 && !HaModificheLocali(_a) && !HaModificheLocali(_b)) return;
        }
    }

    /// <summary>Completa tutte le scritture "a metà" ancora in sospeso (il file diventa il contenuto finale).</summary>
    public void CompletaScrittureAMeta()
    {
        foreach (var (pc, relativo, path, resto) in _scrittureAMetaInSospeso)
        {
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
                stream.Write(resto);
            pc.InScritturaAMeta.Remove(relativo);
        }
        _scrittureAMetaInSospeso.Clear();
    }

    private bool Bloccato(string relativo) =>
        _bloccatiFinoAlTick.TryGetValue(relativo, out var scade) && scade > _tickCorrente;

    private static string Hash(byte[] contenuto) => Convert.ToHexString(SHA256.HashData(contenuto));

    private static Dictionary<string, string> StatoLocale(Dispositivo pc) =>
        Directory.EnumerateFiles(pc.Cartella, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(pc.Cartella, f), f => Hash(File.ReadAllBytes(f)), StringComparer.OrdinalIgnoreCase);

    private bool HaModificheLocali(Dispositivo pc)
    {
        var locale = StatoLocale(pc);
        return locale.Any(kv => !pc.InScritturaAMeta.Contains(kv.Key) &&
                                (!pc.UltimoSincronizzato.TryGetValue(kv.Key, out var s) || s != kv.Value))
            || pc.UltimoSincronizzato.Keys.Any(k => !locale.ContainsKey(k));
    }

    private void CaricaModificheLocali(Dispositivo pc)
    {
        var locale = StatoLocale(pc);
        foreach (var (relativo, hashLocale) in locale)
        {
            if (pc.InScritturaAMeta.Contains(relativo) || Bloccato(relativo)) continue;
            pc.UltimoSincronizzato.TryGetValue(relativo, out var ultimoSync);
            if (ultimoSync == hashLocale) continue; // nessuna modifica locale

            var hashCloud = _cloud.TryGetValue(relativo, out var cloud) ? Hash(cloud) : null;
            var contenutoLocale = File.ReadAllBytes(Path.Combine(pc.Cartella, relativo));
            if (hashCloud == ultimoSync || hashCloud == hashLocale)
            {
                // Il cloud è fermo all'ultima versione nota a questo PC (o ha già lo stesso contenuto): upload.
                _cloud[relativo] = contenutoLocale;
                pc.UltimoSincronizzato[relativo] = hashLocale;
                continue;
            }

            // Conflitto: un altro PC ha cambiato lo stesso file nel frattempo. La versione locale diventa una
            // copia di conflitto (caricata a sua volta), il canonico locale riprende subito la versione del cloud
            // (o sparisce se sul cloud è stato cancellato).
            var percorsoLocale = Path.Combine(pc.Cartella, relativo);
            var relativoCopia = Path.Combine(Path.GetDirectoryName(relativo) ?? "",
                $"{Path.GetFileNameWithoutExtension(relativo)}-{pc.Nome}{Path.GetExtension(relativo)}");
            var percorsoCopia = Path.Combine(pc.Cartella, relativoCopia);
            File.WriteAllBytes(percorsoCopia, contenutoLocale);
            _cloud[relativoCopia] = contenutoLocale;
            pc.UltimoSincronizzato[relativoCopia] = hashLocale;
            if (cloud != null)
            {
                File.WriteAllBytes(percorsoLocale, cloud);
                pc.UltimoSincronizzato[relativo] = hashCloud!;
            }
            else
            {
                File.Delete(percorsoLocale);
                pc.UltimoSincronizzato.Remove(relativo);
            }
        }

        // Cancellazioni locali: file sincronizzato che non esiste più su questo PC.
        foreach (var relativo in pc.UltimoSincronizzato.Keys.Where(k => !locale.ContainsKey(k)).ToList())
        {
            if (Bloccato(relativo)) continue;
            var ultimoSync = pc.UltimoSincronizzato[relativo];
            pc.UltimoSincronizzato.Remove(relativo);
            if (_cloud.TryGetValue(relativo, out var cloud) && Hash(cloud) == ultimoSync)
                _cloud.Remove(relativo);
            // Altrimenti il cloud è cambiato nel frattempo: vince la modifica, il file tornerà al prossimo download.
        }
    }

    private void AccodaDownload(Dispositivo pc, int ritardo, int? scritturaAMeta)
    {
        foreach (var (relativo, contenuto) in _cloud)
        {
            var hashCloud = Hash(contenuto);
            if (pc.UltimoSincronizzato.TryGetValue(relativo, out var s) && s == hashCloud) continue;
            if (_pendenti.Any(p => p.Destinazione == pc && p.PercorsoRelativo.Equals(relativo, StringComparison.OrdinalIgnoreCase) && p.Hash == hashCloud))
                continue;
            _pendenti.RemoveAll(p => p.Destinazione == pc && p.PercorsoRelativo.Equals(relativo, StringComparison.OrdinalIgnoreCase));
            _pendenti.Add(new Download(pc, relativo, contenuto, hashCloud, _tickCorrente + ritardo, scritturaAMeta));
        }

        foreach (var relativo in pc.UltimoSincronizzato.Keys.Where(k => !_cloud.ContainsKey(k)).ToList())
        {
            if (_pendenti.Any(p => p.Destinazione == pc && p.PercorsoRelativo.Equals(relativo, StringComparison.OrdinalIgnoreCase) && p.Cancellazione))
                continue;
            _pendenti.RemoveAll(p => p.Destinazione == pc && p.PercorsoRelativo.Equals(relativo, StringComparison.OrdinalIgnoreCase));
            _pendenti.Add(new Download(pc, relativo, null, null, _tickCorrente + ritardo, null));
        }
    }

    private void ConsegnaMaturi()
    {
        var maturi = _pendenti.Where(p => p.TickConsegna <= _tickCorrente).ToList();
        if (OrdineArbitrario) maturi = maturi.OrderBy(_ => _random.Next()).ToList();

        foreach (var d in maturi)
        {
            var pc = d.Destinazione;
            // Bloccato o ancora in scrittura a metà: resta in coda (mai perso) fino allo sblocco/completamento.
            if (Bloccato(d.PercorsoRelativo) || pc.InScritturaAMeta.Contains(d.PercorsoRelativo)) continue;
            _pendenti.Remove(d);

            var percorso = Path.Combine(pc.Cartella, d.PercorsoRelativo);
            pc.UltimoSincronizzato.TryGetValue(d.PercorsoRelativo, out var ultimoSync);
            var hashLocale = File.Exists(percorso) ? Hash(File.ReadAllBytes(percorso)) : null;
            // Il PC ha modificato il file dopo l'ultima sincronizzazione: il download non lo sovrascrive, sarà
            // l'upload del prossimo giro a rilevare il conflitto (mai una sovrascrittura silenziosa).
            if (hashLocale != ultimoSync) continue;

            if (d.Cancellazione)
            {
                try { if (File.Exists(percorso)) File.Delete(percorso); } catch { continue; }
                pc.UltimoSincronizzato.Remove(d.PercorsoRelativo);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(percorso)!);
            if (d.ScritturaAMetaBytes.HasValue && d.ScritturaAMetaBytes.Value < d.Contenuto!.Length)
            {
                File.WriteAllBytes(percorso, d.Contenuto[..d.ScritturaAMetaBytes.Value]);
                pc.InScritturaAMeta.Add(d.PercorsoRelativo);
                _scrittureAMetaInSospeso.Add((pc, d.PercorsoRelativo, percorso, d.Contenuto[d.ScritturaAMetaBytes.Value..]));
            }
            else
            {
                File.WriteAllBytes(percorso, d.Contenuto!);
            }
            pc.UltimoSincronizzato[d.PercorsoRelativo] = d.Hash!;
        }
    }
}
