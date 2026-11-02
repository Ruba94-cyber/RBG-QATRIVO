using System.Text.Json;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Esporta le CR filtrate in un unico foglio tabellare, con i dettagli in colonne leggibili.</summary>
public static class ChangeRequestExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>Colonne fisse del foglio dati (l'intestazione e' anche l'identificativo per la scelta delle colonne); seguono i campi custom.</summary>
    public static readonly IReadOnlyList<string> IntestazioniBase = new[]
    {
        "Schema", "UID CR", "Revisione dati", "ID", "UID template", "UID gruppo segnalazione",
        "Titolo", "Descrizione breve", "Descrizione", "Motivazione duplicato", "Stato", "Responsabile", "Creato da",
        "Data creazione", "Ultima modifica", "Data implementazione", "Implementata da", "Revisione implementata",
        "Data chiusura", "Chiusa da", "Motivo chiusura", "Chiusa senza validazione", "Archiviata", "Data archiviazione",
        "Veicoli impattati", "Gamma", "Veicolo / Gamma", "Componente impattate", "Strumenti impattati", "Funzionalità impattate",
        "Ticket", "CR correlate", "Matricola cliente", "Licenze",
        "Test di validazione (dettagli)", "Allegati (dettagli)", "Revisioni (dettagli)",
        "Cronologia (dettagli)", "Dati aggiuntivi JSON"
    };

    /// <summary>Campi della pivot: quelli del foglio dati piu' "Giorni di vita" (creazione-chiusura, o creazione-oggi se aperta), unico campo numerico.</summary>
    public static IReadOnlyList<ExportColonna> CampiPivot() => IntestazioniBase.Select(h => new ExportColonna(h, h))
        .Append(new ExportColonna("Giorni di vita", ExportPivotService.EtichettaGiorniDiVita, true)).ToList();

    public static IEnumerable<IReadOnlyList<object?>> RighePivot(IEnumerable<ChangeRequest> crs,
        IReadOnlyDictionary<string, string>? gammaPerVeicolo = null, DateTime? oggi = null)
    {
        var lista = crs.ToList();
        var riferimento = oggi ?? DateTime.Now;
        return RigheDati(lista, gammaPerVeicolo).Zip(lista, (riga, cr) => (IReadOnlyList<object?>)[.. riga,
            ExportPivotService.GiorniDiVita(cr.DataCreazione, cr.ChiusaIl, riferimento)]);
    }

    public static void ScriviFoglio(XLWorkbook workbook, IEnumerable<ChangeRequest> changeRequests,
        CancellationToken ct = default, IReadOnlyDictionary<string, string>? gammaPerVeicolo = null,
        IReadOnlyList<string>? colonne = null)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        ArgumentNullException.ThrowIfNull(changeRequests);
        var crs = changeRequests.ToList();
        var campiCustom = crs.SelectMany(cr => cr.CampiCustom.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        var intestazioni = IntestazioniBase.ToList();
        intestazioni.AddRange(campiCustom.Select(k => $"Campo custom: {k}"));
        var righe = RigheDati(crs, gammaPerVeicolo, campiCustom);
        var indici = ExportColonneSelezione.Indici(intestazioni, colonne);
        var writer = new ExcelExportTableWriter(workbook, "Change Request", ExportColonneSelezione.Estrai(intestazioni, indici));
        foreach (var riga in righe)
        {
            ct.ThrowIfCancellationRequested();
            writer.AggiungiRiga(riga[3] as string ?? "", ExportColonneSelezione.Estrai(riga, indici));
        }
        writer.Completa();
    }

    public static IEnumerable<IReadOnlyList<object?>> RigheDati(IEnumerable<ChangeRequest> crs,
        IReadOnlyDictionary<string, string>? gammaPerVeicolo = null, IReadOnlyList<string>? campiCustom = null)
    {
        campiCustom ??= Array.Empty<string>();
        return crs.Select(cr =>
        {
            var veicoli = string.Join(", ", cr.VeicoliImpattati);
            var gamma = gammaPerVeicolo == null ? "" : string.Join(", ", cr.VeicoliImpattati
                .Select(v => gammaPerVeicolo.GetValueOrDefault(v, ""))
                .Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase));
            var ambitoVeicoloGamma = string.Join(", ", cr.VeicoliImpattati.Select(v =>
            {
                var nomeGamma = gammaPerVeicolo?.GetValueOrDefault(v, "") ?? "";
                return string.IsNullOrWhiteSpace(nomeGamma) ? v : $"{v} ({nomeGamma})";
            }));
            return (IReadOnlyList<object?>)new List<object?>
            {
                cr.SchemaVersion, cr.Uid, cr.RevisioneDati, cr.Id, cr.TemplateUid, cr.GruppoSegnalazioneUid,
                cr.Titolo, cr.DescrizioneBreve, cr.Descrizione, cr.MotivazioneDuplicato, cr.Stato.ToString(), cr.Responsabile, cr.CreatoDa,
                cr.DataCreazione, cr.UltimaModifica, cr.ImplementataIl, cr.ImplementataDa, cr.RevisioneImplementata,
                cr.ChiusaIl, cr.ChiusaDa, cr.MotivoChiusura, cr.ChiusaSenzaValidazione, cr.Archiviata, cr.DataArchiviazione,
                veicoli, gamma, ambitoVeicoloGamma, string.Join(", ", cr.EcuImpattate), string.Join(", ", cr.StrumentiImpattati),
                string.Join(", ", cr.FunzionalitaImpattate), TestoCompleto(string.Join(Environment.NewLine, cr.TicketCollegati.Select(t =>
                    $"{t.Riferimento}; URL={t.Url}; collegato={t.CollegatoIl:dd/MM/yyyy HH:mm} da {t.CollegatoDa}; note={t.Note}")), "Ticket", cr.Id),
                string.Join(", ", cr.CrCorrelate), string.Join(", ", cr.VinCliente), string.Join(", ", cr.Licenze),
                TestoCompleto(string.Join(Environment.NewLine, cr.TestValidazione.Select(FormattaValidazione)), "Test di validazione", cr.Id),
                TestoCompleto(string.Join(Environment.NewLine, cr.Allegati.Select(a =>
                    $"{a.NomeFile}; descrizione={a.Descrizione}; caricato={a.DataCaricamento:dd/MM/yyyy HH:mm} da {a.CaricatoDa}; byte={a.DimensioneByte}; SHA-256={a.Sha256}; archiviato={(a.Archiviata ? "Sì" : "No")}; UID={a.Uid}")), "Allegati", cr.Id),
                TestoCompleto(string.Join(Environment.NewLine, cr.Revisioni.Select(FormattaRevisione)), "Revisioni", cr.Id),
                TestoCompleto(string.Join(Environment.NewLine, cr.Changelog.Select(c =>
                    $"{c.Data:dd/MM/yyyy HH:mm:ss}; {c.Operatore}; {c.Campo}: '{c.ValorePrecedente}' → '{c.ValoreNuovo}'")), "Cronologia", cr.Id),
                cr.ExtensionData == null ? "" : JsonCompleto(cr.ExtensionData, "Dati aggiuntivi", cr.Id)
            }.Concat(campiCustom.Select(k => cr.CampiCustom.TryGetValue(k, out var value) ? value : "")).ToList();
        });
    }

    private static string JsonCompleto<T>(T valore, string campo, string id)
    {
        _ = campo;
        _ = id;
        return JsonSerializer.Serialize(valore, JsonOptions);
    }

    private static string TestoCompleto(string testo, string campo, string id)
    {
        _ = campo;
        _ = id;
        return testo;
    }

    private static string FormattaValidazione(CollegamentoValidazioneCr t)
    {
        var snapshot = t.SnapshotValidazione == null ? "nessuno" :
            $"{t.SnapshotValidazione.EseguitaIl:dd/MM/yyyy}; esito={t.SnapshotValidazione.Esito}; veicoli={string.Join(", ", t.SnapshotValidazione.Veicoli)}; componente={string.Join(", ", t.SnapshotValidazione.Componente)}; strumenti={string.Join(", ", t.SnapshotValidazione.Strumenti)}";
        return $"Test={t.TestId}; obbligatorio={(t.Obbligatorio ? "Sì" : "No")}; veicoli={string.Join(", ", t.VeicoliCoperti)}; componente={string.Join(", ", t.EcuCoperte)}; strumenti={string.Join(", ", t.StrumentiCoperti)}; eccezione={(t.EccezioneCopertura ? "Sì" : "No")} ({t.MotivazioneEccezione}); approvata da {t.ApprovataDa} il {t.ApprovataIl:dd/MM/yyyy}; deviazione approvata={(t.DeviazioneApprovata ? "Sì" : "No")} ({t.MotivazioneDeviazione}) da {t.DeviazioneApprovataDa}; esecuzione validante={t.EsecuzioneValidante}; collegato={t.CollegatoIl:dd/MM/yyyy} da {t.CollegatoDa}; snapshot={snapshot}";
    }

    private static string FormattaRevisione(RevisioneChangeRequest r) =>
        $"Rev. {r.Numero} ({r.Data:dd/MM/yyyy}, {r.Autore}); titolo={r.Titolo}; descrizione breve={r.DescrizioneBreve}; descrizione={r.Descrizione}; motivazione duplicato={r.MotivazioneDuplicato}; veicoli={string.Join(", ", r.VeicoliImpattati)}; componente={string.Join(", ", r.EcuImpattate)}; strumenti={string.Join(", ", r.StrumentiImpattati)}; test validazione={string.Join(", ", r.TestValidazioneId)}; campi personalizzati={FormattaDizionario(r.CampiCustom)}";

    private static string FormattaDizionario(IReadOnlyDictionary<string, string> valori) =>
        string.Join("; ", valori.Select(kv => $"{kv.Key}={kv.Value}"));

}
