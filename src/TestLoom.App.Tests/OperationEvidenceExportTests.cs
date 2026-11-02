using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Reflection;
using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Le evidenze collegate a una operazione compaiono in testo ticket, pacchetto Ticketing (Test e NC di origine) e foglio Report,
/// senza copiare file e senza lasciare riferimenti a evidenze non piu' presenti.</summary>
public sealed class OperationEvidenceExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_EvExport_" + Guid.NewGuid().ToString("N"));
    private readonly AppState _state;
    private readonly TestCase _test;
    private readonly Condizione _opConEvidenza;
    private readonly Condizione _opSenza;
    private readonly Allegato _evidenza;
    private readonly Allegato _generale;

    public OperationEvidenceExportTests()
    {
        Directory.CreateDirectory(_dir);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_dir);
        _opConEvidenza = new Condizione { Funzionalita = "Lettura DID", Esito = Esito.NonConforme };
        _opSenza = new Condizione { Funzionalita = "Reset", Esito = Esito.Conforme };
        _test = new TestCase
        {
            Id = "T-EVX-1", Componente = new EcuSelection(new[] { "BCM" }), DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now,
            Esecuzioni = new List<Esecuzione>
            {
                new() { Iterazione = 1, Data = DateTime.Now, Operatore = "Op", Condizioni = new List<Condizione> { _opConEvidenza, _opSenza } }
            }
        };
        var sorgente = Path.Combine(_dir, "shot.png");
        File.WriteAllBytes(sorgente, [0x89, 0x50, 0x4E, 0x47, 9, 9, 9]);
        var generale = Path.Combine(_dir, "generale.txt");
        File.WriteAllText(generale, "log generale");
        var paths = _state.Paths;
        _evidenza = AttachmentService.Salva(paths, _test, sorgente, "Op");
        _test.Allegati.Add(_evidenza);
        _generale = AttachmentService.Salva(paths, _test, generale, "Op");
        _test.Allegati.Add(_generale);
        EvidenzaOperazione.Aggiungi(_opConEvidenza.ValoriStrumenti, _evidenza.Uid);
        _state.TestCases.Save(_test, isNew: true);
    }

    public void Dispose()
    {
        _state.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void HelperLabelsOnlyLinkedFilesAndIgnoresArchivedOrMissing()
    {
        var etichette = EvidenzaOperazione.EtichettePerAllegato(_test);
        Assert.Equal("REV 1 · Lettura DID", etichette[_evidenza.Uid]);
        Assert.False(etichette.ContainsKey(_generale.Uid));

        _evidenza.Archiviata = true;
        Assert.Empty(EvidenzaOperazione.Evidenze(_test, _opConEvidenza));
        Assert.Empty(EvidenzaOperazione.EtichettePerAllegato(_test));
    }

    [Fact]
    public void TestTicketTextListsEvidencePerOperationAndLabelsAttachments()
    {
        var testo = TicketExportService.GeneraTesto(_test, _state, new HashSet<string>(PolarionSezioni.Tutte));
        Assert.Contains("Evidenze: shot.png", testo);
        Assert.Contains("evidenza di: REV 1 · Lettura DID", testo);
        Assert.DoesNotContain("EVIDENZE::", testo);
        Assert.DoesNotContain("TEMPO::", testo);
        // The general attachment is not attributed to any operation.
        var rigaGenerale = testo.Split('\n').First(l => l.Contains("generale.txt"));
        Assert.DoesNotContain("evidenza di", rigaGenerale);
    }

    [Fact]
    public void PolarionPackageManifestCarriesTheOperationOfEachEvidence()
    {
        var cartella = Path.Combine(_dir, "out");
        var zipPath = PolarionPackageService.Crea(_state.Paths, _test, _state, cartella, new HashSet<string>(PolarionSezioni.Tutte));
        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open(), Encoding.UTF8);
        using var doc = JsonDocument.Parse(reader.ReadToEnd());
        var files = doc.RootElement.GetProperty("attachments").EnumerateArray().ToList();
        Assert.Equal("REV 1 · Lettura DID", files.Single(f => f.GetProperty("NomeFile").GetString() == "shot.png").GetProperty("operation").GetString());
        Assert.Equal("", files.Single(f => f.GetProperty("NomeFile").GetString() == "generale.txt").GetProperty("operation").GetString());
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("evidenze/") && e.FullName.EndsWith("shot.png"));
    }

    private NonConformita SalvaNcDaOperazione()
    {
        var nc = new NonConformita
        {
            Id = "NC-EVX-1", TestCaseId = _test.Id, TestUid = _test.Uid, EsecuzioneIterazione = 1, CondizioneOrigineUid = _opConEvidenza.Uid,
            DataApertura = DateTime.Now, Descrizione = "Problema sulla lettura DID"
        };
        _state.NonConformita.Save(nc);
        return nc;
    }

    [Fact]
    public void NcPackageAndTextIncludeTheOriginOperationEvidenceWithoutCopyingItIntoTheNc()
    {
        var nc = SalvaNcDaOperazione();
        var sezioni = new HashSet<string>(NcSezioni.Tutte);

        var testo = TicketExportService.GeneraTesto(nc, _state, sezioni);
        Assert.Contains("Evidenze dell'operazione di origine (Test T-EVX-1, REV 1", testo);
        Assert.Contains("shot.png", testo);
        Assert.DoesNotContain("generale.txt", testo);

        var zipPath = PolarionPackageService.Crea(_state.Paths, nc, _state, Path.Combine(_dir, "ncout"), sezioni);
        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("evidenze-operazione/") && e.FullName.EndsWith("shot.png"));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith("generale.txt"));
        using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open(), Encoding.UTF8);
        using var doc = JsonDocument.Parse(reader.ReadToEnd());
        var op = doc.RootElement.GetProperty("operationEvidence");
        Assert.Equal("T-EVX-1", op.GetProperty("testId").GetString());
        Assert.Single(op.GetProperty("files").EnumerateArray());
        // No duplication: the NC still owns no attachment of its own.
        Assert.Empty(_state.NonConformita.GetById(nc.Id)!.Allegati);
    }

    [Fact]
    public void NcFromAnOperationWithoutEvidenceOrWithoutTestProducesNoOriginBlock()
    {
        var senza = new NonConformita { Id = "NC-EVX-2", TestUid = _test.Uid, EsecuzioneIterazione = 1, CondizioneOrigineUid = _opSenza.Uid, DataApertura = DateTime.Now };
        Assert.Empty(TicketExportService.EvidenzeOperazioneOrigine(senza, _state).Evidenze);
        var orfana = new NonConformita { Id = "NC-EVX-3", TestUid = Guid.NewGuid(), CondizioneOrigineUid = _opConEvidenza.Uid, DataApertura = DateTime.Now };
        Assert.Empty(TicketExportService.EvidenzeOperazioneOrigine(orfana, _state).Evidenze);
        var manuale = new NonConformita { Id = "NC-EVX-4", DataApertura = DateTime.Now };
        Assert.Empty(TicketExportService.EvidenzeOperazioneOrigine(manuale, _state).Evidenze);
        Assert.DoesNotContain("operazione di origine", TicketExportService.GeneraTesto(senza, _state, new HashSet<string>(NcSezioni.Tutte)));
    }

    [Fact]
    public void ReportTemplateSheetHasAnEvidenceColumnPerOperation()
    {
        var template = new TestTemplate { Nome = "TplReport", Uid = Guid.NewGuid(), FogliReport = new List<FoglioReportTemplate> { new() { NomeFoglio = "Op" } } };
        _test.TestTemplateUid = template.Uid;
        using var wb = new XLWorkbook();
        var metodo = typeof(ReportService).GetMethod("ScriviFogliTemplate", BindingFlags.NonPublic | BindingFlags.Static)!;
        metodo.Invoke(null, new object?[] { wb, new List<TestCase> { _test }, new List<TestTemplate> { template }, null, CancellationToken.None });

        var ws = wb.Worksheets.Single(w => w.Name == "Op");
        var intestazioni = ws.Row(3).CellsUsed().Select(c => c.GetString()).ToList();
        var colonna = intestazioni.IndexOf("Evidenze operazione") + 1;
        Assert.True(colonna > 0, "evidence column present");
        Assert.Equal("shot.png", ws.Cell(4, colonna).GetString());   // operation with evidence
        Assert.Equal("", ws.Cell(5, colonna).GetString());           // operation without
        Assert.DoesNotContain(ws.RangeUsed()!.Cells().Select(c => c.GetString()), v => v.Contains("EVIDENZE::"));
    }
}
