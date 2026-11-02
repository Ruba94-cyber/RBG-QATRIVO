using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace TestLoom.App.Services;

/// <summary>
/// Elementi aperti di recente (Test, Change Request, Segnalazioni di rete) per la sezione "Recenti" della Home.
/// Preferenza LOCALE al PC come ogni <see cref="ViewStateService"/>: vive in config.json (%AppData%), mai nella
/// cartella condivisa. Ogni voce e' "tipo|id|titolo|utcTicks", con tipo test | cr | nc.
///
/// Le voci malformate vengono saltate in lettura senza toccare le altre. Un elemento che non esiste piu' resta
/// salvato (non viene cancellato da qui): la Home lo salta in visualizzazione, e la capacita' lo spinge fuori col
/// tempo.
/// </summary>
public static class RecentItemsService
{
    public const string ChiaveVista = "Home.Recenti";
    public const int Capacita = 15;
    public const string TipoTest = "test";
    public const string TipoChangeRequest = "cr";
    public const string TipoSegnalazioneRete = "nc";

    private const char Separatore = '|';
    private const int LunghezzaMassimaTitolo = 200;
    private static readonly string[] TipiValidi = { TipoTest, TipoChangeRequest, TipoSegnalazioneRete };

    /// <summary>Voce letta. <see cref="AperturaUtc"/> e' l'istante dell'ultima apertura.</summary>
    public sealed record ElementoRecente(string Tipo, string Id, string Titolo, DateTime AperturaUtc);

    /// <summary>Elenco dal piu' recente al meno recente, senza duplicati per tipo+id (vince la voce piu' recente).
    /// Non lancia mai per dati guasti: le voci non leggibili vengono ignorate.</summary>
    public static IReadOnlyList<ElementoRecente> Leggi(AppState state)
    {
        var voci = new List<ElementoRecente>();
        var viste = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var riga in ViewStateService.LeggiLista(state, ChiaveVista))
        {
            if (!TryParse(riga, out var voce)) continue;
            if (!viste.Add(ChiaveDuplicato(voce.Tipo, voce.Id))) continue;
            voci.Add(voce);
        }
        return voci.OrderByDescending(v => v.AperturaUtc).ToList();
    }

    /// <summary>Porta in cima l'elemento (tipo+id), aggiungendolo se assente. Oltre <see cref="Capacita"/> voci
    /// cade la piu' vecchia. Tipo non valido o id vuoto/con separatori: nessuna scrittura.</summary>
    public static void Registra(AppState state, string tipo, string id, string? titolo)
    {
        if (!TipiValidi.Contains(tipo) || !IdValido(id)) return;

        var correnti = Leggi(state);
        // Istante strettamente crescente rispetto alla voce piu' recente: due aperture nello stesso tick restano
        // in ordine anche se l'orologio non avanza abbastanza.
        var ultimoTicks = correnti.Count == 0 ? 0L : correnti[0].AperturaUtc.Ticks;
        var ticks = Math.Max(DateTime.UtcNow.Ticks, ultimoTicks + 1);

        var righe = new List<string>(Math.Min(Capacita, correnti.Count + 1)) { Formatta(tipo, id, titolo, ticks) };
        foreach (var voce in correnti)
        {
            if (string.Equals(voce.Tipo, tipo, StringComparison.Ordinal) &&
                string.Equals(voce.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
            righe.Add(Formatta(voce.Tipo, voce.Id, voce.Titolo, voce.AperturaUtc.Ticks));
        }
        if (righe.Count > Capacita) righe.RemoveRange(Capacita, righe.Count - Capacita);
        ViewStateService.ScriviLista(state, ChiaveVista, righe);
    }

    private static string ChiaveDuplicato(string tipo, string id) => tipo + Separatore + id;

    private static bool IdValido(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id == id.Trim() && id.IndexOfAny(new[] { Separatore, '\r', '\n' }) < 0;

    private static string PulisciTitolo(string? titolo)
    {
        var pulito = (titolo ?? "").Replace(Separatore, ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return pulito.Length <= LunghezzaMassimaTitolo ? pulito : pulito[..LunghezzaMassimaTitolo].TrimEnd();
    }

    private static string Formatta(string tipo, string id, string? titolo, long ticks) =>
        $"{tipo}{Separatore}{id}{Separatore}{PulisciTitolo(titolo)}{Separatore}{ticks.ToString(CultureInfo.InvariantCulture)}";

    private static bool TryParse(string? riga, [NotNullWhen(true)] out ElementoRecente? voce)
    {
        voce = null;
        if (string.IsNullOrEmpty(riga)) return false;
        var parti = riga.Split(Separatore);
        if (parti.Length < 4) return false;

        var tipo = parti[0];
        var id = parti[1];
        if (!TipiValidi.Contains(tipo) || !IdValido(id)) return false;
        if (!long.TryParse(parti[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return false;
        if (ticks <= 0 || ticks >= DateTime.MaxValue.Ticks) return false;

        // Un titolo che contenesse ancora il separatore (voce scritta a mano) resta leggibile come unico campo.
        var titolo = string.Join(Separatore, parti[2..^1]).Trim();
        voce = new ElementoRecente(tipo, id, titolo, new DateTime(ticks, DateTimeKind.Utc));
        return true;
    }
}
