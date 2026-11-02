using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Risolve un Ruolo funzionale nel campo catalogo che lo porta oggi (Addendum: grill-me 2026-08-18,
/// punto 1): mai lancia — un ruolo senza campo assegnato è uno stato valido (il Master ha rinominato/rimosso
/// quel campo dal catalogo), e ogni chiamante deve degradare la propria funzionalità invece di rompersi. Se
/// più di un campo dichiara lo stesso ruolo (stato incoerente), prende il primo e ignora gli altri piuttosto
/// che lanciare.</summary>
public static class RuoloCampoSistemaLookup
{
    public static CampoSistemaDefinizione? PerRuolo(IReadOnlyList<CampoSistemaDefinizione> catalogo, RuoloCampoSistema ruolo) =>
        ruolo == RuoloCampoSistema.Nessuno ? null : catalogo.FirstOrDefault(c => c.Ruolo == ruolo && !c.Archiviata);

    public static string? ChiaveDelRuolo(IReadOnlyList<CampoSistemaDefinizione> catalogo, RuoloCampoSistema ruolo) =>
        PerRuolo(catalogo, ruolo)?.Chiave;

    /// <summary>Etichetta da mostrare per un ruolo, con fallback all'etichetta storica se nessun campo del
    /// catalogo porta quel ruolo o la sua Etichetta è vuota (Master che ha svuotato il campo per errore).</summary>
    public static string EtichettaDelRuolo(IReadOnlyList<CampoSistemaDefinizione> catalogo, RuoloCampoSistema ruolo, string fallback)
    {
        var etichetta = PerRuolo(catalogo, ruolo)?.Etichetta;
        return string.IsNullOrWhiteSpace(etichetta) ? fallback : etichetta;
    }

    /// <summary>Come <see cref="EtichettaDelRuolo"/>, ma distingue una vera personalizzazione dal valore
    /// italiano creato automaticamente dal seed. Il valore seed non è una scelta del Master e deve quindi
    /// seguire la lingua dell'interfaccia; una qualunque etichetta rinominata resta invece invariata.</summary>
    public static string EtichettaLocalizzataDelRuolo(
        IReadOnlyList<CampoSistemaDefinizione> catalogo,
        RuoloCampoSistema ruolo,
        string fallbackLocalizzato)
    {
        var campo = PerRuolo(catalogo, ruolo);
        if (string.IsNullOrWhiteSpace(campo?.Etichetta)) return fallbackLocalizzato;

        var etichettaSeed = CampiSistema.LabelDefault(campo.Chiave);
        return string.Equals(campo.Etichetta, etichettaSeed, StringComparison.Ordinal)
            ? fallbackLocalizzato
            : campo.Etichetta;
    }
}
