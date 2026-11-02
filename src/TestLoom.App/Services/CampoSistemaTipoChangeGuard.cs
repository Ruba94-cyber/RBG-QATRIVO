using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Blocca il cambio di Tipo di un CampoSistemaDefinizione se esistono già dati salvati (Addendum:
/// grill-me 2026-08-18 — "blocca se ci sono dati esistenti", protezione dell'integrità storica scelta
/// esplicitamente invece di una conversione best-effort). Va richiamato dall'editor del catalogo prima di
/// accettare una modifica al Tipo.</summary>
public static class CampoSistemaTipoChangeGuard
{
    public static bool HaDatiEsistenti(CampoSistemaDefinizione definizione, IReadOnlyList<TestCase> testCases) =>
        definizione.Ruolo switch
        {
            RuoloCampoSistema.Componente => testCases.Any(tc => tc.Componente.Count > 0),
            RuoloCampoSistema.Veicolo => testCases.Any(tc => !string.IsNullOrWhiteSpace(tc.Veicolo)),
            RuoloCampoSistema.Funzionalita => testCases.Any(tc => tc.Funzionalita.Count > 0),
            RuoloCampoSistema.StrumentiUsati => testCases.Any(tc => tc.StrumentiUsati.Count > 0),
            RuoloCampoSistema.StatoLavoro => testCases.Any(tc => tc.StatoLavoro != StatoLavoroTest.Pianificato),
            RuoloCampoSistema.Scadenza => testCases.Any(tc => tc.Scadenza.HasValue),
            RuoloCampoSistema.Assegnatario => testCases.Any(tc => !string.IsNullOrWhiteSpace(tc.Assegnatario)),
            _ => testCases.Any(tc => tc.CampiCustom.TryGetValue(definizione.Chiave, out var v) && !string.IsNullOrWhiteSpace(v)),
        };
}
