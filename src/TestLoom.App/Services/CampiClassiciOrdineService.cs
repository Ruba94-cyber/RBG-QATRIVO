using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Riordino dei campi "classici" (di sistema) del modulo Test reale (TestCaseEditorView): a
/// differenza dei campi custom/liberi, questi campi vivevano in una posizione fissa nella UI indipendente da
/// CampoTemplate.Ordine fino alla 6.3.1 (vedi CampoOrdinamentoService, nota F1 .ai/artifacts/review_riordino.json
/// — "posizione fissa... solo editor/anteprima/export la rispettano"). Da qui in poi anche il form reale legge
/// Ordine per il gruppo Dettagli (Veicolo/Componente/Matricola/BaselineSw/BaselineHw) e per Pianificazione (Stato lavoro/
/// Scadenza/Assegnatario/Assegnato da) — non ancora Riferimenti né gli altri campi di Dettagli (Funzionalità/
/// Strumenti usati/Descrizioni), che restano nella posizione fissa preesistente: vivono in blocchi di UI
/// strutturalmente diversi (card intere, aree di testo a piena larghezza) dalla griglia compatta di questi
/// campi, non nella stessa griglia — candidati per un'estensione futura, non affrontata qui per restare nel
/// perimetro della spec 2026-09-23 §2 (vedi RELEASE_NOTES_6.3.1.md, sezione "Riordino dei campi classici del
/// Test e degli Strumenti", "Limiti noti").</summary>
public static class CampiClassiciOrdineService
{
    /// <summary>Calcola, per ciascuna delle chiavi in <paramref name="chiaviDefault"/>, la sua posizione
    /// (rango 0-based) nell'ordine di visualizzazione: se <paramref name="usaOrdineTemplate"/> è falso
    /// (gruppo non ancora migrato per questo template, vedi TestTemplate.CampiClassiciDettagliRiordinati) usa
    /// l'ordine fisso preesistente (l'ordine di <paramref name="chiaviDefault"/> stesso — nessun cambio
    /// visibile finché l'utente non riordina esplicitamente); altrimenti ordina per Ordine del CampoTemplate
    /// corrispondente (<paramref name="ordineDi"/>), stabile a parità, con le chiavi senza campo trovato
    /// (template incompleto/dati di test) in fondo nell'ordine di default invece di sollevare un'eccezione.
    /// Pura: non muta nulla, sicura da richiamare ad ogni ridisegno.</summary>
    public static IReadOnlyDictionary<string, int> CalcolaRango(IReadOnlyList<string> chiaviDefault, Func<string, int?> ordineDi, bool usaOrdineTemplate)
    {
        List<string> visuale;
        if (!usaOrdineTemplate)
        {
            visuale = chiaviDefault.ToList();
        }
        else
        {
            var conOrdine = chiaviDefault.Select(k => (Chiave: k, Ordine: ordineDi(k))).ToList();
            visuale = conOrdine.Where(x => x.Ordine.HasValue)
                .OrderBy(x => x.Ordine!.Value)
                .Select(x => x.Chiave)
                .ToList();
            foreach (var k in chiaviDefault)
                if (!visuale.Contains(k)) visuale.Add(k);
        }

        var risultato = new Dictionary<string, int>();
        for (var i = 0; i < visuale.Count; i++) risultato[visuale[i]] = i;
        return risultato;
    }

    /// <summary>Correzione una tantum, idempotente: nei template esistenti (creati prima della 6.3.1) "Veicolo"
    /// ha sempre Ordine maggiore di "Componente" nel modello (TestTemplateDefaults.SezioneDettagliStandard semina Componente
    /// con Ordine inferiore, segue CampiSistema.Chiavi), anche se il form reale li ha sempre mostrati
    /// nell'ordine opposto (feedback utente 2026-08-26, commento "Veicolo prima di componente" in
    /// TestCaseEditorView.axaml — solo l'ordine visivo cambiò allora, mai il modello). Ora che il form rispetta
    /// davvero Ordine per questo gruppo, senza questa correzione ogni template esistente vedrebbe Componente e Veicolo
    /// scambiati al primo avvio della 6.3.1: una regressione visibile non voluta. Applicata solo alla prima
    /// azione di riordino esplicita di un Master su uno dei campi del gruppo Dettagli nell'editor del template
    /// (mai alla sola apertura di un editor o del modulo Test — stesso principio di CampoOrdinamentoService,
    /// vedi nota di classe lì). Nessun effetto se il template ha già questo flag (già corretto in precedenza, o
    /// Veicolo è già prima di Componente per qualunque altro motivo).</summary>
    public static void CorreggiCompatibilitaVeicoloEcu(TestTemplate template)
    {
        if (template.CampiClassiciDettagliRiordinati) return;
        foreach (var sezione in template.Sezioni)
        {
            var veicolo = sezione.Campi.FirstOrDefault(c => c.Tipo == TipoCampoTemplate.CampoSistema && c.CampoSistemaChiave == CampiSistema.Veicolo);
            var componente = sezione.Campi.FirstOrDefault(c => c.Tipo == TipoCampoTemplate.CampoSistema && c.CampoSistemaChiave == CampiSistema.Componente);
            if (veicolo != null && componente != null && componente.Ordine < veicolo.Ordine)
                (veicolo.Ordine, componente.Ordine) = (componente.Ordine, veicolo.Ordine);
        }
        template.CampiClassiciDettagliRiordinati = true;
    }
}
