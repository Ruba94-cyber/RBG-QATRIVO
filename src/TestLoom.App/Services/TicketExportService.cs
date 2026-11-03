using System.Text;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Sezioni selezionabili del pacchetto Ticketing (Addendum §4, feedback QA: prima sempre tutte,
/// senza modo di limitarsi ai campi che servono davvero). Chiavi stabili usate anche per il default
/// condiviso salvato da un Master (vedi PolarionConfigService).</summary>
public static class PolarionSezioni
{
    public const string DatiGenerali = "DatiGenerali";
    public const string CampiStrumento = "CampiStrumento";
    public const string Riferimenti = "Riferimenti";
    public const string RevisioneCorrente = "RevisioneCorrente";
    public const string Operazioni = "Operazioni";
    public const string Escalation = "Escalation";
    public const string StoricoRevisioni = "StoricoRevisioni";
    public const string Allegati = "Allegati";
    public const string TicketCollegati = "TicketCollegati";

    public static readonly string[] Tutte =
        { DatiGenerali, CampiStrumento, Riferimenti, RevisioneCorrente, Operazioni, Escalation, StoricoRevisioni, Allegati, TicketCollegati };

    /// <summary>Default "di fabbrica" usato solo se il Master non ha ancora salvato un default condiviso.</summary>
    public static readonly string[] Minime = { DatiGenerali, RevisioneCorrente };

    /// <summary>Prefisso delle chiavi dinamiche generate una per ogni SezioneTemplate del tipo di test
    /// corrente (feedback utente 2026-08-21, punto 10): prima ogni campo custom di qualunque sezione finiva
    /// indistinto in un unico bucket fisso "Campi custom", senza poter includere/escludere una singola
    /// sezione. Ora ogni sezione del template è una voce a sé, separata dal catalogo fisso qui sopra.</summary>
    public const string PrefissoSezioneDinamica = "Sezione:";

    public static string ChiaveSezioneDinamica(SezioneTemplate sezione) => PrefissoSezioneDinamica + sezione.Uid;

    public static bool EDinamica(string chiave) => chiave.StartsWith(PrefissoSezioneDinamica, StringComparison.Ordinal);
}

/// <summary>Sezioni selezionabili del pacchetto di una Change Request (feedback utente 2026-08-21, punto 7:
/// "esporta pacchetto CR" deve avere lo stesso dialogo a sezioni di "genera pacchetto Ticketing" nei Test),
/// stesso principio di PolarionSezioni ma sui campi propri della CR invece che del Test.</summary>
public static class CrSezioni
{
    public const string DatiGenerali = "DatiGenerali";
    public const string Ambito = "Ambito";
    public const string CampiCr = "CampiCr";
    public const string TestValidazione = "TestValidazione";
    public const string TicketCollegati = "TicketCollegati";
    public const string Allegati = "Allegati";

    public static readonly string[] Tutte = { DatiGenerali, Ambito, CampiCr, TestValidazione, TicketCollegati, Allegati };
}

/// <summary>Sezioni del pacchetto Ticketing per una Non Conformità senza Test (feedback utente 2026-09-15):
/// stesso schema di CrSezioni, dato che una NC non ha una storia di esecuzioni/condizioni da riportare, esattamente
/// come una Change Request. Niente "Requisiti"/"Test collegati": concetti propri dei Test, non applicabili qui.</summary>
public static class NcSezioni
{
    public const string Origine = "Origine";
    public const string DatiGenerali = "DatiGenerali";
    public const string Ambito = "Ambito";
    public const string CampiStrumento = "CampiStrumento";
    public const string TicketCollegati = "TicketCollegati";
    public const string Allegati = "Allegati";

    public static readonly string[] Tutte = { Origine, DatiGenerali, Ambito, CampiStrumento, TicketCollegati, Allegati };
}

/// <summary>Esporta un test come testo semplice, pensato per essere incollato nel form di apertura di una
/// Claim — solo un aiuto pratico, l'app non si integra col sistema esterno dove la claim vive davvero.
/// Le sezioni incluse sono scelte dall'utente (Addendum §4, feedback QA), non più tutte per forza.</summary>
public static class TicketExportService
{
    public static string GeneraTesto(ChangeRequest cr, AppState state, IReadOnlySet<string> sezioni)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Change Request — pacchetto TICKET/Ticketing ===");
        sb.AppendLine($"Generato il {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Operatore: {state.Config.OperatoreAttivo}");
        sb.AppendLine();
        if (sezioni.Contains(CrSezioni.DatiGenerali))
        {
            sb.AppendLine("--- Dati generali ---");
            Riga(sb, "ID", cr.Id);
            Riga(sb, "Tipo", state.NomeTestTemplate(cr.TemplateUid));
            Riga(sb, "Titolo", cr.Titolo);
            Riga(sb, "Descrizione breve", cr.DescrizioneBreve);
            Riga(sb, "Stato", cr.Stato == StatoChangeRequest.DaValidare ? "Da validare" : cr.Stato.ToString());
            Riga(sb, "Responsabile", cr.Responsabile);
            Riga(sb, "Descrizione tecnica", cr.Descrizione);
            Riga(sb, "Motivazione possibile duplicato", cr.MotivazioneDuplicato);
            sb.AppendLine();
        }
        if (sezioni.Contains(CrSezioni.Ambito))
        {
            sb.AppendLine("--- Ambito impattato ---");
            Riga(sb, "Veicoli impattati", string.Join(", ", cr.VeicoliImpattati));
            Riga(sb, "Componente impattate", string.Join(", ", cr.EcuImpattate));
            Riga(sb, "Strumenti impattati", string.Join(", ", cr.StrumentiImpattati));
            sb.AppendLine();
        }
        if (sezioni.Contains(CrSezioni.CampiCr) && cr.CampiCustom.Count > 0)
        {
            sb.AppendLine("--- Campi CR ---");
            // Chiave = Uid del campo libero nel template CR (ChangeRequestEditorViewModel.CaricaCampiCr): il pacchetto stampa il nome del campo.
            var template = TemplateDi(state, cr.TemplateUid);
            var catalogo = state.CatalogoCampiSistemaCompleto();
            var etichette = template == null ? new Dictionary<string, string>() : EtichetteCampiTemplate(CampiDelTemplate(template), catalogo);
            foreach (var (chiave, valore) in cr.CampiCustom) Riga(sb, EtichettaChiaveCampoCr(chiave, etichette, catalogo), valore);
            sb.AppendLine();
        }
        if (sezioni.Contains(CrSezioni.TestValidazione) && cr.TestValidazione.Count > 0)
        {
            sb.AppendLine("--- Test di validazione ---");
            foreach (var link in cr.TestValidazione)
                sb.AppendLine($"{link.TestId} | {(link.Obbligatorio ? "obbligatorio" : "facoltativo")} | {(link.SnapshotValidazione == null ? "non validante" : link.SnapshotValidazione.Esito.Label())}");
            sb.AppendLine();
        }
        if (sezioni.Contains(CrSezioni.TicketCollegati) && cr.TicketCollegati.Count > 0)
        {
            sb.AppendLine("--- Ticket collegati ---");
            foreach (var ticket in cr.TicketCollegati) sb.AppendLine(ticket.Riferimento);
            sb.AppendLine();
        }
        if (sezioni.Contains(CrSezioni.Allegati) && cr.Allegati.Count > 0)
        {
            sb.AppendLine("--- Allegati (presenti nella cartella condivisa, non inclusi in questo testo) ---");
            foreach (var a in cr.Allegati) sb.AppendLine($"  {a.NomeFile} ({a.DimensioneByte / 1024} KB, caricato da {a.CaricatoDa})");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Non Conformità senza Test (feedback utente 2026-09-15): stesso schema del pacchetto CR, dato che
    /// una segnalazione di rete non ha una storia di esecuzioni/condizioni da riportare. Per una NC che invece
    /// ha già un Test collegato, l'ambito/i dettagli restano quelli originariamente segnalati dalla rete (vedi
    /// NonConformita.EcuSegnalate e affini) — non si legge il Test collegato, che ha già un proprio pacchetto.</summary>
    public static string GeneraTesto(NonConformita nc, AppState state, IReadOnlySet<string> sezioni)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Segnalazione rete assistenziale — pacchetto TICKET/Ticketing ===");
        sb.AppendLine($"Generato il {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Operatore: {state.Config.OperatoreAttivo}");
        sb.AppendLine();
        if (sezioni.Contains(NcSezioni.Origine))
        {
            sb.AppendLine("--- Origine segnalazione ---");
            Riga(sb, "Numero pratica/ticket rete", string.Join(", ", nc.NumeriPraticaRete));
            Riga(sb, "Matricola veicolo cliente", string.Join(", ", nc.VinCliente));
            Riga(sb, "Concessionaria/officina", nc.ConcessionariaOfficina);
            sb.AppendLine();
        }
        if (sezioni.Contains(NcSezioni.DatiGenerali))
        {
            sb.AppendLine("--- Dati generali ---");
            Riga(sb, "ID", nc.Id);
            Riga(sb, "Sintesi", nc.DescrizioneBreveSegnalata);
            Riga(sb, "Descrizione", nc.Descrizione);
            Riga(sb, "Severità", nc.Severita?.ToString());
            Riga(sb, "Priorità", nc.Priorita.ToString());
            Riga(sb, "Owner", nc.Owner);
            sb.AppendLine();
        }
        if (sezioni.Contains(NcSezioni.Ambito))
        {
            sb.AppendLine("--- Ambito segnalato ---");
            Riga(sb, "Componente", string.Join(", ", nc.EcuSegnalate.AsEnumerable()));
            Riga(sb, "Veicoli", string.Join(", ", nc.VeicoliSegnalati));
            Riga(sb, "Funzionalità", string.Join(", ", nc.FunzionalitaSegnalate));
            Riga(sb, "Strumento", nc.StrumentoSegnalato);
            sb.AppendLine();
        }
        if (sezioni.Contains(NcSezioni.CampiStrumento) && nc.ValoriStrumentoSegnalato.Count > 0)
        {
            sb.AppendLine($"--- Strumento diagnostico: {nc.StrumentoSegnalato} ---");
            foreach (var (chiave, valore) in nc.ValoriStrumentoSegnalato.Where(kv => !EvidenzaOperazione.EChiaveRiservata(kv.Key)))
                Riga(sb, EtichettaValoreStrumento(chiave), valore);
            sb.AppendLine();
        }
        if (sezioni.Contains(NcSezioni.TicketCollegati) && nc.CollegamentiPolarion.Count > 0)
        {
            sb.AppendLine("--- Ticket collegati ---");
            foreach (var ticket in nc.CollegamentiPolarion) sb.AppendLine(ticket.Codice);
            sb.AppendLine();
        }
        if (sezioni.Contains(NcSezioni.Allegati))
        {
            var origine = EvidenzeOperazioneOrigine(nc, state);
            if (origine.Evidenze.Count > 0)
            {
                sb.AppendLine($"--- Evidenze dell'operazione di origine (Test {origine.Test!.Id}, REV {origine.Iterazione}; presenti nella cartella condivisa) ---");
                foreach (var a in origine.Evidenze) sb.AppendLine($"  {a.NomeFile} ({a.DimensioneByte / 1024} KB, caricato da {a.CaricatoDa})");
                sb.AppendLine();
            }
        }
        if (sezioni.Contains(NcSezioni.Allegati) && nc.Allegati.Count > 0)
        {
            sb.AppendLine("--- Allegati (presenti nella cartella condivisa, non inclusi in questo testo) ---");
            foreach (var a in nc.Allegati) sb.AppendLine($"  {a.NomeFile} ({a.DimensioneByte / 1024} KB, caricato da {a.CaricatoDa})");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Evidenze dell'operazione da cui nasce la NC, risolte dal Test (TestUid + CondizioneOrigineUid + REV), senza copiare alcun file:
    /// restano allegati del Test. Vuoto se la NC non nasce da un'operazione o il Test non e' piu' disponibile.</summary>
    public static (TestCase? Test, int Iterazione, IReadOnlyList<Allegato> Evidenze) EvidenzeOperazioneOrigine(NonConformita nc, AppState state)
    {
        if (nc.TestUid is not { } testUid || nc.CondizioneOrigineUid is not { } condUid || !state.CartellaValida)
            return (null, 0, Array.Empty<Allegato>());
        var test = state.TestCases.GetByUid(testUid);
        if (test is null) return (null, 0, Array.Empty<Allegato>());
        var esecuzione = test.Esecuzioni.FirstOrDefault(e => e.Iterazione == nc.EsecuzioneIterazione && e.Condizioni.Any(c => c.Uid == condUid))
                         ?? test.Esecuzioni.LastOrDefault(e => e.Condizioni.Any(c => c.Uid == condUid));
        var condizione = esecuzione?.Condizioni.FirstOrDefault(c => c.Uid == condUid);
        return condizione is null || esecuzione is null
            ? (test, 0, Array.Empty<Allegato>())
            : (test, esecuzione.Iterazione, EvidenzaOperazione.Evidenze(test, condizione));
    }

    public static string GeneraTesto(TestCase tc, AppState state, IReadOnlySet<string> sezioni)
    {
        var sb = new StringBuilder();
        var veicoli = state.CartellaValida ? state.Master.Veicoli.Load() : new List<Veicolo>();
        var gamma = string.Join(", ", tc.VeicoliEffettivi.Select(nome => veicoli.FirstOrDefault(v => v.Nome == nome)?.GammaNome).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct());
        var template = state.CartellaValida ? state.Master.TestTemplate.Load().FirstOrDefault(t => t.Uid == tc.TestTemplateUid) : null;
        var catalogoCampiSistema = state.CartellaValida ? state.CatalogoCampiSistemaCompleto() : new List<CampoSistemaDefinizione>();

        // Filtra i campi strumento agli strumenti attualmente selezionati sul test (feedback utente 2026-08-19,
        // punto 3 — "tiene anche dati delle operazioni delle revisioni precedenti"): deselezionare uno
        // strumento non rimuove mai le sue chiavi da ValoriStrumenti/ValoriStrumentiTest (restano nel JSON per
        // non perdere dati se poi lo strumento viene riselezionato), ma senza questo filtro comparivano ancora
        // nell'export come se fossero dati correnti.
        var strumentiCorrenti = new HashSet<string>(tc.StrumentiUsati, StringComparer.OrdinalIgnoreCase);

        sb.AppendLine("=== Riferimento per apertura Claim — TestLoom ===");
        sb.AppendLine($"Generato il {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Operatore: {state.Config.OperatoreAttivo}");
        sb.AppendLine();

        if (sezioni.Contains(PolarionSezioni.DatiGenerali))
        {
            sb.AppendLine("--- Dati generali ---");
            Riga(sb, "ID test", tc.Id);
            Riga(sb, "Componente", string.Join(", ", tc.Componente.AsEnumerable()));
            Riga(sb, "Veicoli", string.Join(", ", tc.VeicoliEffettivi));
            Riga(sb, "Gamma veicolare", gamma);
            Riga(sb, "Strumenti usati", string.Join(", ", tc.StrumentiUsati));
            Riga(sb, "Tipo test", state.NomeTestTemplate(tc.TestTemplateUid));
            Riga(sb, "Stato del lavoro", tc.StatoLavoro.Label());
            Riga(sb, "Assegnato a", tc.Assegnatario);
            if (tc.FuoriCategoria) Riga(sb, "Fuori categoria", "Sì");
            Riga(sb, "Descrizione breve", tc.DescrizioneBreve);
            Riga(sb, "Descrizione", tc.Descrizione);
            Riga(sb, "Descrizione problema", tc.DescrizioneProblema);
            sb.AppendLine();
        }

        if (sezioni.Contains(PolarionSezioni.CampiStrumento) && tc.ValoriStrumentiTest.Count > 0)
        {
            sb.AppendLine("--- Campi strumento (costanti per il test) ---");
            AppendValoriStrumenti(sb, tc.ValoriStrumentiTest, strumentiCorrenti);
            sb.AppendLine();
        }

        if (sezioni.Contains(PolarionSezioni.Riferimenti) && (tc.RequisitoId.Count > 0 || tc.Funzionalita.Count > 0))
        {
            sb.AppendLine("--- Riferimenti ---");
            if (tc.RequisitoId.Count > 0) Riga(sb, "Requisiti collegati", string.Join(", ", tc.RequisitoId));
            if (tc.Funzionalita.Count > 0) Riga(sb, "Funzionalità collegate", string.Join(", ", tc.Funzionalita));
            if (tc.StrumentiUsati.Count > 0) Riga(sb, "Strumenti usati", string.Join(", ", tc.StrumentiUsati));
            sb.AppendLine();
        }

        var ultima = tc.Esecuzioni.Count > 0 ? tc.Esecuzioni[^1] : null;
        if (ultima != null && sezioni.Contains(PolarionSezioni.RevisioneCorrente))
        {
            sb.AppendLine($"--- Revisione corrente (#{tc.NumeroRevisione}) ---");
            Riga(sb, "Data", ultima.Data.ToString("dd/MM/yyyy HH:mm"));
            Riga(sb, "Operatore", ultima.OperatoreVisualizzato);
            Riga(sb, "Esito", ultima.Esito.Label());
            Riga(sb, "Identificativo veicolo/banco (matricola)", ultima.Matricola);
            Riga(sb, "Baseline SW", ultima.BaselineSw);
            Riga(sb, "Baseline HW", ultima.BaselineHw);
            Riga(sb, "Deviazioni", ultima.Deviazioni);
            Riga(sb, "Note", ultima.Note);
            if (ultima.EsitoSovrascritto) Riga(sb, "Esito corretto manualmente", ultima.MotivazioneOverride);
            AppendValoriStrumenti(sb, ultima.ValoriStrumenti, strumentiCorrenti);
            sb.AppendLine();
        }
        if (ultima != null && sezioni.Contains(PolarionSezioni.Operazioni))
        {
            for (int i = 0; i < ultima.Condizioni.Count; i++)
            {
                var cond = ultima.Condizioni[i];
                var etichetta = string.IsNullOrEmpty(cond.Funzionalita) ? $"Operazione #{i + 1}" : cond.Funzionalita;
                sb.AppendLine($"--- {etichetta} — esito: {cond.Esito.Label()} ---");
                AppendValoriStrumenti(sb, cond.ValoriStrumenti, strumentiCorrenti);
                Riga(sb, "Nota", cond.Nota);
                Riga(sb, "Motivazione N.A.", cond.MotivazioneNonApplicabile);
                var evidenzeOp = EvidenzaOperazione.Evidenze(tc, cond);
                if (evidenzeOp.Count > 0) Riga(sb, "Evidenze", string.Join(", ", evidenzeOp.Select(a => a.NomeFile)));
                sb.AppendLine();
            }
        }

        if (sezioni.Contains(PolarionSezioni.Escalation) && tc.TestCollegatiCreati.Count > 0)
        {
            sb.AppendLine("--- Test collegati creati ---");
            foreach (var creato in tc.TestCollegatiCreati) Riga(sb, "Test collegato", creato.TestCollegatoId);
            sb.AppendLine();
        }

        if (sezioni.Contains(PolarionSezioni.DatiGenerali) && tc.TestCollegatoRichiestoId != null)
        { Riga(sb, "Test collegato", tc.TestCollegatoRichiestoId); sb.AppendLine(); }

        if (sezioni.Contains(PolarionSezioni.StoricoRevisioni) && tc.Esecuzioni.Count > 1)
        {
            sb.AppendLine("--- Storico revisioni precedenti ---");
            foreach (var e in tc.Esecuzioni.Take(tc.Esecuzioni.Count - 1).Reverse())
                sb.AppendLine($"  #{e.Iterazione} · {e.Data:dd/MM/yyyy} · {e.OperatoreVisualizzato} · {e.Esito.Label()}" + (string.IsNullOrWhiteSpace(e.Deviazioni) ? "" : $" · {e.Deviazioni}"));
            sb.AppendLine();
        }

        if (sezioni.Contains(PolarionSezioni.Allegati) && tc.Allegati.Count > 0)
        {
            sb.AppendLine("--- Allegati (presenti nella cartella condivisa, non inclusi in questo testo) ---");
            var etichetteOp = EvidenzaOperazione.EtichettePerAllegato(tc);
            foreach (var a in tc.Allegati)
                sb.AppendLine($"  {a.NomeFile} ({a.DimensioneByte / 1024} KB, caricato da {a.CaricatoDa})" +
                              (etichetteOp.TryGetValue(a.Uid, out var op) ? $" — evidenza di: {op}" : ""));
            sb.AppendLine();
        }

        if (sezioni.Contains(PolarionSezioni.TicketCollegati) && tc.TicketCollegati.Count > 0)
        {
            sb.AppendLine("--- Ticket già collegati ---");
            foreach (var t in tc.TicketCollegati) sb.AppendLine($"  {t.Riferimento}");
            sb.AppendLine();
        }

        if (template != null)
        {
            foreach (var sezioneTemplate in template.Sezioni)
            {
                if (!sezioni.Contains(PolarionSezioni.ChiaveSezioneDinamica(sezioneTemplate))) continue;
                var righe = sezioneTemplate.Campi
                    .Select(campo => (Nome: EtichettaCampoCustom(campo, catalogoCampiSistema), Valore: tc.CampiCustom.GetValueOrDefault(ChiaveCampoCustom(campo))))
                    .Where(r => !string.IsNullOrWhiteSpace(r.Valore))
                    .ToList();
                if (righe.Count == 0) continue;
                sb.AppendLine($"--- {sezioneTemplate.Nome} ---");
                foreach (var (nome, valore) in righe) Riga(sb, nome, valore);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>Stessa chiave usata per leggere/scrivere TestCase.CampiCustom di un campo del template
    /// (TestCaseEditorViewModel.CostruisciCampoCustomValore): Uid per i CampoLibero, CampoSistemaChiave per i
    /// CampoSistema custom (Ruolo=Nessuno) — deve restare in sincrono con quella logica o l'export leggerebbe
    /// sempre valori vuoti.</summary>
    private static string ChiaveCampoCustom(CampoTemplate campo) =>
        campo.Tipo == TipoCampoTemplate.CampoLibero ? campo.Uid.ToString() : campo.CampoSistemaChiave!;

    /// <summary>Etichetta di un campo del template. Un campo libero senza nome non mostra mai il suo Uid (un GUID): il pacchetto
    /// scrive una nota. Un campo di sistema senza nome prende l'etichetta del catalogo, o una nota se la chiave non esiste piu'.</summary>
    private static string EtichettaCampoCustom(CampoTemplate campo, IReadOnlyList<CampoSistemaDefinizione> catalogo)
    {
        if (!string.IsNullOrWhiteSpace(campo.Nome)) return campo.Nome;
        if (campo.Tipo == TipoCampoTemplate.CampoLibero) return "(campo senza nome)";
        var definizione = catalogo.FirstOrDefault(c => c.Chiave == campo.CampoSistemaChiave);
        return definizione?.Etichetta ?? $"{campo.CampoSistemaChiave} (campo non più configurato)";
    }

    /// <summary>Template di un Test o di una CR dal Master. Senza cartella valida non c'e' template: le etichette ricadono
    /// sul catalogo dei campi di sistema e sulla nota "campo non più configurato".</summary>
    private static TestTemplate? TemplateDi(AppState state, Guid templateUid) =>
        state.CartellaValida ? state.Master.TestTemplate.Load().FirstOrDefault(t => t.Uid == templateUid) : null;

    /// <summary>Campi di una CR: pianificazione e sezioni, come nell'editor CR (ChangeRequestEditorViewModel.CaricaCampiCr).</summary>
    private static IEnumerable<CampoTemplate> CampiDelTemplate(TestTemplate template) =>
        template.CampiPianificazione.Concat(template.Sezioni.SelectMany(s => s.Campi));

    /// <summary>Etichette per chiave di CampiCustom, dai campi del template: stessa chiave di ChiaveCampoCustom.</summary>
    private static Dictionary<string, string> EtichetteCampiTemplate(IEnumerable<CampoTemplate> campi, IReadOnlyList<CampoSistemaDefinizione> catalogo)
    {
        var etichette = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var campo in campi)
        {
            var chiave = ChiaveCampoCustom(campo);
            if (!string.IsNullOrEmpty(chiave)) etichette.TryAdd(chiave, EtichettaCampoCustom(campo, catalogo));
        }
        return etichette;
    }

    /// <summary>Etichetta di una chiave di ChangeRequest.CampiCustom: prima il campo del template, poi il catalogo dei campi di
    /// sistema. Una chiave che non corrisponde piu' a nessun campo resta leggibile con una nota, invece del solo GUID.</summary>
    internal static string EtichettaChiaveCampoCr(string chiave, IReadOnlyDictionary<string, string> etichetteTemplate, IReadOnlyList<CampoSistemaDefinizione> catalogo)
    {
        if (etichetteTemplate.TryGetValue(chiave, out var etichetta)) return etichetta;
        return catalogo.FirstOrDefault(c => c.Chiave == chiave)?.Etichetta ?? $"{chiave} (campo non più configurato)";
    }

    /// <summary>Chiave interna "Strumento::Campo" -> "Strumento - Campo": la chiave tecnica non compare mai nel pacchetto.</summary>
    private static string EtichettaValoreStrumento(string chiave)
    {
        var parti = chiave.Split("::", 2);
        return parti.Length == 2 ? $"{parti[0]} - {parti[1]}" : chiave;
    }

    /// <summary>Chiave "Strumento::Campo" -> "Strumento - Campo: valore", generico per qualsiasi strumento e
    /// campo configurato da Impostazioni → Strumenti (Addendum redesign 2026-08, revisione feedback QA).
    /// strumentiCorrenti, se passato, esclude le chiavi di uno strumento non più selezionato sul test (mai
    /// ripulite dal dizionario alla deselezione — feedback utente 2026-08-19, punto 3).</summary>
    internal static void AppendValoriStrumenti(StringBuilder sb, Dictionary<string, string> valori, IReadOnlySet<string>? strumentiCorrenti = null)
    {
        foreach (var (chiave, valore) in valori)
        {
            // La durata (TIME) e' una chiave riservata: non e' un campo strumento e non va nel testo del ticket.
            if (EvidenzaOperazione.EChiaveRiservata(chiave)) continue;
            var parti = chiave.Split("::", 2);
            if (strumentiCorrenti != null && parti.Length == 2 && !strumentiCorrenti.Contains(parti[0])) continue;
            Riga(sb, EtichettaValoreStrumento(chiave), valore);
        }
    }

    private static void Riga(StringBuilder sb, string etichetta, string? valore)
    {
        if (string.IsNullOrWhiteSpace(valore)) return;
        sb.AppendLine($"{etichetta}: {valore}");
    }

    public static string Salva(DataPaths paths, TestCase tc, string testo) =>
        ReportService.Salva(paths, $"Claim_{tc.Id}", "txt", Encoding.UTF8.GetBytes(testo));
}
