using System.Reflection;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Inventario centrale ed estendibile di TUTTI i campi dell'applicazione che referenziano un catalogo master
/// "per nome" (stringa singola o lista di stringhe), più il motore generico di Unisci/Rinomina/Dividi che
/// li usa (spec 2026-09-23 "spec_unisci_master.md" — modello WuStatusViewModel.Unisci/RevisioneUdtService).
///
/// Registrare un nuovo riferimento significa aggiungere UNA riga a <see cref="Registro"/>: l'engine sotto
/// (SostituisciInNodo/UsaValoreInNodo) lavora per riflessione su qualunque proprietà string o lista-di-stringhe
/// (List&lt;string&gt;, o una sua sottoclasse come EcuSelection) di un tipo già raggiunto dalla visita a
/// <see cref="NodiTestCase"/>/<see cref="NodiNonConformita"/>/<see cref="NodiChangeRequest"/> o di un catalogo
/// master "ospitante" in <see cref="RiferimentiIncrociati"/> — non serve altro codice per un campo nuovo dello
/// stesso tipo (string/List&lt;string&gt;) su un nodo già visitato.
///
/// Riga "prevista" per Funzionalita.EcuAssociate → Componente (spec_funzionalita_ecu.md, altro agente in parallelo):
/// registrata qui con reflection guard — se la proprietà non esiste ancora sul branch corrente viene saltata in
/// silenzio da <see cref="RiferimentiIncrociati"/> (IsPropertaDisponibile), quindi non richiede alcun intervento
/// per "attivarsi" il giorno in cui il campo verrà aggiunto: la sola condizione è che si chiami esattamente
/// "EcuAssociate" e sia una List&lt;string&gt; su Funzionalita.
/// </summary>
public static class MasterRiferimentiService
{
    /// <summary>Un singolo campo che referenzia un catalogo, su un tipo di nodo raggiunto dalla visita
    /// dell'entità principale (TestCase/NonConformita/ChangeRequest) o su un catalogo master ospitante
    /// (riferimento incrociato master↔master). Puramente dati: nessun delegate, per restare leggibile come
    /// inventario e testabile a sé (MasterRiferimentiServiceTests.Registro_*).</summary>
    public sealed record CampoRiferimento(Type TipoNodo, string NomeProprieta, string Catalogo, string Descrizione)
    {
        /// <summary>Null se la proprietà non esiste (tipicamente il campo "previsto" non ancora attivo su
        /// questo branch): il motore la salta senza errori.</summary>
        public PropertyInfo? Proprieta => TipoNodo.GetProperty(NomeProprieta, BindingFlags.Public | BindingFlags.Instance);
    }

    /// <summary>Inventario centrale. Ogni riga = un campo che referenzia un catalogo master per nome.
    /// TestCase/NonConformita/ChangeRequest: nodi raggiunti da <see cref="NodiTestCase"/> ecc. sotto.
    /// Master↔Master: il tipo ospitante stesso (Componente, Veicolo, Funzionalita, Operatore).
    /// Campi storici di sola attribuzione (CreatoDa, ImplementataDa, ChiusaDa...) sono ESCLUSI di proposito:
    /// registrano chi ha fatto un'azione in un dato momento, non un riferimento vivo da riscrivere quando
    /// l'operatore cambia nome — coerente con MasterEntity.CreatoDa (mai sovrascritto dopo la creazione).</summary>
    public static readonly List<CampoRiferimento> Registro = new()
    {
        // --- TestCase (nodo radice) ---
        new(typeof(TestCase), nameof(TestCase.Componente), "Componente", "TestCase.Componente"),
        new(typeof(TestCase), nameof(TestCase.Veicoli), "Veicolo", "TestCase.Veicoli"),
        new(typeof(TestCase), nameof(TestCase.Funzionalita), "Funzionalita", "TestCase.Funzionalita"),
        new(typeof(TestCase), nameof(TestCase.StrumentiUsati), "StrumentoDiagnostico", "TestCase.StrumentiUsati"),
        new(typeof(TestCase), nameof(TestCase.RequisitoId), "Requisito", "TestCase.RequisitoId"),
        new(typeof(TestCase), nameof(TestCase.Assegnatario), "Operatore", "TestCase.Assegnatario"),

        // --- Condizione (TestCase.Esecuzioni[].Condizioni[]) ---
        new(typeof(Condizione), nameof(Condizione.Componente), "Componente", "TestCase.Esecuzioni[].Condizioni[].Componente"),
        new(typeof(Condizione), nameof(Condizione.Funzionalita), "Funzionalita", "TestCase.Esecuzioni[].Condizioni[].Funzionalita"),
        // 6.5.1: strumento proprietario dell'operazione e veicolo del guasto referenziato (nomi di catalogo, stringhe singole).
        new(typeof(Condizione), nameof(Condizione.Strumento), "StrumentoDiagnostico", "TestCase.Esecuzioni[].Condizioni[].Strumento"),
        new(typeof(Condizione), nameof(Condizione.DtcVeicolo), "Veicolo", "TestCase.Esecuzioni[].Condizioni[].DtcVeicolo"),

        // --- NonConformita (nodo radice) ---
        new(typeof(NonConformita), nameof(NonConformita.EcuSegnalate), "Componente", "NonConformita.EcuSegnalate"),
        new(typeof(NonConformita), nameof(NonConformita.VeicoliSegnalati), "Veicolo", "NonConformita.VeicoliSegnalati"),
        new(typeof(NonConformita), nameof(NonConformita.FunzionalitaSegnalate), "Funzionalita", "NonConformita.FunzionalitaSegnalate"),
        new(typeof(NonConformita), nameof(NonConformita.StrumentoSegnalato), "StrumentoDiagnostico", "NonConformita.StrumentoSegnalato"),
        new(typeof(NonConformita), nameof(NonConformita.Owner), "Operatore", "NonConformita.Owner"),

        // --- OperazioneSegnalazione (NonConformita.OperazioniSegnalate[]) ---
        new(typeof(OperazioneSegnalazione), nameof(OperazioneSegnalazione.Componente), "Componente", "NonConformita.OperazioniSegnalate[].Componente"),
        new(typeof(OperazioneSegnalazione), nameof(OperazioneSegnalazione.Funzionalita), "Funzionalita", "NonConformita.OperazioniSegnalate[].Funzionalita"),

        // --- ChangeRequest (nodo radice) ---
        new(typeof(ChangeRequest), nameof(ChangeRequest.VeicoliImpattati), "Veicolo", "ChangeRequest.VeicoliImpattati"),
        new(typeof(ChangeRequest), nameof(ChangeRequest.EcuImpattate), "Componente", "ChangeRequest.EcuImpattate"),
        new(typeof(ChangeRequest), nameof(ChangeRequest.StrumentiImpattati), "StrumentoDiagnostico", "ChangeRequest.StrumentiImpattati"),
        new(typeof(ChangeRequest), nameof(ChangeRequest.Responsabile), "Operatore", "ChangeRequest.Responsabile"),

        // --- CollegamentoValidazioneCr (ChangeRequest.TestValidazione[]) ---
        new(typeof(CollegamentoValidazioneCr), nameof(CollegamentoValidazioneCr.VeicoliCoperti), "Veicolo", "ChangeRequest.TestValidazione[].VeicoliCoperti"),
        new(typeof(CollegamentoValidazioneCr), nameof(CollegamentoValidazioneCr.EcuCoperte), "Componente", "ChangeRequest.TestValidazione[].EcuCoperte"),
        new(typeof(CollegamentoValidazioneCr), nameof(CollegamentoValidazioneCr.StrumentiCoperti), "StrumentoDiagnostico", "ChangeRequest.TestValidazione[].StrumentiCoperti"),

        // --- RevisioneChangeRequest (ChangeRequest.Revisioni[], storico: aggiornato per coerenza col resto,
        // mai usato per bloccare/consentire l'unione) ---
        new(typeof(RevisioneChangeRequest), nameof(RevisioneChangeRequest.VeicoliImpattati), "Veicolo", "ChangeRequest.Revisioni[].VeicoliImpattati"),
        new(typeof(RevisioneChangeRequest), nameof(RevisioneChangeRequest.EcuImpattate), "Componente", "ChangeRequest.Revisioni[].EcuImpattate"),
        new(typeof(RevisioneChangeRequest), nameof(RevisioneChangeRequest.StrumentiImpattati), "StrumentoDiagnostico", "ChangeRequest.Revisioni[].StrumentiImpattati"),

        // --- Master ↔ Master (cataloghi tra loro) ---
        new(typeof(Componente), nameof(Componente.VeicoliAssociati), "Veicolo", "Componente.VeicoliAssociati"),
        new(typeof(Veicolo), nameof(Veicolo.GammaNome), "Gamma", "Veicolo.GammaNome"),
        new(typeof(Funzionalita), nameof(Funzionalita.Categorie), "Categoria", "Funzionalita.Categorie"),
        new(typeof(Operatore), nameof(Operatore.GammeAssegnate), "Gamma", "Operatore.GammeAssegnate"),

        // --- Audit/configurazione che referenziano un catalogo per nome (review 2026-09-23, MEDIUM: registro
        // incompleto) ---
        new(typeof(RisoluzioneConflittoAudit), nameof(RisoluzioneConflittoAudit.Componente), "Componente", "RisoluzioneConflittoAudit.Componente"),
        new(typeof(RisoluzioneConflittoAudit), nameof(RisoluzioneConflittoAudit.RequisitoId), "Requisito", "RisoluzioneConflittoAudit.RequisitoId"),
        new(typeof(TestTemplate), nameof(TestTemplate.StrumentiPreselezionati), "StrumentoDiagnostico", "TestTemplate.StrumentiPreselezionati"),

        // --- ValutazioneRequisitoEcuVeicolo (Piano 6.3.1 §1: valutazioni Manuale/Propagata per cella
        // Requisito×componente×Veicolo) — stesso principio di RisoluzioneConflittoAudit sopra, con in più SorgenteVeicolo
        // (veicolo di origine di una propagazione) da tenere allineato a un rename/unione del catalogo Veicolo. ---
        new(typeof(ValutazioneRequisitoEcuVeicolo), nameof(ValutazioneRequisitoEcuVeicolo.RequisitoId), "Requisito", "ValutazioneRequisitoEcuVeicolo.RequisitoId"),
        new(typeof(ValutazioneRequisitoEcuVeicolo), nameof(ValutazioneRequisitoEcuVeicolo.Componente), "Componente", "ValutazioneRequisitoEcuVeicolo.Componente"),
        new(typeof(ValutazioneRequisitoEcuVeicolo), nameof(ValutazioneRequisitoEcuVeicolo.Veicolo), "Veicolo", "ValutazioneRequisitoEcuVeicolo.Veicolo"),
        new(typeof(ValutazioneRequisitoEcuVeicolo), nameof(ValutazioneRequisitoEcuVeicolo.SorgenteVeicolo), "Veicolo", "ValutazioneRequisitoEcuVeicolo.SorgenteVeicolo"),

        // Attivata (release/6.3.0, feat/funzionalita-componente 04adbae): Funzionalita.EcuAssociate esiste ora
        // davvero. La sostituzione generica per riflessione basta a mantenere coerente la lista quando il componente
        // referenziata viene unita/rinominata/divisa; l'invariante "TutteLeEcu=false richiede EcuAssociate non
        // vuota" è invece un vincolo SPECIFICO di Funzionalita che la sola sostituzione per lista non può
        // garantire da sola (Dividi/Unisci non svuotano mai una lista senza sostituire, ma il caso in cui
        // un'altra Funzionalita perde per unione l'unica componente rimasta va comunque protetto): vedi
        // MasterUnioneService.RipristinaInvarianteEcuAssociate, richiamato dopo ogni operazione sul catalogo Componente.
        new(typeof(Funzionalita), nameof(Funzionalita.EcuAssociate), "Componente", "Funzionalita.EcuAssociate"),

        // Restrizioni per componente (Funzionalita.VeicoliPerEcu, nodi annidati). Il nome componente non passa dalla sostituzione per lista
        // generica (che non sa copiare alla divisione): lo gestisce SostituisciInRestrizioni, qui registrato per l'inventario.
        // Il veicolo segue Unisci/Rinomina/Dividi del catalogo Veicolo con la stessa sostituzione per lista delle altre liste.
        new(typeof(VeicoliFunzionalitaEcu), nameof(VeicoliFunzionalitaEcu.Componente), "Componente", "Funzionalita.VeicoliPerEcu[].Componente"),
        new(typeof(VeicoliFunzionalitaEcu), nameof(VeicoliFunzionalitaEcu.Veicoli), "Veicolo", "Funzionalita.VeicoliPerEcu[].Veicoli"),

        // --- Voci guasto dei requisiti Extra (6.5.1): DtcRequisito.Veicolo, sia embedded (Requisito.Guasto) sia negli archivi Phase D
        // "requisiti-guasto". Non e un catalogo MasterEntity ospitante: la visita e dedicata (ApplicaSuVociDtc). ---
        new(typeof(DtcRequisito), nameof(DtcRequisito.Veicolo), "Veicolo", "Requisito.Guasto[].Veicolo / archivio requisiti-guasto"),
    };

    /// <summary>Tutti i nomi di catalogo effettivamente registrati (per popolare "Unisci/Rinomina/Dividi
    /// disponibile su questo catalogo" nella UI generica di MasterDataEditorViewModelBase).</summary>
    public static readonly IReadOnlySet<string> CataloghiRegistrati =
        Registro.Select(r => r.Catalogo).ToHashSet(StringComparer.Ordinal);

    // ---- Visita dei nodi di ciascuna entità radice (spiega esplicitamente la forma dell'albero: niente
    // riflessione "a caso" su liste generiche, per restare prevedibile e non toccare mai Changelog/allegati). ----

    private static IEnumerable<object> NodiTestCase(TestCase tc) =>
        new object[] { tc }.Concat(tc.Esecuzioni.SelectMany(e => e.Condizioni.Cast<object>()));

    private static IEnumerable<object> NodiNonConformita(NonConformita nc) =>
        new object[] { nc }.Concat(nc.OperazioniSegnalate.Cast<object>());

    private static IEnumerable<object> NodiChangeRequest(ChangeRequest cr) =>
        new object[] { cr }.Concat(cr.TestValidazione.Cast<object>()).Concat(cr.Revisioni.Cast<object>());

    // ---- Lettura/scrittura generica di un campo string o lista-di-stringhe per riflessione ----

    private static bool UsaValoreInProprieta(object nodo, PropertyInfo prop, string valore)
    {
        var v = prop.GetValue(nodo);
        if (v is string s) return s.Equals(valore, StringComparison.OrdinalIgnoreCase);
        if (v is System.Collections.IEnumerable enumerabile and not string)
            foreach (var el in enumerabile)
                if (el is string es && es.Equals(valore, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Sostituisce vecchio con UNO o PIÙ valori nuovi (Dividi passa più valori, Unisci/Rinomina uno
    /// solo). Su un campo lista: l'occorrenza viene rimossa e tutti i nuovi valori vengono aggiunti, con dedup
    /// case-insensitive che preserva l'ordine di prima apparizione — nessun doppione dopo la sostituzione (spec
    /// punto 2). Su un campo stringa singola con PIÙ valori nuovi (split di un riferimento non-lista, es.
    /// NonConformita.StrumentoSegnalato): limite noto e documentato, si tiene il primo nuovo valore e la
    /// chiamata segnala "limitato" al chiamante, che lo riporta nell'anteprima — nessuna perdita silenziosa.</summary>
    private static bool SostituisciInProprieta(object nodo, PropertyInfo prop, string vecchio, IReadOnlyList<string> nuovi, out bool limitato)
    {
        limitato = false;
        if (prop.PropertyType == typeof(string))
        {
            var v = (string?)prop.GetValue(nodo);
            if (v == null || !v.Equals(vecchio, StringComparison.OrdinalIgnoreCase)) return false;
            // Dividi passa [originale, ...nuove]: il campo singolo resta sull'originale, nessuna scrittura e
            // nessun avviso (la scelta e' voluta, non una perdita).
            if (string.Equals(v, nuovi[0], StringComparison.Ordinal)) return false;
            prop.SetValue(nodo, nuovi[0]);
            limitato = nuovi.Count > 1 && !nuovi[0].Equals(vecchio, StringComparison.OrdinalIgnoreCase);
            return true;
        }
        if (typeof(System.Collections.Generic.IList<string>).IsAssignableFrom(prop.PropertyType))
        {
            if (prop.GetValue(nodo) is not System.Collections.Generic.IList<string> lista) return false;
            var indice = -1;
            for (var i = 0; i < lista.Count; i++)
                if (lista[i]?.Equals(vecchio, StringComparison.OrdinalIgnoreCase) == true) { indice = i; break; }
            if (indice < 0) return false;

            var risultato = new List<string>();
            foreach (var el in lista)
            {
                if (el.Equals(vecchio, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var nuovo in nuovi)
                        if (!risultato.Any(x => x.Equals(nuovo, StringComparison.OrdinalIgnoreCase))) risultato.Add(nuovo);
                }
                else if (!risultato.Any(x => x.Equals(el, StringComparison.OrdinalIgnoreCase)))
                    risultato.Add(el);
            }
            // Nessun cambiamento reale (es. Dividi su una lista che contiene gia' originale + nuove voci): non
            // contare come record toccato, evita riscritture inutili.
            if (risultato.Count == lista.Count && risultato.Zip(lista, (a, b) => string.Equals(a, b, StringComparison.Ordinal)).All(x => x)) return false;
            lista.Clear();
            foreach (var x in risultato) lista.Add(x);
            return true;
        }
        return false;
    }

    /// <summary>Esito di ContaImpatto/ApplicaSuTestCase-NC-CR: conteggi per l'anteprima obbligatoria "verranno
    /// aggiornati X test, Y NC, Z CR, W voci di catalogo" (spec punto 4) più gli avvisi non bloccanti.</summary>
    public sealed class RisultatoImpatto
    {
        public int TestAggiornati { get; set; }
        public int NonConformitaAggiornate { get; set; }
        public int ChangeRequestAggiornate { get; set; }
        public int VociCatalogoAggiornate { get; set; }
        public List<string> Avvisi { get; } = new();
    }

    /// <summary>Conteggio "a secco" (nessuna mutazione) per l'anteprima, prima di qualunque conferma. Cammina
    /// SOLO le collezioni già in memoria (TestCases.GetAll()/NonConformita.GetAll()/ChangeRequests.GetAll()):
    /// costo lineare nel numero di record esistenti, nessuna doppia lettura O(n²) anche a scala 100k (spec
    /// punto 8) — lo stesso principio già usato da RevisioneUdtService.TrovaTestDaAggiornare.</summary>
    public static RisultatoImpatto ContaImpatto(
        string catalogo, string valore,
        IEnumerable<TestCase> testCases, IEnumerable<NonConformita> nonConformita, IEnumerable<ChangeRequest> changeRequests)
    {
        var risultato = new RisultatoImpatto
        {
            TestAggiornati = testCases.Count(tc => TestCaseReferenzia(tc, catalogo, valore)),
            NonConformitaAggiornate = nonConformita.Count(nc => NonConformitaReferenzia(nc, catalogo, valore)),
            ChangeRequestAggiornate = changeRequests.Count(cr => ChangeRequestReferenzia(cr, catalogo, valore)),
        };
        return risultato;
    }

    /// <summary>Pre-filtro economico (nessuna clonazione JSON, nessuna scrittura) da usare PRIMA di clonare/
    /// modificare/salvare un record: su un archivio a scala (100k+ Test) il costo dominante di Unisci/Rinomina/
    /// Dividi deve restare "clona e riscrivi solo i record che useranno davvero il nuovo valore", mai un giro
    /// completo di (de)serializzazione per OGNI record solo per scoprire che non lo riguarda — stesso principio
    /// di RevisioneUdtService.TrovaTestDaAggiornare/SostituisciValore ("muta poi salva", mai "clona sempre poi
    /// scarta").</summary>
    public static bool TestCaseReferenzia(TestCase tc, string catalogo, string valore)
    {
        var campi = Registro.Where(r => r.Catalogo == catalogo && r.Proprieta != null).ToList();
        return NodiTestCase(tc).Any(nodo => campi.Any(c => c.TipoNodo.IsInstanceOfType(nodo) && UsaValoreInProprieta(nodo, c.Proprieta!, valore)));
    }

    public static bool NonConformitaReferenzia(NonConformita nc, string catalogo, string valore)
    {
        var campi = Registro.Where(r => r.Catalogo == catalogo && r.Proprieta != null).ToList();
        return NodiNonConformita(nc).Any(nodo => campi.Any(c => c.TipoNodo.IsInstanceOfType(nodo) && UsaValoreInProprieta(nodo, c.Proprieta!, valore)));
    }

    public static bool ChangeRequestReferenzia(ChangeRequest cr, string catalogo, string valore)
    {
        var campi = Registro.Where(r => r.Catalogo == catalogo && r.Proprieta != null).ToList();
        return NodiChangeRequest(cr).Any(nodo => campi.Any(c => c.TipoNodo.IsInstanceOfType(nodo) && UsaValoreInProprieta(nodo, c.Proprieta!, valore)));
    }

    /// <summary>Applica in-place su un singolo TestCase tutte le sostituzioni registrate per <paramref name="catalogo"/>.
    /// Ritorna true se almeno un campo è stato toccato (il chiamante è responsabile del salvataggio, stesso
    /// schema "muta poi salva" di RevisioneUdtService.SostituisciValore). Rileva inoltre collisioni informative
    /// sulle Condizioni (spec punto 2: "se due condizioni dello stesso test diventano identiche, mantienile
    /// entrambe... segnala nell'anteprima" — qui non si cancella MAI nulla, il conteggio è solo diagnostico).</summary>
    public static bool ApplicaSuTestCase(TestCase tc, string catalogo, string vecchio, IReadOnlyList<string> nuovi, List<string> avvisi)
    {
        var campi = Registro.Where(r => r.Catalogo == catalogo && r.Proprieta != null).ToList();
        var modificato = false;

        // Snapshot pre-sostituzione delle chiavi (Componente ordinato + Funzionalita) per esecuzione, per rilevare
        // collisioni CREATE da questa sostituzione (non quelle già preesistenti prima dell'operazione).
        var chiaviPrima = tc.Esecuzioni.ToDictionary(e => e, ChiaviCondizioni);

        foreach (var nodo in NodiTestCase(tc))
            foreach (var campo in campi.Where(c => c.TipoNodo.IsInstanceOfType(nodo)))
                if (SostituisciInProprieta(nodo, campo.Proprieta!, vecchio, nuovi, out var limitato))
                {
                    modificato = true;
                    if (limitato)
                        avvisi.Add($"Test {tc.Id}: campo singolo '{campo.Descrizione}' non può contenere più valori dopo una divisione — mantenuto solo '{nuovi[0]}'.");
                }

        if (modificato)
            foreach (var es in tc.Esecuzioni)
            {
                var chiaviDopo = ChiaviCondizioni(es).GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var chiaviPrimaEsecuzione = chiaviPrima[es].ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var chiave in chiaviDopo)
                    if (!chiaviPrimaEsecuzione.Contains(chiave) || chiaviPrima[es].Count(x => x.Equals(chiave, StringComparison.OrdinalIgnoreCase)) <= 1)
                        avvisi.Add($"Test {tc.Id}, revisione {es.Iterazione}: dopo l'operazione più condizioni condividono componente+Funzionalità ('{chiave}') — nessuna è stata rimossa, verificare gli esiti.");
            }

        return modificato;
    }

    private static List<string> ChiaviCondizioni(Esecuzione es) =>
        es.Condizioni.Select(c => string.Join("+", c.Componente.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) + "::" + c.Funzionalita).ToList();

    public static bool ApplicaSuNonConformita(NonConformita nc, string catalogo, string vecchio, IReadOnlyList<string> nuovi, List<string> avvisi)
    {
        var campi = Registro.Where(r => r.Catalogo == catalogo && r.Proprieta != null).ToList();
        var modificato = false;
        foreach (var nodo in NodiNonConformita(nc))
            foreach (var campo in campi.Where(c => c.TipoNodo.IsInstanceOfType(nodo)))
                if (SostituisciInProprieta(nodo, campo.Proprieta!, vecchio, nuovi, out var limitato))
                {
                    modificato = true;
                    if (limitato) avvisi.Add($"NC {nc.Id}: campo singolo '{campo.Descrizione}' non può contenere più valori dopo una divisione — mantenuto solo '{nuovi[0]}'.");
                }
        return modificato;
    }

    public static bool ApplicaSuChangeRequest(ChangeRequest cr, string catalogo, string vecchio, IReadOnlyList<string> nuovi, List<string> avvisi)
    {
        var campi = Registro.Where(r => r.Catalogo == catalogo && r.Proprieta != null).ToList();
        var modificato = false;
        foreach (var nodo in NodiChangeRequest(cr))
            foreach (var campo in campi.Where(c => c.TipoNodo.IsInstanceOfType(nodo)))
                if (SostituisciInProprieta(nodo, campo.Proprieta!, vecchio, nuovi, out var limitato))
                {
                    modificato = true;
                    if (limitato) avvisi.Add($"CR {cr.Id}: campo singolo '{campo.Descrizione}' non può contenere più valori dopo una divisione — mantenuto solo '{nuovi[0]}'.");
                }
        return modificato;
    }

    /// <summary>Riferimenti master↔master (Componente.VeicoliAssociati, Veicolo.GammaNome, Funzionalita.Categorie,
    /// Operatore.GammeAssegnate, + Funzionalita.EcuAssociate quando esisterà): applica la stessa sostituzione
    /// a TUTTE le voci di TUTTI i cataloghi master il cui tipo ha un campo registrato per <paramref name="catalogo"/>.
    /// I 4-5 cataloghi ospitanti sono pochi e noti, quindi qui si itera direttamente MasterDataStore invece di
    /// passare per un registro di accessor generici — la "riga da attivare" resta comunque solo in Registro,
    /// questo metodo non richiede modifiche quando il campo Funzionalita.EcuAssociate verrà aggiunto perché la
    /// riflessione lo raggiunge tramite lo stesso ApplicaSuEntitaMaster&lt;T&gt; già usato per Categorie.</summary>
    public static int ApplicaSuCataloghiIncrociati(MasterDataStore master, string catalogo, string vecchio, IReadOnlyList<string> nuovi, List<string> avvisi, string operatore = "")
    {
        // componente già nel catalogo prima dell'operazione (il catalogo Componente viene salvato dopo le sostituzioni): distingue un componente
        // esistente (Unisci: restrizioni unite) da un nome nuovo (Rinomina/Dividi: restrizione solo dal nome vecchio).
        var ecuEsistenti = catalogo == "Componente"
            ? master.Componente.Load().Select(e => e.Nome).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totale = 0;
        totale += ApplicaSuCatalogoOspitante(master.Componente, catalogo, vecchio, nuovi, avvisi, ecuEsistenti);
        totale += ApplicaSuCatalogoOspitante(master.Veicoli, catalogo, vecchio, nuovi, avvisi, ecuEsistenti);
        totale += ApplicaSuCatalogoOspitante(master.Funzionalita, catalogo, vecchio, nuovi, avvisi, ecuEsistenti);
        totale += ApplicaSuCatalogoOspitante(master.Operatori, catalogo, vecchio, nuovi, avvisi, ecuEsistenti);
        // RisoluzioneConflittoAudit.Componente/RequisitoId e TestTemplate.StrumentiPreselezionati (review 2026-09-23,
        // MEDIUM): dati di audit/configurazione che referenziano per nome componente/Requisiti/Strumenti — restavano
        // orfani/disallineati dal catalogo dopo un'unione o rinomina prima di questa correzione.
        totale += ApplicaSuCatalogoOspitante(master.RisoluzioniConflittoAudit, catalogo, vecchio, nuovi, avvisi, ecuEsistenti);
        totale += ApplicaSuCatalogoOspitante(master.TestTemplate, catalogo, vecchio, nuovi, avvisi, ecuEsistenti);
        // ValutazioneRequisitoEcuVeicolo (Piano 6.3.1 §1): rinomina/unione di Componente/Veicolo/Requisito deve
        // aggiornare anche le valutazioni Manuale/Propagata già salvate, altrimenti una cella resterebbe
        // agganciata a un nome non più esistente nel master dopo Unisci/Rinomina/Dividi. A differenza degli
        // altri cataloghi ospitanti sopra, qui la sostituzione può far collidere due righe sulla stessa cella
        // (RequisitoId|Componente|Veicolo): passa dal percorso dedicato che consolida la collisione PRIMA di salvare
        // invece di lasciar fallire il Save con PhaseDMasterBusinessKeyConflictException (review indipendente
        // 2026-09-24 — un'unione componente/Veicolo/Requisito legittima non deve bloccarsi per questo).
        totale += ApplicaSuValutazioniConConsolidamento(master.ValutazioniRequisitoEcuVeicolo, catalogo, vecchio, nuovi, operatore, avvisi);
        totale += ApplicaSuVociDtc(master, catalogo, vecchio, nuovi, operatore, avvisi);
        totale += ApplicaSuRequisitiVeicoliEcu(master, catalogo, vecchio, nuovi);
        return totale;
    }

    /// <summary>Rinomina, unisce o divide i riferimenti informativi Extra, mantenendo tutte le coppie.</summary>
    private static int ApplicaSuRequisitiVeicoliEcu(MasterDataStore master, string catalogo, string vecchio, IReadOnlyList<string> nuovi)
    {
        if ((catalogo != "Componente" && catalogo != "Veicolo") || nuovi.Count == 0) return 0;
        var requisiti = master.Requisiti.Load();
        var atteso = EntityStateFingerprint.Clone(requisiti);
        var toccati = 0;
        foreach (var requisito in requisiti)
        {
            var cambiato = false;
            if (catalogo == "Veicolo")
            {
                var dichiarati = RequisitoApplicabilita.SottoveicoliDi(requisito);
                if (dichiarati.Contains(vecchio, StringComparer.OrdinalIgnoreCase))
                {
                    var aggiornati = dichiarati.SelectMany(v => string.Equals(v, vecchio, StringComparison.OrdinalIgnoreCase) ? nuovi : new[] { v });
                    RequisitoApplicabilita.ImpostaSottoveicoli(requisito, aggiornati);
                    cambiato = true;
                }
            }
            var risultato = new List<RequisitoVeicoloEcu>();
            var viste = new HashSet<(string, string)>();
            foreach (var voce in requisito.VeicoliEcuD4D ?? new())
            {
                var originale = catalogo == "Componente" ? voce.Componente : voce.Veicolo;
                var valori = string.Equals(originale, vecchio, StringComparison.OrdinalIgnoreCase) ? nuovi : new[] { originale };
                if (string.Equals(originale, vecchio, StringComparison.OrdinalIgnoreCase) &&
                    (nuovi.Count != 1 || !string.Equals(originale, nuovi[0], StringComparison.Ordinal))) cambiato = true;
                foreach (var valore in valori)
                {
                    var v = catalogo == "Veicolo" ? valore : voce.Veicolo;
                    var e = catalogo == "Componente" ? valore : voce.Componente;
                    if (viste.Add((v.ToUpperInvariant(), e.ToUpperInvariant()))) risultato.Add(new RequisitoVeicoloEcu { Veicolo = v, Componente = e });
                    else cambiato = true;
                }
            }
            if (!cambiato) continue;
            if (requisito.VeicoliEcuD4D != null) requisito.VeicoliEcuD4D = risultato;
            toccati++;
        }
        if (toccati > 0) master.Requisiti.Save(requisiti, atteso);
        return toccati;
    }

    /// <summary>Voci guasto dei requisiti Extra (embedded in <see cref="Requisito.Guasto"/> e negli archivi "requisiti-guasto"): sostituisce i campi
    /// registrati per <paramref name="catalogo"/> (oggi DtcRequisito.Veicolo). Dopo la sostituzione due voci possono avere la stessa chiave
    /// (Veicolo, componente, Codice): la prima resta, le successive sono identiche per chiave e si omettono (avviso). Gli archivi si scrivono con
    /// <see cref="RequisitoDtcArchivio.Scrivi"/> (merge a 3 vie con le modifiche di altri PC); si leggono solo gli archivi non vuoti.
    /// Ritorna il numero di requisiti toccati.</summary>
    private static int ApplicaSuVociDtc(MasterDataStore master, string catalogo, string vecchio, IReadOnlyList<string> nuovi, string operatore, List<string> avvisi)
    {
        var campi = Registro.Where(r => r.TipoNodo == typeof(DtcRequisito) && r.Catalogo == catalogo && r.Proprieta != null).ToList();
        if (campi.Count == 0) return 0;

        var requisiti = master.Requisiti.Load();
        var atteso = EntityStateFingerprint.Clone(requisiti);
        var archivio = master.RequisitiDtc;
        IReadOnlyDictionary<Guid, int>? conteggi = null;
        var scritture = new List<DtcScrittura>();
        var catalogoCambiato = false;
        var toccati = 0;

        foreach (var req in requisiti)
        {
            List<DtcRequisito> voci;
            DtcArchivioRequisito? baseline = null;
            var embedded = req.Guasto != null;
            if (embedded) voci = req.Guasto!;
            else
            {
                if (!archivio.Disponibile || !RequisitoDtcArchivio.TryUid(req, out var uidLetto)) continue;
                conteggi ??= archivio.ConteggiLeggeri();
                if (!conteggi.TryGetValue(uidLetto, out var n) || n <= 0) continue;
                var letto = archivio.Leggi(req);
                voci = letto.Voci; baseline = letto.Documento;
            }
            if (!voci.Any(v => campi.Any(c => UsaValoreInProprieta(v, c.Proprieta!, vecchio)))) continue;

            var sostituite = new List<DtcRequisito>();
            var viste = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var omesse = 0;
            var cambiato = false;
            foreach (var v in voci)
            {
                var copia = v.Clona();
                foreach (var c in campi)
                    if (SostituisciInProprieta(copia, c.Proprieta!, vecchio, nuovi, out var limitato))
                    {
                        cambiato = true;
                        if (limitato) avvisi.Add($"Requisito {req.Id}: campo singolo '{c.Descrizione}' limitato al primo nuovo valore dopo una divisione.");
                    }
                if (viste.Add(DtcRequisito.Chiave(copia))) sostituite.Add(copia); else { omesse++; cambiato = true; }
            }
            if (!cambiato) continue;
            if (omesse > 0) avvisi.Add($"Requisito {req.Id}: {omesse} voce/i guasti identiche per (veicolo, componente, codice) dopo l'operazione sono state fuse in una.");
            toccati++;
            if (embedded) { req.Guasto = sostituite; catalogoCambiato = true; }
            else
            {
                scritture.Add(new DtcScrittura(req, sostituite, baseline));
                if (omesse > 0) { req.DtcConteggio = sostituite.Count; catalogoCambiato = true; }
            }
        }

        if (scritture.Count > 0) archivio.Scrivi(scritture, operatore);
        if (catalogoCambiato) master.Requisiti.Save(requisiti, atteso);
        return toccati;
    }

    /// <summary>Come <see cref="ApplicaSuCatalogoOspitante{T}"/>, specializzato per
    /// ValutazioneRequisitoEcuVeicolo: la sostituzione di Componente/Veicolo/RequisitoId può far collassare due righe
    /// sulla stessa chiave di cella, cosa che il guard generico Phase D
    /// (PhaseDMasterRepositoryEngine.ValidateBusinessKeys) rifiuterebbe interamente a Save — sicuro ma
    /// bloccherebbe un'unione altrimenti legittima. Consolida quindi le collisioni PRIMA di salvare
    /// (ValutazioneRequisitoEcuVeicoloService.ConsolidaCollisioni: vince la riga con UltimaModifica più
    /// recente, l'altra viene rimossa ma la sua nota/origine/punteggio confluiscono nella vincente, mai persi
    /// in silenzio) e segnala nell'elenco avvisi quante ne sono state consolidate, per l'anteprima di conferma.
    /// Il guard generico resta comunque attivo come rete di sicurezza sul Save finale.</summary>
    private static int ApplicaSuValutazioniConConsolidamento(
        MasterDataRepository<ValutazioneRequisitoEcuVeicolo> repo, string catalogo, string vecchio,
        IReadOnlyList<string> nuovi, string operatore, List<string> avvisi)
    {
        var campi = Registro.Where(r => r.TipoNodo == typeof(ValutazioneRequisitoEcuVeicolo) && r.Catalogo == catalogo && r.Proprieta != null).ToList();
        if (campi.Count == 0) return 0;

        var elementi = repo.Load();
        var atteso = EntityStateFingerprint.Clone(elementi);
        var toccati = 0;
        foreach (var elemento in elementi)
            foreach (var campo in campi)
                if (SostituisciInProprieta(elemento, campo.Proprieta!, vecchio, nuovi, out var limitato))
                {
                    toccati++;
                    if (limitato) avvisi.Add($"ValutazioneRequisitoEcuVeicolo '{ChiaveDescrittiva(elemento)}': campo singolo '{campo.Descrizione}' limitato al primo nuovo valore dopo una divisione.");
                }
        if (toccati == 0) return 0;

        var consolidate = ValutazioneRequisitoEcuVeicoloService.ConsolidaCollisioni(elementi, operatore);
        if (consolidate > 0)
            avvisi.Add($"ValutazioneRequisitoEcuVeicolo: {consolidate} valutazione/i risultavano sulla stessa cella dopo l'operazione e sono state consolidate automaticamente (vince la più recente; nota/origine/punteggio della voce sostituita riportati nella vincente).");

        repo.Save(elementi, atteso);
        return toccati;
    }

    /// <summary>Conteggio "a secco" (nessuna mutazione, nessuna scrittura) di quante valutazioni collidrebbero
    /// sulla stessa cella se <paramref name="vecchio"/> venisse sostituito con <paramref name="nuovo"/> su
    /// <paramref name="catalogo"/> — per l'anteprima di conferma di Unisci/Rinomina (review indipendente
    /// 2026-09-24: "l'anteprima deve mostrare quante collisioni verranno consolidate").</summary>
    public static int ContaCollisioniValutazioni(MasterDataStore master, string catalogo, string vecchio, string nuovo)
    {
        var campi = Registro.Where(r => r.TipoNodo == typeof(ValutazioneRequisitoEcuVeicolo) && r.Catalogo == catalogo && r.Proprieta != null).ToList();
        if (campi.Count == 0) return 0;
        var valutazioni = master.ValutazioniRequisitoEcuVeicolo.Load();
        return ValutazioneRequisitoEcuVeicoloService.ContaCollisioniSeSostituito(valutazioni, campi.Select(c => c.Proprieta!), vecchio, nuovo);
    }

    private static int ApplicaSuCatalogoOspitante<T>(MasterDataRepository<T> repo, string catalogo, string vecchio, IReadOnlyList<string> nuovi, List<string> avvisi,
        IReadOnlySet<string> ecuEsistenti)
        where T : MasterEntity, new()
    {
        var campi = Registro.Where(r => r.TipoNodo == typeof(T) && r.Catalogo == catalogo && r.Proprieta != null).ToList();
        // Restrizioni per componente delle Funzionalità (VeicoliPerEcu): non sono campi di primo livello, si gestiscono a parte.
        var conRestrizioni = typeof(T) == typeof(Funzionalita) && (catalogo == "Componente" || catalogo == "Veicolo");
        if (campi.Count == 0 && !conRestrizioni) return 0;

        var catalogoElementi = repo.Load();
        // Fotografia "prima" catturata SUBITO dopo il Load, prima di qualunque mutazione: stesso schema
        // expectedSnapshot di WuStatusViewModel.Unisci/RevisioneUdtService — un conflitto Phase D deve poter
        // confrontare lo stato remoto con esattamente ciò che questa chiamata ha letto, non con la versione
        // già mutata in memoria.
        var atteso = EntityStateFingerprint.Clone(catalogoElementi);
        var toccati = 0;
        foreach (var elemento in catalogoElementi)
        {
            // Prima dei campi: la regola sulle restrizioni legge l'applicabilità (EcuAssociate) precedente alla sostituzione.
            if (conRestrizioni && elemento is Funzionalita funzionalita && SostituisciInRestrizioni(funzionalita, catalogo, vecchio, nuovi, ecuEsistenti)) toccati++;
            foreach (var campo in campi)
                if (SostituisciInProprieta(elemento!, campo.Proprieta!, vecchio, nuovi, out var limitato))
                {
                    toccati++;
                    if (limitato) avvisi.Add($"{typeof(T).Name} '{ChiaveDescrittiva(elemento)}': campo singolo '{campo.Descrizione}' limitato al primo nuovo valore dopo una divisione.");
                }
        }

        if (toccati > 0)
            repo.Save(catalogoElementi, atteso);
        return toccati;
    }

    private static readonly PropertyInfo PropVeicoliDiVoce = typeof(VeicoliFunzionalitaEcu).GetProperty(nameof(VeicoliFunzionalitaEcu.Veicoli))!;

    /// <summary>Restrizioni per componente di una Funzionalità dopo Unisci/Rinomina/Dividi: sul catalogo componente il nome segue la regola di
    /// combinazione del modello (SostituisciEcuNelleRestrizioni); sul catalogo Veicolo il veicolo segue la sostituzione per lista
    /// delle altre liste. Ritorna true se il contenuto cambia.</summary>
    private static bool SostituisciInRestrizioni(Funzionalita funzionalita, string catalogo, string vecchio, IReadOnlyList<string> nuovi, IReadOnlySet<string> ecuEsistenti)
    {
        if (funzionalita.VeicoliPerEcu.Count == 0) return false;
        if (catalogo == "Componente")
        {
            var prima = funzionalita.VeicoliPerEcuTesto;
            funzionalita.SostituisciEcuNelleRestrizioni(vecchio, nuovi, ecuEsistenti);
            return funzionalita.VeicoliPerEcuTesto != prima;
        }
        var cambiato = false;
        foreach (var voce in funzionalita.VeicoliPerEcu)
            cambiato |= SostituisciInProprieta(voce, PropVeicoliDiVoce, vecchio, nuovi, out _);
        return cambiato;
    }

    private static string ChiaveDescrittiva(object entita) =>
        entita.GetType().GetProperty("Nome")?.GetValue(entita) as string ?? entita.ToString() ?? "";
}
