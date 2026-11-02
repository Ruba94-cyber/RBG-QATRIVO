namespace TestLoom.App.Services;

/// <summary>
/// Layout di default della Home per il team: un solo file condiviso, <c>_ditm/ui/home-team-layout.json</c>, che
/// pubblica solo un operatore Master. Il layout personale di ogni PC (preferenza locale "Home.Layout") ha la
/// precedenza; il layout del team vale per chi non ne ha uno.
/// </summary>
/// <remarks>
/// Il file cambia di rado, quindi le modifiche concorrenti di piu' Master si rilevano con la revisione e non si
/// fondono: una pubblicazione con una revisione aperta obsoleta viene rifiutata (<see cref="ConflittoRevisioneException"/>)
/// senza scrivere nulla, e il Master ricarica il layout del team prima di ripubblicare. Controllo di revisione e
/// scrittura avvengono sotto <see cref="DomainRecordCommitLock"/>, che serializza i processi dello stesso filesystem
/// locale. Fra PC diversi su cartella sincronizzata il lock non e' un lease distribuito: due pubblicazioni nella stessa
/// finestra di scrittura non si distinguono dalla sola revisione, e vince l'ultima scritta.
/// </remarks>
public static class HomeTeamLayoutService
{
    /// <summary>Versione dello schema del file. Una versione diversa viene ignorata, non interpretata.</summary>
    public const int SchemaCorrente = 1;

    /// <summary>Identificativo del layout nei messaggi di conflitto.</summary>
    public const string ElementoId = "home-team-layout";

    public static string PercorsoFile(DataPaths paths) =>
        Path.Combine(paths.Root, "_ditm", "ui", "home-team-layout.json");

    /// <summary>
    /// Legge il layout del team senza lanciare: file assente, vuoto, corrotto o di schema sconosciuto danno un esito
    /// esplicito. La Home ricade sul predefinito; la pubblicazione, davanti a un file illeggibile, non sovrascrive.
    /// </summary>
    public static LetturaLayoutTeam Leggi(DataPaths paths)
    {
        try
        {
            var percorso = PercorsoFile(paths);
            if (!File.Exists(percorso)) return LetturaLayoutTeam.NessunFile;

            var documento = JsonIo.TryRead<HomeTeamLayoutDocumento>(percorso, out var errore);
            if (documento == null) return LetturaLayoutTeam.Illeggibile(errore ?? "file non leggibile");
            if (documento.Schema != SchemaCorrente)
                return LetturaLayoutTeam.Illeggibile($"schema {documento.Schema} non supportato");
            if (documento.Revisione < 1 || documento.Sezioni == null)
                return LetturaLayoutTeam.Illeggibile("revisione o sezioni mancanti");
            return new LetturaLayoutTeam(StatoLayoutTeam.Valido, documento, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LetturaLayoutTeam.Illeggibile(ex.Message);
        }
    }

    /// <summary>Righe "id:1"/"id:0", nello stesso formato della preferenza personale: la Home le normalizza allo stesso modo.</summary>
    public static IReadOnlyList<string> RigheLayout(HomeTeamLayoutDocumento documento) =>
        documento.Sezioni
            .Where(s => s != null && !string.IsNullOrEmpty(s.Id))
            .Select(s => s.Id + ":" + (s.Visibile ? "1" : "0"))
            .ToList();

    /// <summary>
    /// Pubblica il layout come default del team. Riservata ai Master e a workspace scrivibili. Lancia
    /// <see cref="ConflittoRevisioneException"/> se la revisione letta sotto lock non e' <paramref name="revisioneAperta"/>,
    /// e <see cref="InvalidDataException"/> se il file esistente non e' leggibile: in entrambi i casi non scrive nulla.
    /// </summary>
    public static HomeTeamLayoutDocumento Pubblica(AppState state, IReadOnlyList<(string Id, bool Visibile)> sezioni,
        long revisioneAperta, TimeSpan? timeoutLock = null)
    {
        if (!state.EMaster)
            throw new UnauthorizedAccessException("Solo un operatore Master può pubblicare il layout della Home per il team.");
        if (!state.CartellaValida)
            throw new IOException("Pubblicazione del layout della Home non eseguita: cartella condivisa non disponibile.");
        ValidaSezioni(sezioni);

        var paths = state.Paths;
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Pubblicazione layout Home del team");

        var percorso = PercorsoFile(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(percorso)!);
        using var blocco = DomainRecordCommitLock.Acquire("Layout Home del team", ElementoId, percorso, timeoutLock);

        // Controllo e scrittura dentro il lock: la revisione confrontata e' quella che stiamo per sostituire.
        var attuale = Leggi(paths);
        if (attuale.Stato == StatoLayoutTeam.NonLeggibile)
            throw new InvalidDataException(
                $"Il layout del team esistente non è leggibile ({attuale.Errore}). Nessuna modifica è stata scritta.");
        if (attuale.Revisione != revisioneAperta)
            throw new ConflittoRevisioneException(ElementoId, revisioneAperta, attuale.Revisione);

        var documento = new HomeTeamLayoutDocumento
        {
            Schema = SchemaCorrente,
            Sezioni = sezioni.Select(s => new HomeTeamLayoutSezione { Id = s.Id, Visibile = s.Visibile }).ToList(),
            AggiornatoDa = state.OperatoreCorrente?.Nome ?? "",
            AggiornatoUtc = DateTime.UtcNow,
            Revisione = attuale.Revisione + 1,
        };
        JsonIo.WriteAtomic(percorso, documento);
        return documento;
    }

    private static void ValidaSezioni(IReadOnlyList<(string Id, bool Visibile)> sezioni)
    {
        var viste = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, _) in sezioni)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Sezione senza identificativo.", nameof(sezioni));
            if (!viste.Add(id)) throw new ArgumentException($"Sezione duplicata: {id}.", nameof(sezioni));
        }
    }
}

/// <summary>Stato del file del layout del team al momento della lettura.</summary>
public enum StatoLayoutTeam
{
    /// <summary>Nessun file: la Home usa il predefinito.</summary>
    Assente,

    /// <summary>File leggibile e di schema supportato.</summary>
    Valido,

    /// <summary>File presente ma non utilizzabile (corrotto, schema sconosciuto): ignorato, mai sovrascritto senza verifica.</summary>
    NonLeggibile,
}

/// <summary>Esito di <see cref="HomeTeamLayoutService.Leggi"/>: <see cref="Documento"/> e' valorizzato solo con stato Valido.</summary>
public sealed record LetturaLayoutTeam(StatoLayoutTeam Stato, HomeTeamLayoutDocumento? Documento, string? Errore)
{
    public static readonly LetturaLayoutTeam NessunFile = new(StatoLayoutTeam.Assente, null, null);

    public static LetturaLayoutTeam Illeggibile(string errore) => new(StatoLayoutTeam.NonLeggibile, null, errore);

    /// <summary>Revisione del file letto: 0 se il file manca o non e' leggibile, cioe' nessuna revisione verificabile.</summary>
    public long Revisione => Documento?.Revisione ?? 0;
}

/// <summary>Contenuto di <c>home-team-layout.json</c>. Le proprieta' restano in PascalCase, come gli altri JSON condivisi.</summary>
public sealed class HomeTeamLayoutDocumento
{
    public int Schema { get; set; }
    public List<HomeTeamLayoutSezione> Sezioni { get; set; } = new();
    public string AggiornatoDa { get; set; } = "";
    public DateTime AggiornatoUtc { get; set; }
    public long Revisione { get; set; }
}

public sealed class HomeTeamLayoutSezione
{
    public string Id { get; set; } = "";
    public bool Visibile { get; set; }
}
