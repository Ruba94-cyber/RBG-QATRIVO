using System.Text.RegularExpressions;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Azioni sulle anomalie condivise dalla pagina Anomalie, dalla pagina Test e dal dialogo di dettaglio
/// (<c>AnomaliaDettaglioViewModel</c>): un solo punto per la validazione dei ticket TICKET e per i percorsi
/// fail-closed di Riapri/Elimina, cosi' le due pagine non divergono.
/// </summary>
public static class AnomaliaAzioniService
{
    /// <summary>Verifica e traccia una correzione della data effettiva di chiusura su una copia da salvare.</summary>
    public static bool RegistraCorrezioneData(NonConformita candidato, NonConformita? precedente, string operatore)
    {
        if (candidato.StatoWorkflow != StatoAnomalia.Conclusa) return true;
        if (precedente != null && candidato.DataChiusura == precedente.DataChiusura) return true;
        if (!candidato.DataChiusura.HasValue || candidato.DataChiusura.Value < candidato.DataApertura) return false;
        if (precedente != null && candidato.DataChiusura != precedente.DataChiusura)
            candidato.Changelog.Add(new ChangelogEntry { Data = DateTime.Now, Operatore = operatore, Campo = "Data chiusura",
                ValorePrecedente = precedente.DataChiusura?.ToString("O") ?? "", ValoreNuovo = candidato.DataChiusura.Value.ToString("O") });
        return true;
    }
    private static readonly Regex FormatoTicket = new("^TICKET-[A-Z0-9_-]+$", RegexOptions.Compiled);

    /// <summary>Aggiunge a <paramref name="nc"/> un ticket TICKET digitato ma non ancora collegato. Campo vuoto =
    /// niente da fare (true, nessun errore). Ritorna false con la chiave di localizzazione dell'errore se il
    /// formato o il link non sono validi; in quel caso <paramref name="nc"/> non viene modificata.</summary>
    public static bool TentaAggiungiTicket(NonConformita nc, string? riferimento, string? url, string operatore,
        out string? chiaveErrore, out bool aggiunto, DataPaths? paths = null)
    {
        chiaveErrore = null;
        aggiunto = false;
        if (string.IsNullOrWhiteSpace(riferimento)) return true;
        var codice = riferimento.Trim().ToUpperInvariant();
        if (!FormatoTicket.IsMatch(codice)) { chiaveErrore = "FormatoPolarionNonValido"; return false; }
        if (!string.IsNullOrWhiteSpace(url) && !Uri.TryCreate(url, UriKind.Absolute, out _)) { chiaveErrore = "LinkPolarionNonValido"; return false; }
        nc.CollegamentiPolarion.Add(new CollegamentoPolarion { Codice = codice, Url = string.IsNullOrWhiteSpace(url) ? PolarionTicketLinkService.DerivaUrl(paths, codice) : url.Trim(), CollegatoDa = operatore });
        aggiunto = true;
        return true;
    }

    /// <summary>Riporta l'anomalia allo stato aperto salvando una COPIA (mai l'istanza passata): un errore di
    /// revisione/I/O non muta quindi lo stato visualizzato.</summary>
    public static void Riapri(AppState state, NonConformita nc, string? motivo)
    {
        // Ricontrollo nel servizio: l'abilitazione dei pulsanti non e' una barriera sufficiente.
        if (!state.PuoScrivere) throw new UnauthorizedAccessException(LocalizationService.Instance.Traduci("AnomaliaSolaLettura"));
        var motivoRiapertura = (motivo ?? "").Trim();
        if (motivoRiapertura.Length == 0) throw new ArgumentException(LocalizationService.Instance.Traduci("RiaperturaMotivoObbl"), nameof(motivo));
        var revisioneAttesa = nc.RevisioneDati;
        var candidato = EntityStateFingerprint.Clone(nc);
        var motivoPrecedente = candidato.MotivoChiusura;
        NonConformitaRulesService.Riapri(candidato);
        candidato.Changelog.Add(new ChangelogEntry
        {
            Data = DateTime.Now, Operatore = state.Config.OperatoreAttivo, Campo = "Riapertura",
            ValorePrecedente = string.IsNullOrWhiteSpace(motivoPrecedente) ? nameof(StatoAnomalia.Conclusa) : $"{nameof(StatoAnomalia.Conclusa)}: {motivoPrecedente}",
            ValoreNuovo = $"{nameof(StatoAnomalia.Confermata)}: {motivoRiapertura}"
        });
        state.NonConformita.Save(candidato, revisioneAttesa);
    }

    /// <summary>Applica a una COPIA della NC le regole di chiusura (stato Conclusa, data, motivo) e scrive la voce di
    /// storico: unico punto per la chiusura semplice, usato dal dettaglio e dall'editor della segnalazione di rete.</summary>
    public static void ApplicaChiusura(NonConformita candidato, string operatore, string motivo)
    {
        var statoPrecedente = candidato.StatoWorkflow;
        NonConformitaRulesService.Chiudi(candidato);
        candidato.MotivoChiusura = motivo;
        candidato.Changelog.Add(new ChangelogEntry
        {
            Data = DateTime.Now, Operatore = operatore, Campo = "Chiusura",
            ValorePrecedente = statoPrecedente.ToString(), ValoreNuovo = $"{nameof(StatoAnomalia.Conclusa)}: {motivo}"
        });
    }

    /// <summary>Chiusura semplice di una NC (senza aggiornamento dei test collegati): motivo obbligatorio, severita
    /// obbligatoria se richiesta, permesso ricontrollato qui e salvataggio con controllo di revisione.</summary>
    public static void Chiudi(AppState state, NonConformita nc, string? motivo, bool severitaRichiesta)
    {
        if (!state.PuoScrivere) throw new UnauthorizedAccessException(LocalizationService.Instance.Traduci("AnomaliaSolaLettura"));
        var motivoPulito = (motivo ?? "").Trim();
        if (motivoPulito.Length == 0) throw new ArgumentException(LocalizationService.Instance.Traduci("ChiudereAnomaliaMotivo"), nameof(motivo));
        if (severitaRichiesta && !nc.Severita.HasValue) throw new InvalidOperationException(LocalizationService.Instance.Traduci("SeveritaObbl"));
        var revisioneAttesa = nc.RevisioneDati;
        var candidato = EntityStateFingerprint.Clone(nc);
        ApplicaChiusura(candidato, state.Config.OperatoreAttivo, motivoPulito);
        state.NonConformita.Save(candidato, revisioneAttesa);
    }

    /// <summary>
    /// Eliminazione definitiva (recuperabile via DomainRecoveryService) con gli stessi guard del repository.
    /// Le segnalazioni classificate in questa NC tornano 'DaClassificare' nella STESSA unita' di lavoro: un
    /// errore a meta' fa rollback di tutti i file toccati, quindi nessuna segnalazione punta a una NC
    /// inesistente. In caso di errore ricarica gli indici (il rollback puo' lasciare le cache avanzate) e
    /// rilancia.
    /// </summary>
    public static void Elimina(AppState state, NonConformita nc)
    {
        var candidato = EntityStateFingerprint.Clone(nc);
        try
        {
            using var tx = WorkflowTransactionService.Begin(state.Paths, $"delete-nc:{candidato.Uid:N}");
            var collegate = state.Segnalazioni.GetAll().Where(s => s.AnomaliaUid == candidato.Uid && s.Stato == StatoSegnalazione.Classificata).ToList();
            foreach (var s in collegate)
            {
                var attuale = state.Segnalazioni.Get(s.Uid) ?? s;
                var revisioneAttesa = attuale.RevisioneDati;
                var scollegata = EntityStateFingerprint.Clone(attuale);
                scollegata.Stato = StatoSegnalazione.DaClassificare;
                scollegata.AnomaliaUid = null;
                state.Segnalazioni.Save(scollegata, revisioneAttesa);
            }
            state.NonConformita.Elimina(candidato, state.Config.OperatoreAttivo);
            tx.Commit();
        }
        catch
        {
            state.NonConformita.RefreshIndex();
            state.Segnalazioni.RefreshIndex();
            throw;
        }
    }
}
