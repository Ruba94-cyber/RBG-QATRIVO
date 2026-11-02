using TestLoom.App.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace TestLoom.App.Services;

public static class PdfReportService
{
    static PdfReportService() => QuestPDF.Settings.License = LicenseType.Community;

    /// <summary>Testo di una cella della matrice nel PDF: la stessa scelta della vista e dell'Excel (TestoVisualizzato), in un solo punto,
    /// cosi' il test di parita' controlla davvero cio' che il renderer stampa.</summary>
    internal static string TestoCella(CellaMatriceReport cella) => cella.TestoVisualizzato;

    /// <summary>Righe massime delle tabelle del PDF: oltre il limite le righe restano fuori e la nota sotto la tabella dichiara quante ne sono mostrate.</summary>
    internal const int MassimoRequisitiPdf = 60;
    internal const int MassimoAnomaliePdf = 40; // Compatibilità con NotaTroncatura; la tabella report non tronca più le anomalie.

    /// <summary>Nota sotto una tabella troncata ("mostrati X di Y", nella lingua dell'interfaccia). Null se la tabella e' completa.</summary>
    internal static string? NotaTroncatura(string chiaveFmt, int totale, int massimo) =>
        totale > massimo ? string.Format(LocalizationService.Instance.Traduci(chiaveFmt), massimo, totale) : null;

    /// <summary>Genera il PDF del Report. <paramref name="ct"/> viene controllato tra le pagine e tra le righe della matrice (composizione
    /// QuestPDF eseguita da <c>GeneratePdf</c>): un annullamento interrompe la generazione senza produrre alcun PDF. La matrice riceve i
    /// Test dei criteri non TIME e del totale del Test; la durata delle operazioni la applica la matrice (come sulla vista).</summary>
    public static byte[] GeneraPdf(ReportFilter filtro, AppState state, string operatoreGenerazione,
        ConfigurazioneMatriceReport? configurazioneMatrice = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tuttiTestCases = state.TestCases.GetAll();
        var requisiti = state.Master.Requisiti.Load();
        var testCasesGlobali = tuttiTestCases.Where(filtro.Corrisponde).ToList();
        var testCasesMatrice = tuttiTestCases.Where(filtro.CorrispondeMatrice).ToList();
        configurazioneMatrice ??= new ConfigurazioneMatriceReport();
        // Addendum 6.4.0 (totali di intestazione, punto 5): elenco marcature N.A. passato a Genera() PRIMA del
        // calcolo dei Totali, stessa logica di gate usata dall'Excel (ReportService.ScriviMatriceConfigurabile).
        var marcaturePdf = MatriceNonApplicabileService.Leggi(state.Config, configurazioneMatrice.CampiX, configurazioneMatrice.CampiY);
        var preparata = ReportMatriceService.Prepara(testCasesMatrice, configurazioneMatrice,
            state.Master.Veicoli.Load(), state.Master.Funzionalita.Load(), state.Master.TestTemplate.Load(),
            configurazioneMatrice.ModalitaCella != ModalitaCellaMatrice.TestPercentuale ? state.Master.Componente.Load() : null,
            configurazioneMatrice.ModalitaCella != ModalitaCellaMatrice.TestPercentuale ? state.Master.StatiFunzionalitaEcuVeicolo.Load() : null,
            filtro: filtro);
        var matrice = preparata.Completa(marcaturePdf);
        var idsVista = preparata.TestIdsVista;
        var uidsVista = preparata.TestUidsVista;
        var testCases = testCasesGlobali.Where(tc => uidsVista.Contains(tc.Uid)).ToList();
        // Anche il PDF usa la stessa selezione congiunta (filtri globali + dimensioni matrice).
        var origine = ReportOrigineExportService.CalcolaOrigine(testCasesMatrice, state, configurazioneMatrice, ct: ct).Selezione;
        var idsNonOk = origine.TestIdsNonOk;
        var tutteNc = state.NonConformita.GetAll();
        var nonConformitaOrigine = tutteNc.Where(nc =>
            (!string.IsNullOrWhiteSpace(nc.TestCaseId) && idsNonOk.Contains(nc.TestCaseId))
            || nc.ProveCollegate.Any(p => idsNonOk.Contains(p.TestId))).ToList();
        // NC e prove collegate persistono ancora il TestId legacy. La selezione dei Test usa UID; le NC
        // associate a ID ambigui restano consultabili tramite la relazione legacy disponibile.
        var nonConformitaGrafici = tutteNc.Where(nc =>
            (!string.IsNullOrWhiteSpace(nc.TestCaseId) && idsVista.Contains(nc.TestCaseId))
            || nc.ProveCollegate.Any(p => idsVista.Contains(p.TestId))).ToList();
        var grafici = GraficiDatiService.PerReport(requisiti, testCases, nonConformitaGrafici);
        var bucket = ((DatiCopertura)grafici[0].Dati).Bucket;
        var andamento = ((DatiAndamento)grafici[1].Dati).Punti;
        var esiti = (DatiEsiti)grafici[2].Dati;
        var etichetteCampi = CampoSistemaCatalogoService.EtichettePerChiave(state);
        string EtichettaDimensione(string chiave) => chiave.StartsWith(DimensioniMatrice.CampoCustomPrefix, StringComparison.OrdinalIgnoreCase)
            ? etichetteCampi.GetValueOrDefault(chiave[DimensioniMatrice.CampoCustomPrefix.Length..], chiave[DimensioniMatrice.CampoCustomPrefix.Length..])
            : LocalizationService.Instance.Traduci(chiave switch
            {
                DimensioniMatrice.Gamma => "MatriceDimGamma",
                DimensioniMatrice.Sottoveicolo => "MatriceDimSottoveicolo",
                DimensioniMatrice.Componente => "MatriceDimEcu",
                DimensioniMatrice.CategoriaFunzionalita => "MatriceDimCategoriaFunzionalita",
                DimensioniMatrice.Funzionalita => "MatriceDimFunzionalita",
                DimensioniMatrice.Template => "MatriceDimTemplate",
                DimensioniMatrice.Esito => "MatriceDimEsito",
                DimensioniMatrice.Assegnatario => "MatriceDimAssegnatario",
                DimensioniMatrice.Requisito => "MatriceDimRequisito",
                _ => chiave
            });
        var descrizioneFiltriMatrice = configurazioneMatrice.Filtri.Where(x => x.Value.Count > 0)
            .Select(x => $"{EtichettaDimensione(x.Key)}: {string.Join(", ", x.Value)}").ToList();
        var descrizioneFiltri = filtro.Descrizione(CampoSistemaCatalogoService.EtichettePerChiave(state))
            + (descrizioneFiltriMatrice.Count > 0 ? " · Matrice: " + string.Join("; ", descrizioneFiltriMatrice) : "");
        // Overlay N.A. (decisione utente 6.4.0, punto 4), stesso overlay applicato all'export Excel
        // (ReportService.ScriviMatriceConfigurabile): nessuna duplicazione della logica di esclusione dalle
        // percentuali, solo la resa nel PDF cambia sotto.
        MatriceNonApplicabileService.Applica(state.Config, configurazioneMatrice.CampiX, configurazioneMatrice.CampiY, matrice);

        var pngCopertura = ChartRenderer.BarreCopertura(grafici[0].Titolo, bucket, 680, 340);
        var pngAndamento = ChartRenderer.AndamentoNonConformita(grafici[1].Titolo, andamento, 680, 340);
        var pngEsiti = ChartRenderer.DonutEsitiPerSottoveicolo(grafici[2].Titolo,
            esiti.PerSottoveicolo, esiti.PerEsito, 680, 400);

        ct.ThrowIfCancellationRequested();
        var doc = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Column(col =>
                {
                    col.Item().Text("TestLoom - Report Test Diagnostici CS").FontSize(18).Bold();
                    col.Item().Text($"Generato il {DateTime.Now:dd/MM/yyyy HH:mm} da {operatoreGenerazione}").FontSize(9).FontColor(Colors.Grey.Darken1);
                });

                page.Content().PaddingTop(15).Column(col =>
                {
                    col.Spacing(12);

                    col.Item().Text($"Filtri applicati: {descrizioneFiltri}").FontSize(9).Italic().FontColor(Colors.Grey.Darken1);
                    col.Item().Text("Riepilogo copertura").FontSize(13).Bold();
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c => { c.RelativeColumn(); c.RelativeColumn(); c.RelativeColumn(); c.RelativeColumn(); c.RelativeColumn(); });
                        void Cell(string testo, bool bold = false) { var t = table.Cell().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(testo); t.FontSize(9); if (bold) t.Bold(); }
                        Cell("Conforme", true); Cell("Con deviazioni", true); Cell("Non Conforme", true); Cell("Non testato", true); Cell("% completamento", true);
                        Cell(bucket.Conformi.ToString()); Cell(bucket.ConformiConDeviazioni.ToString()); Cell(bucket.NonConformi.ToString()); Cell(bucket.NonTestati.ToString()); Cell($"{bucket.PercentualeCompletamento:0.#}%");
                    });

                    col.Item().Image(pngCopertura);
                    col.Item().Image(pngAndamento);
                    col.Item().Image(pngEsiti);

                    var anomalieAudit = nonConformitaOrigine.OrderByDescending(x => x.DataApertura).ToList();
                    col.Item().Text("Anomalie nel periodo").FontSize(13).Bold();
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c => { c.RelativeColumn(1.2f); c.RelativeColumn(1.5f); c.RelativeColumn(1); c.RelativeColumn(1); c.RelativeColumn(1.3f); c.RelativeColumn(2); });
                        table.Header(header =>
                        {
                            void Header(string t) => header.Cell().Background(Colors.Grey.Lighten3).Padding(4).Text(t).FontSize(8).Bold();
                            Header("ID"); Header("Test"); Header("Stato"); Header("Apertura"); Header("Destinatario"); Header("Descrizione");
                        });
                        foreach (var n in anomalieAudit)
                        {
                            // Le celle possono continuare sulle pagine successive: descrizioni lunghe non devono
                            // forzare l'intera riga in una singola pagina né essere troncate.
                            void Cell(string t) => table.Cell().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(2).Text(t).FontSize(7.5f);
                            Cell(n.Id); Cell(n.TestCaseId); Cell(n.Stato.Label()); Cell(n.DataApertura.ToString("dd/MM/yy")); Cell(n.Destinatario?.Label() ?? ""); Cell(n.Descrizione);
                        }
                    });
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("TestLoom — pagina ");
                    x.CurrentPageNumber();
                    x.Span(" di ");
                    x.TotalPages();
                });
            });

            const int colonnePerPagina = 6;
            for (var inizio = 0; inizio < matrice.Colonne.Count; inizio += colonnePerPagina)
            {
                ct.ThrowIfCancellationRequested();
                var colonnePagina = matrice.Colonne.Skip(inizio).Take(colonnePerPagina).ToList();
                // Addendum 6.4.0 (totali di intestazione, punto 5): stesso ordine/indice di Colonne, quindi
                // lo stesso Skip/Take della pagina corrente allinea i totali alle colonne mostrate.
                var totaliColonnaPagina = matrice.TotaliColonna.Skip(inizio).Take(colonnePerPagina).ToList();
                container.Page(page =>
                {
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(24);
                    page.DefaultTextStyle(x => x.FontSize(8));

                    page.Header().Column(col =>
                    {
                        col.Item().Text($"Matrice di conformità — {LocalizationService.Instance.Traduci(ReportMatriceService.ChiaveEtichettaModalita(configurazioneMatrice.ModalitaCella))}").FontSize(15).Bold();
                        col.Item().Text($"Righe: {string.Join(" › ", configurazioneMatrice.CampiY)} · Colonne: {string.Join(" › ", configurazioneMatrice.CampiX)}")
                            .FontSize(8).FontColor(Colors.Grey.Darken1);
                        col.Item().Text(MatriceTooltipService.DescrizioneModalita(configurazioneMatrice.ModalitaCella))
                            .FontSize(7).Italic().FontColor(Colors.Grey.Darken1);
                    });

                    page.Content().PaddingTop(10).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(1.7f);
                            foreach (var _ in colonnePagina) columns.RelativeColumn();
                        });
                        table.Header(header =>
                        {
                            // Totali di intestazione: riepilogano la distribuzione della base selezionata
                            // per la colonna, con la stessa etichetta mostrata nelle celle.
                            header.Cell().Background(Colors.Grey.Lighten3).Padding(4).Column(col =>
                            {
                                col.Item().Text(string.Join(" › ", configurazioneMatrice.CampiY)).Bold();
                                col.Item().Text($"Totale: {matrice.TotaleGenerale.Testo}").FontSize(6.5f).FontColor(Colors.Grey.Darken1);
                            });
                            for (var i = 0; i < colonnePagina.Count; i++)
                            {
                                var totaleColonna = i < totaliColonnaPagina.Count ? totaliColonnaPagina[i].Testo : "—";
                                header.Cell().Background(Colors.Grey.Lighten3).Padding(4).Column(col =>
                                {
                                    col.Item().Text(ReportMatriceService.EtichettaPercorso(colonnePagina[i]) is { Length: > 0 } etichettaCol ? etichettaCol : LocalizationService.Instance.Traduci(ReportMatriceService.ChiaveEtichettaModalita(configurazioneMatrice.ModalitaCella))).Bold();
                                    col.Item().Text(totaleColonna).FontSize(6.5f).FontColor(Colors.Grey.Darken1);
                                });
                            }
                        });
                        foreach (var riga in matrice.Righe.ConAnnullamento(ct))
                        {
                            table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Column(col =>
                            {
                                col.Item().Text(riga.Percorso).SemiBold();
                                col.Item().Text($"Totale: {riga.TotaleRiga.Testo}").FontSize(6.5f).FontColor(Colors.Grey.Darken1);
                            });
                            foreach (var colonna in colonnePagina)
                            {
                                var cella = riga.Celle.First(c => c.Colonna.Equals(colonna, StringComparison.OrdinalIgnoreCase));
                                var contenitore = table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten2);
                                if (cella.NonApplicabile)
                                {
                                    // Coerente con l'Excel (ReportService.ScriviMatriceConfigurabile): sfondo
                                    // distinto e nota, se presente, su una seconda riga più piccola (il PDF
                                    // non ha un equivalente del commento di cella di ClosedXML).
                                    contenitore.Background(Colors.Grey.Lighten3).Padding(4).Column(col =>
                                    {
                                        col.Item().Text(TestoCella(cella)).Italic();
                                        if (!string.IsNullOrWhiteSpace(cella.NotaNonApplicabile))
                                            col.Item().Text(cella.NotaNonApplicabile).FontSize(6).FontColor(Colors.Grey.Darken2).Italic();
                                    });
                                }
                                else
                                {
                                    contenitore.Padding(4).Text(TestoCella(cella));
                                }
                            }
                        }
                    });

                    page.Footer().AlignCenter().Text(x =>
                    {
                        x.Span("TestLoom — pagina ");
                        x.CurrentPageNumber();
                        x.Span(" di ");
                        x.TotalPages();
                    });
                });
            }
        });

        ct.ThrowIfCancellationRequested();
        return doc.GeneratePdf();
    }
}
