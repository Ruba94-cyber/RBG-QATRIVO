using ClosedXML.Excel;
using TestLoom.App.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestLoom.App.Services;

/// <summary>Esportazione Excel dedicata delle Non Conformità (feedback utente 2026-09-17: "devo anche poter
/// esportare le non conformità con tutti i loro dati"). A differenza dei fogli "Anomalie TICKET retest"/"Anomalie
/// legacy" già presenti nel Report generale (sottoinsiemi di colonne pensati per quel report), qui ogni campo
/// proprio di NonConformita compare in colonna, comprese le informazioni esclusive delle segnalazioni di rete
/// (componente/Veicolo/Funzionalità/Strumento segnalati, licenze, matricola cliente, numeri pratica). Esporta esattamente
/// l'elenco passato dal chiamante: la pagina Anomalie/CR passa le righe già filtrate a schermo, non l'intero
/// archivio.</summary>
public static class NonConformitaExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    /// <summary><paramref name="ct"/> viene controllato a ogni NC esportata: un annullamento interrompe la generazione
    /// senza produrre alcun file (vedi EsportazioneInBackground).</summary>
    public static byte[] GeneraExcel(IEnumerable<NonConformita> elenco, AppState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var wb = new XLWorkbook();
        ScriviFoglio(wb, elenco, state, "Non Conformità", ct);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Colonne del foglio dati, nell'ordine del file: l'intestazione e' anche l'identificativo usato dalla scelta delle colonne.</summary>
    public static readonly IReadOnlyList<string> Intestazioni = new[]
    {
        "ID", "UID", "Schema", "Revisione dati", "Test UID", "UID condizione origine", "UID gruppo segnalazione",
        "Stato", "Stato workflow", "Stato dettaglio", "Severità", "Priorità", "Owner", "Destinatario", "Iterazione test",
        "Veicolo", "Gamma", "Veicolo / Gamma", "Componente", "Funzionalità", "Strumento", "Valori strumento test",
        "Data apertura", "Data chiusura", "Scadenza", "Data fix", "Causa probabile", "Causa probabile (altro)",
        "Motivo chiusura", "Descrizione", "Azione",
        "Test collegato", "Test/Prove collegate", "Ticket TICKET/Ticketing", "Change Request collegate",
        "Da segnalazione rete", "Componente segnalate", "Veicoli segnalati", "Funzionalità segnalate",
        "Strumento segnalato", "Valori strumento segnalato", "Descrizione breve segnalata",
        "Numeri pratica rete", "Matricola cliente", "Licenze", "Concessionaria/Officina",
        "Operazioni segnalate (dettagli)", "Prove collegate (dettagli)", "Allegati (dettagli)",
        "Collegamenti Ticketing (dettagli)", "Cronologia (dettagli)", "Dati aggiuntivi JSON"
    };
    /// <summary>Scrive l'elenco già selezionato in un workbook esistente, mantenendo tutti i campi dell'export NC.</summary>
    public static void ScriviFoglio(XLWorkbook wb, IEnumerable<NonConformita> elenco, AppState state,
        string nomeFoglio = "Non Conformità", CancellationToken ct = default,
        IReadOnlyList<TestCase>? testCasesSnapshot = null, IReadOnlyList<Veicolo>? veicoliSnapshot = null,
        IReadOnlyList<string>? colonne = null)
    {
        var indici = ExportColonneSelezione.Indici(Intestazioni, colonne);
        var writer = new ExcelExportTableWriter(wb, nomeFoglio, ExportColonneSelezione.Estrai(Intestazioni, indici));
        foreach (var valori in RigheDati(elenco, state, ct, testCasesSnapshot, veicoliSnapshot))
            writer.AggiungiRiga(Convert.ToString(valori[0]) ?? "", ExportColonneSelezione.Estrai(valori, indici));
        writer.Completa();
    }

    /// <summary>Campi della pivot: quelli del foglio dati piu' "Giorni di vita" (apertura-chiusura, o apertura-oggi se aperta). Numerici: giorni e iterazione test.</summary>
    public static IReadOnlyList<ExportColonna> CampiPivot() => Intestazioni.Select(h => new ExportColonna(h, h, h == "Iterazione test"))
        .Append(new ExportColonna("Giorni di vita", ExportPivotService.EtichettaGiorniDiVita, true)).ToList();

    public static IEnumerable<IReadOnlyList<object?>> RighePivot(IEnumerable<NonConformita> elenco, AppState state,
        CancellationToken ct = default, DateTime? oggi = null)
    {
        var lista = elenco.ToList();
        var riferimento = oggi ?? DateTime.Now;
        return RigheDati(lista, state, ct).Zip(lista, (riga, nc) => (IReadOnlyList<object?>)[.. riga,
            ExportPivotService.GiorniDiVita(nc.DataApertura, nc.DataChiusura, riferimento)]);
    }

    public static IEnumerable<IReadOnlyList<object?>> RigheDati(IEnumerable<NonConformita> elenco, AppState state,
        CancellationToken ct = default, IReadOnlyList<TestCase>? testCasesSnapshot = null, IReadOnlyList<Veicolo>? veicoliSnapshot = null)
    {
        ct.ThrowIfCancellationRequested();
        var selezionate = elenco.ConAnnullamento(ct).ToList();
        var testCases = testCasesSnapshot ?? state.TestCases.GetAll();
        var testPerId = testCases.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var gammaPerVeicolo = (veicoliSnapshot ?? state.Master.Veicoli.Load())
            .ToDictionary(v => v.Nome, v => v.GammaNome, StringComparer.OrdinalIgnoreCase);
        foreach (var nc in selezionate.ConAnnullamento(ct))
        {
            var testCollegato = string.IsNullOrWhiteSpace(nc.TestCaseId) ? null : testPerId.GetValueOrDefault(nc.TestCaseId);
            var proveCollegate = nc.ProveCollegate.Select(p => p.TestId).Append(nc.TestCaseId)
                .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase);
            var testCorrelati = proveCollegate.Select(id => testPerId.GetValueOrDefault(id)).Where(t => t != null).Cast<TestCase>().ToList();
            var veicoli = nc.VeicoliSegnalati.Concat(testCorrelati.SelectMany(t => t.VeicoliEffettivi))
                .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var gamma = veicoli.Select(v => gammaPerVeicolo.GetValueOrDefault(v, ""))
                .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase);
            var ambitoVeicoloGamma = veicoli.Select(v =>
            {
                var nomeGamma = gammaPerVeicolo.GetValueOrDefault(v, "");
                return string.IsNullOrWhiteSpace(nomeGamma) ? v : $"{v} ({nomeGamma})";
            });
            var componente = nc.EcuSegnalate.Concat(testCorrelati.SelectMany(t => t.EcuEffettive))
                .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase);
            var funzionalita = nc.FunzionalitaSegnalate.Concat(testCorrelati.SelectMany(t => t.Funzionalita))
                .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase);
            var strumenti = testCorrelati.SelectMany(t => t.StrumentiUsati).Append(nc.StrumentoSegnalato)
                .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase);

            var valori = new object?[]
            {
                nc.Id, nc.Uid.ToString(), nc.SchemaVersion, nc.RevisioneDati, nc.TestUid?.ToString() ?? "",
                nc.CondizioneOrigineUid?.ToString() ?? "", nc.GruppoSegnalazioneUid?.ToString() ?? "",
                nc.Stato.Label(), nc.StatoWorkflow.Label(), nc.StatoDettaglio?.Label() ?? "", nc.Severita?.ToString() ?? "",
                nc.Priorita.Label(), nc.Owner, nc.Destinatario?.Label() ?? "", nc.EsecuzioneIterazione,
                string.Join(", ", veicoli), string.Join(", ", gamma), string.Join(", ", ambitoVeicoloGamma),
                string.Join(", ", componente), string.Join(", ", funzionalita), string.Join(", ", strumenti),
                string.Join(Environment.NewLine, testCorrelati.Select(t =>
                    $"Test {t.Id}: strumenti={string.Join(", ", t.StrumentiUsati)}; valori={FormattaDizionario(t.ValoriStrumentiTest)}")),
                nc.DataApertura, nc.DataChiusura, nc.Scadenza, nc.DataFix, nc.CausaProbabile?.ToString() ?? "",
                nc.CausaProbabileAltro ?? "", nc.MotivoChiusura, nc.Descrizione, nc.Azione,
                testCollegato?.Id ?? nc.TestCaseId, string.Join(", ", proveCollegate),
                string.Join(", ", nc.CollegamentiPolarion.Select(t => t.Codice)), string.Join(", ", nc.ChangeRequestIds),
                nc.DaSegnalazioneRete ? "Sì" : "No", nc.EcuSegnalate.ToString(), string.Join(", ", nc.VeicoliSegnalati),
                string.Join(", ", nc.FunzionalitaSegnalate), nc.StrumentoSegnalato,
                string.Join(", ", nc.ValoriStrumentoSegnalato.Select(kv => $"{kv.Key}={kv.Value}")),
                nc.DescrizioneBreveSegnalata, string.Join(", ", nc.NumeriPraticaRete), string.Join(", ", nc.VinCliente),
                string.Join(", ", nc.Licenze), nc.ConcessionariaOfficina,
                string.Join(Environment.NewLine, nc.OperazioniSegnalate.Select((o, i) =>
                    $"Operazione {i + 1}: componente={o.Componente}; Funzionalità={o.Funzionalita}; Valori strumento={FormattaDizionario(o.ValoriStrumenti)}")),
                string.Join(Environment.NewLine, nc.ProveCollegate.Select(p =>
                    $"Test={p.TestId}; UID test={p.TestUid}; iterazione={p.EsecuzioneIterazione}; UID condizione={p.CondizioneUid}; evidenza comparativa={(p.EvidenzaComparativa ? "Sì" : "No")}")),
                string.Join(Environment.NewLine, nc.Allegati.Select(a =>
                    $"{a.NomeFile}; descrizione={a.Descrizione}; caricato={a.DataCaricamento:dd/MM/yyyy HH:mm} da {a.CaricatoDa}; byte={a.DimensioneByte}; SHA-256={a.Sha256}; archiviato={(a.Archiviata ? "Sì" : "No")}; UID={a.Uid}")),
                string.Join(Environment.NewLine, nc.CollegamentiPolarion.Select(p =>
                    $"{p.Codice}; URL={p.Url}; collegato={p.CollegatoIl:dd/MM/yyyy HH:mm} da {p.CollegatoDa}; note={p.Note}; UID={p.Uid}")),
                string.Join(Environment.NewLine, nc.Changelog.Select(v =>
                    $"{v.Data:dd/MM/yyyy HH:mm:ss}; {v.Operatore}; {v.Campo}: '{v.ValorePrecedente}' → '{v.ValoreNuovo}'")),
                nc.ExtensionData == null ? "" : JsonSerializer.Serialize(nc.ExtensionData, JsonOptions)
            };
            yield return valori;
        }
    }

    private static string FormattaDizionario(IReadOnlyDictionary<string, string> valori) =>
        string.Join("; ", valori.Where(kv => !EvidenzaOperazione.EChiaveRiservata(kv.Key)).Select(kv => $"{kv.Key}={kv.Value}"));
}
