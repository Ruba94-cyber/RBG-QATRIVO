using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia;
using Avalonia.Media;
using TestLoom.App.Services;
using TestLoom.App.Models;

namespace TestLoom.App;

/// <summary>Predicati di ricerca per gli AutoCompleteBox di Requisiti/Test collegati (Addendum: collaudo live
/// 2026-08-17, punto 3 del grill-me): cercano sia sull'identificativo sia sul testo descrittivo, non solo
/// sull'Id come farebbe il filtro Contains di default dell'AutoCompleteBox.</summary>
public static class AutoCompleteFilters
{
    // Testo vuoto -> nessun filtro applicato (item is X basta): con MinimumPrefixLength="0" sull'
    // AutoCompleteBox, un click che apre la lista senza aver ancora scritto nulla deve mostrare tutte le
    // voci, non un elenco vuoto (feedback utente 2026-08-19 — "le combobox non fanno aprire la lista senza
    // prima scrivere qualcosa").
    public static readonly Avalonia.Controls.AutoCompleteFilterPredicate<object?> Requisito = (testo, item) =>
        item is Requisito r && (string.IsNullOrEmpty(testo) ||
        r.Id.Contains(testo, StringComparison.OrdinalIgnoreCase) || r.Testo.Contains(testo, StringComparison.OrdinalIgnoreCase));

    /// <summary>Cerca anche per componente, non solo Id/descrizione breve (feedback utente 2026-08-20, punto 21:
    /// "quando ci sono test da collegare è quasi impossibile ricordarmi l'id... magari per descrizione breve o
    /// per componente") — ricordare il componente di un test è molto più facile del suo id generato.</summary>
    public static readonly Avalonia.Controls.AutoCompleteFilterPredicate<object?> TestCorrelato = (testo, item) =>
        item is TestCase t && (string.IsNullOrEmpty(testo) ||
        t.Id.Contains(testo, StringComparison.OrdinalIgnoreCase) || t.DescrizioneBreve.Contains(testo, StringComparison.OrdinalIgnoreCase)
        || t.Componente.Contains(testo, StringComparison.OrdinalIgnoreCase));

    /// <summary>Filtro generico per elenchi di stringhe da dati master (componente, Veicolo, Funzionalità,
    /// Assegnatario, Gamma, Categoria...): riusabile ovunque un AutoCompleteBox proponga voci man mano che
    /// crescono, invece del solo scroll di un ComboBox (feedback utente 2026-08-19, punto 1).</summary>
    public static readonly Avalonia.Controls.AutoCompleteFilterPredicate<object?> Stringa = (testo, item) =>
        item is string s && (string.IsNullOrEmpty(testo) || s.Contains(testo, StringComparison.OrdinalIgnoreCase));

    public static readonly Avalonia.Controls.AutoCompleteFilterPredicate<object?> TestTemplate = (testo, item) =>
        item is TestTemplate t && (string.IsNullOrEmpty(testo) || t.Nome.Contains(testo, StringComparison.OrdinalIgnoreCase));

    public static readonly Avalonia.Controls.AutoCompleteFilterPredicate<object?> Anomalia = (testo, item) =>
        item is NonConformita n && (string.IsNullOrEmpty(testo) ||
        n.Id.Contains(testo, StringComparison.OrdinalIgnoreCase) ||
        n.Descrizione.Contains(testo, StringComparison.OrdinalIgnoreCase) ||
        n.Owner.Contains(testo, StringComparison.OrdinalIgnoreCase));
}

/// <summary>AND tollerante per combinare in un MultiBinding l'IsVisible di una sezione già esistente (spesso
/// bindato a un int Count, es. "CampiCustomPianificazioneVisual.Count" — Avalonia lo accetta su un target bool
/// via conversione implicita non-zero=vero) con la nuova condizione di scheda attiva (Addendum: audit UX
/// 2026-08-26, punto 1). Non uso Avalonia.Data.Converters.BoolConverters.And perché il suo cast a bool non è
/// garantito tollerante verso un int/altro IConvertible nella catena di un MultiBinding — qui invece ogni
/// valore passa da Convert.ToBoolean, lo stesso genere di conversione tollerante che IsVisible applica già
/// da solo su un singolo Binding.</summary>
public class TuttiVeriConverter : IMultiValueConverter
{
    public static readonly TuttiVeriConverter Instance = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is IConvertible ic && System.Convert.ToBoolean(ic, culture));
}

/// <summary>Evidenzia la scheda attiva nella striscia schede dell'editor Test, solo in creazione (Addendum:
/// audit UX 2026-08-26, punto 1): confronta SchedaCorrente (int) con l'indice passato come ConverterParameter,
/// entrambi come stringa. Bindato direttamente su Background/Foreground invece che su una classe CSS-style
/// (Classes.X) per evitare qualunque ambiguità di precedenza con lo Setter statico di Button.Secondary già
/// applicato allo stesso pulsante — un valore locale vince sempre su un Setter di stile, senza bisogno di
/// verificare l'ordine di dichiarazione dei selettori.</summary>
public class SchedaAttivaSfondoConverter : IValueConverter
{
    public static readonly SchedaAttivaSfondoConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString() ? new SolidColorBrush(Color.Parse("#2563EB")) : Brushes.Transparent;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class SchedaAttivaTestoConverter : IValueConverter
{
    public static readonly SchedaAttivaTestoConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString() ? Brushes.White : new SolidColorBrush(Color.Parse("#334155"));
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Join di una List&lt;string&gt; con ", " per mostrarla come testo semplice (Addendum §1, feedback
/// QA: usato per Funzionalità nella vista di sola lettura di una revisione storica).</summary>
public class JoinStringsConverter : IValueConverter
{
    public static readonly JoinStringsConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<string> lista ? string.Join(", ", lista) : null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>6.5.2: visibilita della combo Funzionalita di riga. Valori: [0] Strumento proprietario (string?), [1] test "solo guasto" (bool),
/// [2] nomi degli strumenti Da testare (IEnumerable&lt;string&gt;).</summary>
public class FunzionalitaRigaVisibileConverter : IMultiValueConverter
{
    public static readonly FunzionalitaRigaVisibileConverter Instance = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        Services.OperazioniStrumentoRegole.FunzionalitaRigaVisibile(values.Count > 0 ? values[0] as string : null,
            values.Count > 1 && values[1] is true, values.Count > 2 ? values[2] as IEnumerable<string> : null);
}

/// <summary>Join dei riferimenti ticket per la riga Change Request della pagina Anomalie (feedback utente
/// 2026-08-21, punto 11): stesso bisogno di JoinStringsConverter ma su List&lt;TicketRif&gt;, non su una lista
/// di stringhe già pronte.</summary>
public class JoinTicketRiferimentiConverter : IValueConverter
{
    public static readonly JoinTicketRiferimentiConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<Models.TicketRif> lista ? string.Join(", ", lista.Select(t => t.Riferimento)) : null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Abilita l'apertura di un link ticket soltanto se è un URL HTTP(S) assoluto.</summary>
public class UrlWebValidoConverter : IValueConverter
{
    public static readonly UrlWebValidoConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string url && IsWebUrl(url);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    public static bool IsWebUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>Visibilità del testo semplice dei ticket senza un URL HTTP(S) sicuro.</summary>
public class UrlWebNonValidoConverter : IValueConverter
{
    public static readonly UrlWebNonValidoConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not string url || !UrlWebValidoConverter.IsWebUrl(url);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class FiltroTicketEstaLabelConverter : IValueConverter
{
    public static readonly FiltroTicketEstaLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Services.FiltroPresenzaTicketEsta filtro
            ? Services.LocalizationService.Instance.Traduci(filtro switch
            {
                Services.FiltroPresenzaTicketEsta.ConTicket => "FiltroConEsta",
                Services.FiltroPresenzaTicketEsta.SenzaTicket => "FiltroSenzaEsta",
                _ => "FiltroTuttiEsta"
            })
            : null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Testo del pulsante-picker (feedback utente 2026-08-20): mostra il valore scelto se presente,
/// altrimenti il placeholder passato come ConverterParameter. Sempre bindato alla proprietà selezionata
/// direttamente (mai a un percorso annidato tipo "TestVincoloSelezionato.Id"): TargetNullValue su un percorso
/// annidato non scatta in modo affidabile nei binding compilati di Avalonia quando l'oggetto intermedio è
/// null (torna stringa vuota invece del placeholder — bug osservato in questa stessa sessione sul campo "Test
/// collegato"). Bindando l'oggetto intero, il null è quello vero della proprietà, non un percorso derivato.</summary>
public class ScegliPlaceholderConverter : IValueConverter
{
    public static readonly ScegliPlaceholderConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var placeholder = parameter is string chiave ? Services.LocalizationService.Instance.Traduci(chiave) : "";
        return value switch
        {
            null => placeholder,
            string s => string.IsNullOrWhiteSpace(s) ? placeholder : s,
            Models.EcuSelection componente => componente.Count == 0 ? placeholder : componente.ToString(),
            Models.TestTemplate t => t.Nome,
            Models.TestCase tc => tc.Id,
            Models.Requisito r => r.Id,
            Models.NonConformita n => n.Id,
            _ => value.ToString() ?? placeholder
        };
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Ponte tra il campo Valore (string, formato "gg/mm/aaaa") dei campi custom di tipo Data e
/// CalendarDatePicker.SelectedDate (DateTime?) — feedback utente 2026-08-20, punto 12: vuole sia il calendario
/// sia la possibilità di scrivere la data a mano, che CalendarDatePicker offre già in un unico controllo (testo
/// libero editabile + bottone calendario), a differenza di un DatePicker puro. Lo storage resta una stringa
/// (nessuna modifica al modello/validazione/report esistenti): il converter tollera in ingresso anche testo
/// scritto a mano non ancora completo/valido, ritornando null invece di lanciare.</summary>
public class DataTestoConverter : IValueConverter
{
    public static readonly DataTestoConverter Instance = new();
    private const string Formato = "dd/MM/yyyy";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string testo && DateTime.TryParseExact(testo.Trim(), Formato, CultureInfo.InvariantCulture, DateTimeStyles.None, out var data)
            ? data : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime data ? data.ToString(Formato, CultureInfo.InvariantCulture) : "";
}

/// <summary>Testo &lt;-&gt; data per i filtri di data con CalendarDatePicker (Anomalie, Lista Test): accetta gg/mm/aaaa,
/// g/m/aaaa e aaaa-mm-gg, come il vecchio campo di testo. Un testo non interpretabile lancia FormatException: il
/// controllo lo segnala con DateValidationError e il filtro non viene applicato (vedi FiltroDataCalendario).</summary>
public class DataFiltroTestoConverter : IValueConverter
{
    public static readonly DataFiltroTestoConverter Instance = new();
    private const string Formato = "dd/MM/yyyy";
    private static readonly string[] FormatiAccettati = { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd" };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime data ? data.ToString(Formato, CultureInfo.InvariantCulture) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Testo vuoto o di soli spazi: nessuna data (il controllo azzera il valore senza segnalare errori).
        if (value is not string testo || string.IsNullOrWhiteSpace(testo)) return null;
        if (DateTime.TryParseExact(testo.Trim(), FormatiAccettati, CultureInfo.InvariantCulture, DateTimeStyles.None, out var data))
            return data.Date;
        throw new FormatException($"Data non valida: '{testo}'.");
    }
}

/// <summary>Ponte tra i filtri di data dei ViewModel (DateTimeOffset?) e la SelectedDate del CalendarDatePicker, che e' un
/// DateTime?: la data di calendario resta la stessa, senza ora ne' fuso (inizio giornata locale).</summary>
public class DataOffsetDataConverter : IValueConverter
{
    public static readonly DataOffsetDataConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTimeOffset data) return data.Date;
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime data) return new DateTimeOffset(data.Date);
        return null;
    }
}

public class CampiStrumentoCountConverter : IMultiValueConverter
{
    public static readonly CampiStrumentoCountConverter Instance = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = values.Count > 1 && values[1] is int n ? n : 0;
        return string.Format(Services.LocalizationService.Instance.Traduci("GestisciCampiFmt"), count);
    }
}

/// <summary>Formatta un conteggio con una risorsa localizzata. Il secondo valore della MultiBinding è la
/// lingua e serve a rivalutare il testo immediatamente quando l'utente cambia lingua.</summary>
public class LocalizedCountConverter : IMultiValueConverter
{
    public static readonly LocalizedCountConverter Instance = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        // long: il totale di un elenco paginato (TotaleSegnalazioni) puo' superare int.
        var count = values.Count > 0 ? values[0] switch { int n => (long)n, long l => l, _ => 0L } : 0L;
        return parameter is string key
            ? string.Format(Services.LocalizationService.Instance.Traduci(key), count)
            : count.ToString(culture);
    }
}

/// <summary>Formatta il primo valore con una stringa localizzata (chiave in ConverterParameter); i bool
/// diventano Sì/No localizzati. Il secondo binding (Lingua) serve solo da trigger di ricalcolo.</summary>
public class LocalizedFormatConverter : IMultiValueConverter
{
    public static readonly LocalizedFormatConverter Instance = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var loc = Services.LocalizationService.Instance;
        object? v = values.Count > 0 ? values[0] : null;
        if (v is bool b) v = loc.Traduci(b ? "SiLabel" : "NoLabel");
        return parameter is string key ? string.Format(loc.Traduci(key), v) : v?.ToString();
    }
}

/// <summary>Confronta un TipoCampoStrumento con un nome passato come stringa in ConverterParameter, per
/// mostrare/nascondere il controllo di editing giusto in base al tipo del campo (Addendum redesign 2026-08,
/// revisione feedback QA). Stringa invece di x:Static enum: nessun precedente di quel pattern in questo
/// progetto, questo è più semplice da verificare a colpo d'occhio nello XAML.</summary>
public class TipoCampoEqualsConverter : IValueConverter
{
    public static readonly TipoCampoEqualsConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TipoCampoStrumento t && parameter is string s && string.Equals(t.ToString(), s, StringComparison.OrdinalIgnoreCase);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Null resta null (non un'etichetta) apposta: nei filtri "nessuna selezione" deve mostrare il
/// PlaceholderText della ComboBox, non un'etichetta testuale che sembri un valore scelto. Dove null ha
/// invece un significato reale da mostrare (es. StatoCorrente = "nessuna esecuzione ancora"), il binding
/// specifico usa TargetNullValue per fornire quel testo, non questo converter.</summary>
public class EsitoLabelConverter : IValueConverter
{
    public static readonly EsitoLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch { Esito e => e.Label(), _ => null };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Descrizione localizzata del valore di esito corrente mostrato in Home.</summary>
public class EsitoTooltipConverter : IValueConverter
{
    public static readonly EsitoTooltipConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Services.MatriceTooltipService.DescrizioneEsito(value is Esito e ? e : null);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Spiegazione contestuale dei valori di una cella o di un totale della matrice Report.</summary>
public class ReportMatriceTooltipConverter : IValueConverter
{
    public static readonly ReportMatriceTooltipConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Services.CellaMatriceReport cella => Services.MatriceTooltipService.TooltipCella(cella),
        Services.TotaleMatriceReport totale when string.Equals(parameter as string, "stato", StringComparison.OrdinalIgnoreCase)
            => Services.MatriceTooltipService.TooltipTotaleStato(totale),
        Services.TotaleMatriceReport totale => Services.MatriceTooltipService.TooltipTotalePercentuale(totale),
        _ => null,
    };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Etichetta tradotta del passo guasto (Validazione/Devalidazione), 6.5.0.</summary>
public class PassoDtcLabelConverter : IValueConverter
{
    public static readonly PassoDtcLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PassoDtc p ? Services.LocalizationService.Instance.Traduci(p == PassoDtc.Devalidazione ? "DtcPassoDevalidazione" : "DtcPassoValidazione") : null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Etichetta tradotta del tipo di strumento (Da testare / Comportamento / Supporto), 6.5.1.</summary>
public class TipoStrumentoLabelConverter : IValueConverter
{
    public static readonly TipoStrumentoLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TipoStrumento t ? Services.LocalizationService.Instance.Traduci(t switch
        {
            TipoStrumento.DaTestare => "TipoStrumentoDaTestare",
            TipoStrumento.Comportamento => "TipoStrumentoComportamento",
            _ => "TipoStrumentoSupporto",
        }) : null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Badge "operazione ripetuta" (feedback utente 2026-08-24): il solo simbolo 🔁 non basta a capire DA
/// QUALE operazione derivi un tentativo, e con più ripetizioni sembrano tutte uguali (stessa Funzionalità
/// copiata dall'origine). Risale alla radice della catena (OperazioneOrigineUid) dentro CondizioniVisual,
/// mostra la sua Funzionalità (o un testo generico se non ne ha ancora una) e il numero del tentativo corrente
/// nella catena, contando in ordine di lista — Ripeti inserisce sempre subito dopo il predecessore, quindi
/// l'ordine di CondizioniVisual coincide con l'ordine cronologico dei tentativi.</summary>
public class EtichettaRipetizioneConverter : IMultiValueConverter
{
    public static readonly EtichettaRipetizioneConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not Condizione condizione || condizione.OperazioneOrigineUid is not { } radiceUid)
            return null;
        var tutte = values[1] as IEnumerable<Condizione>;
        if (tutte == null) return null;

        var catena = tutte.Where(c => c.Uid == radiceUid || c.OperazioneOrigineUid == radiceUid).ToList();
        var radice = catena.FirstOrDefault(c => c.Uid == radiceUid);
        var tentativo = catena.IndexOf(condizione) + 1;
        if (tentativo <= 0) tentativo = catena.Count;

        var loc = Services.LocalizationService.Instance;
        return !string.IsNullOrWhiteSpace(radice?.Funzionalita)
            ? string.Format(loc.Traduci("OperazioneRipetutaBadgeConEtichettaFmt"), radice!.Funzionalita, tentativo)
            : string.Format(loc.Traduci("OperazioneRipetutaBadgeFmt"), tentativo);
    }
}

public class TipoCampoStrumentoLabelConverter : IValueConverter
{
    public static readonly TipoCampoStrumentoLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch { TipoCampoStrumento t => t.Label(), _ => null };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class PunteggioConformitaLabelConverter : IValueConverter
{
    public static readonly PunteggioConformitaLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch { PunteggioConformita p => p.Label(), _ => null };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Etichetta di una voce nel menu REV (Addendum §6, feedback QA): numero, data, operatore ed esito
/// in un colpo d'occhio, così scegliere la revisione giusta non richiede di aprirle una per una.</summary>
public class EsecuzioneRevLabelConverter : IValueConverter
{
    public static readonly EsecuzioneRevLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Esecuzione e => $"Rev. {e.Iterazione} · {e.Data:dd/MM/yyyy} · {e.Operatore} · {e.Esito.Label()}",
        _ => null
    };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class EsitoColorConverter : IValueConverter
{
    public static readonly EsitoColorConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Esito.Conforme => new SolidColorBrush(Color.Parse("#DCFCE7")),
        Esito.ConformeConDeviazioni => new SolidColorBrush(Color.Parse("#FEF3C7")),
        Esito.NonConforme => new SolidColorBrush(Color.Parse("#FEE2E2")),
        Esito.DaRivedere => new SolidColorBrush(Color.Parse("#DBEAFE")),
        Esito.NonEseguitoNA => new SolidColorBrush(Color.Parse("#F1F5F9")),
        _ => new SolidColorBrush(Color.Parse("#F1F5F9"))
    };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Sfondo del campo/riga in base a ColoreEvidenziazione (feedback utente 2026-08-24, punto 8) —
/// stesse tinte pastello già usate da EsitoColorConverter per Giallo/Rosso, coerenti con la palette esistente
/// invece di introdurre nuovi colori. Nessuno resta trasparente: un campo mai evidenziato non deve spiccare.</summary>
public class ColoreEvidenziazioneBrushConverter : IValueConverter
{
    public static readonly ColoreEvidenziazioneBrushConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ColoreEvidenziazione.Giallo => new SolidColorBrush(Color.Parse(TemaScuro ? "#5C4A12" : "#FEF3C7")),
        ColoreEvidenziazione.Rosso => new SolidColorBrush(Color.Parse(TemaScuro ? "#5E2222" : "#FEE2E2")),
        _ => Brushes.Transparent
    };
    // Nel tema scuro il testo del campo e' chiaro: la tinta pastello lo renderebbe illeggibile, quindi si usa la stessa tinta scurita.
    private static bool TemaScuro => Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Etichetta del pulsante che apre ListaTestoEditorWindow (punto 8): "Aggiungi righe" a vuoto,
/// "Modifica lista (N)" quando già popolato — stesso schema del pulsante Opzioni Lista esistente.</summary>
public class ModificaListaTestoLabelConverter : IValueConverter
{
    public static readonly ModificaListaTestoLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value as int? ?? 0;
        var loc = TestLoom.App.Services.LocalizationService.Instance;
        return n > 0 ? string.Format(loc.Traduci("ModificaListaTestoBtnFmt"), n) : loc.Traduci("AggiungiRigheBtn");
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Testo del pulsante Opzioni di un campo di sistema di tipo Lista (colonna azioni del catalogo): "Opzioni"
/// senza voci, "Opzioni (N)" con N voci. Testo corto perché la colonna azioni è stretta. Il secondo valore della MultiBinding
/// (Lingua) serve solo da trigger: senza di esso il testo non si rilocalizza al cambio lingua (P4 9).</summary>
public class OpzioniListaLabelConverter : IMultiValueConverter
{
    public static readonly OpzioniListaLabelConverter Instance = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var testo = TestLoom.App.Services.LocalizationService.Instance.Traduci("CampoSistemaOpzioniBtn");
        var n = values.Count > 0 && values[0] is IReadOnlyCollection<string> voci ? voci.Count : 0;
        return n > 0 ? $"{testo} ({n})" : testo;
    }
}

/// <summary>Sfondo del badge di stato nella pagina WU Status (feedback utente 2026-08-24) — stesse tinte
/// pastello di EsitoColorConverter/ColoreEvidenziazioneBrushConverter, coerenti con la palette esistente.</summary>
public class StatoWorkingUnitColorConverter : IValueConverter
{
    public static readonly StatoWorkingUnitColorConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Models.StatoManualeWorkingUnit.Ok => new SolidColorBrush(Color.Parse("#DCFCE7")),
        Models.StatoManualeWorkingUnit.Problemi => new SolidColorBrush(Color.Parse("#FEE2E2")),
        _ => new SolidColorBrush(Color.Parse("#F1F5F9"))
    };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Testo dei badge WU: lo sfondo degli stati e' un pastello chiaro anche nel
/// tema scuro; usare la tinta di primo piano dello stesso stato evita testo chiaro
/// su sfondo chiaro. I colori corrispondono a quelli degli esiti Report.</summary>
public class StatoWorkingUnitTextColorConverter : IValueConverter
{
    public static readonly StatoWorkingUnitTextColorConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Models.StatoManualeWorkingUnit.Ok => new SolidColorBrush(Color.Parse("#166534")),
        Models.StatoManualeWorkingUnit.Problemi => new SolidColorBrush(Color.Parse("#991B1B")),
        _ => new SolidColorBrush(Color.Parse("#334155"))
    };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class StatoManualeWorkingUnitLabelConverter : IValueConverter
{
    public static readonly StatoManualeWorkingUnitLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Models.StatoManualeWorkingUnit s ? s.Label() : null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class EsitoTextColorConverter : IValueConverter
{
    public static readonly EsitoTextColorConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Esito.Conforme => new SolidColorBrush(Color.Parse("#166534")),
        Esito.ConformeConDeviazioni => new SolidColorBrush(Color.Parse("#92400E")),
        Esito.NonConforme => new SolidColorBrush(Color.Parse("#991B1B")),
        Esito.DaRivedere => new SolidColorBrush(Color.Parse("#1E40AF")),
        _ => new SolidColorBrush(Color.Parse("#475569"))
    };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Colori del badge di stato sincronizzazione (Addendum §4, feedback QA): verde se allineato, ambra
/// altrimenti — stesso schema cromatico dei badge Esito, non un terzo set di colori nuovo.</summary>
public class BoolToSyncColorConverter : IValueConverter
{
    public static readonly BoolToSyncColorConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new SolidColorBrush(Color.Parse("#DCFCE7")) : new SolidColorBrush(Color.Parse("#FEF3C7"));
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class BoolToSyncTextColorConverter : IValueConverter
{
    public static readonly BoolToSyncTextColorConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new SolidColorBrush(Color.Parse("#166534")) : new SolidColorBrush(Color.Parse("#92400E"));
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class EsitoNonApplicabileConverter : IValueConverter
{
    public static readonly EsitoNonApplicabileConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Esito.NonApplicabile;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Risolve un TestCase.TestTemplateUid nel nome del template (Addendum redesign 2026-08, punto 2:
/// sostituisce il vecchio TipoTestLabelConverter su un enum fisso). Legge da TestTemplateDisplay, lo stesso
/// principio di LocalizationService.Instance già usato altrove: un singleton statico letto direttamente da
/// XAML invece di passare il dato attraverso ogni riga della griglia.</summary>
public class TestTemplateLabelConverter : IValueConverter
{
    public static readonly TestTemplateLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Guid uid ? Services.TestTemplateDisplay.NomeDi(uid) : value?.ToString();
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Vero solo per OrigineTest.ImportStatoUdt (decisione utente 6.4.0, "Test importati: solo stato
/// finale"): usato per mostrare/nascondere il badge "Importato da UnitLogger" nella lista/editor Test.</summary>
public class OrigineImportatoUdtConverter : IValueConverter
{
    public static readonly OrigineImportatoUdtConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Models.OrigineTest o && o == Models.OrigineTest.ImportStatoUdt;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class WorkflowLabelConverter : IValueConverter
{
    public static readonly WorkflowLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StatoLavoroTest s => s.Label(), StatoAnomalia s => s.Label(), PrioritaOperativa p => p.Label(),
        AmbienteProva a => a.Label(), SeveritaAnomalia s => s.Label(),
        StatoNonConformita s => s.Label(),
        StatoChangeRequest cr => cr switch
        {
            StatoChangeRequest.Aperta => Services.LocalizationService.Instance.Traduci("CrEdStatoAperta"),
            StatoChangeRequest.Implementata => Services.LocalizationService.Instance.Traduci("CrEdStatoImplementata"),
            StatoChangeRequest.DaValidare => Services.LocalizationService.Instance.Traduci("CrEdDaValidare"),
            StatoChangeRequest.Validata => Services.LocalizationService.Instance.Traduci("CrEdStatoValidata"),
            StatoChangeRequest.Chiusa => Services.LocalizationService.Instance.Traduci("CrEdStatoChiusa"),
            StatoChangeRequest.Annullata => Services.LocalizationService.Instance.Traduci("CrEdStatoAnnullata"),
            _ => cr.ToString(),
        },
        ViewModels.FiltroPolarioneOpzione fp => fp switch
        {
            ViewModels.FiltroPolarioneOpzione.ConPolarion => Services.LocalizationService.Instance.Traduci("FiltroConPolarion"),
            ViewModels.FiltroPolarioneOpzione.SenzaPolarion => Services.LocalizationService.Instance.Traduci("FiltroSenzaPolarion"),
            _ => Services.LocalizationService.Instance.Traduci("FiltroTuttiPolarion"),
        },
        ViewModels.FiltroTipoAnomaliaOCr ft => ft switch
        {
            ViewModels.FiltroTipoAnomaliaOCr.Anomalie => Services.LocalizationService.Instance.Traduci("FiltroTipoAnomalie"),
            ViewModels.FiltroTipoAnomaliaOCr.ChangeRequest => Services.LocalizationService.Instance.Traduci("FiltroTipoChangeRequest"),
            _ => Services.LocalizationService.Instance.Traduci("FiltroTipoEntrambi"),
        },
        _ => value?.ToString()
    };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class StatoDettaglioNcLabelConverter : IValueConverter
{
    public static readonly StatoDettaglioNcLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch { StatoDettaglioNc s => s.Label(), _ => null };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class DestinatarioLabelConverter : IValueConverter
{
    public static readonly DestinatarioLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch { DestinatarioSegnalazione d => d.Label(), _ => null };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class CausaProbabileLabelConverter : IValueConverter
{
    public static readonly CausaProbabileLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch { CausaProbabileUdt c => c.Label(), _ => null };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class StringJoinConverter : IValueConverter
{
    public static readonly StringJoinConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<string> list ? string.Join(", ", list) : value?.ToString();
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class CountGreaterThanZeroConverter : IValueConverter
{
    public static readonly CountGreaterThanZeroConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i ? i > 0 : value is System.Collections.ICollection c && c.Count > 0;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Soglia oltre la quale un campo Lista passa da ComboBox pura al picker con ricerca già usato per
/// Componente/Veicolo/Template (feedback utente 2026-08-21, punto 2): fissa nel codice, non configurabile dal
/// Master, per evitare comportamenti incoerenti tra strumenti diversi se qualcuno la cambiasse per errore.</summary>
public class ListaOltreSogliaConverter : IValueConverter
{
    public static readonly ListaOltreSogliaConverter Instance = new();
    public const int Soglia = 10;
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is System.Collections.ICollection c && c.Count > Soglia;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class ListaEntroSogliaConverter : IValueConverter
{
    public static readonly ListaEntroSogliaConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is System.Collections.ICollection c && c.Count <= ListaOltreSogliaConverter.Soglia;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Sfondo distinto per una cella della matrice Report marcata "N.A." (decisione utente 6.4.0, punto
/// 4): grigio, coerente con la palette pastello già usata per gli altri stati (EsitoColorConverter) ma
/// deliberatamente neutro — N.A. non è né un esito positivo né negativo, è un'esclusione dal calcolo.</summary>
public class CellaNonApplicabileBrushConverter : IValueConverter
{
    public static readonly CellaNonApplicabileBrushConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new SolidColorBrush(Color.Parse("#E2E8F0")) : Brushes.Transparent;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Testo del tooltip di una cella della matrice Report: la nota inserita dall'utente se presente,
/// altrimenti il messaggio generico "cella esclusa dalle percentuali" — mai una stringa vuota, che in Avalonia
/// equivale a "nessun tooltip" e nasconderebbe il fatto che la cella è marcata N.A.</summary>
public class CellaNonApplicabileTooltipConverter : IMultiValueConverter
{
    public static readonly CellaNonApplicabileTooltipConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var tooltipManuale = values.Count > 2 ? values[2] as string : null;
        if (values.Count > 0 && values[0] is true)
        {
            var nota = values.Count > 1 ? values[1] as string : null;
            var tooltipNA = string.IsNullOrWhiteSpace(nota)
                ? Services.LocalizationService.Instance.Traduci("MatriceCellaNATooltip")
                : nota;
            return string.IsNullOrWhiteSpace(tooltipManuale)
                ? tooltipNA
                : tooltipNA + Environment.NewLine + tooltipManuale;
        }

        return string.IsNullOrWhiteSpace(tooltipManuale) ? null : tooltipManuale;
    }
}

/// <summary>Testo in corsivo per il valore di una cella della matrice Report marcata N.A., stesso segnale
/// visivo discreto già usato altrove per distinguere uno stato "escluso" senza aggiungere un terzo colore.</summary>
public class BoolToItalicConverter : IValueConverter
{
    public static readonly BoolToItalicConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FontStyle.Italic : FontStyle.Normal;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b && !b;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b && !b;
}

/// <summary>Traduce l'etichetta di una voce di navigazione (Addendum §20): il valore vero e proprio
/// (NavItem.Titolo, es. "Test") resta la chiave di instradamento interna, invariata, usata ovunque altrove
/// nel codice per identificare la voce — qui si traduce solo per la visualizzazione. Il secondo binding
/// (TestLoom.App.Services.LocalizationService.Instance.Lingua) non serve al valore in sé: la sua unica
/// funzione è forzare il ricalcolo di questa MultiBinding quando la lingua cambia, dato che NavItem non è
/// osservabile e altrimenti l'etichetta resterebbe quella con cui è stata creata la prima volta.</summary>
public class NavLabelConverter : IMultiValueConverter
{
    public static readonly NavLabelConverter Instance = new();

    /// <summary>Titolo tradotto con "Nav" + Titolo come chiave (Addendum: collaudo, feedback QA — bug: per le
    /// voci il cui Titolo segue un'Etichetta rinominata dal Master nel catalogo campi di sistema, quella
    /// chiave non esiste mai nel dizionario; Traduci restituiva la chiave grezza invariata, mostrando
    /// letteralmente "Nav" incollato davanti al testo scelto dal Master. Se la traduzione non esiste, si
    /// mostra il Titolo così com'è, mai la chiave di lookup.</summary>
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 0 || values[0] is not string titolo) return null;
        var chiave = "Nav" + titolo;
        var tradotto = TestLoom.App.Services.LocalizationService.Instance.Traduci(chiave);
        return tradotto == chiave ? titolo : tradotto;
    }
}

/// <summary>Altezza massima della zona sopra la tabella delle pagine master = altezza pagina - riserva (parametro,
/// default 400 px: intestazione + banner + griglia principale MinHeight 200 + paginazione + margini), con minimo 160.
/// Cosi' filtri/expander (Unione, Categorie/Gamme) usano TUTTO lo spazio non necessario alla griglia, invece di una
/// frazione fissa che su 1280x800 li riduceva a ~245 px (Rinomina/Dividi e le righe annidate non si vedevano).
/// Altezza 0/NaN (primo layout) -> 420.
/// 6.6.1: il parametro e' "riserva" oppure "riserva|minimo" (DIP), cosi' anche i pannelli sopra le finestre (ricerca,
/// notifiche, guida) possono legarsi all'altezza reale della finestra. Senza minimo esplicito vale 160.</summary>
public class AltezzaResiduaConverter : IValueConverter
{
    public const double Minimo = 160;
    public const double RiservaPredefinita = 400;
    public const double MassimoPrimoLayout = 420;
    public static readonly AltezzaResiduaConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double altezza || double.IsNaN(altezza) || double.IsInfinity(altezza) || altezza <= 0) return MassimoPrimoLayout;
        var (riserva, minimo) = Parametri(parameter);
        return Math.Max(minimo, altezza - riserva);
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    internal static (double Riserva, double Minimo) Parametri(object? parameter)
    {
        var parti = (parameter as string)?.Split('|');
        var riserva = RiservaPredefinita;
        var minimo = Minimo;
        if (parti is { Length: > 0 } && double.TryParse(parti[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) riserva = r;
        if (parti is { Length: > 1 } && double.TryParse(parti[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) minimo = m;
        return (riserva, minimo);
    }
}

/// <summary>Numero di colonne di un UniformGrid dalla larghezza disponibile (6.6.1: KPI di Home e Audit).
/// Parametro "minColonna|massimo" in DIP (massimo = numero di elementi). Entrano tante colonne quante ne permette la
/// larghezza minima per colonna; poi le righe si bilanciano: con c colonne servono r = ceil(N / c) righe e ogni riga
/// prende ceil(N / r) elementi. Cosi' non compare mai una riga orfana: 7 elementi con 6 colonne danno 4 + 3, non 6 + 1;
/// 6 elementi con 4 colonne danno 3 + 3. Larghezza 0/NaN (primo layout) -> massimo.</summary>
public class ColonneDaLarghezzaConverter : IValueConverter
{
    public static readonly ColonneDaLarghezzaConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var (minColonna, massimo) = Parametri(parameter);
        return Calcola(value is double larghezza ? larghezza : double.NaN, minColonna, massimo);
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    internal static int Calcola(double larghezza, double minColonna, int elementi)
    {
        if (elementi < 1) return 1;
        if (double.IsNaN(larghezza) || double.IsInfinity(larghezza) || larghezza <= 0 || minColonna <= 0) return elementi;
        var colonne = (int)Math.Clamp(Math.Floor(larghezza / minColonna), 1, elementi);
        var righe = (int)Math.Ceiling((double)elementi / colonne);
        return (int)Math.Ceiling((double)elementi / righe);
    }

    internal static (double MinColonna, int Massimo) Parametri(object? parameter)
    {
        var parti = (parameter as string)?.Split('|');
        var minColonna = 0.0;
        var massimo = int.MaxValue;
        if (parti is { Length: > 0 } && double.TryParse(parti[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) minColonna = m;
        if (parti is { Length: > 1 } && int.TryParse(parti[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)) massimo = x;
        return (minColonna, massimo);
    }
}

/// <summary>Visibilita' del "Copia dalla precedente" di una singola operazione: visibile solo se l'operazione NON e' la prima della
/// revisione (sulla prima non c'e' nulla da copiare). Valori: Uid dell'operazione, Uid della prima operazione. Il pulsante viene
/// nascosto, non disabilitato (6.6.1).</summary>
public class NonPrimaCondizioneConverter : IMultiValueConverter
{
    public static readonly NonPrimaCondizioneConverter Instance = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count == 2 && values[0] is Guid uid && values[1] is Guid primo && uid != primo;
}

/// <summary>Larghezza di un elemento in una card a colonne (WrapPanel con ItemWidth = questo valore): con N colonne (parametro)
/// larghezza / N in Standard e Ampio, larghezza piena in Compatto e Minimo, come chiede il layout adattivo (6.6.1). Il modo e'
/// quello della finestra in cui sta la card; per i dialoghi resta quello dell'apertura.</summary>
public class LarghezzaColonnaConverter : IValueConverter
{
    public static readonly LarghezzaColonnaConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double larghezza || double.IsNaN(larghezza) || larghezza <= 0) return double.NaN;
        var colonne = int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Max(1, n) : 1;
        return TestLoom.App.Services.LayoutAdattivo.ModoCorrente <= TestLoom.App.Services.ModoLayout.Compatto ? larghezza : larghezza / colonne;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Titolo di un Expander legato a un'etichetta di ruolo di sistema (es. "Gamma", "Categoria"): se il Master
/// non l'ha rinominata (valore = fallback italiano storico) la mostra nella lingua corrente, altrimenti la
/// rinomina resta invariata. Parametro "FallbackItaliano|ChiaveTraduzione". Il secondo binding (Lingua) forza il
/// ricalcolo al cambio lingua.</summary>
public class EtichettaRuoloLocalizzataConverter : IMultiValueConverter
{
    public static readonly EtichettaRuoloLocalizzataConverter Instance = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var etichetta = values.Count > 0 ? values[0] as string : null;
        var parti = (parameter as string)?.Split('|');
        if (parti is not { Length: 2 }) return etichetta;
        if (string.IsNullOrWhiteSpace(etichetta) || string.Equals(etichetta, parti[0], StringComparison.Ordinal))
            return TestLoom.App.Services.LocalizationService.Instance.Traduci(parti[1]);
        return etichetta;
    }
}

/// <summary>Colore di sfondo o di testo di una cella della matrice Report (6.6.1): N.A. grigio; in modalità Stato il colore
/// dello stato; altrimenti trasparente (testo: colore del tema). Il parametro "sfondo" o "testo" sceglie il pennello.</summary>
public class CellaStatoPennelloConverter : IValueConverter
{
    public static readonly CellaStatoPennelloConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var testo = string.Equals(parameter as string, "testo", StringComparison.OrdinalIgnoreCase);
        if (value is not CellaMatriceReport cella) return testo ? AvaloniaProperty.UnsetValue : Brushes.Transparent;
        // N.A. ha uno sfondo grigio CHIARO fisso anche in Dark. Il testo deve
        // quindi avere l'inchiostro scuro (mai ereditarne uno bianco dal tema).
        if (cella.NonApplicabile)
            return new SolidColorBrush(Color.Parse(testo ? "#334155" : "#E2E8F0"));
        var (sfondo, colore) = cella.ModalitaStato ? ColoriStato(cella.CodiceStato) : ("", "");
        if (testo) return colore.Length == 0 ? AvaloniaProperty.UnsetValue : new SolidColorBrush(Color.Parse(colore));
        return sfondo.Length == 0 ? Brushes.Transparent : new SolidColorBrush(Color.Parse(sfondo));
    }

    /// <summary>Sfondo e testo per stato: stessi colori della legenda della vista (Report, sezione Stato).</summary>
    private static (string Sfondo, string Testo) ColoriStato(StatoCellaMatrice stato) => stato switch
    {
        StatoCellaMatrice.Conforme => ("#DCFCE7", "#166534"),
        StatoCellaMatrice.NonConforme => ("#FEE2E2", "#991B1B"),
        StatoCellaMatrice.ConformeConDeviazioni => ("#FEF3C7", "#92400E"),
        StatoCellaMatrice.DaEseguire => ("#DBEAFE", "#1E40AF"),
        StatoCellaMatrice.NonTestato => ("#F1F5F9", "#475569"),
        StatoCellaMatrice.Misto => ("#EDE9FE", "#5B21B6"),
        StatoCellaMatrice.DaDecidere => ("#FFF7ED", "#9A3412"),
        _ => ("", ""),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Simbolo del pulsante di un gruppo di intestazione: gruppo compresso (▸, espandi) oppure espanso (▾, comprimi).</summary>
public class GruppoCompressoSimboloConverter : IValueConverter
{
    public static readonly GruppoCompressoSimboloConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "▸" : "▾";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Elenco di veicoli -&gt; testo di colonna: il primo in ordine alfabetico e, se ce ne sono altri, "+N". Stessa regola di
/// <see cref="ChangeRequestOrdinamentoComparer.VeicoliOrdinati"/>, che e' anche la chiave di ordinamento della colonna.</summary>
public class VeicoliSommarioConverter : IValueConverter
{
    public static readonly VeicoliSommarioConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<string> veicoli) return "";
        var ordinati = VeicoliOrdinati(veicoli);
        return ordinati.Count == 0 ? "" : ordinati.Count == 1 ? ordinati[0] : $"{ordinati[0]} +{ordinati.Count - 1}";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    internal static List<string> VeicoliOrdinati(IEnumerable<string> veicoli) =>
        veicoli.Where(v => !string.IsNullOrWhiteSpace(v)).OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase).ToList();
}

/// <summary>Elenco di veicoli -&gt; tooltip con un veicolo per riga (vuoto se non ce ne sono).</summary>
public class VeicoliElencoConverter : IValueConverter
{
    public static readonly VeicoliElencoConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<string> veicoli ? string.Join(Environment.NewLine, VeicoliSommarioConverter.VeicoliOrdinati(veicoli)) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
