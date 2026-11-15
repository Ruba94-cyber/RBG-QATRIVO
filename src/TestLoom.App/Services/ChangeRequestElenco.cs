using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Filtri della lista Change Request applicabili alla proiezione. Stessa semantica di
/// <c>NonConformitaViewModel.CalcolaChangeRequestsFiltrate</c> (percorso di riferimento, usato anche come ripiego): tutti i
/// criteri in AND, una lista nulla non vincola. Le date sono gia' ridotte al giorno dal chiamante.</summary>
public sealed record ChangeRequestElencoFiltro(
    StatoChangeRequest? Stato = null, string? Testo = null, string? Ticket = null,
    IReadOnlyCollection<string>? Veicoli = null, IReadOnlyCollection<string>? Componente = null, IReadOnlyCollection<string>? Gamma = null,
    bool NessunaCr = false, IReadOnlyCollection<string>? Assegnatari = null, IReadOnlyCollection<string>? Funzionalita = null,
    DateTime? AperturaDa = null, DateTime? AperturaA = null, DateTime? ChiusuraDa = null, DateTime? ChiusuraA = null,
    IReadOnlyCollection<string>? Creatori = null, IReadOnlyDictionary<string, string>? GammaDiVeicolo = null);

/// <summary>Valori distinti presenti nelle CR attive, nell'ordine di lettura storico dell'elenco (ultima modifica decrescente,
/// Id, nome file): il primo spelling incontrato di ciascun valore (senza distinguere le maiuscole) e' quello mostrato.</summary>
public sealed record ChangeRequestElencoValori(
    IReadOnlyList<string> Veicoli, IReadOnlyList<string> Componente, IReadOnlyList<string> Funzionalita,
    IReadOnlyList<string> Responsabili, IReadOnlyList<string> Creatori);

/// <summary>Riga leggera di elenco di una CR attiva: solo le chiavi di filtro, ordinamento e ricerca della lista. Immutabile dopo la
/// costruzione (un cambiamento sostituisce la riga). L'oggetto completo si legge soltanto per le righe della pagina visibile.</summary>
public sealed class RigaElencoCr
{
    internal string Nome = "";
    internal long MtimeTicks, Lunghezza, Sequenza;
    public string Id = "", Titolo = "", DescrizioneBreve = "", Descrizione = "", Responsabile = "", CreatoDa = "";
    public Guid Uid;
    internal ulong UidAlto, UidBasso;
    public StatoChangeRequest Stato;
    internal long CreazioneTicks, ChiusaTicks, ImplementataTicks, ModificataTicks;
    internal string[] Veicoli = Array.Empty<string>(), Componente = Array.Empty<string>(), Funzionalita = Array.Empty<string>(), Ticket = Array.Empty<string>();
    internal string PrimoTicket = "", PrimoVeicolo = "";

    /// <summary>Stima della memoria occupata dalla riga (oggetto, array e stringhe non condivise), in byte.</summary>
    internal long StimaByte()
    {
        long Str(string s) => s.Length == 0 ? 0 : 22 + (long)s.Length * 2;
        long Arr(string[] a) => a.Length == 0 ? 0 : 24 + 8L * a.Length;
        // Responsabile/CreatoDa/Veicoli/Componente/Funzionalita sono internati (condivisi): contati solo i riferimenti.
        return 24 + 8 * 18 + 16 + 8 * 8 + Str(Nome) + Str(Id) + Str(Titolo) + Str(DescrizioneBreve) + Str(Descrizione)
            + Arr(Veicoli) + Arr(Componente) + Arr(Funzionalita) + Arr(Ticket) + Ticket.Sum(Str) + Str(PrimoTicket);
    }
}

public sealed record ChangeRequestElencoPagina(long Totale, long Pagina, IReadOnlyList<RigaElencoCr> Righe);

/// <summary>
/// Proiezione di elenco in RAM delle Change Request attive (non archiviate), con ordinamenti precalcolati per colonna. Sostituisce
/// la deserializzazione di tutte le CR a ogni refresh: filtro, ordinamento, ricerca e conteggi lavorano su queste righe leggere,
/// fuori dal thread UI e con annullamento cooperativo. Aggiornamento incrementale: <see cref="Upsert"/>/<see cref="Rimuovi"/> a ogni
/// salvataggio o eliminazione, <see cref="ApplicaSincronizzazione"/> per le riconciliazioni (sync, conflitti, ricostruzione).
/// Thread-safe (lock lettore/scrittore): le interrogazioni girano in parallelo, le mutazioni sono brevi.
/// </summary>
public sealed class ChangeRequestElenco
{
    private const int ControlloAnnullamentoOgni = 2048;
    /// <summary>Oltre questo numero di righe cambiate in una sincronizzazione gli ordinamenti si ricostruiscono al primo uso.</summary>
    private const int SogliaAggiornamentoIncrementale = 512;
    private static readonly ChangeRequestOrdinamentoChiave[] ChiaviOrdinamento = Enum.GetValues<ChangeRequestOrdinamentoChiave>();

    private sealed class Ordine
    {
        public RigaElencoCr[] A;
        public int N;
        public Ordine(RigaElencoCr[] a, int n) { A = a; N = n; }
    }

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<string, RigaElencoCr> _righe = new(StringComparer.OrdinalIgnoreCase);
    private readonly Ordine?[] _ordini = new Ordine?[ChiaviOrdinamento.Length];
    private readonly ConcurrentDictionary<string, string> _pool = new(StringComparer.Ordinal);
    private string _culturaOrdini = "";
    private ChangeRequestElencoValori? _distinti;
    private long _sequenza;
    private int _sincronizzazioniAttive;
    private readonly Dictionary<string, long> _rimossiDuranteSincronizzazione = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Numero di righe (CR attive) nella proiezione.</summary>
    public int Count { get { _lock.EnterReadLock(); try { return _righe.Count; } finally { _lock.ExitReadLock(); } } }

    /// <summary>Incrementato a ogni mutazione: permette ai chiamanti di riusare risultati derivati finche' non cambia.</summary>
    public long Versione => Interlocked.Read(ref _versione);
    private long _versione;

    // ------------------------------------------------------------------ costruzione delle righe

    private string Internato(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length > 64) return s;
        return _pool.GetOrAdd(s, s);
    }

    private string[] Internati(List<string>? valori)
    {
        if (valori == null || valori.Count == 0) return Array.Empty<string>();
        var a = new string[valori.Count];
        for (var i = 0; i < a.Length; i++) a[i] = Internato(valori[i]);
        return a;
    }

    /// <summary>Costruisce la riga di una CR (thread-safe: usabile da piu' thread durante il caricamento iniziale).</summary>
    public RigaElencoCr Crea(ChangeRequest cr, string percorso, long mtimeTicks, long lunghezza)
    {
        Span<byte> b = stackalloc byte[16];
        cr.Uid.TryWriteBytes(b);
        // Ordine lessicografico del testo "D" (esadecimale minuscolo) = ordine numerico dei campi Data1, Data2, Data3 e dei 8 byte finali.
        var alto = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(b) << 32) | ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(b[4..]) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(b[6..]);
        var basso = BinaryPrimitives.ReadUInt64BigEndian(b[8..]);
        var ticket = cr.TicketCollegati == null ? Array.Empty<string>()
            : cr.TicketCollegati.Select(t => t?.Riferimento ?? "").ToArray();
        return new RigaElencoCr
        {
            Nome = Path.GetFileName(percorso), MtimeTicks = mtimeTicks, Lunghezza = lunghezza,
            Id = cr.Id ?? "", Titolo = cr.Titolo ?? "", DescrizioneBreve = cr.DescrizioneBreve ?? "", Descrizione = cr.Descrizione ?? "",
            Responsabile = Internato(cr.Responsabile), CreatoDa = Internato(cr.CreatoDa),
            Uid = cr.Uid, UidAlto = alto, UidBasso = basso, Stato = cr.Stato,
            CreazioneTicks = cr.DataCreazione.Ticks,
            ChiusaTicks = cr.ChiusaIl?.Ticks ?? long.MinValue,
            ImplementataTicks = cr.ImplementataIl?.Ticks ?? long.MinValue,
            ModificataTicks = cr.UltimaModifica.ToUniversalTime().Ticks,
            Veicoli = Internati(cr.VeicoliImpattati), Componente = Internati(cr.EcuImpattate), Funzionalita = Internati(cr.FunzionalitaImpattate),
            Ticket = ticket,
            PrimoTicket = ChangeRequestOrdinamentoComparer.PrimoTicket(cr),
            PrimoVeicolo = Internato(ChangeRequestOrdinamentoComparer.PrimoVeicolo(cr)),
        };
    }

    // ------------------------------------------------------------------ mutazioni

    /// <summary>Aggiornamento incrementale dopo un salvataggio: sostituisce la riga della CR (o la aggiunge). Una CR archiviata non
    /// fa parte dell'elenco: va rimossa con <see cref="Rimuovi"/>.</summary>
    public void Upsert(RigaElencoCr riga)
    {
        _lock.EnterWriteLock();
        try
        {
            riga.Sequenza = Interlocked.Increment(ref _sequenza);
            _rimossiDuranteSincronizzazione.Remove(riga.Nome);
            SostituisciSottoLock(riga);
            Invalida();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void Rimuovi(string percorso)
    {
        var nome = Path.GetFileName(percorso);
        _lock.EnterWriteLock();
        try
        {
            var seq = Interlocked.Increment(ref _sequenza);
            if (_sincronizzazioniAttive > 0) _rimossiDuranteSincronizzazione[nome] = seq;
            if (RimuoviSottoLock(nome)) Invalida();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void Azzera()
    {
        _lock.EnterWriteLock();
        try
        {
            Interlocked.Increment(ref _sequenza);
            _righe.Clear();
            Array.Clear(_ordini);
            Invalida();
        }
        finally { _lock.ExitWriteLock(); }
    }

    private void Invalida()
    {
        _distinti = null;
        Interlocked.Increment(ref _versione);
    }

    private void SostituisciSottoLock(RigaElencoCr nuova)
    {
        if (_righe.TryGetValue(nuova.Nome, out var vecchia))
        {
            RimuoviDaOrdini(vecchia);
        }
        _righe[nuova.Nome] = nuova;
        InserisciInOrdini(nuova);
    }

    private bool RimuoviSottoLock(string nome)
    {
        if (!_righe.Remove(nome, out var vecchia)) return false;
        RimuoviDaOrdini(vecchia);
        return true;
    }

    private void RimuoviDaOrdini(RigaElencoCr riga)
    {
        for (var k = 0; k < _ordini.Length; k++)
        {
            var o = _ordini[k];
            if (o == null) continue;
            var cmp = Confronto(ChiaviOrdinamento[k]);
            var i = Array.BinarySearch(o.A, 0, o.N, riga, cmp);
            // Con chiave totale (Id, Uid) la posizione e' unica; se la riga non si trova (stato incoerente) l'ordine si ricostruisce.
            var trovato = -1;
            if (i >= 0)
            {
                for (var j = i; j >= 0 && j > i - 8 && cmp.Compare(o.A[j], riga) == 0; j--) if (ReferenceEquals(o.A[j], riga)) { trovato = j; break; }
                for (var j = i + 1; trovato < 0 && j < o.N && j < i + 8 && cmp.Compare(o.A[j], riga) == 0; j++) if (ReferenceEquals(o.A[j], riga)) trovato = j;
            }
            if (trovato < 0) { _ordini[k] = null; continue; }
            Array.Copy(o.A, trovato + 1, o.A, trovato, o.N - trovato - 1);
            o.A[--o.N] = null!;
        }
    }

    private void InserisciInOrdini(RigaElencoCr riga)
    {
        for (var k = 0; k < _ordini.Length; k++)
        {
            var o = _ordini[k];
            if (o == null) continue;
            var i = Array.BinarySearch(o.A, 0, o.N, riga, Confronto(ChiaviOrdinamento[k]));
            if (i < 0) i = ~i;
            if (o.N == o.A.Length) Array.Resize(ref o.A, Math.Max(16, o.A.Length + o.A.Length / 4));
            Array.Copy(o.A, i, o.A, i + 1, o.N - i);
            o.A[i] = riga;
            o.N++;
        }
    }

    // ------------------------------------------------------------------ sincronizzazione con l'indice

    /// <summary>Segna l'inizio di una sincronizzazione con l'indice. Finche' e' attiva le rimozioni vengono ricordate: una riga
    /// toccata da un salvataggio/eliminazione successivo a <paramref name="sequenzaInizio"/> non viene mai sovrascritta ne' rimossa
    /// da dati letti prima.</summary>
    public IDisposable IniziaSincronizzazione(out long sequenzaInizio)
    {
        _lock.EnterWriteLock();
        try
        {
            _sincronizzazioniAttive++;
            sequenzaInizio = Interlocked.Read(ref _sequenza);
        }
        finally { _lock.ExitWriteLock(); }
        return new FineSincronizzazione(this);
    }

    private sealed class FineSincronizzazione : IDisposable
    {
        private ChangeRequestElenco? _e;
        public FineSincronizzazione(ChangeRequestElenco e) => _e = e;
        public void Dispose()
        {
            var e = Interlocked.Exchange(ref _e, null);
            if (e == null) return;
            e._lock.EnterWriteLock();
            try
            {
                if (--e._sincronizzazioniAttive == 0) e._rimossiDuranteSincronizzazione.Clear();
            }
            finally { e._lock.ExitWriteLock(); }
        }
    }

    /// <summary>Confronta gli stamp dell'indice con le righe: percorsi nuovi o con stamp diverso da rileggere, nomi presenti
    /// in proiezione e non nell'indice da rimuovere.</summary>
    public (List<string> DaLeggere, List<string> DaRimuovere) Differenze(IReadOnlyList<(string Path, long MtimeTicks, long Length)> stampIndice)
    {
        var daLeggere = new List<string>();
        var visti = new HashSet<string>(stampIndice.Count, StringComparer.OrdinalIgnoreCase);
        _lock.EnterReadLock();
        try
        {
            foreach (var (path, mtime, len) in stampIndice)
            {
                var nome = Path.GetFileName(path);
                visti.Add(nome);
                if (!_righe.TryGetValue(nome, out var r) || r.MtimeTicks != mtime || r.Lunghezza != len) daLeggere.Add(path);
            }
            var daRimuovere = new List<string>();
            foreach (var nome in _righe.Keys) if (!visti.Contains(nome)) daRimuovere.Add(nome);
            return (daLeggere, daRimuovere);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Applica in un colpo righe lette e rimozioni calcolate da una sincronizzazione iniziata a <paramref name="sequenzaInizio"/>.
    /// Le righe toccate da un'altra mutazione dopo quell'istante hanno la precedenza.</summary>
    public int ApplicaSincronizzazione(IReadOnlyList<RigaElencoCr> daInserire, IReadOnlyList<string> daRimuovere, long sequenzaInizio)
    {
        _lock.EnterWriteLock();
        try
        {
            var cambiate = 0;
            var incrementale = daInserire.Count + daRimuovere.Count <= SogliaAggiornamentoIncrementale;
            foreach (var nome in daRimuovere)
            {
                if (!_righe.TryGetValue(nome, out var r) || r.Sequenza > sequenzaInizio) continue;
                if (incrementale) RimuoviDaOrdini(r);
                _righe.Remove(nome);
                cambiate++;
            }
            foreach (var nuova in daInserire)
            {
                if (_rimossiDuranteSincronizzazione.TryGetValue(nuova.Nome, out var seqRimozione) && seqRimozione > sequenzaInizio) continue;
                if (_righe.TryGetValue(nuova.Nome, out var vecchia))
                {
                    if (vecchia.Sequenza > sequenzaInizio) continue;
                    if (incrementale) RimuoviDaOrdini(vecchia);
                }
                _righe[nuova.Nome] = nuova;
                if (incrementale) InserisciInOrdini(nuova);
                cambiate++;
            }
            if (cambiate > 0)
            {
                if (!incrementale) Array.Clear(_ordini);
                Invalida();
            }
            return cambiate;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ------------------------------------------------------------------ ordinamento

    /// <summary>Ordine crescente totale per chiave: chiave, poi Id (confronto della cultura, senza maiuscole), poi UID come testo ordinale.
    /// Il decrescente e' l'esatto ordine inverso (la direzione moltiplica tutta la catena di confronti).</summary>
    private static IComparer<RigaElencoCr> Confronto(ChangeRequestOrdinamentoChiave chiave) => Comparer<RigaElencoCr>.Create((a, b) =>
    {
        var c = chiave switch
        {
            ChangeRequestOrdinamentoChiave.Apertura => a.CreazioneTicks.CompareTo(b.CreazioneTicks),
            ChangeRequestOrdinamentoChiave.Id => NoCase(a.Id, b.Id),
            ChangeRequestOrdinamentoChiave.Titolo => NoCase(a.Titolo, b.Titolo),
            ChangeRequestOrdinamentoChiave.DescrizioneBreve => NoCase(a.DescrizioneBreve, b.DescrizioneBreve),
            ChangeRequestOrdinamentoChiave.Descrizione => NoCase(a.Descrizione, b.Descrizione),
            ChangeRequestOrdinamentoChiave.Ticket => NoCase(a.PrimoTicket, b.PrimoTicket),
            ChangeRequestOrdinamentoChiave.Stato => ((int)a.Stato).CompareTo((int)b.Stato),
            ChangeRequestOrdinamentoChiave.Veicolo => NoCase(a.PrimoVeicolo, b.PrimoVeicolo),
            ChangeRequestOrdinamentoChiave.Implementazione => a.ImplementataTicks.CompareTo(b.ImplementataTicks),
            ChangeRequestOrdinamentoChiave.Chiusura => a.ChiusaTicks.CompareTo(b.ChiusaTicks),
            _ => 0,
        };
        if (c == 0) c = NoCase(a.Id, b.Id);
        if (c == 0) c = a.UidAlto != b.UidAlto ? (a.UidAlto < b.UidAlto ? -1 : 1) : a.UidBasso.CompareTo(b.UidBasso);
        return c;
    });

    private static int NoCase(string a, string b) => string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Costruisce (se manca) l'ordine crescente della chiave. L'ordinamento della copia delle righe gira fuori da ogni lock (piu'
    /// colonne possono costruirsi in parallelo e le interrogazioni non attendono); la pubblicazione avviene solo se nel frattempo nessuna
    /// mutazione ha cambiato le righe, altrimenti si ripete.</summary>
    private void AssicuraOrdine(ChangeRequestOrdinamentoChiave chiave)
    {
        var k = Array.IndexOf(ChiaviOrdinamento, chiave);
        for (var tentativo = 0; ; tentativo++)
        {
            RigaElencoCr[] copia;
            long versione;
            var cultura = CultureInfo.CurrentCulture.Name;
            _lock.EnterReadLock();
            try
            {
                if (_ordini[k] != null && cultura == _culturaOrdini) return;
                copia = new RigaElencoCr[_righe.Count];
                _righe.Values.CopyTo(copia, 0);
                versione = Interlocked.Read(ref _versione);
            }
            finally { _lock.ExitReadLock(); }

            Array.Sort(copia, Confronto(chiave));

            _lock.EnterWriteLock();
            try
            {
                if (Interlocked.Read(ref _versione) == versione)
                {
                    if (cultura != _culturaOrdini) { Array.Clear(_ordini); _culturaOrdini = cultura; }
                    _ordini[k] = new Ordine(copia, copia.Length);
                    return;
                }
            }
            finally { _lock.ExitWriteLock(); }
            // Mutazioni concorrenti durante l'ordinamento: si riprova (le mutazioni sono rare e brevi).
            if (tentativo >= 20) Thread.Yield();
        }
    }

    /// <summary>Costruisce in parallelo tutti gli ordinamenti (precaricamento): il primo uso di ogni colonna risponde subito.</summary>
    public void PreparaOrdinamenti(CancellationToken ct = default) =>
        Parallel.ForEach(ChiaviOrdinamento, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount)) },
            k => AssicuraOrdine(k));

    // ------------------------------------------------------------------ interrogazione

    /// <summary>Filtro + ordinamento + pagina. Totale e pagina effettiva sono esatti (scansione completa delle righe: nessuna stima).</summary>
    public ChangeRequestElencoPagina Interroga(ChangeRequestElencoFiltro f, ChangeRequestOrdinamento ordinamento, long paginaRichiesta,
        int dimensionePagina, CancellationToken ct = default)
    {
        var predicato = new Predicato(f);
        for (var tentativo = 0; ; tentativo++)
        {
            ct.ThrowIfCancellationRequested();
            _lock.EnterReadLock();
            try
            {
                var k = Array.IndexOf(ChiaviOrdinamento, ordinamento.Chiave);
                var o = _ordini[k];
                if (o != null && CultureInfo.CurrentCulture.Name == _culturaOrdini)
                    return Scansiona(o, predicato, ordinamento.Decrescente, paginaRichiesta, dimensionePagina, ct);
            }
            finally { _lock.ExitReadLock(); }
            AssicuraOrdine(ordinamento.Chiave);
        }
    }

    private static ChangeRequestElencoPagina Scansiona(Ordine o, Predicato p, bool decrescente, long paginaRichiesta, int dimensione, CancellationToken ct)
    {
        List<RigaElencoCr> trovate;
        if (p.NessunVincolo) trovate = new List<RigaElencoCr>(o.N);
        else trovate = new List<RigaElencoCr>();
        if (!p.NessunRisultato)
        {
            if (decrescente)
                for (var i = o.N - 1; i >= 0; i--)
                {
                    if ((i & (ControlloAnnullamentoOgni - 1)) == 0) ct.ThrowIfCancellationRequested();
                    var r = o.A[i];
                    if (p.Corrisponde(r)) trovate.Add(r);
                }
            else
                for (var i = 0; i < o.N; i++)
                {
                    if ((i & (ControlloAnnullamentoOgni - 1)) == 0) ct.ThrowIfCancellationRequested();
                    var r = o.A[i];
                    if (p.Corrisponde(r)) trovate.Add(r);
                }
        }
        var totale = trovate.Count;
        var pagine = Math.Max(1L, checked((totale + dimensione - 1L) / dimensione));
        var pagina = Math.Min(Math.Max(1L, paginaRichiesta), pagine);
        var offset = checked((int)((pagina - 1L) * dimensione));
        var righe = trovate.Skip(offset).Take(dimensione).ToList();
        return new ChangeRequestElencoPagina(totale, pagina, righe);
    }

    /// <summary>Predicato compilato dal filtro: stessi criteri e stesso confronto del percorso di riferimento.</summary>
    private sealed class Predicato
    {
        private readonly StatoChangeRequest? _stato;
        private readonly string? _testo, _ticket;
        private readonly HashSet<string>? _veicoli, _ecu, _gamma, _assegnatari, _creatori, _funzionalita;
        private readonly IReadOnlyDictionary<string, string>? _mappa;
        private readonly long? _aperturaDa, _aperturaA, _chiusuraDa, _chiusuraA;
        private readonly bool _haChiusura;
        public bool NessunRisultato { get; }
        public bool NessunVincolo { get; }

        public Predicato(ChangeRequestElencoFiltro f)
        {
            _stato = f.Stato;
            _testo = f.Testo; _ticket = f.Ticket;
            HashSet<string>? Set(IReadOnlyCollection<string>? v) => v == null ? null : new HashSet<string>(v, StringComparer.OrdinalIgnoreCase);
            _veicoli = Set(f.Veicoli); _ecu = Set(f.Componente); _gamma = Set(f.Gamma);
            _assegnatari = Set(f.Assegnatari); _creatori = Set(f.Creatori); _funzionalita = Set(f.Funzionalita);
            if (_gamma != null) _mappa = f.GammaDiVeicolo ?? throw new InvalidOperationException("Filtro gamma senza lookup dei veicoli.");
            _aperturaDa = f.AperturaDa?.Ticks; _aperturaA = f.AperturaA?.Ticks;
            _chiusuraDa = f.ChiusuraDa?.Ticks; _chiusuraA = f.ChiusuraA?.Ticks;
            _haChiusura = f.ChiusuraDa != null || f.ChiusuraA != null;
            NessunRisultato = f.NessunaCr;
            NessunVincolo = _stato == null && _testo == null && _ticket == null && _veicoli == null && _ecu == null && _gamma == null
                && _assegnatari == null && _creatori == null && _funzionalita == null && _aperturaDa == null && _aperturaA == null && !_haChiusura;
        }

        public bool Corrisponde(RigaElencoCr r)
        {
            if (NessunVincolo) return true;
            if (_stato.HasValue && r.Stato != _stato.Value) return false;
            if (_aperturaDa.HasValue && Giorno(r.CreazioneTicks) < _aperturaDa.Value) return false;
            if (_aperturaA.HasValue && Giorno(r.CreazioneTicks) > _aperturaA.Value) return false;
            if (_haChiusura)
            {
                if (r.ChiusaTicks == long.MinValue) return false;
                var g = Giorno(r.ChiusaTicks);
                if (_chiusuraDa.HasValue && g < _chiusuraDa.Value) return false;
                if (_chiusuraA.HasValue && g > _chiusuraA.Value) return false;
            }
            if (_assegnatari != null && !_assegnatari.Contains(r.Responsabile)) return false;
            if (_creatori != null && !_creatori.Contains(r.CreatoDa)) return false;
            if (_veicoli != null && !Qualsiasi(r.Veicoli, _veicoli)) return false;
            if (_ecu != null && !Qualsiasi(r.Componente, _ecu)) return false;
            if (_funzionalita != null && !Qualsiasi(r.Funzionalita, _funzionalita)) return false;
            if (_gamma != null && !GammaCorrisponde(r)) return false;
            if (_ticket != null && !ContieneTicket(r)) return false;
            if (_testo != null
                && !(r.Id.Contains(_testo, StringComparison.OrdinalIgnoreCase)
                    || r.Titolo.Contains(_testo, StringComparison.OrdinalIgnoreCase)
                    || r.Descrizione.Contains(_testo, StringComparison.OrdinalIgnoreCase))) return false;
            return true;
        }

        private static long Giorno(long ticks) => ticks - ticks % TimeSpan.TicksPerDay;

        private static bool Qualsiasi(string[] valori, HashSet<string> set)
        {
            for (var i = 0; i < valori.Length; i++) if (set.Contains(valori[i])) return true;
            return false;
        }

        private bool GammaCorrisponde(RigaElencoCr r)
        {
            for (var i = 0; i < r.Veicoli.Length; i++)
                if (_mappa!.TryGetValue(r.Veicoli[i], out var g) && g != null && _gamma!.Contains(g)) return true;
            return false;
        }

        private bool ContieneTicket(RigaElencoCr r)
        {
            for (var i = 0; i < r.Ticket.Length; i++)
                if (r.Ticket[i].Contains(_ticket!, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    // ------------------------------------------------------------------ aggregati

    /// <summary>Valori distinti di veicoli, componente, funzionalita', responsabili e creatori delle CR attive (cache per versione).</summary>
    public ChangeRequestElencoValori Distinti()
    {
        _lock.EnterReadLock();
        try
        {
            var gia = _distinti;
            if (gia != null) return gia;
            // Ordine di lettura storico: ultima modifica decrescente, Id (NOCASE ASCII), nome file ordinale.
            var righe = _righe.Values.ToArray();
            Array.Sort(righe, (a, b) =>
            {
                var c = b.ModificataTicks.CompareTo(a.ModificataTicks);
                if (c == 0) c = AnomalieOrdinamentoComparer.ConfrontaNoCase(a.Id, b.Id);
                if (c == 0) c = string.CompareOrdinal(a.Nome, b.Nome);
                return c;
            });
            var veicoli = new Distinguitore(); var componente = new Distinguitore(); var funz = new Distinguitore();
            var resp = new Distinguitore(); var creatori = new Distinguitore();
            foreach (var r in righe)
            {
                foreach (var v in r.Veicoli) veicoli.Aggiungi(v);
                foreach (var e in r.Componente) componente.Aggiungi(e);
                foreach (var f in r.Funzionalita) funz.Aggiungi(f);
                resp.Aggiungi(r.Responsabile);
                creatori.Aggiungi(r.CreatoDa);
            }
            var risultato = new ChangeRequestElencoValori(veicoli.Valori, componente.Valori, funz.Valori, resp.Valori, creatori.Valori);
            // Pubblicazione senza upgrade del lock: la cache e' un riferimento, scritto solo qui e azzerato da Invalida (sotto write lock).
            Interlocked.CompareExchange(ref _distinti, risultato, null);
            return risultato;
        }
        finally { _lock.ExitReadLock(); }
    }

    private sealed class Distinguitore
    {
        private readonly HashSet<string> _visti = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Valori = new();
        public void Aggiungi(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            if (_visti.Add(v)) Valori.Add(v);
        }
    }

    /// <summary>Parte della stima dovuta al testo delle descrizioni (dipende dai dati: 22 B + 2 B per carattere), in byte.</summary>
    public long StimaDescrizioniByte()
    {
        _lock.EnterReadLock();
        try
        {
            long tot = 0;
            foreach (var r in _righe.Values) tot += r.Descrizione.Length == 0 ? 0 : 22 + 2L * r.Descrizione.Length;
            return tot;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Stima della RAM della proiezione (righe + ordinamenti), in byte.</summary>
    public long StimaByte()
    {
        _lock.EnterReadLock();
        try
        {
            long tot = 0;
            foreach (var r in _righe.Values) tot += r.StimaByte() + 40; // 40 = voce di dizionario
            foreach (var o in _ordini) if (o != null) tot += 24L + 8L * o.A.Length;
            // stringhe internate (una volta sola)
            foreach (var s in _pool.Keys) tot += 22 + 2L * s.Length;
            return tot;
        }
        finally { _lock.ExitReadLock(); }
    }
}
