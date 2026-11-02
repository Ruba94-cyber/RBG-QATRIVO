using ClosedXML.Excel;

namespace TestLoom.App.Services;

/// <summary>
/// Scrive una tabella Excel senza perdere stringhe oltre il limite di cella e senza superare il limite
/// di righe di un foglio. I valori lunghi vengono suddivisi in fogli di dettaglio referenziati da ID e campo.
/// </summary>
public sealed class ExcelExportTableWriter
{
    public const int LimiteRigheFoglio = 1_048_576;
    public const int LimiteCaratteriCella = 32_767;
    private const int DimensioneBloccoTesto = 30_000;
    private const int LimiteInterruzioniRigaCella = 253;

    private readonly XLWorkbook _workbook;
    private readonly string _nomeBase;
    private readonly IReadOnlyList<string> _intestazioni;
    private readonly int _limiteRighe;
    private readonly List<IXLWorksheet> _fogli = [];
    private readonly List<IXLWorksheet> _fogliDettagli = [];
    private int _rigaCorrente;
    private long _progressivo;

    public ExcelExportTableWriter(XLWorkbook workbook, string nomeFoglio, IReadOnlyList<string> intestazioni,
        int limiteRighe = LimiteRigheFoglio)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        ArgumentException.ThrowIfNullOrWhiteSpace(nomeFoglio);
        ArgumentNullException.ThrowIfNull(intestazioni);
        if (intestazioni.Count == 0) throw new ArgumentException("La tabella deve avere almeno una colonna.", nameof(intestazioni));
        if (limiteRighe < 2 || limiteRighe > LimiteRigheFoglio)
            throw new ArgumentOutOfRangeException(nameof(limiteRighe));

        _workbook = workbook;
        _nomeBase = nomeFoglio;
        _intestazioni = intestazioni.ToList();
        _limiteRighe = limiteRighe;
        _rigaCorrente = 2;
        _fogli.Add(CreaFoglioPrincipale(1));
    }

    /// <summary>Nome del primo foglio della tabella (quello con le intestazioni in riga 1, usato dalle formule dei riepiloghi).</summary>
    public string NomeFoglioPrincipale => _fogli[0].Name;

    /// <summary>Vero se la tabella ha superato il limite di righe di un foglio e continua su altri fogli.</summary>
    public bool SuPiuFogli => _fogli.Count > 1;

    /// <summary>Righe di dati scritte finora, intestazione esclusa.</summary>
    public long RigheScritte => _progressivo;

    public void AggiungiRiga(string riferimento, IReadOnlyList<object?> valori)
    {
        ArgumentNullException.ThrowIfNull(valori);
        if (valori.Count != _intestazioni.Count)
            throw new ArgumentException($"Attese {_intestazioni.Count} celle, ricevute {valori.Count}.", nameof(valori));

        if (_rigaCorrente > _limiteRighe)
        {
            _rigaCorrente = 2;
            _fogli.Add(CreaFoglioPrincipale(_fogli.Count + 1));
        }

        _progressivo++;
        var riga = _rigaCorrente++;
        var riferimentoRiga = string.IsNullOrWhiteSpace(riferimento) ? $"Riga {_progressivo}" : riferimento;
        var foglio = _fogli[^1];
        for (var i = 0; i < valori.Count; i++)
        {
            var cella = foglio.Cell(riga, i + 1);
            switch (valori[i])
            {
                case null:
                    break;
                case string testo when RichiedeFoglioDettagli(testo):
                    var campo = _intestazioni[i];
                    cella.Value = AggiungiDettaglio(riferimentoRiga, campo, testo);
                    break;
                case string testo:
                    cella.Value = testo;
                    break;
                case DateTime data:
                    cella.Value = data;
                    cella.Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
                    break;
                case DateTimeOffset data:
                    cella.Value = data.DateTime;
                    cella.Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
                    break;
                case int numero:
                    cella.Value = numero;
                    break;
                case long numero:
                    cella.Value = numero;
                    break;
                case decimal numero:
                    cella.Value = numero;
                    break;
                case double numero:
                    cella.Value = numero;
                    break;
                case bool valore:
                    cella.Value = valore ? "Sì" : "No";
                    break;
                case Guid valore:
                    cella.Value = valore.ToString();
                    break;
                case Enum valore:
                    cella.Value = valore.ToString();
                    break;
                default:
                    cella.Value = Convert.ToString(valori[i], System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    break;
            }
        }
    }

    public void Completa()
    {
        foreach (var foglio in _fogli.Concat(_fogliDettagli))
        {
            var ultimaRiga = foglio.LastRowUsed()?.RowNumber() ?? 1;
            if (ultimaRiga > 1) foglio.Range(1, 1, ultimaRiga, foglio.LastColumnUsed()?.ColumnNumber() ?? 1).SetAutoFilter();
            foglio.SheetView.FreezeRows(1);
        }

        foreach (var foglio in _fogli)
        {
            foglio.Column(1).Width = 22;
            for (var colonna = 2; colonna <= _intestazioni.Count; colonna++) foglio.Column(colonna).Width = 24;
        }
        foreach (var foglio in _fogliDettagli)
        {
            foglio.Column(1).Width = 26;
            foglio.Column(2).Width = 32;
            foglio.Column(3).Width = 12;
            foglio.Column(4).Width = 100;
        }
    }

    private string AggiungiDettaglio(string riferimento, string campo, string valore)
    {
        var blocchi = new List<string>();
        for (var offset = 0; offset < valore.Length;)
        {
            var fine = Math.Min(offset + DimensioneBloccoTesto, valore.Length);
            var interruzioni = 0;
            for (var indice = offset; indice < fine; indice++)
            {
                if (valore[indice] != '\n') continue;
                interruzioni++;
                if (interruzioni > LimiteInterruzioniRigaCella)
                {
                    fine = indice;
                    break;
                }
            }
            if (fine == offset) fine = Math.Min(offset + DimensioneBloccoTesto, valore.Length);
            if (fine < valore.Length && fine > offset && char.IsHighSurrogate(valore[fine - 1])) fine--;
            var lunghezza = fine - offset;
            // Evita di spezzare una coppia surrogate ai confini dei blocchi.
            blocchi.Add(valore.Substring(offset, lunghezza));
            offset += lunghezza;
        }

        var nomiFogli = new List<string>();
        for (var blocco = 0; blocco < blocchi.Count; blocco++)
        {
            var foglio = OttieniFoglioDettagli();
            if (nomiFogli.Count == 0 || nomiFogli[^1] != foglio.Name) nomiFogli.Add(foglio.Name);
            var riga = _rigaDettaglioCorrente++;
            foglio.Cell(riga, 1).SetValue(riferimento);
            foglio.Cell(riga, 2).SetValue(campo);
            foglio.Cell(riga, 3).SetValue($"{blocco + 1}/{blocchi.Count}");
            foglio.Cell(riga, 4).SetValue(blocchi[blocco]);
        }

        var percorso = nomiFogli.Count == 1 ? $"nel foglio '{nomiFogli[0]}'" :
            $"nei fogli di dettaglio da '{nomiFogli[0]}' a '{nomiFogli[^1]}'";
        return $"Testo esteso: cercare ID '{riferimento}' e campo '{campo}' {percorso}, ordinando per Parte.";
    }

    private static bool RichiedeFoglioDettagli(string testo) =>
        testo.Length > LimiteCaratteriCella || testo.Count(carattere => carattere == '\n') > LimiteInterruzioniRigaCella;

    private int _rigaDettaglioCorrente;

    private IXLWorksheet OttieniFoglioDettagli()
    {
        if (_fogliDettagli.Count == 0 || _rigaDettaglioCorrente > _limiteRighe)
        {
            _rigaDettaglioCorrente = 2;
            _fogliDettagli.Add(CreaFoglioDettagli(_fogliDettagli.Count + 1));
        }
        return _fogliDettagli[^1];
    }

    private string AggiungiFoglio(string nomeDesiderato)
    {
        var nome = nomeDesiderato.Length <= 31 ? nomeDesiderato : nomeDesiderato[..31];
        var baseNome = nome;
        var suffisso = 2;
        while (_workbook.Worksheets.Any(x => string.Equals(x.Name, nome, StringComparison.OrdinalIgnoreCase)))
        {
            var finale = $" ({suffisso++})";
            nome = baseNome[..Math.Min(baseNome.Length, 31 - finale.Length)] + finale;
        }
        return nome;
    }

    private IXLWorksheet CreaFoglioPrincipale(int indice)
    {
        var nome = indice == 1 ? _nomeBase : $"{_nomeBase} ({indice})";
        var foglio = _workbook.Worksheets.Add(AggiungiFoglio(nome));
        ScriviIntestazioni(foglio, _intestazioni);
        return foglio;
    }

    private IXLWorksheet CreaFoglioDettagli(int indice)
    {
        var nome = NomeFamigliaDettagli(indice);
        var foglio = _workbook.Worksheets.Add(AggiungiFoglio(nome));
        ScriviIntestazioni(foglio, ["ID riferimento", "Campo", "Parte", "Testo completo"]);
        return foglio;
    }

    private string NomeFamigliaDettagli(int indice = 1)
    {
        var nome = $"{_nomeBase} dettagli estesi";
        return indice == 1 ? nome : $"{nome} ({indice})";
    }

    private string NomeFoglioDettagliEffettivo() =>
        _fogliDettagli.Count > 0 ? _fogliDettagli[0].Name : AggiungiFoglio(NomeFamigliaDettagli());

    private static void ScriviIntestazioni(IXLWorksheet foglio, IReadOnlyList<string> intestazioni)
    {
        for (var colonna = 0; colonna < intestazioni.Count; colonna++) foglio.Cell(1, colonna + 1).Value = intestazioni[colonna];
        foglio.Range(1, 1, 1, intestazioni.Count).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.LightBlue);
    }
}
