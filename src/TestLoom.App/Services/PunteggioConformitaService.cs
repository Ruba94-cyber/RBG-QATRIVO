using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Calcola il punteggio 0-5/N.A. per l'audit di conformità e lo mantiene sincronizzato con i
/// Requisiti collegati a un'Esecuzione — stesso principio di Esecuzione.SincronizzaEsito per le Condizioni,
/// ma per un dato puramente additivo che non influenza mai Esito/NonConformitaRulesService.</summary>
public static class PunteggioConformitaService
{
    public static PunteggioConformita? DaEsito(Esito esito) => esito switch
    {
        Esito.Conforme => PunteggioConformita.Conforme,
        Esito.ConformeConDeviazioni => PunteggioConformita.ModeratamenteConforme,
        Esito.NonConforme => PunteggioConformita.NonConforme,
        Esito.NonApplicabile => PunteggioConformita.NonApplicabile,
        _ => null, // DaRivedere, NonEseguitoNA: nessun automatico, va inserito a mano
    };

    public static void SincronizzaPunteggi(Esecuzione esecuzione, IReadOnlyList<string> requisitiCollegati)
    {
        esecuzione.PunteggiRequisiti.RemoveAll(p => !requisitiCollegati.Contains(p.RequisitoId));

        var automatico = DaEsito(esecuzione.Esito);
        foreach (var requisitoId in requisitiCollegati)
        {
            var voce = esecuzione.PunteggiRequisiti.FirstOrDefault(p => p.RequisitoId == requisitoId);
            if (voce == null)
            {
                if (automatico == null) continue; // resta assente finché non inserita a mano
                esecuzione.PunteggiRequisiti.Add(new PunteggioRequisito { RequisitoId = requisitoId, Valore = automatico.Value });
            }
            else if (!voce.Sovrascritto)
            {
                // Senza esito automatico (DaRivedere, NonEseguito/N.A.) la voce automatica non conserva il valore precedente.
                if (automatico != null) voce.Valore = automatico.Value;
                else esecuzione.PunteggiRequisiti.Remove(voce);
            }
        }
    }
}
