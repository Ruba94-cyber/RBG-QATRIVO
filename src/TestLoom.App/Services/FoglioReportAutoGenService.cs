using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Genera automaticamente un foglio report di default per un template appena creato (feedback utente
/// 2026-08-20, punto 23): supera la scelta del 17/08 di richiedere sempre una configurazione manuale — un
/// Master può ancora personalizzare/riordinare le colonne dopo, ma non parte più da un foglio vuoto. Una
/// colonna per ogni campo di Pianificazione + Dettagli, nell'ordine in cui compaiono nel template. Non tocca
/// template che hanno già almeno un foglio configurato (manualmente o da una generazione precedente) — non
/// deve sovrascrivere personalizzazioni già fatte.</summary>
public static class FoglioReportAutoGenService
{
    public static void GeneraSeAssente(TestTemplate template)
    {
        if (template.FogliReport.Count > 0) return;

        var colonne = new List<ColonnaReportTemplate>();
        var ordine = 0;
        void Aggiungi(CampoTemplate campo)
        {
            colonne.Add(new ColonnaReportTemplate
            {
                Ordine = ordine++,
                Intestazione = campo.Nome,
                Origine = campo.Tipo == TipoCampoTemplate.CampoSistema ? OrigineColonnaReport.CampoSistema : OrigineColonnaReport.CampoTemplate,
                Chiave = campo.Tipo == TipoCampoTemplate.CampoSistema ? (campo.CampoSistemaChiave ?? "") : campo.Uid.ToString(),
            });
        }

        foreach (var campo in template.CampiPianificazione.OrderBy(c => c.Ordine)) Aggiungi(campo);
        foreach (var sezione in template.Sezioni.OrderBy(s => s.Ordine))
            foreach (var campo in sezione.Campi.OrderBy(c => c.Ordine)) Aggiungi(campo);

        if (colonne.Count == 0) return; // niente da mettere in un foglio: template ancora senza campi

        template.FogliReport.Add(new FoglioReportTemplate
        {
            Ordine = 0,
            NomeFoglio = template.Nome.Length > 0 ? template.Nome : "Report",
            Colonne = colonne,
        });
    }
}
