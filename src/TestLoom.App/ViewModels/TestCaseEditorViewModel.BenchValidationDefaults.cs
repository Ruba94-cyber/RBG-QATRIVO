using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Default personali del modulo di inserimento guasto per la validazione al banco.</summary>
public partial class TestCaseEditorViewModel
{
    // Restano nel profilo dell'operatore attivo, usando lo stesso archivio dei default dei campi strumento.
    // Il segmento dedicato evita collisioni con eventuali campi configurati dal Master sullo strumento.
    private const string DtcInjectionDefaultPrefix = "Guasto Injection::__BenchValidation::";
    private const string DtcInjectionDefaultVeicoloKey = DtcInjectionDefaultPrefix + "Veicolo";
    private const string DtcInjectionDefaultEcuKey = DtcInjectionDefaultPrefix + "Componente";
    private const string DtcInjectionDefaultCodiceKey = DtcInjectionDefaultPrefix + "Codice";
    private const string DtcInjectionDefaultRequisitoKey = DtcInjectionDefaultPrefix + "Requisito";

    private bool _dtcInjectionDefaultVeicoloApplicato;
    private bool _dtcInjectionDefaultEcuApplicato;
    private bool _dtcInjectionDefaultCodiceApplicato;
    private bool _dtcInjectionDefaultRequisitoApplicato;
    private bool _dtcInjectionAssegnazioneAutomaticaEcu;
    private bool _dtcInjectionEcuSceltaEsplicitamente;

    public bool HaDefaultDtcInjectionPersonale => new[]
    {
        DtcInjectionDefaultVeicoloKey,
        DtcInjectionDefaultEcuKey,
        DtcInjectionDefaultCodiceKey,
        DtcInjectionDefaultRequisitoKey,
    }.Any(Chiave => !string.IsNullOrWhiteSpace(ValoreDefaultDtcInjection(Chiave)));

    private string? ValoreDefaultDtcInjection(string chiave)
    {
        var valori = _state.OperatoreCorrente?.ValoriDefaultCampi;
        return valori != null && valori.TryGetValue(chiave, out var valore) ? valore : null;
    }

    private static string? NormalizzaDefaultDtcInjection(string? valore) =>
        string.IsNullOrWhiteSpace(valore) ? null : valore.Trim();

    /// <summary>Applica una sola volta i default personali validi per questo Test. Una scelta non presente
    /// nei Veicoli/componente/Requisiti collegati al test viene ignorata, lasciando attivi i default contestuali esistenti.</summary>
    private void ApplicaDefaultDtcInjection(IReadOnlyList<string> veicoli)
    {
        if (!UsaStrumentoDtcInjection || !PuoModificareContenuto || RevisioneCorrente == null)
            return;

        // Ogni valore ha il proprio contesto: componente/codice/requisito si possono applicare anche a un Test senza
        // veicolo; il default veicolo e il componente restano in attesa se il Test non offre ancora opzioni valide.
        if (!_dtcInjectionDefaultVeicoloApplicato && veicoli.Count > 0)
        {
            _dtcInjectionDefaultVeicoloApplicato = true;
            var veicolo = ValoreDefaultDtcInjection(DtcInjectionDefaultVeicoloKey);
            if (!string.IsNullOrWhiteSpace(veicolo) && veicoli.Contains(veicolo, StringComparer.OrdinalIgnoreCase))
                DtcInsVeicolo = veicolo;
        }

        ApplicaDefaultEcuDtcInjection();

        if (!_dtcInjectionDefaultRequisitoApplicato)
        {
            var requisito = ValoreDefaultDtcInjection(DtcInjectionDefaultRequisitoKey);
            if (string.IsNullOrWhiteSpace(requisito))
                _dtcInjectionDefaultRequisitoApplicato = true;
            else
            {
                var nessuno = NessunRequisitoEtichetta;
                if (DtcInsRequisitiOpzioni.Contains(requisito, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(DtcInsRequisitoSel) || DtcInsRequisitoSel.Equals(nessuno, StringComparison.OrdinalIgnoreCase))
                        DtcInsRequisitoSel = requisito;
                    _dtcInjectionDefaultRequisitoApplicato = true;
                }
                else if (!string.IsNullOrWhiteSpace(DtcInsRequisitoSel) && !DtcInsRequisitoSel.Equals(nessuno, StringComparison.OrdinalIgnoreCase))
                    _dtcInjectionDefaultRequisitoApplicato = true; // scelta esplicita: non sovrascriverla a un aggiornamento successivo
            }
        }

    }

    /// <summary>Attende che una selezione veicolo renda disponibili componente e applica il default una sola volta.
    /// Una scelta esplicita dell'utente mantiene la precedenza sul profilo.</summary>
    private void ApplicaDefaultEcuDtcInjection()
    {
        if (_dtcInjectionDefaultEcuApplicato || !UsaStrumentoDtcInjection || !PuoModificareContenuto || RevisioneCorrente == null)
            return;

        var opzioniEcu = DtcInsEcuOpzioni;
        if (opzioniEcu.Count == 0) return;

        _dtcInjectionDefaultEcuApplicato = true;
        if (_dtcInjectionEcuSceltaEsplicitamente) return;
        var componente = ValoreDefaultDtcInjection(DtcInjectionDefaultEcuKey);
        if (!string.IsNullOrWhiteSpace(componente) && opzioniEcu.Contains(componente, StringComparer.OrdinalIgnoreCase))
            ImpostaEcuDtcInjectionAutomaticamente(componente);
    }

    private void ImpostaEcuDtcInjectionAutomaticamente(string? componente)
    {
        _dtcInjectionAssegnazioneAutomaticaEcu = true;
        try { DtcInsEcu = componente; }
        finally { _dtcInjectionAssegnazioneAutomaticaEcu = false; }
    }

    /// <summary>Applica il default del codice solo dopo che veicolo, componente e requisito sono stati risolti.
    /// Se il test contiene già la stessa chiave guasto, non precompila un valore che l'azione Aggiungi rifiuterebbe.</summary>
    private void ApplicaDefaultCodiceDtcInjection()
    {
        if (_dtcInjectionDefaultCodiceApplicato) return;
        _dtcInjectionDefaultCodiceApplicato = true;
        var codice = ValoreDefaultDtcInjection(DtcInjectionDefaultCodiceKey);
        if (string.IsNullOrWhiteSpace(codice) || !DtcOperazioneRegole.CodiceValido(codice) ||
            DtcInsScelto != null || !string.IsNullOrWhiteSpace(DtcInsCodiceManuale)) return;

        var requisitoId = RequisitoDaEtichetta(DtcInsRequisitoSel);
        if (EsisteDtcInserito(requisitoId, DtcInsVeicolo, DtcInsEcu, codice)) return;
        DtcInsCodiceManuale = codice;
    }

    /// <summary>Memorizza i valori correnti del modulo guasto nel profilo dell'operatore attivo.</summary>
    [RelayCommand]
    private void SalvaDtcInjectionDefaults()
    {
        if (_state.OperatoreCorrente == null) return;

        var codice = DtcInsScelto?.Codice ?? DtcInsCodiceManuale;
        var requisito = DtcInsScelto?.RequisitoId ?? RequisitoDaEtichetta(DtcInsRequisitoSel);
        var defaults = new Dictionary<string, string?>
        {
            [DtcInjectionDefaultVeicoloKey] = NormalizzaDefaultDtcInjection(DtcInsVeicolo),
            [DtcInjectionDefaultEcuKey] = NormalizzaDefaultDtcInjection(DtcInsEcu),
            [DtcInjectionDefaultRequisitoKey] = NormalizzaDefaultDtcInjection(requisito),
        };
        // Inserire sempre la chiave: un codice vuoto o non valido deve cancellare
        // l'eventuale default precedente, non lasciarlo attivo nei Test successivi.
        defaults[DtcInjectionDefaultCodiceKey] = DtcOperazioneRegole.CodiceValido(codice)
            ? NormalizzaDefaultDtcInjection(codice)
            : null;
        _state.SalvaValoriDefaultCampi(defaults);
        OnPropertyChanged(nameof(HaDefaultDtcInjectionPersonale));
    }

    /// <summary>Rimuove tutti i default del modulo guasto del solo operatore attivo.</summary>
    [RelayCommand]
    private void RimuoviDtcInjectionDefaults()
    {
        if (_state.OperatoreCorrente == null) return;

        _state.SalvaValoriDefaultCampi(new Dictionary<string, string?>
        {
            [DtcInjectionDefaultVeicoloKey] = null,
            [DtcInjectionDefaultEcuKey] = null,
            [DtcInjectionDefaultCodiceKey] = null,
            [DtcInjectionDefaultRequisitoKey] = null,
        });
        OnPropertyChanged(nameof(HaDefaultDtcInjectionPersonale));
    }
}
