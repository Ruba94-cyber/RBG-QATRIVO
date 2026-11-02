using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Flusso di lavoro della Rapida sull'elenco operazioni: filtro "solo da eseguire", salto alla prossima operazione da eseguire,
/// "segna le restanti OK" (con conferma), riepilogo dei Dettagli e badge dello stato TICKET locale. Solo presentazione e comandi che
/// riusano gli stessi esiti rapidi: nessuno stato in piu' viene salvato nel Test.
/// </summary>
public partial class TestCaseEditorViewModel
{
    private const string PreferenzaTuttiICampi = "TestEditor.AllFields";
    private string ChiavePreferenzaTuttiICampi => PreferenzaTuttiICampi + "|" + _state.Config.OperatoreAttivo;

    /// <summary>Mostra solo le operazioni ancora "Da eseguire" (la coda di lavoro). Si resetta aprendo un altro Test.</summary>
    [ObservableProperty] private bool soloDaEseguireRapido;

    public bool HaOperazioniRapide => HaRevisioneCorrente && TipoTestScelto && ModoEditorRapido;

    /// <summary>Operazioni ancora "Da eseguire" della revisione corrente (zero se non modificabile).</summary>
    public int OperazioniRestantiRapide => RevisioneCorrente?.Condizioni.Count(c => c.Esito == Esito.DaEseguire) ?? 0;

    public bool PuoSegnareRestantiOk => PuoModificareContenuto && ModoEditorRapido && OperazioniRestantiRapide > 0;

    /// <summary>Veicolo · componente del Test, al posto dei campi opzionali nascosti della Rapida.</summary>
    public string RiepilogoDettagliRapido =>
        string.Join(" · ", new[] { TestCase.VeicoliTesto, string.Join(", ", TestCase.EcuEffettive) }.Where(x => !string.IsNullOrWhiteSpace(x)));

    public bool HaRiepilogoDettagliRapido => ModoEditorRapido && !IsNew && RiepilogoDettagliRapido.Length > 0;

    public bool HaStatoEstaRapido => ModoEditorRapido && !IsNew && (EstaAtteseLocali > 0 || EstaCollegamentiDitm > 0 || EstaStatoNonDisponibile);

    partial void OnSoloDaEseguireRapidoChanged(bool value) => NotificaFlussoRapido();

    internal void NotificaFlussoRapido()
    {
        OnPropertyChanged(nameof(HaOperazioniRapide));
        OnPropertyChanged(nameof(OperazioniRestantiRapide));
        OnPropertyChanged(nameof(PuoSegnareRestantiOk));
        OnPropertyChanged(nameof(RiepilogoDettagliRapido));
        OnPropertyChanged(nameof(HaRiepilogoDettagliRapido));
        OnPropertyChanged(nameof(HaStatoEstaRapido));
    }

    private void CaricaPreferenzaTuttiICampi()
    {
        try
        {
            mostraTuttiICampiRapido = string.Equals(ViewStateService.Leggi(_state, ChiavePreferenzaTuttiICampi), "1", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLogService.Registra("Preferenza campi Rapida", ex);
        }
    }

    private void SalvaPreferenzaTuttiICampi(bool valore)
    {
        // Preferenza personale e locale a questo PC: mai nel Test e mai nella cartella condivisa.
        try { ViewStateService.Scrivi(_state, ChiavePreferenzaTuttiICampi, valore ? "1" : "0"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLogService.Registra("Preferenza campi Rapida", ex);
        }
    }

    /// <summary>Prossima operazione "Da eseguire" dopo quella indicata (con ritorno all'inizio). Null se non ce ne sono.</summary>
    public Condizione? TrovaProssimaDaEseguire(Condizione? dopo)
    {
        var elenco = RevisioneCorrente?.Condizioni;
        if (elenco is null || elenco.Count == 0) return null;
        var partenza = dopo is null ? -1 : elenco.IndexOf(dopo);
        for (var passo = 1; passo <= elenco.Count; passo++)
        {
            var candidata = elenco[(partenza + passo + elenco.Count) % elenco.Count];
            if (candidata.Esito == Esito.DaEseguire && !ReferenceEquals(candidata, dopo)) return candidata;
        }
        return null;
    }

    /// <summary>Imposta "Conforme" su tutte le operazioni ancora "Da eseguire" della revisione corrente, dopo una conferma che ne
    /// riporta il numero. Passa dallo stesso workflow dell'esito rapido (copertura, segnalazioni, NC candidate).</summary>
    [RelayCommand]
    private async Task SegnaRestantiOk()
    {
        if (!PuoSegnareRestantiOk || RevisioneCorrente is null) return;
        var tutte = RevisioneCorrente.Condizioni.Where(c => c.Esito == Esito.DaEseguire).ToList();
        // Con motivazioni precedenti il workflow chiederebbe una conferma per ogni operazione: quelle si lasciano a chi le tratta una per una.
        var daSegnare = tutte.Where(c => string.IsNullOrWhiteSpace(c.DescrizioneProblema) && string.IsNullOrWhiteSpace(c.DescrizioneDeviazione)
                                         && string.IsNullOrWhiteSpace(c.MotivazioneNonApplicabile)).ToList();
        var saltate = tutte.Count - daSegnare.Count;
        var loc = LocalizationService.Instance;
        if (daSegnare.Count == 0)
        {
            if (saltate > 0) Avvisi = Avvisi.Append(string.Format(loc.Traduci("EditorQuickMarkRestOkSaltateFmt"), saltate)).ToList();
            return;
        }
        if (!await ConfirmService.Chiedi(loc.Traduci("EditorQuickMarkRestOkTitolo"),
                string.Format(loc.Traduci("EditorQuickMarkRestOkBodyFmt"), daSegnare.Count),
                loc.Traduci("EditorQuickMarkRestOkConferma"), loc.Traduci("Annulla")))
            return;
        // Dopo l'attesa della conferma l'editor puo' essere cambiato: si ricontrolla ogni operazione.
        foreach (var condizione in daSegnare)
            if (condizione.Esito == Esito.DaEseguire) await ImpostaEsitoRapidoAsync(condizione, Esito.Conforme);
        if (saltate > 0) Avvisi = Avvisi.Append(string.Format(loc.Traduci("EditorQuickMarkRestOkSaltateFmt"), saltate)).ToList();
        NotificaFlussoRapido();
    }
}
