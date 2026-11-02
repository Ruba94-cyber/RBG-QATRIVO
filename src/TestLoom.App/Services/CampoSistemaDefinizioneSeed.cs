using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Seed una tantum del catalogo campi di sistema (Addendum: grill-me 2026-08-18, punto 1): dà una
/// voce di catalogo a ciascuna delle chiavi finora fisse in CampiSistema, preservando la Chiave esistente
/// (i dati già salvati sui TestCase restano leggibili) e assegnando il Ruolo alle 8 chiavi da cui dipende
/// oggi codice non-di-presentazione (Coverage/filtri/dedup/import).</summary>
public static class CampoSistemaDefinizioneSeed
{
    private static readonly Dictionary<string, RuoloCampoSistema> Ruoli = new()
    {
        [CampiSistema.Componente] = RuoloCampoSistema.Componente,
        [CampiSistema.Veicolo] = RuoloCampoSistema.Veicolo,
        [CampiSistema.Funzionalita] = RuoloCampoSistema.Funzionalita,
        [CampiSistema.StrumentiUsati] = RuoloCampoSistema.StrumentiUsati,
        [CampiSistema.StatoLavoro] = RuoloCampoSistema.StatoLavoro,
        [CampiSistema.Scadenza] = RuoloCampoSistema.Scadenza,
        [CampiSistema.Assegnatario] = RuoloCampoSistema.Assegnatario,
    };

    /// <summary>Seed del catalogo "Campi Pianificazione" (sezione Impostazioni dedicata) — SOLO le chiavi di
    /// CampiSistema.ChiaviPianificazione, un file separato da Seed Dettagli così i due editor di Impostazioni
    /// possono riusare MasterDataEditorViewModelBase&lt;T&gt; senza bisogno di filtrare Items per Gruppo.</summary>
    public static List<CampoSistemaDefinizione> SeedPianificazione() =>
        CampiSistema.ChiaviPianificazione.Select(chiave => Voce(chiave, GruppoCampoSistema.Pianificazione)).ToList();

    /// <summary>Seed del catalogo "Campi Dettagli": le chiavi di CampiSistema.Chiavi più Gamma/Categoria, che
    /// non sono campi di TestCase (vivono su Veicolo/Funzionalita, non compaiono in CampiSistema.Chiavi e
    /// quindi mai nel picker "+ Aggiungi campo" del form test) ma condividono lo stesso meccanismo di
    /// Etichetta rinominabile per l'intestazione dei loro pannelli annidati — non hanno una sezione propria
    /// che valga la pena creare solo per due righe, quindi vivono qui insieme agli altri campi Dettagli.</summary>
    public static List<CampoSistemaDefinizione> SeedDettagli()
    {
        var voci = CampiSistema.Chiavi.Select(chiave => Voce(chiave, GruppoCampoSistema.Dettagli)).ToList();
        voci.Add(new CampoSistemaDefinizione { Chiave = "Gamma", Etichetta = "Gamma", Gruppo = GruppoCampoSistema.Dettagli, Ruolo = RuoloCampoSistema.Gamma, UltimaModifica = DateTime.Now, ModificatoDa = "Setup" });
        voci.Add(new CampoSistemaDefinizione { Chiave = "Categoria", Etichetta = "Categoria", Gruppo = GruppoCampoSistema.Dettagli, Ruolo = RuoloCampoSistema.Categoria, UltimaModifica = DateTime.Now, ModificatoDa = "Setup" });
        return voci;
    }

    private static CampoSistemaDefinizione Voce(string chiave, GruppoCampoSistema gruppo) => new()
    {
        Chiave = chiave,
        Etichetta = CampiSistema.LabelDefault(chiave),
        Gruppo = gruppo,
        Tipo = chiave == CampiSistema.Scadenza ? TipoCampoStrumento.Data : TipoCampoStrumento.Testo,
        Ruolo = Ruoli.GetValueOrDefault(chiave, RuoloCampoSistema.Nessuno),
        UltimaModifica = DateTime.Now,
        ModificatoDa = "Setup",
    };
}
