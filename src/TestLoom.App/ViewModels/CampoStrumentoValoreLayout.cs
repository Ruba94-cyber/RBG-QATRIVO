using TestLoom.App.Models;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Divide i campi di uno strumento con un determinato Ambito tra quelli diretti (Gruppo vuoto, sempre
/// visibili) e quelli raggruppati (stesso Gruppo, dietro un pannello a tendina collassato con quel titolo).
/// Estratta da TestCaseEditorViewModel.SpaccaPerGruppo (feedback utente 2026-09-18: la stessa logica di
/// rendering dei campi va condivisa fra Test e Segnalazione di rete — "il più simile possibile
/// all'inserimento presente nei test") così i due editor non hanno due implementazioni che possono divergere
/// nel tempo. Pura: non dipende da AppState, solo dai default già risolti dal chiamante.
/// </summary>
public static class CampoStrumentoValoreLayout
{
    public static (List<CampoStrumentoValore> Diretti, List<GruppoCampiStrumento> Gruppi) SpaccaPerGruppo(
        IEnumerable<StrumentoDiagnostico> strumenti, Dictionary<string, string> valori, TipoAmbitoCampo ambito,
        IReadOnlyDictionary<string, string>? defaultsOperatore, bool materializzaDefault = true)
    {
        var diretti = new List<CampoStrumentoValore>();
        var gruppi = new Dictionary<string, GruppoCampiStrumento>();
        foreach (var strumento in strumenti)
        {
            // OrderBy(Ordine) (revisione F1 .ai/artifacts/review_riordino.json): rende Ordine autoritativo
            // anche per il form REALE del Test/Segnalazione di rete, non solo per l'editor e l'anteprima —
            // stabile a parità di Ordine (dati esistenti, tutti a 0 finché nessuno riordina esplicitamente),
            // quindi nessun cambio visibile finché un utente non usa i pulsanti ▲/▼.
            foreach (var def in strumento.Campi.OrderBy(d => d.Ordine).Where(d => d.Ambito == ambito))
            {
                var chiave = $"{strumento.Nome}::{def.Nome}";
                var mioDefault = defaultsOperatore != null && defaultsOperatore.TryGetValue(chiave, out var v) ? v : null;
                var campo = new CampoStrumentoValore(valori, strumento.Nome, def, mioDefault, materializzaDefault);
                if (string.IsNullOrWhiteSpace(def.Gruppo)) { diretti.Add(campo); continue; }
                if (!gruppi.TryGetValue(def.Gruppo, out var gruppo))
                    gruppi[def.Gruppo] = gruppo = new GruppoCampiStrumento { Nome = def.Gruppo };
                gruppo.Campi.Add(campo);
            }
        }
        return (diretti, gruppi.Values.ToList());
    }
}
