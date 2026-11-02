using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>Un guasto aggiunto al Test con lo strumento guasto Injection (6.5.1, fase 4): VISTA derivata dalle operazioni della REV corrente
/// (la coppia Validazione/Devalidazione dello stesso guasto); nessun dato persistito in piu. La riga si ricostruisce a ogni modifica.</summary>
public sealed class DtcInseritoRiga
{
    public DtcInseritoRiga(string codice, string componente, string? veicolo, string requisitoId, IReadOnlyList<Condizione> operazioni)
    {
        Codice = codice; Componente = componente; Veicolo = veicolo; RequisitoId = requisitoId; Operazioni = operazioni;
    }

    public string Codice { get; }
    public string Componente { get; }
    public string? Veicolo { get; }
    /// <summary>"" = guasto libero (nessun requisito: fuori da Audit).</summary>
    public string RequisitoId { get; }
    /// <summary>Le operazioni del guasto (di norma Validazione + Devalidazione), nell'ordine della REV.</summary>
    public IReadOnlyList<Condizione> Operazioni { get; }

    public bool Libero => RequisitoId.Length == 0;
    /// <summary>Rimozione/modifica ammesse solo se NESSUNA operazione ha un esito registrato.</summary>
    public bool Modificabile => DtcOperazioneRegole.PuoDeselezionare(Operazioni);
    public string Chiave => DtcOperazioneRegole.ChiaveDtc(RequisitoId, Veicolo, Componente, Codice);
    public string Riepilogo => string.Format(LocalizationService.Instance.Traduci("DtcInsOperazioniFmt"), Operazioni.Count);
    /// <summary>Etichetta del requisito nel menu ("(nessuno)" se libero).</summary>
    public string RequisitoEtichetta => Libero ? TestCaseEditorViewModel.NessunRequisitoEtichetta : RequisitoId;

    /// <summary>Id stabile per UI Automation: codice, componente, veicolo e requisito, con i caratteri non alfanumerici sostituiti da "_".</summary>
    public string Id => Pulisci(Codice + "_" + Componente + (string.IsNullOrWhiteSpace(Veicolo) ? "" : "_" + Veicolo) + (Libero ? "" : "_" + RequisitoId));
    public string AutomationIdCodice => "Editor_DtcInjection_Riga_" + Id + "_Codice_Text";
    public string AutomationIdEcu => "Editor_DtcInjection_Riga_" + Id + "_Componente_Combo";
    public string AutomationIdRequisito => "Editor_DtcInjection_Riga_" + Id + "_Requisito_Combo";
    public string AutomationIdRimuovi => "Editor_DtcInjection_Riga_" + Id + "_Rimuovi_Button";

    private static string Pulisci(string s) => new(s.Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '_').ToArray());
}
