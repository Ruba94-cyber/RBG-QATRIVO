using TestLoom.App.Models;

namespace TestLoom.App.Services;

public enum EsitoTransizioneCr
{
    Ok,
    NonAutorizzato,
    StatoNonValido,
    MotivoMancante
}

/// <summary>Regole uniche di chiusura e riapertura di una Change Request. Editor completo e Modifica rapida le
/// usano come mutazione di una COPIA della CR da salvare con il controllo di revisione del repository: se un altro
/// PC ha modificato il record nel frattempo il Save fallisce con conflitto e nulla viene scritto, quindi la
/// marcatura "chiusa senza validazione" (calcolata qui dallo stato del record su cui si basa il Save) non puo'
/// mai riferirsi a dati superati.
/// La chiusura e' sempre possibile (da qualunque stato attivo) con un motivo scritto obbligatorio, ma solo per il
/// Master; se la CR non era Validata viene marcata ChiusaSenzaValidazione.</summary>
public static class ChangeRequestChiusuraService
{
    public static bool PuoChiudere(ChangeRequest cr, bool puoScrivere, bool eMaster) =>
        puoScrivere && eMaster && !cr.Archiviata
        && cr.Stato is not (StatoChangeRequest.Chiusa or StatoChangeRequest.Annullata);

    public static bool PuoRiaprire(ChangeRequest cr, bool puoScrivere, bool eMaster) =>
        puoScrivere && eMaster && !cr.Archiviata && cr.Stato == StatoChangeRequest.Chiusa;

    public static EsitoTransizioneCr Chiudi(ChangeRequest cr, string operatore, string? motivo,
        bool puoScrivere, bool eMaster, DateTime adesso)
    {
        if (!puoScrivere || !eMaster) return EsitoTransizioneCr.NonAutorizzato;
        if (!PuoChiudere(cr, puoScrivere, eMaster)) return EsitoTransizioneCr.StatoNonValido;
        var motivoPulito = (motivo ?? "").Trim();
        if (motivoPulito.Length == 0) return EsitoTransizioneCr.MotivoMancante;

        var precedente = cr.Stato;
        var senzaValidazione = precedente != StatoChangeRequest.Validata;
        cr.Stato = StatoChangeRequest.Chiusa;
        cr.ChiusaIl = adesso;
        cr.ChiusaDa = operatore;
        cr.MotivoChiusura = motivoPulito;
        cr.ChiusaSenzaValidazione = senzaValidazione;
        cr.Changelog.Add(new ChangelogEntry
        {
            Data = adesso,
            Operatore = operatore,
            Campo = "stato",
            ValorePrecedente = precedente.ToString(),
            ValoreNuovo = (senzaValidazione ? "Chiusa (senza validazione): " : "Chiusa: ") + motivoPulito
        });
        return EsitoTransizioneCr.Ok;
    }

    public static EsitoTransizioneCr Riapri(ChangeRequest cr, string operatore, string? motivo,
        bool puoScrivere, bool eMaster, DateTime adesso)
    {
        if (!puoScrivere || !eMaster) return EsitoTransizioneCr.NonAutorizzato;
        if (!PuoRiaprire(cr, puoScrivere, eMaster)) return EsitoTransizioneCr.StatoNonValido;
        var motivoPulito = (motivo ?? "").Trim();
        if (motivoPulito.Length == 0) return EsitoTransizioneCr.MotivoMancante;

        var precedente = cr.ChiusaSenzaValidazione ? "Chiusa (senza validazione)" : "Chiusa";
        AzzeraDatiChiusura(cr);
        cr.Stato = StatoChangeRequest.Aperta;
        cr.Changelog.Add(new ChangelogEntry
        {
            Data = adesso,
            Operatore = operatore,
            Campo = "stato",
            ValorePrecedente = precedente,
            ValoreNuovo = "Aperta: " + motivoPulito
        });
        return EsitoTransizioneCr.Ok;
    }

    /// <summary>Una CR tornata Aperta (riapertura o modifica tecnica dell'ambito) non ha piu' una chiusura da
    /// descrivere: i dati restano nel Changelog, non nei campi correnti.</summary>
    public static void AzzeraDatiChiusura(ChangeRequest cr)
    {
        cr.ChiusaIl = null;
        cr.ChiusaDa = "";
        cr.MotivoChiusura = "";
        cr.ChiusaSenzaValidazione = false;
    }
}
