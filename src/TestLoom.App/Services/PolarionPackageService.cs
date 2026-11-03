using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace TestLoom.App.Services;

/// <summary>Pacchetto offline/manuale per Ticketing: nessuna API, token o credenziale.</summary>
public static class PolarionPackageService
{
    static PolarionPackageService() => QuestPDF.Settings.License = LicenseType.Community;

    /// <summary>Cartella di destinazione scelta dall'utente tramite picker nativo (Addendum §3, feedback QA):
    /// non più sempre nella cartella Report condivisa. Nome file = data + componente + descrizione breve, non più
    /// l'Id del test, per essere riconoscibile a colpo d'occhio da chi apre la claim. Sezioni scelte
    /// dall'utente nel dialogo di anteprima (Addendum §4), non più sempre tutte.</summary>
    public static string Crea(DataPaths paths, TestCase test, AppState state, string cartellaDestinazione, IReadOnlySet<string> sezioni)
    {
        var testo = TicketExportService.GeneraTesto(test, state, sezioni);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Scrivi(zip, $"TestLoom_{test.Id}.txt", Encoding.UTF8.GetBytes(testo));
            Scrivi(zip, $"TestLoom_{test.Id}.pdf", GeneraPdf(test, testo));
            var manifest = new
            {
                schema = 1, testId = test.Id, generatedAt = DateTimeOffset.Now,
                generatedBy = state.Config.OperatoreAttivo,
                polarionIntegration = "manual-only-no-api",
                attachments = test.Allegati.Where(a => !a.Archiviata).Select(a => new
                {
                    a.NomeFile, a.Sha256, a.DimensioneByte,
                    operation = EvidenzaOperazione.EtichettePerAllegato(test).GetValueOrDefault(a.Uid, "")
                })
            };
            Scrivi(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, JsonIo.Options));
            foreach (var allegato in test.Allegati.Where(a => !a.Archiviata))
            {
                var sorgente = AttachmentService.PercorsoCompleto(paths, test, allegato);
                if (!File.Exists(sorgente)) continue;
                var entry = zip.CreateEntry($"evidenze/{allegato.Uid:N}_{allegato.NomeFile}", CompressionLevel.Optimal);
                using var destinazione = entry.Open();
                using var origine = File.OpenRead(sorgente);
                origine.CopyTo(destinazione);
            }
        }

        Directory.CreateDirectory(cartellaDestinazione);
        var nomeBase = NomeFile(test);
        var nomeFile = $"{nomeBase}.zip";
        var percorso = Path.Combine(cartellaDestinazione, nomeFile);
        var i = 1;
        while (File.Exists(percorso))
        {
            nomeFile = $"{nomeBase}_{i}.zip";
            percorso = Path.Combine(cartellaDestinazione, nomeFile);
            i++;
        }
        EsportazioneFile.ScriviBytesAtomico(percorso, output.ToArray());
        return percorso;
    }

    /// <summary>Sovraccarico Change Request (feedback utente 2026-08-21, punto 7: "esporta pacchetto CR" deve
    /// essere identico a "genera pacchetto Ticketing" nei Test) — stessa forma del pacchetto (txt+pdf+manifest+
    /// evidenze in uno zip, nome file riconoscibile, scelta sezioni), sorgente dati e allegati diversi.</summary>
    public static string Crea(DataPaths paths, ChangeRequest cr, AppState state, string cartellaDestinazione, IReadOnlySet<string> sezioni)
    {
        var testo = TicketExportService.GeneraTesto(cr, state, sezioni);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Scrivi(zip, $"TestLoom_{cr.Id}.txt", Encoding.UTF8.GetBytes(testo));
            Scrivi(zip, $"TestLoom_{cr.Id}.pdf", GeneraPdf(cr.Id, testo));
            var manifest = new
            {
                schema = 1, changeRequestId = cr.Id, generatedAt = DateTimeOffset.Now,
                generatedBy = state.Config.OperatoreAttivo,
                polarionIntegration = "manual-only-no-api",
                attachments = cr.Allegati.Where(a => !a.Archiviata).Select(a => new { a.NomeFile, a.Sha256, a.DimensioneByte })
            };
            Scrivi(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, JsonIo.Options));
            foreach (var allegato in cr.Allegati.Where(a => !a.Archiviata))
            {
                var sorgente = AttachmentService.PercorsoCompleto(paths, cr, allegato);
                if (!File.Exists(sorgente)) continue;
                var entry = zip.CreateEntry($"evidenze/{allegato.Uid:N}_{allegato.NomeFile}", CompressionLevel.Optimal);
                using var destinazione = entry.Open();
                using var origine = File.OpenRead(sorgente);
                origine.CopyTo(destinazione);
            }
        }

        Directory.CreateDirectory(cartellaDestinazione);
        var nomeBase = NomeFile(cr);
        var nomeFile = $"{nomeBase}.zip";
        var percorso = Path.Combine(cartellaDestinazione, nomeFile);
        var i = 1;
        while (File.Exists(percorso))
        {
            nomeFile = $"{nomeBase}_{i}.zip";
            percorso = Path.Combine(cartellaDestinazione, nomeFile);
            i++;
        }
        EsportazioneFile.ScriviBytesAtomico(percorso, output.ToArray());
        return percorso;
    }

    /// <summary>Sovraccarico Non Conformità senza Test (feedback utente 2026-09-15: "dovrà essere possibile
    /// generare il pacchetto ticketing... esattamente come i test") — stessa forma del pacchetto CR, dato che
    /// una segnalazione di rete non ha una storia di esecuzioni/condizioni da riportare.</summary>
    public static string Crea(DataPaths paths, NonConformita nc, AppState state, string cartellaDestinazione, IReadOnlySet<string> sezioni)
    {
        var testo = TicketExportService.GeneraTesto(nc, state, sezioni);
        var origineOp = TicketExportService.EvidenzeOperazioneOrigine(nc, state);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Scrivi(zip, $"TestLoom_{nc.Id}.txt", Encoding.UTF8.GetBytes(testo));
            Scrivi(zip, $"TestLoom_{nc.Id}.pdf", GeneraPdf(nc.Id, testo));
            var manifest = new
            {
                schema = 1, nonConformitaId = nc.Id, generatedAt = DateTimeOffset.Now,
                generatedBy = state.Config.OperatoreAttivo,
                polarionIntegration = "manual-only-no-api",
                attachments = nc.Allegati.Where(a => !a.Archiviata).Select(a => new { a.NomeFile, a.Sha256, a.DimensioneByte }),
                operationEvidence = new
                {
                    testId = origineOp.Test?.Id, revision = origineOp.Iterazione,
                    files = origineOp.Evidenze.Select(a => new { a.NomeFile, a.Sha256, a.DimensioneByte })
                }
            };
            Scrivi(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, JsonIo.Options));
            foreach (var allegato in nc.Allegati.Where(a => !a.Archiviata))
            {
                var sorgente = AttachmentService.PercorsoCompleto(paths, nc, allegato);
                if (!File.Exists(sorgente)) continue;
                var entry = zip.CreateEntry($"evidenze/{allegato.Uid:N}_{allegato.NomeFile}", CompressionLevel.Optimal);
                using var destinazione = entry.Open();
                using var origine = File.OpenRead(sorgente);
                origine.CopyTo(destinazione);
            }
            // Evidenze dell'operazione da cui nasce la NC: lette dal Test (nessuna copia/duplicazione in TestLoom), solo aggiunte al pacchetto.
            foreach (var allegato in origineOp.Evidenze)
            {
                var sorgente = AttachmentService.PercorsoCompleto(paths, origineOp.Test!, allegato);
                if (!File.Exists(sorgente)) continue;
                var entry = zip.CreateEntry($"evidenze-operazione/{allegato.Uid:N}_{allegato.NomeFile}", CompressionLevel.Optimal);
                using var destinazione = entry.Open();
                using var origine = File.OpenRead(sorgente);
                origine.CopyTo(destinazione);
            }
        }

        Directory.CreateDirectory(cartellaDestinazione);
        var nomeBase = NomeFile(nc);
        var nomeFile = $"{nomeBase}.zip";
        var percorso = Path.Combine(cartellaDestinazione, nomeFile);
        var i = 1;
        while (File.Exists(percorso))
        {
            nomeFile = $"{nomeBase}_{i}.zip";
            percorso = Path.Combine(cartellaDestinazione, nomeFile);
            i++;
        }
        EsportazioneFile.ScriviBytesAtomico(percorso, output.ToArray());
        return percorso;
    }

    /// <summary>Data + Id + sintesi, stesso principio di NomeFile(ChangeRequest) ma con la sintesi della
    /// segnalazione al posto del titolo (una NC non ha un campo Titolo).</summary>
    private static string NomeFile(NonConformita nc)
    {
        var parti = new List<string> { nc.DataApertura.ToString("yyyyMMdd"), nc.Id };
        parti.Add(string.IsNullOrWhiteSpace(nc.DescrizioneBreveSegnalata) ? nc.Id : nc.DescrizioneBreveSegnalata);
        return Sanitizza(parti);
    }

    /// <summary>Data + componente + descrizione breve, sanitizzati per essere un nome file valido su Windows: se la
    /// descrizione breve non è compilata si ripiega sull'Id del test, per non generare un nome ambiguo.</summary>
    private static string NomeFile(TestCase test)
    {
        var parti = new List<string> { test.DataCreazione.ToString("yyyyMMdd"), test.Componente };
        parti.Add(string.IsNullOrWhiteSpace(test.DescrizioneBreve) ? test.Id : test.DescrizioneBreve);
        return Sanitizza(parti);
    }

    /// <summary>Data + Id + titolo, stesso principio di NomeFile(TestCase) ma senza componente (una CR ne può avere
    /// più di una o nessuna).</summary>
    private static string NomeFile(ChangeRequest cr)
    {
        var parti = new List<string> { cr.DataCreazione.ToString("yyyyMMdd"), cr.Id };
        parti.Add(string.IsNullOrWhiteSpace(cr.Titolo) ? cr.Id : cr.Titolo);
        return Sanitizza(parti);
    }

    private static string Sanitizza(List<string> parti)
    {
        var nome = string.Join("_", parti.Where(p => !string.IsNullOrWhiteSpace(p)));
        foreach (var c in Path.GetInvalidFileNameChars().Append(' ')) nome = nome.Replace(c, '-');
        while (nome.Contains("--")) nome = nome.Replace("--", "-");
        return nome.Trim('-', '_');
    }

    private static void Scrivi(ZipArchive zip, string nome, byte[] bytes)
    {
        var entry = zip.CreateEntry(nome, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static byte[] GeneraPdf(TestCase test, string testo) => GeneraPdf(test.Id, testo);

    private static byte[] GeneraPdf(string titolo, string testo)
    {
        var doc = Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4); page.Margin(28); page.DefaultTextStyle(x => x.FontSize(9));
            page.Header().Column(c => { c.Item().Text($"RBG - QATRIVO · {titolo}").FontSize(17).Bold(); c.Item().Text("Evidence package for manual Ticketing entry · no API integration").FontColor(Colors.Grey.Darken1); });
            page.Content().PaddingTop(12).Column(c =>
            {
                c.Spacing(3);
                foreach (var line in testo.Replace("\r", "").Split('\n'))
                    c.Item().Text(string.IsNullOrWhiteSpace(line) ? " " : line);
            });
            page.Footer().AlignCenter().Text(x => { x.Span("RBG - QATRIVO · "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
        }));
        return doc.GeneratePdf();
    }
}
