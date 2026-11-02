using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Motore transazionale di Unisci/Rinomina/Dividi per QUALUNQUE catalogo master registrato in
/// <see cref="MasterRiferimentiService.Registro"/> (spec 2026-09-23 "spec_unisci_master.md"), stesso modello di
/// WuStatusViewModel.Unisci/RevisioneUdtService + WorkflowTransactionService.Begin (spec punto 6: atomicità,
/// un conflitto su un qualunque record annulla tutto). Solo orchestrazione qui: la ricerca/sostituzione dei
/// riferimenti vive in MasterRiferimentiService, la UI in MasterUnioneViewModel.
///
/// Permessi: solo Master (spec punto 1) — verificato qui in aggiunta al ViewModel (difesa in profondità, non
/// solo un IsEnabled lato UI).
/// </summary>
public static class MasterUnioneService
{
    private static void RichiedeMaster(AppState state)
    {
        if (!state.EMaster)
            throw new UnauthorizedAccessException("Unisci/Rinomina/Dividi sui cataloghi master richiede il ruolo Master.");
    }

    /// <summary>Unisce <paramref name="scartata"/> in <paramref name="corretta"/>: tutti i riferimenti diventano
    /// <paramref name="corretta"/>, le proprietà utili di <paramref name="scartata"/> vengono fuse (unione, mai
    /// perse) e la voce scartata viene rimossa dal catalogo (spec punto 2).</summary>
    public static void Unisci<T>(
        AppState state, MasterDataRepository<T> repo, string catalogo, string scartata, string corretta,
        Func<T, string> keyOf, List<string> avvisi, IProgress<(int Fatti, int Totale)>? progresso = null) where T : MasterEntity, new()
    {
        RichiedeMaster(state);
        if (string.IsNullOrWhiteSpace(scartata) || string.IsNullOrWhiteSpace(corretta))
            throw new ArgumentException("Voce scartata e voce corretta sono obbligatorie.");
        if (scartata.Equals(corretta, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("La voce scartata e la voce corretta coincidono.");

        using var tx = WorkflowTransactionService.Begin(state.Paths, $"merge-master:{catalogo}:{scartata}->{corretta}");

        var catalogoElementi = repo.Load();
        var attesoCatalogo = EntityStateFingerprint.Clone(catalogoElementi);
        var elementoScartato = catalogoElementi.FirstOrDefault(e => keyOf(e).Equals(scartata, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"La voce '{scartata}' non è più disponibile nel catalogo.");
        var elementoCorretto = catalogoElementi.FirstOrDefault(e => keyOf(e).Equals(corretta, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"La voce '{corretta}' non è più disponibile nel catalogo.");

        if (elementoScartato is Requisito rs && elementoCorretto is Requisito rc && RifiutoUnioneTipiDiversi(state, rs, rc) is { } rifiuto)
            throw new InvalidOperationException(rifiuto);

        FondiProprietaSpecifiche(elementoScartato, elementoCorretto, avvisi);
        // Requisiti Extra: i guasti vivono nell'archivio per requisito. La destinazione riceve le voci dell'origine (unione per
        // Codice|componente) con scrittura dell'archivio PRIMA del commit; l'archivio dell'origine si tombstona solo a commit riuscito.
        var rimuoviArchivioOrigine = false;
        if (elementoScartato is Requisito reqScartato && elementoCorretto is Requisito reqCorretto)
        {
            try { state.Master.RequisitiDtc.UnisciInDestinazione(reqScartato, reqCorretto, state.Config.OperatoreAttivo); }
            catch (MasterDataConflictException ex)
            {
                throw new InvalidOperationException($"Unione annullata: i guasti del requisito '{corretta}' sono stati modificati da un altro PC. Riprova ({ex.Message}).", ex);
            }
            rimuoviArchivioOrigine = true;
        }

        var totaleStimato = StimaTotaleRecordCoinvolti(state, catalogo, scartata);
        AggiornaRecordDominio(state, catalogo, scartata, new[] { corretta }, avvisi, progresso, totaleStimato);
        MasterRiferimentiService.ApplicaSuCataloghiIncrociati(state.Master, catalogo, scartata, new[] { corretta }, avvisi, state.Config.OperatoreAttivo);
        RipristinaInvarianteEcuAssociate(state, catalogo, avvisi);

        catalogoElementi.Remove(elementoScartato);
        elementoCorretto.Changelog.Add(new ChangelogEntry
        {
            Data = DateTime.Now,
            Operatore = state.Config.OperatoreAttivo,
            Campo = "(unione)",
            ValorePrecedente = scartata,
            ValoreNuovo = $"unita in questa voce ({corretta})"
        });
        elementoCorretto.UltimaModifica = DateTime.Now;
        elementoCorretto.ModificatoDa = state.Config.OperatoreAttivo;

        repo.Save(catalogoElementi, attesoCatalogo);
        tx.Commit();
        if (rimuoviArchivioOrigine && elementoScartato is Requisito origine)
        {
            try { state.Master.RequisitiDtc.Rimuovi(origine, state.Config.OperatoreAttivo); }
            catch (Exception ex) { CrashLogService.Registra($"Archivio guasto del requisito unito '{scartata}' non rimosso (resta orfano, ignorato in lettura)", ex); }
        }
    }

    /// <summary>Unione requisiti di TIPO diverso (Extra/Standard) con guasto nell'origine: i guasti finirebbero in un requisito senza pannello
    /// guasto (irraggiungibili) e l'archivio origine verrebbe tombstonato. Ritorna il messaggio localizzato di rifiuto, altrimenti null.
    /// Non scrive nulla.</summary>
    public static string? RifiutoUnioneTipiDiversi(AppState state, Requisito scartato, Requisito corretto)
    {
        var ts = RequisitoApplicabilita.TipoDi(scartato); var tc = RequisitoApplicabilita.TipoDi(corretto);
        if (ts == tc) return null;
        if (ts == TipoRequisito.Extra && tc == TipoRequisito.Standard &&
            (scartato.VeicoliEcuD4D?.Count > 0 || RequisitoApplicabilita.SottoveicoliDi(scartato).Count > 0))
            return LocalizationService.Instance.Scegli("Unione annullata: il requisito Extra contiene associazioni veicolo o componente che non sarebbero accessibili nel requisito Standard.",
                "Merge cancelled: the Extra requirement contains vehicle or component associations that would not be accessible in the Standard requirement.",
                "Fusion annulée : les associations véhicule ou componente de l’exigence Extra ne seraient pas accessibles dans l’exigence Standard.");
        state.Master.RequisitiDtc.Risolvi(new[] { scartato });
        if (scartato.DtcTotale <= 0) return null;
        return string.Format(LocalizationService.Instance.Traduci("MasterUnioneTipiDiversiDtcFmt"),
            scartato.Id, ts, corretto.Id, tc, scartato.DtcTotale);
    }

    /// <summary>Rinomina <paramref name="vecchio"/> in <paramref name="nuovo"/> mantenendo Uid/storia della
    /// voce: stesso motore di Unisci, ma la voce non viene rimossa, solo rinominata (spec punto 3). Il chiamante
    /// deve avere già verificato che <paramref name="nuovo"/> non esista (altrimenti proporre Unisci).</summary>
    public static void Rinomina<T>(
        AppState state, MasterDataRepository<T> repo, string catalogo, string vecchio, string nuovo,
        Func<T, string> keyOf, List<string> avvisi, IProgress<(int Fatti, int Totale)>? progresso = null) where T : MasterEntity, new()
    {
        RichiedeMaster(state);
        if (string.IsNullOrWhiteSpace(vecchio) || string.IsNullOrWhiteSpace(nuovo))
            throw new ArgumentException("Nome precedente e nuovo nome sono obbligatori.");
        nuovo = nuovo.Trim(); // niente spazi iniziali/finali nella chiave salvata.
        // Solo un nome IDENTICO (maiuscole incluse) e' un no-op: "componente1" -> "Componente1" e' una rinomina legittima (cambia
        // la grafia); il controllo di duplicato sotto esclude la voce stessa, quindi non collide con se' stessa.
        if (vecchio.Equals(nuovo, StringComparison.Ordinal))
            throw new ArgumentException("Il nuovo nome coincide con quello precedente.");

        using var tx = WorkflowTransactionService.Begin(state.Paths, $"rename-master:{catalogo}:{vecchio}->{nuovo}");

        var catalogoElementi = repo.Load();
        var attesoCatalogo = EntityStateFingerprint.Clone(catalogoElementi);
        var elemento = catalogoElementi.FirstOrDefault(e => keyOf(e).Equals(vecchio, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"La voce '{vecchio}' non è più disponibile nel catalogo.");
        if (catalogoElementi.Any(e => !ReferenceEquals(e, elemento) && keyOf(e).Equals(nuovo, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Esiste già una voce '{nuovo}': usare Unisci invece di Rinomina.");

        var totaleStimato = StimaTotaleRecordCoinvolti(state, catalogo, vecchio);
        AggiornaRecordDominio(state, catalogo, vecchio, new[] { nuovo }, avvisi, progresso, totaleStimato);
        MasterRiferimentiService.ApplicaSuCataloghiIncrociati(state.Master, catalogo, vecchio, new[] { nuovo }, avvisi, state.Config.OperatoreAttivo);
        RipristinaInvarianteEcuAssociate(state, catalogo, avvisi);

        ImpostaChiave(elemento, keyOf, nuovo);
        elemento.Changelog.Add(new ChangelogEntry
        {
            Data = DateTime.Now,
            Operatore = state.Config.OperatoreAttivo,
            Campo = "(rinomina)",
            ValorePrecedente = vecchio,
            ValoreNuovo = nuovo
        });
        elemento.UltimaModifica = DateTime.Now;
        elemento.ModificatoDa = state.Config.OperatoreAttivo;

        repo.Save(catalogoElementi, attesoCatalogo);
        tx.Commit();
    }

    /// <summary>Divide <paramref name="originale"/> aggiungendo <paramref name="nuovi"/> (almeno una): decisione
    /// utente 2026-09-30, l'originale NON viene mai rimosso (scrivere "A, Nuova" sembrava una rinomina perché A
    /// spariva). Le nuove voci non ancora nel catalogo vengono create copiando le proprietà dell'originale; i
    /// campi lista dei record che referenziavano l'originale ottengono originale + nuove voci, i campi singoli
    /// (Test.Componente, Veicolo, RequisitoId...) restano sull'originale. I nomi uguali all'originale (senza
    /// distinzione di maiuscole) o duplicati vengono ignorati. Requisiti Extra: le copie nascono SENZA guasto (nuovo Uid, nessun
    /// archivio) e l'originale conserva i suoi: i guasti non vengono ne duplicati ne persi; vanno assegnati alla copia a mano.</summary>
    public static void Dividi<T>(
        AppState state, MasterDataRepository<T> repo, string catalogo, string originale, IReadOnlyList<string> nuovi,
        Func<T, string> keyOf, List<string> avvisi, IProgress<(int Fatti, int Totale)>? progresso = null) where T : MasterEntity, new()
    {
        RichiedeMaster(state);
        if (string.IsNullOrWhiteSpace(originale)) throw new ArgumentException("La voce da dividere è obbligatoria.");
        nuovi = nuovi.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
            .Where(n => !n.Equals(originale, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (nuovi.Count < 1) throw new ArgumentException("Dividi richiede almeno una nuova voce di destinazione, diversa dall'originale.");

        using var tx = WorkflowTransactionService.Begin(state.Paths, $"split-master:{catalogo}:{originale}->{string.Join("+", nuovi)}");

        var catalogoElementi = repo.Load();
        var attesoCatalogo = EntityStateFingerprint.Clone(catalogoElementi);
        var elementoOriginale = catalogoElementi.FirstOrDefault(e => keyOf(e).Equals(originale, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"La voce '{originale}' non è più disponibile nel catalogo.");

        var nuoveVociCreate = new List<T>();
        foreach (var nome in nuovi)
        {
            if (catalogoElementi.Any(e => keyOf(e).Equals(nome, StringComparison.OrdinalIgnoreCase))) continue;
            var nuovaVoce = new T { UltimaModifica = DateTime.Now, ModificatoDa = state.Config.OperatoreAttivo, CreatoDa = state.Config.OperatoreAttivo };
            ImpostaChiave(nuovaVoce, keyOf, nome);
            CopiaProprietaSpecifiche(elementoOriginale, nuovaVoce);
            nuovaVoce.Changelog.Add(new ChangelogEntry
            {
                Data = DateTime.Now, Operatore = state.Config.OperatoreAttivo, Campo = "(divisione)",
                ValorePrecedente = originale, ValoreNuovo = $"creata dalla divisione di '{originale}'"
            });
            nuoveVociCreate.Add(nuovaVoce);
        }

        // Sostituzione "originale -> [originale, ...nuovi]": le liste mantengono A e ottengono le nuove voci (dedup
        // in SostituisciInProprieta), i campi singoli restano su A (nuovi[0] == originale, nessuna scrittura).
        var sostituzione = new List<string> { originale };
        // Ortografia canonica: un nome digitato con maiuscole diverse da una voce gia' in catalogo ("componente2" vs
        // "Componente2") usa la voce esistente, non crea record con la grafia digitata.
        sostituzione.AddRange(nuovi.Select(n =>
        {
            var esistente = catalogoElementi.FirstOrDefault(e => keyOf(e).Equals(n, StringComparison.OrdinalIgnoreCase));
            return esistente != null ? keyOf(esistente) : n;
        }));
        var totaleStimato = StimaTotaleRecordCoinvolti(state, catalogo, originale);
        AggiornaRecordDominio(state, catalogo, originale, sostituzione, avvisi, progresso, totaleStimato);
        MasterRiferimentiService.ApplicaSuCataloghiIncrociati(state.Master, catalogo, originale, sostituzione, avvisi, state.Config.OperatoreAttivo);
        RipristinaInvarianteEcuAssociate(state, catalogo, avvisi);

        catalogoElementi.AddRange(nuoveVociCreate);

        repo.Save(catalogoElementi, attesoCatalogo);
        tx.Commit();
    }

    /// <summary>Totale usato solo per l'avanzamento "n/N" mostrato in UI (review 2026-09-23, MEDIUM: nessun
    /// feedback su un'operazione che a scala può durare minuti). Riusa lo stesso conteggio economico
    /// dell'anteprima già mostrata al Master prima della conferma — nessuna doppia scansione più pesante.</summary>
    private static int StimaTotaleRecordCoinvolti(AppState state, string catalogo, string valore)
    {
        var impatto = MasterRiferimentiService.ContaImpatto(catalogo, valore, state.TestCases.GetAll(), state.NonConformita.GetAll(), state.ChangeRequests.PeekAllReadOnly());
        return Math.Max(1, impatto.TestAggiornati + impatto.NonConformitaAggiornate + impatto.ChangeRequestAggiornate);
    }

    /// <summary>Riscrive TestCase/NonConformita/ChangeRequest coinvolti (spec: TestCase, NonConformita,
    /// ChangeRequest, Segnalazioni rete — queste ultime vivono su NonConformita.DaSegnalazioneRete, stesso
    /// oggetto). Un solo passaggio su ciascuna collezione con GetAll() già in memoria (nessuna doppia lettura),
    /// stesso principio di scala di RevisioneUdtService/WuStatusViewModel.</summary>
    private static void AggiornaRecordDominio(AppState state, string catalogo, string vecchio, IReadOnlyList<string> nuovi, List<string> avvisi,
        IProgress<(int Fatti, int Totale)>? progresso, int totaleStimato)
    {
        var fatti = 0;
        void SegnalaAvanzamento() => progresso?.Report((++fatti, totaleStimato));

        // Pre-filtro economico PRIMA di clonare (MasterRiferimentiService.*Referenzia, nessuna (de)serializzazione):
        // su 100k+ record il costo dominante deve restare "clona e riscrivi solo chi useràdavvero il nuovo
        // valore". Clonare/valutare OGNI record per scoprire poi che non lo riguarda ha reso il primo benchmark
        // a scala (100k Test, ~1000 coinvolti) 450s invece di pochi secondi — corretto qui, non un dettaglio.
        foreach (var tc in state.TestCases.GetAll())
        {
            if (!MasterRiferimentiService.TestCaseReferenzia(tc, catalogo, vecchio)) continue;
            var copia = JsonSerializer.Deserialize<TestCase>(JsonSerializer.Serialize(tc, JsonIo.Options), JsonIo.Options)!;
            if (MasterRiferimentiService.ApplicaSuTestCase(copia, catalogo, vecchio, nuovi, avvisi))
            {
                copia.UltimaModifica = DateTime.Now;
                copia.ModificatoDa = state.Config.OperatoreAttivo;
                // Il nome di catalogo vale anche nelle REV storiche (è la stessa entità): eccezione stretta e
                // limitata a questo Save, vedi SalvaRiscritturaRiferimentiTest.
                SalvaRiscritturaRiferimentiTest(state.TestCases, copia, tc.RevisioneDati);
            }
            SegnalaAvanzamento();
        }

        foreach (var nc in state.NonConformita.GetAll())
        {
            if (!MasterRiferimentiService.NonConformitaReferenzia(nc, catalogo, vecchio)) continue;
            var copia = nc.Clona();
            if (MasterRiferimentiService.ApplicaSuNonConformita(copia, catalogo, vecchio, nuovi, avvisi))
            {
                copia.Changelog.Add(new ChangelogEntry
                {
                    Data = DateTime.Now, Operatore = state.Config.OperatoreAttivo, Campo = $"(riferimento {catalogo})",
                    ValorePrecedente = vecchio, ValoreNuovo = string.Join(", ", nuovi)
                });
                state.NonConformita.Save(copia, copia.RevisioneDati);
            }
            SegnalaAvanzamento();
        }

        foreach (var cr in state.ChangeRequests.GetAll())
        {
            if (!MasterRiferimentiService.ChangeRequestReferenzia(cr, catalogo, vecchio)) continue;
            var copia = JsonSerializer.Deserialize<ChangeRequest>(JsonSerializer.Serialize(cr, JsonIo.Options), JsonIo.Options)!;
            if (MasterRiferimentiService.ApplicaSuChangeRequest(copia, catalogo, vecchio, nuovi, avvisi))
            {
                copia.Changelog.Add(new ChangelogEntry
                {
                    Data = DateTime.Now, Operatore = state.Config.OperatoreAttivo, Campo = $"(riferimento {catalogo})",
                    ValorePrecedente = vecchio, ValoreNuovo = string.Join(", ", nuovi)
                });
                copia.UltimaModifica = DateTime.Now;
                state.ChangeRequests.Save(copia, cr.RevisioneDati);
            }
            SegnalaAvanzamento();
        }
    }

    /// <summary>Unico punto di salvataggio di un Test in cui cambia un riferimento di catalogo (Unisci/Rinomina/Dividi dei
    /// cataloghi master e Unisci Pacchetto): stessa guardia di revisione di un Save normale (un Test modificato da un altro
    /// PC non viene sovrascritto) e stessa transazione, ma nessuna nuova REV. L'eccezione alla guardia delle REV resta cosi'
    /// confinata a questo motore, vedi TestRevisionSafety.</summary>
    public static void SalvaRiscritturaRiferimentiTest(TestCaseRepository testCases, TestCase copia, long revisioneAttesa)
    {
        using (TestRevisionSafety.AmmettiRiscritturaRiferimentiCatalogo())
            testCases.Save(copia, isNew: false, revisioneAttesa: revisioneAttesa);
    }

    /// <summary>Assegna il nuovo nome alla proprietà chiave del catalogo (Nome/Id/WorkingUnitRev...): keyOf è
    /// una funzione di lettura, qui serve anche la scrittura — per i cataloghi coperti da questa feature la
    /// chiave è sempre una proprietà "Nome" pubblica scrivibile (Componente/Veicolo/Funzionalita/Gamma/Categoria/
    /// StrumentoDiagnostico/Operatore/Requisito.Id). Riflessione minima e mirata, non un accessor generico
    /// completo: fallisce in modo esplicito se un catalogo futuro avesse una chiave composta o di sola lettura.</summary>
    private static void ImpostaChiave<T>(T elemento, Func<T, string> keyOf, string nuovoValore) where T : MasterEntity
    {
        var prop = typeof(T).GetProperty("Nome") ?? typeof(T).GetProperty("Id");
        if (prop == null || !prop.CanWrite || prop.PropertyType != typeof(string))
            throw new InvalidOperationException($"Il catalogo {typeof(T).Name} non espone una chiave 'Nome'/'Id' scrivibile: Rinomina/Dividi non supportati per questo tipo.");
        prop.SetValue(elemento, nuovoValore);
    }

    /// <summary>Fusione (unione, mai perdita) delle proprietà "utili" note per i cataloghi già esistenti (spec
    /// punto 2: Funzionalita.Categorie/EcuAssociate, Componente.VeicoliAssociati, Veicolo.GammaNome — vince quella
    /// della voce corretta, segnalata se diversa). Funzionalita.TutteLeEcu/EcuAssociate sono gestiti a mano
    /// (non per riflessione generica) perché sono mutuamente esclusivi: unire ciecamente le due liste
    /// lascerebbe EcuAssociate popolata anche quando TutteLeEcu risulta true, violando l'invariante del
    /// modello — vedi anche MasterUnioneService.RipristinaInvarianteEcuAssociate per il caso complementare
    /// (unione/divisione del catalogo componente che svuota la lista di un'ALTRA Funzionalita).</summary>
    private static void FondiProprietaSpecifiche<T>(T scartato, T corretto, List<string> avvisi) where T : MasterEntity
    {
        switch ((object)scartato)
        {
            case Requisito rs when corretto is Requisito rc && RequisitoApplicabilita.ED4D(rs) && RequisitoApplicabilita.ED4D(rc):
                var veicoliOrigine = RequisitoApplicabilita.SottoveicoliDi(rs);
                var veicoliDestinazione = RequisitoApplicabilita.SottoveicoliDi(rc);
                RequisitoApplicabilita.ImpostaSottoveicoli(rc, veicoliOrigine.Count == 0 || veicoliDestinazione.Count == 0
                    ? Array.Empty<string>() : veicoliOrigine.Concat(veicoliDestinazione));
                var coppie = (rc.VeicoliEcuD4D ?? new()).Concat(rs.VeicoliEcuD4D ?? new());
                rc.VeicoliEcuD4D = coppie.GroupBy(x => (x.Veicolo.ToUpperInvariant(), x.Componente.ToUpperInvariant()))
                    .Select(g => new RequisitoVeicoloEcu { Veicolo = g.First().Veicolo, Componente = g.First().Componente }).ToList();
                if (rc.VeicoliEcuD4D.Count == 0) rc.VeicoliEcuD4D = null;
                break;
            case Componente es when corretto is Componente ec:
                UnisciListaStringhe(es.VeicoliAssociati, ec.VeicoliAssociati);
                break;
            case Funzionalita fs when corretto is Funzionalita fc:
                // Restrizioni per componente: unite PRIMA di toccare TutteLeEcu/EcuAssociate, che la regola legge nello stato precedente.
                fc.UnisciVeicoliPerEcuDa(fs);
                UnisciListaStringhe(fs.Categorie, fc.Categorie);
                UnisciListaStringhe(fs.ApplicabilePerVeicolo, fc.ApplicabilePerVeicolo);
                // TutteLeEcu/EcuAssociate sono mutuamente esclusivi (Funzionalita.EcuAssociateTesto): "tutte le
                // componente" è sempre la scelta più ampia e più sicura, quindi vince se una delle due voci unite ce
                // l'ha già — mai restringere per effetto di un'unione. Altrimenti (entrambe già limitate a un
                // sottoinsieme) le liste si fondono per unione, stesso principio delle altre proprietà qui sopra.
                if (fs.TutteLeEcu || fc.TutteLeEcu) { fc.TutteLeEcu = true; fc.EcuAssociate = new(); }
                else UnisciListaStringhe(fs.EcuAssociate, fc.EcuAssociate);
                break;
            case Veicolo vs when corretto is Veicolo vc:
                if (string.IsNullOrWhiteSpace(vc.GammaNome) && !string.IsNullOrWhiteSpace(vs.GammaNome))
                    vc.GammaNome = vs.GammaNome;
                else if (!string.IsNullOrWhiteSpace(vs.GammaNome) && !string.IsNullOrWhiteSpace(vc.GammaNome)
                         && !vs.GammaNome.Equals(vc.GammaNome, StringComparison.OrdinalIgnoreCase))
                    avvisi.Add($"Veicolo: Gamma diversa tra le due voci unite ('{vs.GammaNome}' e '{vc.GammaNome}') — mantenuta quella della voce corretta.");
                break;
            case Operatore os when corretto is Operatore oc:
                UnisciListaStringhe(os.GammeAssegnate, oc.GammeAssegnate);
                break;
        }
    }

    /// <summary>Copia (non unione: la destinazione è una voce nuova, vuota) le stesse proprietà "utili" di
    /// FondiProprietaSpecifiche, usata da Dividi per popolare le voci create dalla divisione.</summary>
    private static void CopiaProprietaSpecifiche<T>(T origine, T destinazione) where T : MasterEntity
    {
        switch ((object)origine)
        {
            case Requisito ro when destinazione is Requisito rd:
                RequisitoApplicabilita.ImpostaTipo(rd, RequisitoApplicabilita.TipoDi(ro));
                RequisitoApplicabilita.ImpostaSottoveicoli(rd, RequisitoApplicabilita.SottoveicoliDi(ro));
                rd.VeicoliEcuD4D = ro.VeicoliEcuD4D?.Select(x => new RequisitoVeicoloEcu { Veicolo = x.Veicolo, Componente = x.Componente }).ToList();
                break;
            case Componente eo when destinazione is Componente ed:
                ed.VeicoliAssociati = new List<string>(eo.VeicoliAssociati);
                break;
            case Funzionalita fo when destinazione is Funzionalita fd:
                fd.Categorie = new List<string>(fo.Categorie);
                fd.ApplicabilePerVeicolo = new List<string>(fo.ApplicabilePerVeicolo);
                fd.TutteLeEcu = fo.TutteLeEcu;
                fd.EcuAssociate = new List<string>(fo.EcuAssociate);
                // Restrizioni per componente copiate per valore: la nuova voce non deve condividere le liste con l'originale.
                fd.VeicoliPerEcu = fo.VeicoliPerEcu
                    .Select(v => new VeicoliFunzionalitaEcu { Componente = v.Componente, Veicoli = new List<string>(v.Veicoli) }).ToList();
                break;
            case Veicolo vo when destinazione is Veicolo vd:
                vd.GammaNome = vo.GammaNome;
                break;
            case Operatore oo when destinazione is Operatore od:
                od.GammeAssegnate = new List<string>(oo.GammeAssegnate);
                break;
        }
    }

    /// <summary>Rete di sicurezza dopo QUALUNQUE operazione sul catalogo componente: la sola sostituzione per lista
    /// (MasterRiferimentiService, che sostituisce ma non cancella mai senza rimpiazzo) non può da sola produrre
    /// una Funzionalita con EcuAssociate vuota, ma l'invariante "TutteLeEcu=false richiede EcuAssociate non
    /// vuota" resta un vincolo del modello che vale la pena verificare esplicitamente qui, non solo confidare
    /// che nessun percorso futuro lo violi: una Funzionalita con EcuAssociate vuota e TutteLeEcu=false sarebbe
    /// invisibile in ogni picker Test filtrato per componente (spec_funzionalita_ecu.md), quindi si torna al default
    /// sicuro "Tutti i componenti" invece di lasciarla orfana, con Changelog e avviso nell'anteprima.</summary>
    private static void RipristinaInvarianteEcuAssociate(AppState state, string catalogo, List<string> avvisi)
    {
        if (catalogo != "Componente") return;

        var catalogoFunzionalita = state.Master.Funzionalita.Load();
        var attesoFunzionalita = EntityStateFingerprint.Clone(catalogoFunzionalita);
        var toccate = 0;
        foreach (var f in catalogoFunzionalita)
        {
            if (!f.TutteLeEcu && f.EcuAssociate.Count == 0)
            {
                f.TutteLeEcu = true;
                f.Changelog.Add(new ChangelogEntry
                {
                    Data = DateTime.Now, Operatore = state.Config.OperatoreAttivo, Campo = "(TutteLeEcu)",
                    ValorePrecedente = "false (nessuna componente associata rimasta)", ValoreNuovo = "true"
                });
                f.UltimaModifica = DateTime.Now;
                f.ModificatoDa = state.Config.OperatoreAttivo;
                avvisi.Add($"Funzionalita '{f.Nome}': nessuna componente associata rimasta dopo l'operazione, impostata su 'Tutti i componenti' per non lasciarla senza componente applicabili.");
                toccate++;
            }
            // Restrizioni per componente: si riallineano dopo l'invariante sopra, così le voci dei componenti ancora applicabili restano.
            if (f.NormalizzaVeicoliPerEcu()) toccate++;
        }
        if (toccate > 0) state.Master.Funzionalita.Save(catalogoFunzionalita, attesoFunzionalita);
    }

    private static void UnisciListaStringhe(List<string> origine, List<string> destinazione)
    {
        foreach (var valore in origine)
            if (!destinazione.Any(x => x.Equals(valore, StringComparison.OrdinalIgnoreCase)))
                destinazione.Add(valore);
    }
}
