using System.Text.Json;
using ClosedXML.Excel;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Export di consultazione di tutti i Test (non soltanto della pagina SQLite visibile):
/// riepilogo, revisioni, operazioni e ogni campo serializzato, inclusi dizionari configurabili
/// e proprieta' introdotte da nuove versioni. Non modifica i Test e non legge allegati binari.
/// L'insieme dei Test deve essere gia' limitato ai permessi dell'operatore dal chiamante.
/// </summary>
public static class TestCaseCompleteExportService
{
    public static void Esporta(string percorso, IEnumerable<TestCase> test, CancellationToken ct = default,
        IReadOnlyCollection<StrumentoDiagnostico>? catalogoStrumenti = null,
        IReadOnlyCollection<TestTemplate>? catalogoTipiTest = null,
        IReadOnlySet<string>? singoliCampiStrumento = null,
        IReadOnlySet<string>? singoliCampiCustom = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(percorso);
        ArgumentNullException.ThrowIfNull(test);
        EsportazioneFile.ScriviAtomico(percorso, temporaneo =>
        {
            using var wb = CreaWorkbook(test, ct, catalogoStrumenti, catalogoTipiTest, singoliCampiStrumento, singoliCampiCustom);
            ct.ThrowIfCancellationRequested();
            // Il temporaneo termina con ".tmp": ClosedXML rifiuta SaveAs(percorso) con estensione non Excel, quindi si scrive su stream.
            using var flusso = new FileStream(temporaneo, FileMode.Create, FileAccess.Write, FileShare.None);
            wb.SaveAs(flusso);
        }, ct);
    }

    /// <summary>Genera fogli normalizzati con ID/Uid e preserva i valori estesi nel foglio Campi.
    /// L'export non e' un formato di importazione e non contiene i file allegati, soltanto i riferimenti.</summary>
    public static XLWorkbook CreaWorkbook(IEnumerable<TestCase> test, CancellationToken ct = default,
        IReadOnlyCollection<StrumentoDiagnostico>? catalogoStrumenti = null,
        IReadOnlyCollection<TestTemplate>? catalogoTipiTest = null,
        IReadOnlySet<string>? singoliCampiStrumento = null,
        IReadOnlySet<string>? singoliCampiCustom = null,
        ExportConfigurazione? configurazione = null)
    {
        ArgumentNullException.ThrowIfNull(test);
        // Definizioni correnti, NON retrodatate: gli snapshot delle REV conservano i valori
        // storici, ma la loro definizione potrebbe essere cambiata da allora.
        var catalogo = catalogoStrumenti?
            .Where(x => !string.IsNullOrWhiteSpace(x.Nome))
            .GroupBy(x => x.Nome, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // Lookup O(1) per chiave "Strumento::Campo": su archivi con molte operazioni
        // evita di riscorrere i campi del catalogo per ogni valore salvato.
        var definizioniPerChiave = new Dictionary<string, DefinizioneCampoStrumento>(
            StringComparer.OrdinalIgnoreCase);
        if (catalogo != null)
            foreach (var strumento in catalogo.Values)
                foreach (var campo in strumento.Campi)
                    if (!string.IsNullOrWhiteSpace(campo.Nome))
                        definizioniPerChiave.TryAdd(strumento.Nome + "::" + campo.Nome, campo);
        var strumentiCoinvolti = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nomiTipiTest = (catalogoTipiTest ?? [])
            .GroupBy(t => t.Uid).ToDictionary(g => g.Key, g => g.First().Nome);
        var wb = new XLWorkbook();
        try
        {
            var guida = wb.Worksheets.Add("Guida");
            guida.Cell(1, 1).Value = "RBG - QATRIVO · Esportazione completa Test";
            guida.Cell(2, 1).Value = "Generato";
            guida.Cell(2, 2).Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            guida.Cell(3, 1).Value = "Contenuto";
            guida.Cell(3, 2).Value = "Test attivi autorizzati secondo i filtri scelti (oppure tutti se esplicitamente richiesto), non solo quelli della pagina corrente. Change Request esclusi.";
            guida.Cell(4, 1).Value = "Test";
            guida.Cell(4, 2).Value = "Una riga per Test: tipo Test dal catalogo corrente, identificativi, stato, esito, ambito, descrizioni. Un tipo eliminato resta identificabile dal suo UID.";
            guida.Cell(5, 1).Value = "Revisioni";
            guida.Cell(5, 2).Value = "Una riga per ogni REV, anche storica. Il nome Tipo Test storico proviene dallo snapshot della REV (vuoto se non disponibile).";
            guida.Cell(6, 1).Value = "Operazioni";
            guida.Cell(6, 2).Value = "Tutti i tentativi con esito, ordine, efficacia dell'ultimo tentativo, durata (minuti) e riferimenti alle evidenze/allegati.";
            guida.Cell(7, 1).Value = "Campi";
            guida.Cell(7, 2).Value = "Tutte le proprieta' JSON persistite (percorso e valore): campi strumento per Test, REV e operazione, campi custom, allegati, ticket, punteggi, audit e campi futuri.";
            guida.Cell(8, 1).Value = "Campi strumenti";
            guida.Cell(8, 2).Value = "Solo campi strumento, anche NON compilati se definiti nel catalogo. Durata, evidenze e campi tecnici riservati sono separati. Il default non sostituisce mai un valore mancante.";
            guida.Cell(9, 1).Value = "Test incompleto";
            guida.Cell(9, 2).Value = "La colonna Funzionalita indicata aiuta a individuare i Test senza classificazione; verificarne la pertinenza per il tipo di Test prima di considerarli incompleti.";
            guida.Cell(10, 1).Value = "Modalita di export";
            guida.Cell(10, 2).Value = "Include tutti i Test attivi consentiti, senza applicare i filtri della pagina, ed esclude i Change Request. Il file serve per analisi, non per importazione.";
            guida.Cell(11, 1).Value = "Definizioni strumenti";
            guida.Cell(11, 2).Value = "Catalogo attuale dei campi per gli strumenti usati dai Test: la configurazione puo' essere diversa da quella storica della REV.";
            guida.Cell(12, 1).Value = "Campi custom";
            guida.Cell(12, 2).Value = "Campi personalizzati del Test corrente e delle fotografie storiche di ogni REV.";
            guida.Cell(13, 1).Value = "Punteggi requisiti";
            guida.Cell(13, 2).Value = "Valutazioni per requisito di ogni REV, con indicazione di override e motivazione.";
            guida.Cell(14, 1).Value = "Allegati";
            guida.Cell(14, 2).Value = "Metadati degli allegati ed eventuali REV/operazioni per cui sono evidenze. I file binari non sono inclusi.";
            guida.Cell(15, 1).Value = "Ticket";
            guida.Cell(15, 2).Value = "Riferimenti esterni collegati ai Test, con note e date.";
            guida.Cell(16, 1).Value = "Cronologia Test";
            guida.Cell(16, 2).Value = "Storico delle modifiche dei Test: prima/dopo e operatore.";
            guida.Cell(17, 1).Value = "Note pianificazione";
            guida.Cell(17, 2).Value = "Motivazioni storiche delle variazioni a scadenza, assegnazione e pianificazione.";
            guida.Cell(18, 1).Value = "Sicurezza";
            guida.Cell(18, 2).Value = "Il file puo' contenere informazioni interne e tecniche. Conservarlo in un percorso autorizzato.";
            guida.Column(1).Width = 25;
            guida.Column(2).Width = 115;
            guida.Cell(1, 1).Style.Font.Bold = true;

            var riepilogo = new ExcelExportTableWriter(wb, "Test", new[]
            {
                "ID Test", "UID Test", "Template UID", "Tipo Test", "Creato il", "Modificato il", "Creato da",
                "Modificato da", "Assegnatario", "Stato lavoro", "Esito ultimo", "Origine",
                "Veicolo/i", "Componente", "Funzionalita", "Funzionalita indicata", "Requisiti", "Strumenti",
                "Descrizione breve", "Descrizione", "Problema", "Revisioni", "Operazioni totali",
                "Priorita", "Severita", "Scadenza", "Matricola contesto", "Fuori categoria",
                "Campi custom", "Campi strumenti per Test", "Ticket collegati", "Allegati", "Ultimo aggiornamento dati"
            });
            var revisioni = new ExcelExportTableWriter(wb, "Revisioni", new[]
            {
                "ID Test", "UID Test", "REV", "Data", "Operatore", "Origine",
                "Esito", "Esito sovrascritto", "Motivazione override", "Matricola", "Baseline SW",
                "Baseline HW", "Deviazioni", "Note", "NC origine", "NC collegata",
                "Numero operazioni", "Punteggi requisiti", "Campi strumenti per REV", "Dettagli storici disponibili",
                "Tipo Test alla REV"
            });
            var operazioni = new ExcelExportTableWriter(wb, "Operazioni", new[]
            {
                "ID Test", "UID Test", "REV", "UID operazione", "UID operazione radice",
                "Strumento", "Componente", "Funzionalita", "Guasto", "Guasto passo", "Requisito guasto",
                "Veicolo guasto", "Esito", "Nota", "Motivazione N.A.", "Deviazione",
                "Problema", "Campi strumenti operazione", "Segnalazione UID",
                "N. tentativo nella REV", "Ultimo tentativo efficace", "Durata minuti",
                "N. evidenze", "UID evidenze"
            });
            // Il JSON integrale e' molto voluminoso. In un export interattivo
            // con foglio Campi deselezionato NON generarlo affatto: risparmia
            // memoria ed evita che campi esplicitamente esclusi finiscano nel file.
            ExcelExportTableWriter? campi = configurazione != null
                && !configurazione.HaSezione("Campi") ? null
                : new ExcelExportTableWriter(wb, "Campi", new[]
                {
                    "ID Test", "UID Test", "Percorso JSON", "Tipo", "Valore"
                });
            var strumenti = new ExcelExportTableWriter(wb, "Campi strumenti", new[]
            {
                "ID Test", "UID Test", "Livello", "REV", "UID operazione",
                "Strumento", "Nome campo", "Chiave originale", "Valore",
                "Ambito nel catalogo", "Tipo campo", "Gruppo", "Unita misura",
                "Opzioni disponibili", "Valore predefinito", "Stato valore", "Definizione corrente",
                "Ambito coerente"
            });
            var definizioniStrumenti = new ExcelExportTableWriter(wb, "Definizioni strumenti", new[]
            {
                "Strumento", "Tipo strumento", "Archiviato", "Ordine strumento",
                "Campo", "Ambito", "Tipo campo", "Gruppo", "Unita misura",
                "Opzioni disponibili", "Valore predefinito", "Ordine campo", "Stato catalogo"
            });
            var personalizzati = new ExcelExportTableWriter(wb, "Campi custom", new[]
            {
                "ID Test", "UID Test", "Contesto", "REV", "Chiave campo", "Valore"
            });
            var punteggi = new ExcelExportTableWriter(wb, "Punteggi requisiti", new[]
            {
                "ID Test", "UID Test", "REV", "Requisito", "Valore", "Sovrascritto", "Motivazione override"
            });
            var allegati = new ExcelExportTableWriter(wb, "Allegati", new[]
            {
                "ID Test", "UID Test", "UID allegato", "Nome file", "Caricato il", "Caricato da",
                "Dimensione byte", "SHA256", "Descrizione", "Archiviato", "Evidenza di"
            });
            var ticket = new ExcelExportTableWriter(wb, "Ticket", new[]
            {
                "ID Test", "UID Test", "Riferimento", "URL", "Collegato il", "Collegato da", "Note"
            });
            var changelog = new ExcelExportTableWriter(wb, "Cronologia Test", new[]
            {
                "ID Test", "UID Test", "Data", "Operatore", "Campo", "Valore precedente", "Valore nuovo"
            });
            var notePianificazione = new ExcelExportTableWriter(wb, "Note pianificazione", new[]
            {
                "ID Test", "UID Test", "Data", "Operatore", "Campi modificati", "Motivazione"
            });

            foreach (var tc in test.ConAnnullamento(ct))
            {
                ct.ThrowIfCancellationRequested();
                var id = tc.Id;
                var uid = tc.Uid.ToString("D");
                var nomeTipo = nomiTipiTest.GetValueOrDefault(tc.TestTemplateUid);
                riepilogo.AggiungiRiga(id, new object?[]
                {
                    id, uid, tc.TestTemplateUid, nomeTipo, tc.DataCreazione, tc.UltimaModifica, tc.CreatoDa,
                    tc.ModificatoDa, tc.Assegnatario, tc.StatoLavoro, tc.StatoCorrente?.ToString(), tc.Origine,
                    string.Join(", ", tc.VeicoliEffettivi), string.Join(", ", tc.Componente.ToList()), 
                    string.Join(", ", tc.Funzionalita), tc.Funzionalita.Count > 0, string.Join(", ", tc.RequisitoId),
                    string.Join(", ", tc.StrumentiUsati), tc.DescrizioneBreve, tc.Descrizione,
                    tc.DescrizioneProblema, tc.Esecuzioni.Count,
                    tc.Esecuzioni.Sum(e => e.Condizioni.Count), tc.Priorita, tc.Severita,
                    tc.Scadenza, tc.ContestoPredefinito?.Matricola, tc.FuoriCategoria,
                    tc.CampiCustom.Count, tc.ValoriStrumentiTest.Count, tc.TicketCollegati.Count,
                    tc.Allegati.Count, tc.RevisioneDati
                });

                foreach (var campo in tc.CampiCustom.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ConAnnullamento(ct))
                    if (singoliCampiCustom == null || singoliCampiCustom.Contains(campo.Key))
                        personalizzati.AggiungiRiga(id, new object?[]
                        {
                            id, uid, "Test corrente", null, campo.Key, campo.Value
                        });
                foreach (var rif in tc.TicketCollegati.ConAnnullamento(ct))
                    ticket.AggiungiRiga(id, new object?[]
                    {
                        id, uid, rif.Riferimento, rif.Url, rif.CollegatoIl, rif.CollegatoDa, rif.Note
                    });
                foreach (var entry in tc.Changelog.ConAnnullamento(ct))
                    changelog.AggiungiRiga(id, new object?[]
                    {
                        id, uid, entry.Data, entry.Operatore, entry.Campo,
                        entry.ValorePrecedente, entry.ValoreNuovo
                    });
                foreach (var nota in tc.NotePianificazione.ConAnnullamento(ct))
                    notePianificazione.AggiungiRiga(id, new object?[]
                    {
                        id, uid, nota.Data, nota.Operatore, nota.CampiModificati, nota.Nota
                    });
                var evidenzePerAllegato = tc.Allegati.Count > 0
                    ? EvidenzaOperazione.EtichettePerAllegato(tc) : null;
                foreach (var allegato in tc.Allegati.ConAnnullamento(ct))
                    allegati.AggiungiRiga(id, new object?[]
                    {
                        id, uid, allegato.Uid, allegato.NomeFile,
                        allegato.DataCaricamento, allegato.CaricatoDa, allegato.DimensioneByte,
                        allegato.Sha256, allegato.Descrizione, allegato.Archiviata,
                        evidenzePerAllegato?.GetValueOrDefault(allegato.Uid)
                    });

                ScriviStrumenti(tc.ValoriStrumentiTest, tc.StrumentiUsati, TipoAmbitoCampo.PerTest,
                    "Test corrente", id, uid, null, null, strumenti, catalogo, definizioniPerChiave, strumentiCoinvolti, singoliCampiStrumento, ct);
                foreach (var rev in tc.Esecuzioni.ConAnnullamento(ct))
                {
                    // Il set di strumenti della REV e' lo snapshot storico quando disponibile.
                    // Le definizioni del catalogo sono comunque esplicitamente correnti.
                    var usatiNellaRev = rev.Dettagli?.StrumentiUsati ?? tc.StrumentiUsati;
                    ScriviStrumenti(rev.ValoriStrumenti, usatiNellaRev, TipoAmbitoCampo.PerRevisione,
                        "Revisione", id, uid, rev.Iterazione, null, strumenti, catalogo, definizioniPerChiave, strumentiCoinvolti, singoliCampiStrumento, ct);
                    if (rev.Dettagli != null)
                    {
                        ScriviStrumenti(rev.Dettagli.ValoriStrumentiTest, rev.Dettagli.StrumentiUsati,
                            TipoAmbitoCampo.PerTest, "Snapshot Test nella REV",
                            id, uid, rev.Iterazione, null, strumenti, catalogo, definizioniPerChiave, strumentiCoinvolti, singoliCampiStrumento, ct);
                        foreach (var campo in rev.Dettagli.CampiCustom
                                     .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ConAnnullamento(ct))
                            if (singoliCampiCustom == null || singoliCampiCustom.Contains(campo.Key))
                                personalizzati.AggiungiRiga(id, new object?[]
                                {
                                    id, uid, "Snapshot Test nella REV", rev.Iterazione, campo.Key, campo.Value
                                });
                    }
                    foreach (var punteggio in rev.PunteggiRequisiti.ConAnnullamento(ct))
                        punteggi.AggiungiRiga(id, new object?[]
                        {
                            id, uid, rev.Iterazione, punteggio.RequisitoId, punteggio.Valore,
                            punteggio.Sovrascritto, punteggio.MotivazioneOverride
                        });
                    revisioni.AggiungiRiga(id, new object?[]
                    {
                        id, uid, rev.Iterazione, rev.Data, rev.Operatore, rev.Origine, rev.Esito,
                        rev.EsitoSovrascritto, rev.MotivazioneOverride, rev.Matricola,
                        rev.BaselineSw, rev.BaselineHw, rev.Deviazioni, rev.Note,
                        rev.NonConformitaOrigineId, rev.NonConformitaId,
                        rev.Condizioni.Count, rev.PunteggiRequisiti.Count,
                        rev.ValoriStrumenti.Count, rev.Dettagli != null,
                        rev.Dettagli?.TestTemplateNome
                    });
                    var operazioniEffettive = rev.CondizioniEffettive.Select(o => o.Uid).ToHashSet();
                    var numeroTentativo = 0;
                    foreach (var op in rev.Condizioni.ConAnnullamento(ct))
                    {
                        numeroTentativo++;
                        var usatiOperazione = string.IsNullOrWhiteSpace(op.Strumento)
                            ? usatiNellaRev : usatiNellaRev.Append(op.Strumento);
                        ScriviStrumenti(op.ValoriStrumenti, usatiOperazione, TipoAmbitoCampo.PerOperazione,
                            "Operazione", id, uid, rev.Iterazione,
                            op.Uid.ToString("D"), strumenti, catalogo, definizioniPerChiave, strumentiCoinvolti, singoliCampiStrumento, ct);
                        var evidenzeOperazione = EvidenzaOperazione.Leggi(op.ValoriStrumenti);
                        operazioni.AggiungiRiga(id, new object?[]
                        {
                            id, uid, rev.Iterazione, op.Uid, op.OperazioneOrigineUid,
                            op.Strumento, string.Join(", ", op.Componente.ToList()), op.Funzionalita,
                            op.Guasto, op.DtcPasso, op.DtcRequisitoId, op.DtcVeicolo,
                            op.Esito, op.Nota, op.MotivazioneNonApplicabile,
                            op.DescrizioneDeviazione, op.DescrizioneProblema,
                            op.ValoriStrumenti.Count(kv => !EvidenzaOperazione.EChiaveRiservata(kv.Key)),
                            op.SegnalazioneUid, numeroTentativo, operazioniEffettive.Contains(op.Uid),
                            DurataOperazione.Leggi(op.ValoriStrumenti),
                            evidenzeOperazione.Count,
                            string.Join(", ", evidenzeOperazione)
                        });
                    }
                }

                // Serializer di produzione: JsonExtensionData e dizionari vengono esportati esattamente
                // con le chiavi persistite, senza dipendere da un elenco hard-coded di campi.
                if (campi != null)
                {
                    var json = JsonSerializer.Serialize(tc, JsonIo.CompactOptions);
                    using var documento = JsonDocument.Parse(json);
                    ScriviCampi(documento.RootElement, "$", id, uid, campi, ct);
                }
            }

            riepilogo.Completa();
            revisioni.Completa();
            operazioni.Completa();
            campi?.Completa();
            ScriviDefinizioniStrumenti(definizioniStrumenti, strumentiCoinvolti, catalogo, singoliCampiStrumento, ct);
            strumenti.Completa();
            definizioniStrumenti.Completa();
            personalizzati.Completa();
            punteggi.Completa();
            allegati.Completa();
            ticket.Completa();
            changelog.Completa();
            notePianificazione.Completa();
            return wb;
        }
        catch
        {
            wb.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Per ogni contesto (Test, REV, operazione) esporta sia i valori realmente salvati
    /// sia i campi previsti dal catalogo ma ancora vuoti. Le chiavi storiche/sconosciute
    /// rimangono sempre nell'export, senza migrazioni o modifiche al Test.
    /// </summary>
    private static void ScriviStrumenti(
        IReadOnlyDictionary<string, string>? valori,
        IEnumerable<string>? strumentiSelezionati,
        TipoAmbitoCampo ambito,
        string livello, string id, string uid, int? rev, string? operazioneUid,
        ExcelExportTableWriter writer,
        IReadOnlyDictionary<string, StrumentoDiagnostico>? catalogo,
        IReadOnlyDictionary<string, DefinizioneCampoStrumento> definizioniPerChiave,
        ISet<string> strumentiCoinvolti,
        IReadOnlySet<string>? singoliCampiStrumento, CancellationToken ct)
    {
        var nomi = (strumentiSelezionati ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var nome in nomi) strumentiCoinvolti.Add(nome);
        var chiaviSalvate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (chiave, valore) in (valori ?? new Dictionary<string, string>())
                     .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ConAnnullamento(ct))
        {
            // Durata, evidenze e preferenze di visualizzazione NON sono campi dello
            // strumento: restano nel foglio Campi JSON e ora anche nelle colonne dedicate
            // di Operazioni, evitando di presentare "TEMPO" come strumento inesistente.
            if (EvidenzaOperazione.EChiaveRiservata(chiave)) continue;
            chiaviSalvate.Add(chiave);
            if (singoliCampiStrumento != null && !singoliCampiStrumento.Contains(chiave)) continue;
            ScriviRigaStrumento(chiave, valore, salvato: true, ambito, livello, id, uid, rev,
                operazioneUid, writer, catalogo, definizioniPerChiave, strumentiCoinvolti);
        }

        if (catalogo == null) return; // Non inventare definizioni senza anagrafica.
        foreach (var nome in nomi)
        {
            ct.ThrowIfCancellationRequested();
            if (!catalogo.TryGetValue(nome, out var strumento)) continue;
            foreach (var definizione in strumento.Campi.Where(c => c.Ambito == ambito))
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(definizione.Nome)) continue;
                var chiave = strumento.Nome + "::" + definizione.Nome;
                if (!chiaviSalvate.Add(chiave)) continue;
                if (singoliCampiStrumento != null && !singoliCampiStrumento.Contains(chiave)) continue;
                // Il default e' riportato in una colonna separata, NON nel valore.
                ScriviRigaStrumento(chiave, null, salvato: false, ambito, livello, id, uid,
                    rev, operazioneUid, writer, catalogo, definizioniPerChiave, strumentiCoinvolti);
            }
        }
    }

    private static void ScriviRigaStrumento(
        string chiave, string? valore, bool salvato, TipoAmbitoCampo ambito, string livello,
        string id, string uid, int? rev, string? operazioneUid,
        ExcelExportTableWriter writer,
        IReadOnlyDictionary<string, StrumentoDiagnostico>? catalogo,
        IReadOnlyDictionary<string, DefinizioneCampoStrumento> definizioniPerChiave,
        ISet<string> strumentiCoinvolti)
    {
        var separatore = chiave.IndexOf("::", StringComparison.Ordinal);
        var nomeStrumento = separatore > 0 ? chiave[..separatore] : "";
        var nomeCampo = separatore > 0 ? chiave[(separatore + 2)..] : chiave;
        if (!string.IsNullOrWhiteSpace(nomeStrumento)) strumentiCoinvolti.Add(nomeStrumento);
        var catalogato = catalogo != null && !string.IsNullOrWhiteSpace(nomeStrumento) &&
            catalogo.ContainsKey(nomeStrumento);
        var definizione = definizioniPerChiave.TryGetValue(chiave, out var trovata)
            ? trovata : null;
        var definizioneCorrente = definizione != null
            ? "Definito nel catalogo corrente"
            : catalogo == null ? "Catalogo non disponibile"
            : catalogato ? "Campo non piu' definito" : "Strumento non presente nel catalogo";
        var stato = !salvato ? "Non compilato"
            : string.IsNullOrWhiteSpace(valore) ? "Vuoto salvato" : "Compilato";
        writer.AggiungiRiga(id, new object?[]
        {
            id, uid, livello, rev, operazioneUid, nomeStrumento, nomeCampo, chiave, valore,
            definizione?.Ambito, definizione?.Tipo, definizione?.Gruppo, definizione?.UnitaMisura,
            definizione == null ? null : string.Join(" | ", definizione.OpzioniLista),
            definizione?.ValoreDefault, stato, definizioneCorrente,
            definizione == null ? null : definizione.Ambito == ambito
        });
    }

    private static void ScriviDefinizioniStrumenti(
        ExcelExportTableWriter writer, IEnumerable<string> nomiCoinvolti,
        IReadOnlyDictionary<string, StrumentoDiagnostico>? catalogo,
        IReadOnlySet<string>? singoliCampiStrumento, CancellationToken ct)
    {
        foreach (var nome in nomiCoinvolti.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ConAnnullamento(ct))
        {
            // Una scelta di campi e' un limite di visibilita' anche per il
            // catalogo: non reintrodurre le definizioni deselezionate.
            if (singoliCampiStrumento != null
                && !singoliCampiStrumento.Any(chiave => chiave.StartsWith(nome + "::", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (catalogo == null || !catalogo.TryGetValue(nome, out var strumento))
            {
                writer.AggiungiRiga(nome, new object?[]
                {
                    nome, null, null, null, null, null, null, null, null, null, null,
                    null, catalogo == null ? "Catalogo non disponibile" : "Strumento non presente nel catalogo"
                });
                continue;
            }

            if (strumento.Campi.Count == 0)
                writer.AggiungiRiga(nome, new object?[]
                {
                    strumento.Nome, strumento.TipoEffettivo, strumento.Archiviata, strumento.Ordine,
                    null, null, null, null, null, null, null, null, "Strumento senza campi definiti"
                });
            foreach (var campo in strumento.Campi.ConAnnullamento(ct))
            {
                if (singoliCampiStrumento != null
                    && !singoliCampiStrumento.Contains(strumento.Nome + "::" + campo.Nome)) continue;
                writer.AggiungiRiga(nome, new object?[]
                {
                    strumento.Nome, strumento.TipoEffettivo, strumento.Archiviata, strumento.Ordine,
                    campo.Nome, campo.Ambito, campo.Tipo, campo.Gruppo, campo.UnitaMisura,
                    string.Join(" | ", campo.OpzioniLista), campo.ValoreDefault,
                    campo.Ordine, "Definizione corrente"
                });
            }
        }
    }

    private static void ScriviCampi(JsonElement valore, string percorso, string id, string uid,
        ExcelExportTableWriter writer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (valore.ValueKind)
        {
            case JsonValueKind.Object:
                var proprieta = valore.EnumerateObject().ToList();
                if (proprieta.Count == 0) writer.AggiungiRiga(id, new object?[] { id, uid, percorso, "object", "{}" });
                foreach (var p in proprieta)
                    ScriviCampi(p.Value, percorso + "[" + JsonSerializer.Serialize(p.Name) + "]", id, uid, writer, ct);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in valore.EnumerateArray())
                    ScriviCampi(item, percorso + "[" + index++ + "]", id, uid, writer, ct);
                if (index == 0) writer.AggiungiRiga(id, new object?[] { id, uid, percorso, "array", "[]" });
                break;
            case JsonValueKind.String:
                writer.AggiungiRiga(id, new object?[] { id, uid, percorso, "string", valore.GetString() });
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.AggiungiRiga(id, new object?[] { id, uid, percorso, "null", null });
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.AggiungiRiga(id, new object?[] { id, uid, percorso, "boolean", valore.GetBoolean() });
                break;
            default:
                // Numeri come testo originale per non perdere i numeri a 64 bit o la precisione
                // che Excel arrotonderebbe automaticamente oltre le 15 cifre.
                writer.AggiungiRiga(id, new object?[] { id, uid, percorso, "number", valore.GetRawText() });
                break;
        }
    }
}
