using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TestLoom.App.Services;

/// <summary>
/// Implementazione di produzione di <see cref="IOneDriveAccesso"/> per Windows, senza permessi di amministratore e senza
/// dipendere da API private del client OneDrive. Usa i meccanismi pubblici di Cloud Files (cldapi / attributi NTFS):
/// <list type="bullet">
/// <item>file "solo online": FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (0x400000), RECALL_ON_OPEN (0x40000) oppure OFFLINE (0x1000);</item>
/// <item>segnaposto Cloud Files non in sync (modifica locale non ancora caricata): <c>CfGetPlaceholderStateFromAttributeTag</c>
///   su attributi + tag di reparse restituiti da FindFirstFileEx (nessun I/O per file oltre l'enumerazione);</item>
/// <item>file NUOVO non ancora preso in carico da OneDrive: file regolare, non segnaposto, in una radice che usa Files On-Demand
///   (almeno un segnaposto visto) e modificato di recente o già noto come pendente dalla chiusura precedente;</item>
/// <item>richiamo (hydrate): lettura completa del file, che fa scaricare il contenuto al provider Cloud Files;</item>
/// <item>client: processo "OneDrive" dell'utente; avvio con OneDrive.exe /background dal percorso di installazione per-utente.</item>
/// </list>
/// Se cldapi.dll non è disponibile si ricade sui soli attributi (segnaposto = allineato). Ogni errore di enumerazione è non fatale.
/// </summary>
public sealed class OneDriveAccessoWindows : IOneDriveAccesso
{
    private const uint AttrOffline = 0x1000;
    private const uint AttrReparsePoint = 0x400;
    private const uint AttrDirectory = 0x10;
    private const uint AttrRecallOnOpen = 0x40000;
    private const uint AttrPinned = 0x80000;
    private const uint AttrUnpinned = 0x100000;
    private const uint AttrRecallOnDataAccess = 0x400000;

    private const uint ReparseSymlink = 0xA000000C;
    private const uint ReparseMountPoint = 0xA0000003;
    private const uint ReparseCloudBase = 0x9000001A;
    private const uint ReparseCloudMask = 0xFFFF0FFF;

    private const uint CfPlaceholder = 0x1;
    private const uint CfInSync = 0x8;

    /// <summary>Un file regolare modificato da meno di questo tempo, in una radice Files On-Demand, è considerato non ancora caricato.</summary>
    private static readonly TimeSpan FinestraNuoviFile = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Un file noto come pendente dalla chiusura precedente ma NON segnaposto conta ancora come "da caricare" solo se scritto da
    /// meno di questo tempo: oltre, OneDrive ha avuto giorni per prenderlo in carico e lo stato non e' piu' dimostrabile
    /// (falso positivo permanente). I segnaposto restano giudicati dal flag di sincronizzazione, senza limite di eta'.
    /// </summary>
    internal static readonly TimeSpan FinestraSospetti = TimeSpan.FromHours(48);

    private static readonly string[] VariabiliRadici = { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" };

    public bool EDentroOneDrive(string percorso)
    {
        if (string.IsNullOrWhiteSpace(percorso)) return false;
        string completo;
        try { completo = Path.GetFullPath(percorso).TrimEnd('\\', '/'); }
        catch (Exception) { return false; }

        foreach (var nome in VariabiliRadici)
        {
            var radice = Environment.GetEnvironmentVariable(nome)?.TrimEnd('\\', '/');
            if (string.IsNullOrWhiteSpace(radice)) continue;
            if (completo.Equals(radice, StringComparison.OrdinalIgnoreCase) ||
                completo.StartsWith(radice + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Librerie SharePoint/OneDrive for Business sincronizzate fuori da %OneDrive%: un antenato con attributi Cloud Files.
        try
        {
            var dir = new DirectoryInfo(completo);
            for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                if (!dir.Exists) continue;
                if (((uint)dir.Attributes & (AttrPinned | AttrUnpinned | AttrRecallOnDataAccess | AttrRecallOnOpen)) != 0) return true;
            }
        }
        catch (Exception) { }
        return false;
    }

    public bool ClientInEsecuzione()
    {
        try
        {
            var processi = Process.GetProcessesByName("OneDrive");
            try { return processi.Length > 0; }
            finally { foreach (var p in processi) p.Dispose(); }
        }
        catch (Exception) { return false; }
    }

    public bool AvviaClient()
    {
        try
        {
            var candidati = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive", "OneDrive.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive", "OneDrive.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft OneDrive", "OneDrive.exe"),
            };
            var exe = candidati.FirstOrDefault(File.Exists);
            if (exe == null) return false;
            using var p = Process.Start(new ProcessStartInfo(exe, "/background") { UseShellExecute = false, CreateNoWindow = true });
            return p != null;
        }
        catch (Exception) { return false; }
    }

    public ScansioneCloud Scansiona(string radice, IReadOnlyCollection<string> percorsiSospetti, CancellationToken ct)
    {
        var sospetti = new HashSet<string>(
            percorsiSospetti.Select(p => Path.GetFullPath(Path.Combine(radice, p))), StringComparer.OrdinalIgnoreCase);
        var problemi = new List<FileCloud>();
        var candidatiNuovi = new List<FileCloud>();
        var totale = 0;
        var visiSegnaposto = false;
        var limiteRecente = DateTime.UtcNow - FinestraNuoviFile;
        var limiteSospetti = DateTime.UtcNow - FinestraSospetti;

        var pila = new Stack<string>();
        pila.Push(Path.GetFullPath(radice).TrimEnd('\\'));
        while (pila.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pila.Pop();
            using var handle = FindFirstFileEx(@"\" + @"\?\" + dir + @"\*", 1 /*FindExInfoBasic*/, out var dati, 0, IntPtr.Zero, 2 /*LARGE_FETCH*/);
            if (handle.IsInvalid) continue; // cartella sparita o non leggibile: non è un errore di scansione
            do
            {
                var nome = dati.cFileName;
                if (nome == "." || nome == "..") continue;
                var completo = dir + "\\" + nome;
                var attr = dati.dwFileAttributes;
                var tag = dati.dwReserved0;
                if ((attr & AttrDirectory) != 0)
                {
                    // Le cartelle segnaposto OneDrive hanno un reparse cloud e vanno attraversate; si saltano solo link e giunzioni.
                    var collegamento = (attr & AttrReparsePoint) != 0 && (tag == ReparseSymlink || tag == ReparseMountPoint);
                    if (!collegamento) pila.Push(completo);
                    continue;
                }

                totale++;
                var dimensione = ((long)dati.nFileSizeHigh << 32) | dati.nFileSizeLow;
                var segnaposto = (attr & AttrReparsePoint) != 0 && (tag & ReparseCloudMask) == ReparseCloudBase;
                if (segnaposto) visiSegnaposto = true;

                if ((attr & (AttrRecallOnDataAccess | AttrRecallOnOpen | AttrOffline)) != 0)
                {
                    problemi.Add(new FileCloud(completo, StatoFileCloud.SoloOnline, dimensione));
                }
                else if (segnaposto)
                {
                    if (!InSync(attr, tag)) problemi.Add(new FileCloud(completo, StatoFileCloud.InAttesaCaricamento, dimensione));
                }
                else
                {
                    var ticks = ((long)(uint)dati.ftLastWriteTime.dwHighDateTime << 32) | (uint)dati.ftLastWriteTime.dwLowDateTime;
                    var ultimaScrittura = DateTime.FromFileTimeUtc(ticks);
                    if ((sospetti.Contains(completo) && ultimaScrittura >= limiteSospetti) || ultimaScrittura >= limiteRecente)
                        candidatiNuovi.Add(new FileCloud(completo, StatoFileCloud.InAttesaCaricamento, dimensione));
                }
            }
            while (FindNextFile(handle, out dati));
        }

        // Senza Files On-Demand (nessun segnaposto in tutta la radice) i file non diventano mai segnaposto: non c'è modo
        // affidabile di sapere se un file regolare è stato caricato, quindi si contano solo quelli già noti come pendenti.
        if (visiSegnaposto) problemi.AddRange(candidatiNuovi);
        else problemi.AddRange(candidatiNuovi.Where(f => sospetti.Contains(f.Percorso)));
        return new ScansioneCloud(totale, problemi);
    }

    public void Scarica(string percorso, CancellationToken ct)
    {
        using var stream = new FileStream(percorso, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        while (stream.Read(buffer, 0, buffer.Length) > 0) ct.ThrowIfCancellationRequested();
    }

    public bool InternetOnline()
    {
        try
        {
            if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return false;
            // Prova reale: connessione TCP al servizio di accesso Microsoft (usato da OneDrive), con timeout breve.
            foreach (var host in new[] { "login.microsoftonline.com", "onedrive.live.com" })
            {
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    if (client.ConnectAsync(host, 443).Wait(TimeSpan.FromSeconds(3)) && client.Connected) return true;
                }
                catch (Exception) { }
            }
            return false;
        }
        catch (Exception) { return true; } // errore del controllo: non e' prova di assenza di rete
    }

    public void Sollecita(string percorso)
    {
        try
        {
            // Alterna l'attributo "archivio" e lo ripristina: genera una notifica di modifica per il client OneDrive
            // senza toccare contenuto, data di scrittura o dimensione (quindi nessun effetto su indici e revisioni).
            var originale = File.GetAttributes(percorso);
            File.SetAttributes(percorso, originale ^ FileAttributes.Archive);
            File.SetAttributes(percorso, originale);
        }
        catch (Exception) { /* file aperto da altri o non accessibile: nessun effetto */ }
    }

    private static bool InSync(uint attributi, uint tag)
    {
        try
        {
            var stato = CfGetPlaceholderStateFromAttributeTag(attributi, tag);
            if (stato == 0xFFFFFFFF) return true; // stato non determinabile: non si blocca l'app per un'incertezza
            return (stato & CfInSync) != 0 || (stato & CfPlaceholder) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return true; // senza cldapi si ricade sui soli attributi
        }
    }

    // ------------------------------------------------------------------ P/Invoke

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFindHandle() : base(true) { }
        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindFirstFileExW")]
    private static extern SafeFindHandle FindFirstFileEx(string nomeFile, int infoLevel, out Win32FindData dati, int searchOp, IntPtr filtro, int flag);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindNextFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFile(SafeFindHandle handle, out Win32FindData dati);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);

    [DllImport("cldapi.dll")]
    private static extern uint CfGetPlaceholderStateFromAttributeTag(uint fileAttributes, uint reparseTag);
}
