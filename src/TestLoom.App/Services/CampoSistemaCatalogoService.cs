using System.Globalization;
using System.Text;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Campi realmente nuovi (Ruolo=Nessuno, non archiviati) del catalogo condiviso di un gruppo
/// (Addendum: grill-me 2026-08-18, follow-up): usato sia dal "Gestisci campi" di Tipi di test — dove un
/// Master può selezionarli/crearne di nuovi — sia dai filtri custom di Dashboard/Report, così la stessa
/// definizione di "campo utilizzabile" non si duplica nei due posti.</summary>
public static class CampoSistemaCatalogoService
{
    public static List<CampoSistemaDefinizione> CampiLiberi(AppState state, GruppoCampoSistema gruppo)
    {
        if (!state.CartellaValida) return new();
        var repo = gruppo == GruppoCampoSistema.Pianificazione ? state.Master.CampiSistemaPianificazione : state.Master.CampiSistemaDettagli;
        return repo.Load().Where(c => c.Ruolo == RuoloCampoSistema.Nessuno && !c.Archiviata).ToList();
    }

    /// <summary>Campi liberi di ENTRAMBI i gruppi, per filtri Dashboard/Report che non distinguono
    /// Pianificazione/Dettagli (Addendum: grill-me 2026-08-18, seconda follow-up — "includilo ora").</summary>
    public static List<CampoSistemaDefinizione> CampiLiberiTuttiIGruppi(AppState state) =>
        CampiLiberi(state, GruppoCampoSistema.Pianificazione).Concat(CampiLiberi(state, GruppoCampoSistema.Dettagli)).ToList();

    /// <summary>Sottoinsieme di CampiLiberiTuttiIGruppi che il Master ha esplicitamente marcato utilizzabile
    /// come filtro (Addendum: grill-me 2026-08-18, terza follow-up — "non hanno senso come filtri", "a mia
    /// discrezione"): distinto da CampiLiberi/CampiLiberiTuttiIGruppi, che restano invariati perché usati
    /// anche per popolare il picker "campo di sistema" nel "Gestisci campi" di Tipi di test — lì servono TUTTI
    /// i campi Ruolo=Nessuno (inclusi quelli storici come Descrizione), non solo quelli filtrabili.</summary>
    public static List<CampoSistemaDefinizione> CampiUtilizzabiliComeFiltro(AppState state) =>
        CampiLiberiTuttiIGruppi(state).Where(c => c.UsabileComeFiltro).ToList();

    /// <summary>Etichetta per Chiave di tutti i campi di sistema di entrambi i gruppi (archiviati inclusi): serve a descrivere un filtro
    /// salvato o applicato con il nome leggibile del campo, anche se nel frattempo e' stato archiviato.</summary>
    public static Dictionary<string, string> EtichettePerChiave(AppState state)
    {
        var etichette = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!state.CartellaValida) return etichette;
        return EtichettePerChiave(state.Master.CampiSistemaPianificazione.Load(), state.Master.CampiSistemaDettagli.Load());
    }

    public static Dictionary<string, string> EtichettePerChiave(IReadOnlyList<CampoSistemaDefinizione> pianificazione,
        IReadOnlyList<CampoSistemaDefinizione> dettagli)
    {
        var etichette = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var campo in pianificazione.Concat(dettagli))
            if (!string.IsNullOrWhiteSpace(campo.Chiave)) etichette.TryAdd(campo.Chiave, campo.Etichetta);
        return etichette;
    }

    /// <summary>Valori distinti attualmente presenti su un campo custom, per popolare un filtro multi-select
    /// senza assumere un elenco di opzioni fisso (valido anche per campi Testo/Numero, non solo Lista).</summary>
    public static List<string> ValoriDistinti(IEnumerable<TestCase> testCases, string chiave) =>
        testCases.Select(tc => tc.CampiCustom.GetValueOrDefault(chiave))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Voci offerte da un filtro su un campo di sistema: per un campo Lista prima le opzioni definite nel catalogo (nel loro
    /// ordine), poi i valori presenti nei test che non compaiono gia' tra quelle; per gli altri tipi solo i valori presenti. Il confronto
    /// non distingue maiuscole, come i filtri stessi: "usb" in un Test e "USB" nel catalogo sono la stessa voce, mostrata con la grafia del
    /// catalogo. I valori presenti restano nell'ordine ricevuto (quello di <see cref="ValoriDistinti"/>).</summary>
    public static List<string> OpzioniFiltro(TipoCampoStrumento tipo, IEnumerable<string>? opzioniDefinite, IEnumerable<string> valoriPresenti)
    {
        var voci = new List<string>();
        var viste = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tipo == TipoCampoStrumento.Lista && opzioniDefinite != null)
            foreach (var opzione in opzioniDefinite)
                if (!string.IsNullOrWhiteSpace(opzione) && viste.Add(opzione)) voci.Add(opzione);
        foreach (var valore in valoriPresenti)
            if (!string.IsNullOrWhiteSpace(valore) && viste.Add(valore)) voci.Add(valore);
        return voci;
    }

    /// <summary>Criterio dei filtri sui campi di sistema (Lista Test): per ogni campo con almeno un valore scelto il Test deve avere un
    /// valore fra quelli scelti, senza distinguere maiuscole; un Test senza il campo non corrisponde. Piu' campi scelti: devono
    /// corrispondere tutti (AND), i valori dello stesso campo sono alternativi (OR).</summary>
    public static bool CorrispondeCampiCustom(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> selezioni, IReadOnlyDictionary<string, string> campiCustom)
    {
        foreach (var (chiave, valoriAmmessi) in selezioni)
        {
            if (valoriAmmessi.Count == 0) continue;
            if (!campiCustom.TryGetValue(chiave, out var valore) || !valoriAmmessi.Contains(valore, StringComparer.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>Chiavi dei campi di sistema usati dai template dati: sono i soli campi custom pertinenti a un Test di quei tipi. Stessi tre
    /// elenchi che legge l'editor del Test (campi di pianificazione, sezioni, riferimenti).</summary>
    public static IReadOnlySet<string> ChiaviCampiSistemaDeiTemplate(IEnumerable<TestTemplate> templates) =>
        templates.SelectMany(t => t.CampiPianificazione.Concat(t.Sezioni.SelectMany(s => s.Campi)).Concat(t.CampiRiferimenti))
            .Where(c => c.Tipo == TipoCampoTemplate.CampoSistema && !string.IsNullOrWhiteSpace(c.CampoSistemaChiave))
            .Select(c => c.CampoSistemaChiave!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Chiave leggibile per un campo NUOVO del catalogo, derivata dall'Etichetta invece di un GUID
    /// opaco (Addendum: grill-me 2026-08-18, terza follow-up, punto 6 — "come chiave... è necessario prendere
    /// l'etichetta stessa che dovrà essere univoca"): i campi già esistenti con Chiave a GUID non vengono
    /// toccati (deciso in grilling: "solo i nuovi campi"). Univoca su chiaviEsistenti, che il chiamante deve
    /// popolare con le chiavi di ENTRAMBI i gruppi Pianificazione+Dettagli insieme — condividono lo stesso
    /// dizionario TestCase.CampiCustom, quindi una collisione tra gruppi diversi sovrascriverebbe in silenzio
    /// il valore dell'altro campo.</summary>
    public static string GeneraChiaveDaEtichetta(string etichetta, IEnumerable<string> chiaviEsistenti)
    {
        var esistenti = new HashSet<string>(chiaviEsistenti, StringComparer.OrdinalIgnoreCase);
        var baseSlug = Slugify(etichetta);
        if (baseSlug.Length == 0) baseSlug = "campo";

        var candidata = baseSlug;
        var contatore = 2;
        while (esistenti.Contains(candidata))
        {
            candidata = $"{baseSlug}-{contatore}";
            contatore++;
        }
        return candidata;
    }

    private static string Slugify(string testo)
    {
        var normalizzato = testo.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalizzato)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue; // rimuove accenti (à -> a)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }
}
