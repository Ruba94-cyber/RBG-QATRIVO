using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Una riga del menu "Campi visibili": un campo strumento per operazione che si puo' mostrare o nascondere in questo Test.</summary>
public sealed partial class VoceVistaCampo : ObservableObject
{
    private readonly Action<VoceVistaCampo, bool> _alCambio;
    private bool _inizializzando = true;

    public VoceVistaCampo(string strumento, string nome, bool visibile, bool compilato, Action<VoceVistaCampo, bool> alCambio)
    {
        Strumento = strumento;
        Nome = nome;
        Chiave = $"{strumento}::{nome}";
        _alCambio = alCambio;
        visibileValue = visibile;
        Compilato = compilato;
        _inizializzando = false;
    }

    public string Strumento { get; }
    public string Nome { get; }
    public string Chiave { get; }
    public string Etichetta => $"{Strumento} · {Nome}";

    /// <summary>Il campo ha gia' un valore in almeno una operazione: resta visibile e non si puo' nascondere finche' non e' svuotato.</summary>
    public bool Compilato { get; }
    public bool PuoCambiare => !Compilato;

    [ObservableProperty] private bool visibileValue;

    public bool Visibile
    {
        get => VisibileValue;
        set
        {
            if (_inizializzando || Compilato || value == VisibileValue) return;
            VisibileValue = value;
            OnPropertyChanged();
            _alCambio(this, value);
        }
    }
}

/// <summary>
/// Visibilita' dei campi strumento per operazione (solo presentazione). Default dal tipo di test
/// (TestTemplate.CampiOperazioneNascosti), override del singolo Test nella chiave riservata di ValoriStrumentiTest.
/// Il campo con un valore diverso dal default resta sempre visibile: nessun dato viene nascosto a chi l'ha compilato.
/// </summary>
public partial class TestCaseEditorViewModel
{
    public ObservableCollection<VoceVistaCampo> VociVistaCampi { get; } = new();
    public bool HaVociVistaCampi => VociVistaCampi.Count > 0;
    [ObservableProperty] private string riepilogoVistaCampi = "";

    /// <summary>Campi per operazione dello strumento con la loro visibilita' in questo Test (per la finestra della rotellina).</summary>
    public IReadOnlyList<VoceVistaCampo> VociVistaDi(string strumento) =>
        VociVistaCampi.Where(v => string.Equals(v.Strumento, strumento, StringComparison.OrdinalIgnoreCase)).ToList();

    private static string ChiaveCampo(CampoStrumentoValore c) => $"{c.Strumento}::{c.Nome}";

    private bool CampoHaValoreReale(CampoStrumentoValore campo)
    {
        return HaValoreRealeChiave(ChiaveCampo(campo), campo.ValoreDefault);
    }

    private Dictionary<string, bool>? _cacheValoriReali;

    private bool HaValoreRealeChiave(string chiave, string? valoreDefault)
    {
        var cache = _cacheValoriReali;
        var chiaveCache = chiave + "\u0001" + valoreDefault;
        if (cache != null && cache.TryGetValue(chiaveCache, out var noto)) return noto;
        var risultato = CalcolaValoreRealeChiave(chiave, valoreDefault);
        if (cache != null) cache[chiaveCache] = risultato;
        return risultato;
    }

    private bool CalcolaValoreRealeChiave(string chiave, string? valoreDefault)
    {
        foreach (var esecuzione in Esecuzioni.Count > 0 ? Esecuzioni.ToList() : TestCase.Esecuzioni)
            foreach (var c in esecuzione.Condizioni)
                if (c.ValoriStrumenti.TryGetValue(chiave, out var v) && !string.IsNullOrWhiteSpace(v) &&
                    !string.Equals(v, valoreDefault, StringComparison.Ordinal))
                    return true;
        return false;
    }

    /// <summary>Applica la vista: toglie i campi nascosti dai campi diretti e dai gruppi (i gruppi rimasti vuoti spariscono).</summary>
    private (List<CampoStrumentoValore> Diretti, List<GruppoCampiStrumento> Gruppi) ApplicaVistaCampi(
        List<CampoStrumentoValore> diretti, List<GruppoCampiStrumento> gruppi)
    {
        var overrides = VistaCampiOperazione.LeggiOverride(TestCase.ValoriStrumentiTest);
        var predefiniti = TemplateCorrente?.CampiOperazioneNascosti;
        if (overrides.Count == 0 && (predefiniti is null || predefiniti.Count == 0)) return (diretti, gruppi);

        bool Nascondi(CampoStrumentoValore c) => !string.IsNullOrEmpty(c.Strumento) &&
            VistaCampiOperazione.Nascosto(ChiaveCampo(c), predefiniti, overrides, CampoHaValoreReale(c));
        var dirittiVisibili = diretti.Where(c => !Nascondi(c)).ToList();
        foreach (var g in gruppi) g.Campi = g.Campi.Where(c => !Nascondi(c)).ToList();
        return (dirittiVisibili, gruppi.Where(g => g.Campi.Count > 0).ToList());
    }

    /// <summary>Ricostruisce il menu dai campi per operazione degli strumenti selezionati.</summary>
    internal void CostruisciVociVistaCampi()
    {
        VociVistaCampi.Clear();
        var selezionati = StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Valore).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overrides = VistaCampiOperazione.LeggiOverride(TestCase.ValoriStrumentiTest);
        var predefiniti = TemplateCorrente?.CampiOperazioneNascosti;
        foreach (var strumento in _tuttiGliStrumenti.Where(s => selezionati.Contains(s.Nome)))
            foreach (var campo in strumento.Campi.Where(c => c.Ambito == TipoAmbitoCampo.PerOperazione).OrderBy(c => c.Ordine))
            {
                var chiave = $"{strumento.Nome}::{campo.Nome}";
                var compilato = HaValoreRealeChiave(chiave, campo.ValoreDefault);
                var nascosto = VistaCampiOperazione.Nascosto(chiave, predefiniti, overrides, compilato);
                VociVistaCampi.Add(new VoceVistaCampo(strumento.Nome, campo.Nome, !nascosto, compilato, CambiaVisibilitaCampo));
            }
        foreach (var opzione in StrumentiOptions)
        {
            opzione.VociCampi.Clear();
            foreach (var voce in VociVistaCampi.Where(v => string.Equals(v.Strumento, opzione.Valore, StringComparison.OrdinalIgnoreCase)))
                opzione.VociCampi.Add(voce);
            opzione.HaVociCampi = opzione.VociCampi.Count > 0;
            opzione.MostraRotellina = PuoGestireStrumenti || opzione.HaVociCampi;
        }
        OnPropertyChanged(nameof(HaVociVistaCampi));
        AggiornaRiepilogoVistaCampi();
    }

    private void AggiornaRiepilogoVistaCampi()
    {
        var nascosti = VociVistaCampi.Count(v => !v.Visibile);
        var loc = LocalizationService.Instance;
        RiepilogoVistaCampi = nascosti == 0
            ? loc.Traduci("VistaCampiTuttiVisibili")
            : string.Format(loc.Traduci("VistaCampiNascostiFmt"), nascosti, VociVistaCampi.Count);
    }

    private void CambiaVisibilitaCampo(VoceVistaCampo voce, bool visibile)
    {
        if (!PuoModificareContenuto)
        {
            // Sola lettura/revisione storica: nessuna modifica al Test, la scelta viene annullata.
            CostruisciVociVistaCampi();
            return;
        }
        TestCase.ValoriStrumentiTest ??= new();
        VistaCampiOperazione.Imposta(TestCase.ValoriStrumentiTest, voce.Chiave, visibile, TemplateCorrente?.CampiOperazioneNascosti);
        ModificheNonSalvate = true;
        RicalcolaCampiVisualOperazioni();
        AggiornaRiepilogoVistaCampi();
    }

    /// <summary>Torna al default del tipo di test (toglie l'override di questo Test).</summary>
    [RelayCommand]
    private void RipristinaVistaCampi()
    {
        if (!PuoModificareContenuto || !TestCase.ValoriStrumentiTest.ContainsKey(VistaCampiOperazione.ChiaveValoriStrumentiTest)) return;
        TestCase.ValoriStrumentiTest.Remove(VistaCampiOperazione.ChiaveValoriStrumentiTest);
        ModificheNonSalvate = true;
        CostruisciVociVistaCampi();
        RicalcolaCampiVisualOperazioni();
    }

    private void RicalcolaCampiVisualOperazioni()
    {
        _cacheValoriReali = new();
        try { RicalcolaCampiVisualOperazioniCore(); }
        finally { _cacheValoriReali = null; }
    }

    private void RicalcolaCampiVisualOperazioniCore()
    {
        var selezionati = StrumentiOptions.Where(o => o.Selezionato).Select(o => o.Valore).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var strumentiSelezionati = _tuttiGliStrumenti.Where(s => selezionati.Contains(s.Nome)).ToList();
        foreach (var esecuzione in Esecuzioni)
        {
            var strumentiEsecuzione = StrumentiStoriciDi(esecuzione, strumentiSelezionati);
            foreach (var condizione in esecuzione.Condizioni)
                AggiornaCampiVisualCondizione(condizione, strumentiEsecuzione);
        }
        SincronizzaCondizioniVisual();
    }
}
