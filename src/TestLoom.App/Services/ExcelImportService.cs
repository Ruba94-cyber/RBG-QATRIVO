using System.Text;
using ClosedXML.Excel;
using ExcelDataReader;

namespace TestLoom.App.Services;

/// <summary>Parametri scelti dall'utente prima di un import Excel (feedback utente 2026-08-25, punto 5): foglio
/// e riga di intestazione non sono più assunti automaticamente (un banner/titolo unito sulla riga 1, o dati su
/// un foglio diverso dal primo, facevano perdere colonne in silenzio), l'utente li conferma nel dialog
/// ImportExcelOpzioniWindow prima di procedere.</summary>
public record ImportExcelRichiesta(string PercorsoFile, string? Foglio, int RigaIntestazione);

/// <summary>Eccezione dedicata per un file scelto dall'utente che non è un Excel leggibile (feedback utente
/// 2026-09-16, caso reale: un .xls storico esportato con estensione ".xlsx" faceva fallire ClosedXML con
/// un'eccezione tecnica che i chiamanti (OnImportaExcel nelle View) scrivevano SOLO nel crash.log, mai a
/// video — sembrava che l'import "non avesse fatto niente"). Ogni chiamante intercetta questa eccezione e
/// mostra .Message direttamente all'operatore, un messaggio comprensibile invece della StackTrace tecnica di
/// ClosedXML/ExcelDataReader.</summary>
public sealed class FormatoExcelNonValidoException(string messaggio) : Exception(messaggio);

/// <summary>Lettura di un file Excel scelto dall'utente per il primo import massivo dei master data (richiesta
/// utente: l'app parte vuota, un import da Excel — una riga per dato — velocizza il primo popolamento invece
/// di dover ricreare a mano decine di componente/veicoli/requisiti). Riconosce il formato REALE dal contenuto del
/// file (firma binaria), non dall'estensione scelta dall'utente o dal filtro del file picker (che filtra solo
/// per nome): un file può chiamarsi ".xlsx" ed essere in realtà un vecchio binario Excel 97-2003 esportato da
/// un altro strumento (feedback utente 2026-09-16, caso reale: "Funzionalita_Master_Deploy_UnitLogger.xlsx" ha firma
/// OLE2/CFBF, non ZIP — ClosedXML, che legge solo il formato Open XML moderno, falliva sempre su quel file,
/// indipendentemente dal numero di righe: il "funziona solo sotto 100 righe" segnalato era quasi certamente
/// una coincidenza su quali file erano vecchio/nuovo formato, non un limite di righe — che infatti non esiste
/// da nessuna parte in questo file né nei chiamanti). Il formato moderno resta letto con ClosedXML (già
/// dipendenza del progetto anche per l'export, vedi ReportService); il vecchio binario .xls è letto con
/// ExcelDataReader, aggiunta minima solo per questo fallback.</summary>
public static class ExcelImportService
{
    private static int _codePagesRegistrate;

    /// <summary>ExcelDataReader (usato solo per il vecchio formato binario .xls) si appoggia ai code page
    /// storici per le stringhe non-Unicode: senza questa registrazione lancia "NotSupportedException: No data
    /// is available for encoding 1252" su qualunque .xls reale prodotto da Excel. Interlocked invece di un
    /// semplice bool: idempotente ed è sicuro chiamarla da più punti (servizio, eventuali test) senza rischiare
    /// la doppia registrazione, che Encoding.RegisterProvider non tollera (lancia se richiamata due volte).</summary>
    private static void AssicuraCodePages()
    {
        if (Interlocked.Exchange(ref _codePagesRegistrate, 1) != 0) return;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private enum Formato { Xlsx, XlsLegacy, Ignoto }

    /// <summary>Firma ZIP (PK\x03\x04, primi 4 byte) per ogni Open XML moderno (.xlsx/.xlsm); firma OLE2/CFBF
    /// (D0 CF 11 E0 A1 B1 1A E1, primi 8 byte) per il vecchio binario Excel 97-2003 — la stessa usata anche da
    /// .doc/.ppt storici e da un .xlsx protetto da password (che è un pacchetto OOXML cifrato dentro un
    /// contenitore OLE2, non uno zip in chiaro: anche quel caso finisce quindi nel fallback "vecchio formato",
    /// dove il tentativo di lettura fallirà comunque con un messaggio che suggerisce di controllare la
    /// password — non c'è modo di distinguerlo da un vero .xls senza provare a decifrarlo).</summary>
    private static Formato RilevaFormato(Stream file)
    {
        var firma = new byte[8];
        var letti = file.Read(firma, 0, firma.Length);
        file.Position = 0;
        if (letti >= 4 && firma[0] == 0x50 && firma[1] == 0x4B && firma[2] == 0x03 && firma[3] == 0x04) return Formato.Xlsx;
        if (letti >= 8 && firma[0] == 0xD0 && firma[1] == 0xCF && firma[2] == 0x11 && firma[3] == 0xE0
                       && firma[4] == 0xA1 && firma[5] == 0xB1 && firma[6] == 0x1A && firma[7] == 0xE1) return Formato.XlsLegacy;
        return Formato.Ignoto;
    }

    private static FormatoExcelNonValidoException ErroreFormatoIgnoto() => new(
        "Questo file non è un .xlsx/.xls Excel leggibile (potrebbe essere corrotto, protetto da password, o " +
        "non essere davvero un file Excel nonostante il nome/estensione). Riaprilo in Excel e salvalo di nuovo " +
        "come .xlsx, poi riprova.");

    private static FormatoExcelNonValidoException ErroreLettura(Exception ex) => new(
        $"Impossibile leggere il file Excel: {ex.Message} Verifica che non sia protetto da password e che non " +
        "sia danneggiato, poi riprova.");

    /// <summary>Nomi dei fogli del workbook, nell'ordine in cui compaiono — popola il selettore foglio del
    /// dialog di import.</summary>
    public static List<string> ElencaFogli(Stream file)
    {
        switch (RilevaFormato(file))
        {
            case Formato.Xlsx:
                try { using var wb = new XLWorkbook(file); return wb.Worksheets.Select(w => w.Name).ToList(); }
                catch (Exception ex) { throw ErroreLettura(ex); }
            case Formato.XlsLegacy:
                AssicuraCodePages();
                try
                {
                    using var reader = ExcelReaderFactory.CreateBinaryReader(file);
                    var fogli = new List<string>();
                    do { fogli.Add(reader.Name); } while (reader.NextResult());
                    return fogli;
                }
                catch (Exception ex) { throw ErroreLettura(ex); }
            default:
                throw ErroreFormatoIgnoto();
        }
    }

    /// <summary>Lettura posizionale (feedback utente 2026-08-20, punto 8): colonna A dell'Excel = primo
    /// elemento di ogni riga, B = secondo, ecc. — non più per intestazione. La vecchia lettura per intestazione
    /// richiedeva che il testo in cima alla colonna corrispondesse esattamente al nome della proprietà C#
    /// interna (es. "GammaNome", non "Gamma" come mostrato a schermo in Veicoli): una colonna non riconosciuta
    /// veniva ignorata in silenzio, causa più probabile del "non funziona" segnalato allora. Foglio e riga di
    /// intestazione (feedback utente 2026-08-25, punto 5) sono ora scelti esplicitamente dall'utente invece che
    /// dedotti da "primo foglio del workbook" / "prima riga non vuota": un banner/titolo unito su più colonne
    /// prima della vera intestazione faceva calcolare un numero di colonne sbagliato (solo la cella-àncora del
    /// merge risulta "usata"), e un workbook con più fogli (es. versione bilingue) leggeva sempre il primo anche
    /// quando i dati voluti stavano su un altro. Righe completamente vuote saltate. I dati oltre l'ultima colonna
    /// dell'intestazione non vengono letti: <paramref name="avvisi"/>, se valorizzato, riceve un avviso per quelle colonne.</summary>
    public static List<List<string>> LeggiRigheOrdinate(Stream file, string? nomeFoglio, int rigaIntestazione, List<string>? avvisi = null)
    {
        return RilevaFormato(file) switch
        {
            Formato.Xlsx => LeggiConClosedXml(file, nomeFoglio, rigaIntestazione, avvisi),
            Formato.XlsLegacy => LeggiConExcelDataReader(file, nomeFoglio, rigaIntestazione, avvisi),
            _ => throw ErroreFormatoIgnoto(),
        };
    }

    private static List<List<string>> LeggiConClosedXml(Stream file, string? nomeFoglio, int rigaIntestazione, List<string>? avvisi)
    {
        var righe = new List<List<string>>();
        XLWorkbook wb;
        try { wb = new XLWorkbook(file); }
        catch (Exception ex) { throw ErroreLettura(ex); }
        using (wb)
        {
            var ws = (string.IsNullOrWhiteSpace(nomeFoglio) ? null : wb.Worksheets.FirstOrDefault(w => w.Name == nomeFoglio))
                     ?? wb.Worksheets.FirstOrDefault();
            if (ws == null || rigaIntestazione < 1) return righe;

            var headerRow = ws.Row(rigaIntestazione);
            var ultimaColonna = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 0;
            if (ultimaColonna == 0) return righe;

            var senzaIntestazione = new SortedSet<int>();
            foreach (var row in ws.RowsUsed().Where(r => r.RowNumber() > rigaIntestazione))
            {
                var valori = Enumerable.Range(1, ultimaColonna).Select(i => row.Cell(i).GetString().Trim()).ToList();
                if (valori.Any(v => !string.IsNullOrWhiteSpace(v))) righe.Add(valori);
                foreach (var cella in row.CellsUsed().Where(c => c.Address.ColumnNumber > ultimaColonna && !string.IsNullOrWhiteSpace(c.GetString())))
                    senzaIntestazione.Add(cella.Address.ColumnNumber);
            }
            AggiungiAvvisoColonne(avvisi, senzaIntestazione);
            return righe;
        }
    }

    /// <summary>Stessa logica posizionale di LeggiConClosedXml, sul vecchio formato binario .xls via
    /// ExcelDataReader (forward-only: due passate sullo stream — una per i nomi dei fogli, la seconda per i
    /// dati — invece di una, perché IExcelDataReader non permette di tornare indietro a un foglio già
    /// superato). Stessa regola di fallback di ClosedXML: se il foglio richiesto non esiste (più) nel file, usa
    /// il primo invece di restituire una lista vuota per un nome ormai sbagliato.</summary>
    private static List<List<string>> LeggiConExcelDataReader(Stream file, string? nomeFoglio, int rigaIntestazione, List<string>? avvisi)
    {
        var righe = new List<List<string>>();
        if (rigaIntestazione < 1) return righe;
        AssicuraCodePages();

        // Bufferizzato in memoria invece di riusare "file" per le due passate: IExcelDataReader chiude lo
        // stream sottostante al proprio Dispose (a differenza di ClosedXML), quindi una seconda passata sullo
        // stesso stream lancerebbe ObjectDisposedException. I file .xls qui sono cataloghi master data, non
        // voluminosi: il costo di tenerli in memoria una volta è trascurabile.
        byte[] contenuto;
        using (var buffer = new MemoryStream()) { file.CopyTo(buffer); contenuto = buffer.ToArray(); }

        List<string> nomiFogli;
        try
        {
            using var msNomi = new MemoryStream(contenuto);
            using var readerNomi = ExcelReaderFactory.CreateBinaryReader(msNomi);
            nomiFogli = new List<string>();
            do { nomiFogli.Add(readerNomi.Name); } while (readerNomi.NextResult());
        }
        catch (Exception ex) { throw ErroreLettura(ex); }

        var nomeTarget = (string.IsNullOrWhiteSpace(nomeFoglio) ? null : nomiFogli.FirstOrDefault(n => n == nomeFoglio))
                         ?? nomiFogli.FirstOrDefault();
        if (nomeTarget == null) return righe;

        try
        {
            using var msDati = new MemoryStream(contenuto);
            using var reader = ExcelReaderFactory.CreateBinaryReader(msDati);
            do
            {
                if (reader.Name != nomeTarget) continue;
                var numeroRiga = 0;
                var ultimaColonna = -1;
                var senzaIntestazione = new SortedSet<int>();
                while (reader.Read())
                {
                    numeroRiga++;
                    if (numeroRiga == rigaIntestazione) { ultimaColonna = UltimaColonnaUsata(reader); continue; }
                    if (numeroRiga <= rigaIntestazione || ultimaColonna < 0) continue;
                    var valori = Enumerable.Range(0, ultimaColonna + 1).Select(i => (reader.GetValue(i)?.ToString() ?? "").Trim()).ToList();
                    if (valori.Any(v => !string.IsNullOrWhiteSpace(v))) righe.Add(valori);
                    for (var i = ultimaColonna + 1; i < reader.FieldCount; i++)
                        if (!string.IsNullOrWhiteSpace(reader.GetValue(i)?.ToString())) senzaIntestazione.Add(i + 1);
                }
                AggiungiAvvisoColonne(avvisi, senzaIntestazione);
                break;
            } while (reader.NextResult());
        }
        catch (Exception ex) { throw ErroreLettura(ex); }
        return righe;
    }

    /// <summary>Avviso per i dati in colonne senza intestazione: la lettura e' posizionale sull'ultima colonna della riga di
    /// intestazione, quindi quelle colonne non entrano nell'import e l'operatore deve saperlo. Numeri di colonna 1-based.</summary>
    private static void AggiungiAvvisoColonne(List<string>? avvisi, IEnumerable<int> numeriColonna)
    {
        if (avvisi == null) return;
        var lettere = numeriColonna.Select(LetteraColonna).ToList();
        if (lettere.Count > 0)
            avvisi.Add(string.Format(LocalizationService.Instance.Traduci("ImportColonneSenzaIntestazioneFmt"), string.Join(", ", lettere)));
    }

    /// <summary>Lettera di colonna Excel da numero 1-based (1 = A, 27 = AA).</summary>
    private static string LetteraColonna(int numero)
    {
        var lettera = "";
        for (var n = numero; n > 0; n = (n - 1) / 26) lettera = (char)('A' + (n - 1) % 26) + lettera;
        return lettera;
    }

    private static int UltimaColonnaUsata(IExcelDataReader reader)
    {
        for (var i = reader.FieldCount - 1; i >= 0; i--)
            if (!string.IsNullOrWhiteSpace(reader.GetValue(i)?.ToString())) return i;
        return -1;
    }
}
