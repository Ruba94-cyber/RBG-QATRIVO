namespace TestLoom.App.Services;

/// <summary>Contratto minimo per un elemento riordinabile in un catalogo di campi/sezioni configurabile
/// (Sezioni/CampoTemplate di TestTemplate, campi custom di uno StrumentoDiagnostico). Ordine è la fonte di
/// verità del rendering ovunque il campo compare (editor, anteprima TestFormPreviewRenderer, export
/// FoglioReportAutoGenService — tutti già in OrderBy(Ordine) prima di questa modifica): la posizione fisica
/// nella List&lt;T&gt; JSON-serializzata non conta più per la visualizzazione, solo Ordine. Vedi spec
/// riordino campi 2026-09-23 e revisione .ai/artifacts/review_riordino.json (F1/F2).</summary>
public interface IOrdinabile
{
    int Ordine { get; set; }
}

/// <summary>Riordino generico per qualunque catalogo di campi/sezioni configurabile del template di test o
/// di uno strumento diagnostico.
///
/// Principio centrale (revisione F2): la sola LETTURA per la visualizzazione (<see cref="OrdinePerVisualizzazione{T}"/>)
/// non deve mai mutare i dati — usa un ordinamento stabile per Ordine crescente, così dati legacy con Ordine
/// assente/duplicato/a zero si vedono già nell'ordine con cui comparivano prima di questa funzionalità (LINQ
/// OrderBy è uno stable sort: a parità di chiave mantiene l'ordine relativo della sorgente), senza bisogno di
/// alcuna migrazione scritta all'apertura di un editor. Solo un'azione utente esplicita (sposta su/giù,
/// inserisci un nuovo campo, rimuovi un campo) normalizza davvero Ordine — mai la sola apertura della
/// finestra — per non generare uno stato "modificato"/un salvataggio silenzioso senza intervento reale
/// dell'utente (vedi MasterDataEditorViewModelBase.HaModifiche, che confronta con lo snapshot preso
/// all'inizio della sessione di modifica).</summary>
public static class CampoOrdinamentoService
{
    /// <summary>Ordine di visualizzazione "vero", stesso criterio già usato da TestFormPreviewRenderer e
    /// FoglioReportAutoGenService: Ordine crescente, stabile a parità (mantiene l'ordine relativo di
    /// provenienza). Non muta mai <paramref name="lista"/> né i suoi elementi — sicura da chiamare ad ogni
    /// ridisegno, incluso il primo, senza alcun effetto collaterale sullo stato "modificato".</summary>
    public static List<T> OrdinePerVisualizzazione<T>(IEnumerable<T> lista) where T : IOrdinabile
        => lista.OrderBy(x => x.Ordine).ToList();

    /// <summary>Sposta l'elemento di una posizione verso l'alto nell'ordine di visualizzazione. Nessun
    /// effetto se già in cima o se l'elemento non è nella lista. Normalizza Ordine su tutta la lista come
    /// parte di questa azione utente (non prima), poi scambia con il precedente.</summary>
    public static bool SpostaSu<T>(IList<T> lista, T elemento) where T : IOrdinabile
    {
        var visuale = OrdinePerVisualizzazione(lista);
        var indice = visuale.IndexOf(elemento);
        if (indice <= 0) return false;
        Normalizza(lista);
        (visuale[indice].Ordine, visuale[indice - 1].Ordine) = (visuale[indice - 1].Ordine, visuale[indice].Ordine);
        return true;
    }

    /// <summary>Sposta l'elemento di una posizione verso il basso nell'ordine di visualizzazione. Nessun
    /// effetto se già in fondo o se l'elemento non è nella lista.</summary>
    public static bool SpostaGiu<T>(IList<T> lista, T elemento) where T : IOrdinabile
    {
        var visuale = OrdinePerVisualizzazione(lista);
        var indice = visuale.IndexOf(elemento);
        if (indice < 0 || indice >= visuale.Count - 1) return false;
        Normalizza(lista);
        (visuale[indice].Ordine, visuale[indice + 1].Ordine) = (visuale[indice + 1].Ordine, visuale[indice].Ordine);
        return true;
    }

    /// <summary>Inserisce nuovo in una posizione scelta nell'ordine di visualizzazione: dopo l'elemento
    /// "dopo" se presente (in fondo se "dopo" è null/non trovato e inizio è false), oppure in cima se inizio
    /// è true (dopo viene allora ignorato). Aggiunge nuovo in fondo alla lista fisica (la posizione fisica
    /// non conta più per la visualizzazione, solo Ordine) e riallinea Ordine di tutti gli elementi alla
    /// posizione scelta.</summary>
    public static void InserisciInPosizione<T>(IList<T> lista, T nuovo, T? dopo = default, bool inizio = false) where T : IOrdinabile
    {
        var visuale = OrdinePerVisualizzazione(lista);
        int indice;
        if (inizio) indice = 0;
        else
        {
            var i = dopo != null ? visuale.IndexOf(dopo) : -1;
            indice = i < 0 ? visuale.Count : i + 1;
        }
        visuale.Insert(indice, nuovo);
        lista.Add(nuovo);
        for (var i = 0; i < visuale.Count; i++) visuale[i].Ordine = i;
    }

    /// <summary>Riallinea Ordine=posizione-nell'ordine-di-visualizzazione-corrente per tutti gli elementi
    /// della lista, senza alterare quell'ordine (idempotente: chiamarla su dati già allineati non cambia
    /// nulla). Usata internamente da SpostaSu/SpostaGiu/InserisciInPosizione come parte di un'azione utente
    /// esplicita — MAI chiamata automaticamente alla sola apertura di un editor (vedi nota di classe).</summary>
    public static void Normalizza<T>(IList<T> lista) where T : IOrdinabile
    {
        var visuale = OrdinePerVisualizzazione(lista);
        for (var i = 0; i < visuale.Count; i++) visuale[i].Ordine = i;
    }
}
