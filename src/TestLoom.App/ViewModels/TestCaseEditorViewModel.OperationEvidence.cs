using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Evidenze collegate alla singola operazione (Condizione) di una REV. Il file resta un normale allegato del Test
/// (stesso AttachmentService, limiti, rimozione differita, bozze e recupero); il collegamento e' l'Uid dell'allegato
/// salvato nella chiave riservata della Condizione (vedi <see cref="EvidenzaOperazione"/>). Nessuna modifica allo schema.
/// </summary>
public partial class TestCaseEditorViewModel
{
    /// <summary>Operazione modificabile: stessa regola degli esiti rapidi (REV corrente, editor in modifica, non storico).</summary>
    private bool OperazioneEvidenzeModificabile(Condizione? operazione) =>
        operazione is not null && PuoModificareContenuto && !TestRichiedeEcu &&
        RevisioneCorrente?.Condizioni.Any(c => ReferenceEquals(c, operazione)) == true;

    /// <summary>Salva il file come allegato del Test e lo collega all'operazione indicata. Se il collegamento non e'
    /// possibile (operazione non della REV corrente, sola lettura, limite) il file non viene aggiunto.</summary>
    public async Task AggiungiEvidenzaOperazioneSicura(Condizione? operazione, string? percorsoFile)
    {
        if (!OperazioneEvidenzeModificabile(operazione) || string.IsNullOrEmpty(percorsoFile)) return;
        if (EvidenzaOperazione.Leggi(operazione!.ValoriStrumenti).Count >= EvidenzaOperazione.MassimoPerOperazione)
        {
            ErroriValidazione = new List<string> { LocalizationService.Instance.Traduci("EvidenzaOperazioneLimite") };
            return;
        }
        var allegato = await AggiungiAllegatoSicuroCore(percorsoFile);
        if (allegato is null) return;
        // L'await puo' aver cambiato REV o sostituito il Test (rollback): il file resta allegato del Test, ma non si scrive su un'operazione non piu' valida.
        if (!OperazioneEvidenzeModificabile(operazione))
        {
            ErroriValidazione = new List<string> { LocalizationService.Instance.Traduci("EvidenzaOperazioneNonCollegata") };
            return;
        }
        operazione.ValoriStrumenti ??= new();
        if (!EvidenzaOperazione.Aggiungi(operazione.ValoriStrumenti, allegato.Uid))
            ErroriValidazione = new List<string> { LocalizationService.Instance.Traduci("EvidenzaOperazioneLimite") };
        AggiornaEvidenzeOperazioni();
        RinfrescaAllegatoVisuale(allegato);
        ModificheNonSalvate = true;
    }

    /// <summary>Riusa un allegato gia' presente nel Test come evidenza di un'altra operazione (stesso file, nessuna copia).</summary>
    public bool CollegaEvidenzaEsistente(Condizione? operazione, Allegato? allegato)
    {
        if (!OperazioneEvidenzeModificabile(operazione) || allegato is null || allegato.Archiviata ||
            !TestCase.Allegati.Any(a => a.Uid == allegato.Uid)) return false;
        operazione!.ValoriStrumenti ??= new();
        if (!EvidenzaOperazione.Aggiungi(operazione.ValoriStrumenti, allegato.Uid)) return false;
        AggiornaEvidenzeOperazioni();
        RinfrescaAllegatoVisuale(allegato);
        ModificheNonSalvate = true;
        return true;
    }

    /// <summary>Toglie il collegamento tra l'allegato e le operazioni della REV corrente. Il file resta tra gli allegati del Test.</summary>
    [RelayCommand]
    private void ScollegaEvidenzaOperazione(Allegato? allegato)
    {
        if (allegato is null || !PuoModificareContenuto || RevisioneCorrente is null) return;
        var cambiato = false;
        foreach (var c in RevisioneCorrente.Condizioni)
            if (c.ValoriStrumenti is not null && EvidenzaOperazione.Rimuovi(c.ValoriStrumenti, allegato.Uid)) cambiato = true;
        if (!cambiato) return;
        AggiornaEvidenzeOperazioni();
        RinfrescaAllegatoVisuale(allegato);
        ModificheNonSalvate = true;
    }

    /// <summary>Allegati del Test collegati all'operazione nella sua REV (esclusi quelli archiviati o non piu' presenti).</summary>
    public IReadOnlyList<Allegato> EvidenzeDiOperazione(Condizione? operazione)
    {
        if (operazione?.ValoriStrumenti is null) return Array.Empty<Allegato>();
        return EvidenzaOperazione.Leggi(operazione.ValoriStrumenti)
            .Select(u => TestCase.Allegati.FirstOrDefault(a => a.Uid == u && !a.Archiviata))
            .OfType<Allegato>().ToList();
    }

    /// <summary>Rimuove da ogni REV i collegamenti verso un allegato eliminato dal Test: nessun riferimento orfano.</summary>
    private void RimuoviCollegamentiEvidenzaAllegato(Allegato allegato)
    {
        foreach (var esecuzione in TestCase.Esecuzioni.Concat(Esecuzioni).Distinct())
            foreach (var c in esecuzione.Condizioni)
                if (c.ValoriStrumenti is not null) EvidenzaOperazione.Rimuovi(c.ValoriStrumenti, allegato.Uid);
    }

    /// <summary>Ricalcola conteggi ed etichette. Dato derivato, mai persistito: si rigenera da Allegati + chiavi riservate.</summary>
    internal void AggiornaEvidenzeOperazioni()
    {
        var presenti = TestCase.Allegati.Where(a => !a.Archiviata).ToDictionary(a => a.Uid, a => a);
        foreach (var a in TestCase.Allegati) a.EtichettaOperazione = "";
        foreach (var esecuzione in Esecuzioni.Count > 0 ? Esecuzioni.ToList() : TestCase.Esecuzioni)
            foreach (var c in esecuzione.Condizioni)
            {
                var collegati = EvidenzaOperazione.Leggi(c.ValoriStrumenti).Where(presenti.ContainsKey).ToList();
                c.ImpostaNumeroEvidenze(collegati.Count);
                if (collegati.Count == 0) continue;
                var nome = string.IsNullOrWhiteSpace(c.Funzionalita) ? c.Uid.ToString("N")[..6] : c.Funzionalita;
                var etichetta = $"REV {esecuzione.Iterazione} · {nome}";
                foreach (var u in collegati)
                    presenti[u].EtichettaOperazione = presenti[u].EtichettaOperazione.Length == 0
                        ? etichetta : presenti[u].EtichettaOperazione + "; " + etichetta;
            }
    }

    /// <summary>L'elenco Allegati non osserva le proprieta' dei suoi elementi: si rimpiazza la riga per mostrare l'etichetta aggiornata.</summary>
    private void RinfrescaAllegatoVisuale(Allegato allegato)
    {
        var indice = AllegatiVisual.IndexOf(allegato);
        if (indice < 0) return;
        AllegatiVisual.RemoveAt(indice);
        AllegatiVisual.Insert(indice, allegato);
    }
}
