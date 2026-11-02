using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Cache statica Uid->Nome dei TestTemplate, aggiornata a ogni RicaricaTutto (Addendum redesign
/// 2026-08, punto 2): serve ESCLUSIVAMENTE a TestTemplateLabelConverter (XAML), il solo punto che non ha
/// accesso diretto ad AppState — stesso principio già usato per LocalizationService.Instance, un singleton
/// letto direttamente da XAML/converter invece di passare il dato attraverso ogni riga della griglia.
///
/// Punto 0 del piano (2026-09-18): essendo un campo statico globale, due AppState indipendenti (più
/// workspace nello stesso processo, oggi solo in test paralleli — in produzione esiste sempre e solo un
/// AppState per processo) si sovrascrivono a vicenda. Prima di questo intervento mancava un'asserzione
/// diretta sul risultato di NomeDi: la relazione con gli altri fallimenti sporadici della suite completa
/// resta non dimostrata. La rilettura di AppState.RicaricaTutto()
/// da un thread mentre un altro test legge NomeDi resta una race dimostrabile (vedi
/// TestTemplateDisplayRaceTests). Per questo tutti gli altri punti di lettura (ViewModel e servizi export/
/// report, che hanno già un riferimento AppState/lista Master.TestTemplate a disposizione) sono stati spostati
/// su AppState.NomeTestTemplate, scoped alla propria istanza: qui resta solo l'unico caso realmente privo di
/// alternativa.</summary>
public static class TestTemplateDisplay
{
    private static Dictionary<Guid, string> _nomi = new();

    public static void Aggiorna(IEnumerable<TestTemplate> templates) =>
        _nomi = templates.ToDictionary(t => t.Uid, t => t.Nome);

    public static string NomeDi(Guid uid) => _nomi.TryGetValue(uid, out var nome) ? nome : "?";
}
