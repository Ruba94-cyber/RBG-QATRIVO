using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Overlay "N.A." applicato SOPRA il risultato già calcolato da ReportMatriceService (decisione utente piano
/// 6.4.0, punto 4: marcatura manuale per cella della matrice, esclusa dalle percentuali, salvata con il report
/// ed esportata in Excel). Questo servizio è intenzionalmente in un file separato e non conosce COME i
/// conteggi (Conformi/Valutati/Totali) vengono prodotti: lavora solo sul risultato finale
/// (RisultatoMatriceReport/CellaMatriceReport). Un altro sviluppatore sta cambiando in parallelo la semantica
/// di "operazione eseguita" dentro ReportMatriceService — questa separazione minimizza la superficie di
/// conflitto e garantisce che l'overlay N.A. resti corretto qualunque sia quella semantica.
///
/// Persistenza: locale in AppConfig.MatriceNonApplicabili (stesso config.json/stessa persistenza di
/// CronologiaReport), chiave = combinazione di assi (Chiave). La cella dentro ogni combinazione è identificata
/// dai VALORI reali degli assi, non da un indice di posizione: sopravvive a riordini/filtri, e se un valore
/// d'asse sparisce la marcatura resta dormiente senza errori (Trova() semplicemente non la trova più, finché
/// il valore non ricompare).
/// </summary>
public static class MatriceNonApplicabileService
{
    /// <summary>Separatore fra AsseX e AsseY nella chiave persistita. Non "|": le dimensioni custom
    /// ("custom:" + chiave campo scelta liberamente dal Master, vedi DimensioniMatrice.CampoCustomPrefix)
    /// potrebbero in teoria contenere quel carattere, il che aprirebbe una collisione fra combinazioni di assi
    /// diverse (es. AsseX="a|b", AsseY="c" produrrebbe la stessa chiave di AsseX="a", AsseY="b|c"). "\u001f"
    /// (Unit Separator ASCII) non è digitabile da un campo di testo del Master: stesso principio già usato da
    /// UdtLogImportService.ChiaveConfrontoWorkingUnit per lo stesso motivo.</summary>
    private const char SeparatoreAssi = '\u001f';

    /// <summary>Chiave persistente per la combinazione di assi: include ESPLICITAMENTE quali due dimensioni
    /// sono su AsseX/AsseY (non solo i valori delle celle), quindi una N.A. marcata su una combinazione di assi
    /// (es. Componente×CategoriaFunzionalita) non può mai riapparire su una combinazione diversa — dimensioni diverse
    /// (es. Veicolo×Funzionalita) o le stesse due dimensioni scambiate (Componente×CategoriaFunzionalita vs
    /// CategoriaFunzionalita×Componente) producono chiavi diverse per costruzione, non solo "di solito".
    ///
    /// Non legata a un "report salvato con nome": TestLoom non ha oggi un concetto di report nominato/persistito
    /// oltre a AppConfig.CronologiaReport, che è solo lo storico dei percorsi dei file già esportati (un nuovo
    /// export con "Salva con nome" crea sempre un file nuovo con timestamp, mai una sovrascrittura — vedi
    /// ReportCronologiaService). Legare la marcatura a una voce di quello storico la farebbe sparire ad ogni
    /// nuovo export sulla stessa combinazione di assi, il contrario di quanto chiesto ("N.A. salvata insieme al
    /// report" nel senso di "il report che sto configurando", non di un singolo file già scritto). La
    /// combinazione di assi è quindi l'identità più stabile disponibile oggi per "questo report".</summary>
    public static string Chiave(string asseX, string asseY) => $"{asseX}{SeparatoreAssi}{asseY}";

    /// <summary>Separatore fra i campi di UN asse (6.6.1, campi annidati, fino a tre per asse). Con un solo campo per asse la
    /// chiave è identica a quella dei report senza annidamento: le marcature esistenti restano valide e leggibili.</summary>
    private const char SeparatoreCampi = ReportMatriceService.SepLivello;

    /// <summary>Chiave persistente per tutti i campi dei due assi (6.6.1). Asse senza campi = segmento vuoto.</summary>
    public static string ChiaveAssi(IReadOnlyList<string> campiX, IReadOnlyList<string> campiY) =>
        $"{string.Join(SeparatoreCampi, campiX)}{SeparatoreAssi}{string.Join(SeparatoreCampi, campiY)}";

    public static IReadOnlyList<CellaNonApplicabile> Leggi(AppConfig config, string asseX, string asseY) =>
        Leggi(config, new[] { asseX }, new[] { asseY });

    /// <summary>Marcature della combinazione di campi degli assi (un campo per asse, o più campi annidati).</summary>
    public static IReadOnlyList<CellaNonApplicabile> Leggi(AppConfig config, IReadOnlyList<string> campiX, IReadOnlyList<string> campiY) =>
        config.MatriceNonApplicabili.TryGetValue(ChiaveAssi(campiX, campiY), out var lista) ? lista : Array.Empty<CellaNonApplicabile>();

    private static CellaNonApplicabile? Trova(IEnumerable<CellaNonApplicabile> lista, string valoreX, string valoreY) =>
        lista.FirstOrDefault(c => string.Equals(c.ValoreAsseX, valoreX, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.ValoreAsseY, valoreY, StringComparison.OrdinalIgnoreCase));

    public static bool EImpostata(AppConfig config, string asseX, string asseY, string valoreX, string valoreY) =>
        Trova(Leggi(config, asseX, asseY), valoreX, valoreY) != null;

    public static bool EImpostata(AppConfig config, IReadOnlyList<string> campiX, IReadOnlyList<string> campiY, string valoreX, string valoreY) =>
        Trova(Leggi(config, campiX, campiY), valoreX, valoreY) != null;

    /// <summary>Imposta (o aggiorna la nota di) la marcatura N.A. per una cella. Ritorna false — nessuna
    /// scrittura necessaria — se la marcatura esisteva già con la stessa nota, stesso principio di
    /// ViewStateService.Scrivi (evita un salvataggio ridondante di config.json).</summary>
    public static bool Imposta(AppConfig config, string asseX, string asseY, string valoreX, string valoreY, string? nota) =>
        Imposta(config, new[] { asseX }, new[] { asseY }, valoreX, valoreY, nota);

    public static bool Imposta(AppConfig config, IReadOnlyList<string> campiX, IReadOnlyList<string> campiY, string valoreX, string valoreY, string? nota)
    {
        var chiave = ChiaveAssi(campiX, campiY);
        if (!config.MatriceNonApplicabili.TryGetValue(chiave, out var lista))
        {
            lista = new List<CellaNonApplicabile>();
            config.MatriceNonApplicabili[chiave] = lista;
        }
        var esistente = Trova(lista, valoreX, valoreY);
        if (esistente != null)
        {
            if (esistente.Nota == nota) return false;
            esistente.Nota = nota;
            return true;
        }
        lista.Add(new CellaNonApplicabile { ValoreAsseX = valoreX, ValoreAsseY = valoreY, Nota = nota });
        return true;
    }

    /// <summary>Rimuove la marcatura N.A. di una cella. Ritorna false se non era marcata (nessuna scrittura
    /// necessaria).</summary>
    public static bool Rimuovi(AppConfig config, string asseX, string asseY, string valoreX, string valoreY) =>
        Rimuovi(config, new[] { asseX }, new[] { asseY }, valoreX, valoreY);

    public static bool Rimuovi(AppConfig config, IReadOnlyList<string> campiX, IReadOnlyList<string> campiY, string valoreX, string valoreY)
    {
        var chiave = ChiaveAssi(campiX, campiY);
        if (!config.MatriceNonApplicabili.TryGetValue(chiave, out var lista)) return false;
        var rimossi = lista.RemoveAll(c => string.Equals(c.ValoreAsseX, valoreX, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.ValoreAsseY, valoreY, StringComparison.OrdinalIgnoreCase));
        if (rimossi == 0) return false;
        if (lista.Count == 0) config.MatriceNonApplicabili.Remove(chiave);
        return true;
    }

    /// <summary>Applica l'overlay a un risultato già calcolato: marca NonApplicabile/NotaNonApplicabile sulle
    /// celle corrispondenti (per valore riga/colonna, non per indice). Le celle marcate restano nel risultato,
    /// solo escluse dal calcolo di Percentuale — CellaMatriceReport.Percentuale/Testo è l'unico punto dove le
    /// percentuali sono lette da vista, Excel e PDF, quindi marcarle lì basta a escluderle ovunque.</summary>
    public static void Applica(RisultatoMatriceReport risultato, IReadOnlyList<CellaNonApplicabile> marcature)
    {
        if (marcature.Count == 0) return;
        foreach (var riga in risultato.Righe)
        {
            foreach (var cella in riga.Celle)
            {
                if (!cella.Marcabile) continue; // gruppo compresso o totale: nessuna marcatura N.A.
                var trovata = Trova(marcature, cella.Colonna, riga.Riga);
                if (trovata == null) continue;
                cella.NonApplicabile = true;
                cella.NotaNonApplicabile = trovata.Nota;
            }
        }
    }

    public static void Applica(AppConfig config, string asseX, string asseY, RisultatoMatriceReport risultato) =>
        Applica(risultato, Leggi(config, asseX, asseY));

    public static void Applica(AppConfig config, IReadOnlyList<string> campiX, IReadOnlyList<string> campiY, RisultatoMatriceReport risultato) =>
        Applica(risultato, Leggi(config, campiX, campiY));
}
