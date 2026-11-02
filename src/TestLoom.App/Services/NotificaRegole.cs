namespace TestLoom.App.Services;

/// <summary>Regole di business delle notifiche (feedback utente 6.5.0, punto 6): non notificare a una persona
/// un'ASSEGNAZIONE che ha appena fatto lei stessa. Si applica solo all'evento di assegnazione del Test (l'unica
/// entita' con assegnatario e notifica di assegnazione: Anomalie/NC e Pacchetto non hanno assegnatario). Le
/// notifiche di stato/aggiornamento delle Change Request NON sono toccate: l'autore puo' essere destinatario di
/// un'azione richiesta. L'autore e' l'operatore che compie l'azione (Config.OperatoreAttivo): e'
/// il dato affidabile gia' disponibile in ogni punto di salvataggio, a differenza di TestCase.CreatoDa che
/// resta vuoto per i test creati prima della sua introduzione. Autore sconosciuto = si notifica (comportamento sicuro).</summary>
public static class NotificaRegole
{
    public static bool DeveNotificareAssegnazione(string? autore, string? assegnatario)
    {
        if (string.IsNullOrWhiteSpace(assegnatario)) return false;
        if (string.IsNullOrWhiteSpace(autore)) return true;
        return !string.Equals(autore.Trim(), assegnatario.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
