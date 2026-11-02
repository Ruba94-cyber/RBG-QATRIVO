using System.Diagnostics;
using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>Addendum 6.4.0 ("percentuale complessiva sull'intestazione di ogni colonna e di ogni riga, più un
/// totale generale nell'angolo"): il totale di una colonna/riga/generale conta le combinazioni funzionalità×
/// componente×veicolo DISTINTE presenti in quella colonna/riga/matrice, ognuna UNA volta col suo esito più recente —
/// mai la somma delle celle. Una cella marcata N.A. esclude la combinazione dal totale solo se la combinazione
/// non compare in nessun'altra cella non-N.A. della stessa colonna/riga/matrice. I totali sono sull'intera
/// matrice filtrata, indipendenti dalla finestra/pagina visibile.
///
/// Revisione indipendente 2026-09-26: ReportMatriceService.Genera/GeneraFinestra/MatricePreparata.Finestra/
/// Completa NON accettano più un `Func&lt;string,string,bool&gt;` (un predicato non è confrontabile fra due
/// chiamate, quindi impediva di sapere se la marcatura N.A. era cambiata rispetto all'ultima Finestra() sulla
/// stessa MatricePreparata — vedi MEDIO della revisione), ma l'elenco `IReadOnlyList&lt;CellaNonApplicabile&gt;`
/// letto da MatriceNonApplicabileService.Leggi: MatricePreparata ne calcola una firma deterministica e riusa i
/// Totali già calcolati se la firma non è cambiata (vedi ReportMatriceTotaliCacheTests per la copertura della
/// cache stessa).</summary>
public sealed class ReportMatriceTotaliIntestazioneTests
{
    private static readonly ConfigurazioneMatriceReport ConfigEcuAssegnatario = new()
    {
        AsseX = DimensioniMatrice.Componente,
        AsseY = DimensioniMatrice.Assegnatario
    };

    private static TestCase CreaTest(string id, string funzionalita, string componente, string veicolo, string assegnatario,
        DateTime data, Esito esito, OrigineTest origine = OrigineTest.TestLoom)
    {
        var tc = new TestCase
        {
            Id = id,
            Componente = new EcuSelection([componente]),
            Veicoli = [veicolo],
            Funzionalita = [funzionalita],
            Assegnatario = assegnatario,
            Origine = origine
        };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = data,
            Condizioni = [new Condizione { Funzionalita = funzionalita, Esito = esito, Componente = new EcuSelection([componente]) }]
        });
        return tc;
    }

    private static List<CellaNonApplicabile> Marcature(params (string X, string Y)[] celle) =>
        celle.Select(c => new CellaNonApplicabile { ValoreAsseX = c.X, ValoreAsseY = c.Y }).ToList();

    /// <summary>Contratto stato corrente (commit 4fdf2b6, "Fix current-state matrix axis projection"): la combinazione F×componente×veicolo
    /// ha UN rappresentante corrente (il Test più recente, qui Luigi) e la proiezione sull'asse Assegnatario usa il rappresentante:
    /// la combinazione compare una sola volta, nella riga del rappresentante, e conta una volta nel totale di colonna. La versione
    /// precedente (una riga per Assegnatario) è superata.</summary>
    [Fact]
    public void TotaleColonna_CombinazioneConDueAssegnatari_ContaUnaVoltaNelRappresentante()
    {
        var mario = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", "Mario", new DateTime(2026, 1, 1), Esito.Conforme);
        var luigi = CreaTest("T-2", "Lettura guasto", "BCM", "Daily A", "Luigi", new DateTime(2026, 1, 2), Esito.Conforme);

        var risultato = ReportMatriceService.Genera([mario, luigi], ConfigEcuAssegnatario, [], [], []);

        var riga = Assert.Single(risultato.Righe);
        Assert.Equal("Luigi", riga.Riga);
        var totaleBcm = risultato.TotaliColonna[risultato.Colonne.IndexOf("BCM")];
        Assert.Equal(1, totaleBcm.Valutati);
        Assert.Equal(1, totaleBcm.Conformi);
        Assert.Equal(100d, totaleBcm.Percentuale);
    }

    /// <summary>La combinazione core (funzionalità×componente×veicolo) usa l'esito più recente fra TUTTI i Test che
    /// la determinano, indipendentemente da quale riga (Assegnatario) lo ha prodotto — qui Luigi (più recente)
    /// vince su Mario, quindi il totale di colonna riflette NonConforme, non una media delle due righe.</summary>
    [Fact]
    public void TotaleColonna_EsitoPiuRecenteFraPiuTest()
    {
        var mario = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", "Mario", new DateTime(2026, 1, 1), Esito.Conforme);
        var luigi = CreaTest("T-2", "Lettura guasto", "BCM", "Daily A", "Luigi", new DateTime(2026, 1, 2), Esito.NonConforme);

        var risultato = ReportMatriceService.Genera([mario, luigi], ConfigEcuAssegnatario, [], [], []);

        var totaleBcm = risultato.TotaliColonna[risultato.Colonne.IndexOf("BCM")];
        Assert.Equal(1, totaleBcm.Valutati);
        Assert.Equal(0, totaleBcm.Conformi);
        // Stato %: distribuzione sulle combinazioni applicabili; lo stato è quello più recente (Luigi, NonConforme).
        Assert.Equal(new StatiMatrice(0, 1, 0, 0, 0, 0), totaleBcm.StatiPercentuale);
        Assert.Equal(0d, totaleBcm.Percentuale);
    }

    /// <summary>Una combinazione con solo operazioni "Da eseguire" non è mai "valutata" (Valutati 0). Nello Stato % è però
    /// applicabile e conta nel totale come "da eseguire" (decisione: Stato % = distribuzione su tutte le combinazioni applicabili,
    /// incluse le non testate): la percentuale conforme è 0% su 1, non "—".</summary>
    [Fact]
    public void TotaleColonna_SoloDaEseguire_NonContaComeValutata()
    {
        var pianificato = new TestCase { Id = "T-1", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"], Assegnatario = "Mario" };
        pianificato.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(2026, 1, 1),
            Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.DaEseguire, Componente = new EcuSelection(["BCM"]) }]
        });

        var risultato = ReportMatriceService.Genera([pianificato], ConfigEcuAssegnatario, [], [], []);

        var totaleBcm = risultato.TotaliColonna[risultato.Colonne.IndexOf("BCM")];
        Assert.Equal(0, totaleBcm.Valutati);
        Assert.Equal(new StatiMatrice(0, 0, 0, 1, 0, 0), totaleBcm.StatiPercentuale);
        Assert.Equal(0d, totaleBcm.Percentuale);
    }

    /// <summary>Contratto stato corrente (commit 4fdf2b6): la combinazione F×componente×veicolo compare in UNA sola cella, quella del
    /// rappresentante corrente (qui Luigi, Test più recente). Marcare N.A. quella cella la esclude dai totali della sua colonna e
    /// riga; marcare una cella che non è del rappresentante (Mario) non ha effetto. La versione precedente (due righe) è superata.</summary>
    [Fact]
    public void TotaleColonnaRiga_NASulRappresentante_EsclusaDaColonnaERiga_NASuNonRappresentante_NessunEffetto()
    {
        var mario = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", "Mario", new DateTime(2026, 1, 1), Esito.Conforme);
        var luigi = CreaTest("T-2", "Lettura guasto", "BCM", "Daily A", "Luigi", new DateTime(2026, 1, 2), Esito.Conforme);

        var marioNA = ReportMatriceService.Genera([mario, luigi], ConfigEcuAssegnatario, [], [], [], Marcature(("BCM", "Mario")));
        var totaleBcm = marioNA.TotaliColonna[marioNA.Colonne.IndexOf("BCM")];
        Assert.Equal(1, totaleBcm.Valutati); // la marcatura su una cella che non è del rappresentante non ha effetto
        Assert.Equal(1, totaleBcm.Conformi);

        var risultato = ReportMatriceService.Genera([mario, luigi], ConfigEcuAssegnatario, [], [], [], Marcature(("BCM", "Luigi")));
        var totaleBcmNA = risultato.TotaliColonna[risultato.Colonne.IndexOf("BCM")];
        Assert.Equal(0, totaleBcmNA.Valutati); // l'unica cella della combinazione è N.A.: esclusa dalla colonna
        var rigaLuigi = risultato.Righe.Single(r => r.Riga == "Luigi");
        Assert.Equal(0, rigaLuigi.TotaleRiga.Valutati);
        Assert.Equal("—", rigaLuigi.TotaleRiga.Testo);
    }

    /// <summary>Revisione F5 (asse multi-valore): una funzionalità con DUE categorie compare in due righe. Marcare N.A. una sola di
    /// queste celle non la esclude dai totali: resta contata nella colonna e nel totale generale grazie all'altra cella non N.A.,
    /// ed esce soltanto dalla riga marcata, che non ha più celle valutabili.</summary>
    [Fact]
    public void TotaleColonna_AltraCellaNonNA_MantieneLaCombinazioneContata_AsseMultiValore()
    {
        var tc = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", "Mario", new DateTime(2026, 1, 1), Esito.Conforme);
        var config = new ConfigurazioneMatriceReport { CampiX = [DimensioniMatrice.Componente], CampiY = [DimensioniMatrice.CategoriaFunzionalita] };
        var funzionalita = new[] { new Funzionalita { Nome = "Lettura guasto", Categorie = ["CatA", "CatB"] } };

        var risultato = ReportMatriceService.Genera([tc], config, [], funzionalita, [], Marcature(("BCM", "CatA")));

        var totaleBcm = risultato.TotaliColonna[risultato.Colonne.IndexOf("BCM")];
        Assert.Equal(1, totaleBcm.Valutati);
        Assert.Equal(1, totaleBcm.Conformi);
        Assert.Equal(1, risultato.TotaleGenerale.Valutati);
        Assert.Equal(0, risultato.Righe.Single(r => r.Riga == "CatA").TotaleRiga.Valutati);
        Assert.Equal(1, risultato.Righe.Single(r => r.Riga == "CatB").TotaleRiga.Valutati);
    }

    /// <summary>Se TUTTE le celle in cui compare una combinazione sono marcate N.A. (qui l'unica cella della
    /// combinazione), la combinazione è esclusa anche dal totale generale.</summary>
    [Fact]
    public void TotaleGenerale_CombinazioneInteramenteNA_Esclusa()
    {
        var mario = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", "Mario", new DateTime(2026, 1, 1), Esito.Conforme);
        var altra = CreaTest("T-2", "Frenata", "EDC", "Daily B", "Luigi", new DateTime(2026, 1, 1), Esito.NonConforme);

        var risultato = ReportMatriceService.Genera([mario, altra], ConfigEcuAssegnatario, [], [], [], Marcature(("BCM", "Mario")));

        // Solo la combinazione di Luigi (EDC) resta nel totale generale: 0/1 (NonConforme).
        Assert.Equal(1, risultato.TotaleGenerale.Valutati);
        Assert.Equal(0, risultato.TotaleGenerale.Conformi);
    }

    /// <summary>Rilievo BASSO della revisione: quando OGNI cella della matrice è marcata N.A. (non solo una
    /// combinazione), il totale generale e i totali di TUTTE le righe/colonne devono restare "—" (nulla
    /// valutato) — non solo "esclusa una combinazione", ma l'intera aggregazione a zero.</summary>
    [Fact]
    public void Totali_InteraMatriceMarcataNA_TotaleGeneraleERigheEColonneRestanoSenzaValore()
    {
        var mario = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", "Mario", new DateTime(2026, 1, 1), Esito.Conforme);
        var luigi = CreaTest("T-2", "Frenata", "EDC", "Daily B", "Luigi", new DateTime(2026, 1, 1), Esito.NonConforme);

        // Le uniche due celle della matrice (BCM×Mario e EDC×Luigi) sono entrambe marcate N.A.
        var risultato = ReportMatriceService.Genera([mario, luigi], ConfigEcuAssegnatario, [], [], [],
            Marcature(("BCM", "Mario"), ("EDC", "Luigi")));

        Assert.Equal("—", risultato.TotaleGenerale.Testo);
        Assert.Equal(0, risultato.TotaleGenerale.Valutati);
        Assert.All(risultato.TotaliColonna, t => Assert.Equal("—", t.Testo));
        Assert.All(risultato.Righe, r => Assert.Equal("—", r.TotaleRiga.Testo));
    }

    /// <summary>Il totale generale conta le combinazioni distinte dell'INTERA matrice filtrata: la
    /// combinazione condivisa da Mario/Luigi conta una volta, la combinazione di Frenata×EDC conta una volta
    /// in più (Test/combinazioni diverse si sommano, la stessa combinazione non si duplica).</summary>
    [Fact]
    public void TotaleGenerale_CombinazioniDistinteDellIntereaMatrice()
    {
        var mario = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", "Mario", new DateTime(2026, 1, 1), Esito.Conforme);
        var luigi = CreaTest("T-2", "Lettura guasto", "BCM", "Daily A", "Luigi", new DateTime(2026, 1, 1), Esito.Conforme);
        var altra = CreaTest("T-3", "Frenata", "EDC", "Daily B", "Mario", new DateTime(2026, 1, 1), Esito.NonConforme);

        var risultato = ReportMatriceService.Genera([mario, luigi, altra], ConfigEcuAssegnatario, [], [], []);

        // Due combinazioni core distinte: (Lettura guasto, BCM, Daily A) e (Frenata, EDC, Daily B). Stato %: una OK e una NOK.
        Assert.Equal(2, risultato.TotaleGenerale.Valutati);
        Assert.Equal(1, risultato.TotaleGenerale.Conformi);
        Assert.Equal(new StatiMatrice(1, 1, 0, 0, 0, 0), risultato.TotaleGenerale.StatiPercentuale);
        Assert.Equal(50d, risultato.TotaleGenerale.Percentuale);
    }

    /// <summary>Punto 4 dell'addendum: i totali sono calcolati sull'intera matrice filtrata, non sulla sola
    /// finestra/pagina materializzata — qui una finestra di una sola colonna/riga mostra comunque i totali
    /// corretti calcolati su TUTTE le combinazioni (due colonne, due righe).</summary>
    [Fact]
    public void Totali_IndipendentiDallaFinestraVisibile()
    {
        var mario = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", "Mario", new DateTime(2026, 1, 1), Esito.Conforme);
        var luigi = CreaTest("T-2", "Frenata", "EDC", "Daily B", "Luigi", new DateTime(2026, 1, 1), Esito.NonConforme);

        var completo = ReportMatriceService.Genera([mario, luigi], ConfigEcuAssegnatario, [], [], []);
        var finestra = ReportMatriceService.GeneraFinestra([mario, luigi], ConfigEcuAssegnatario, [], [], [], 0, 1, 0, 1).Risultato;

        // La finestra materializza solo 1x1 celle, ma i totali (indice per indice) restano quelli della
        // matrice intera: stesso valore per la stessa colonna/riga fra i due risultati.
        Assert.Single(finestra.Colonne);
        Assert.Single(finestra.Righe);
        var colonnaFinestra = finestra.Colonne[0];
        var indiceCompleto = completo.Colonne.IndexOf(colonnaFinestra);
        Assert.Equal(completo.TotaliColonna[indiceCompleto].Testo, finestra.TotaliColonna[0].Testo);
        var rigaFinestra = finestra.Righe[0].Riga;
        var rigaCompleta = completo.Righe.Single(r => r.Riga == rigaFinestra);
        Assert.Equal(rigaCompleta.TotaleRiga.Testo, finestra.Righe[0].TotaleRiga.Testo);
        Assert.Equal(completo.TotaleGenerale.Testo, finestra.TotaleGenerale.Testo);
        // Il totale generale conta ENTRAMBE le combinazioni anche se la finestra ne mostra solo una.
        Assert.Equal(2, finestra.TotaleGenerale.Valutati);
    }

    /// <summary>Contratto stato corrente, commit 4fdf2b6 ("Fix current-state matrix axis projection"): ogni
    /// combinazione funzionalità×componente×veicolo è ridotta PRIMA al suo rappresentante corrente (esito più recente fra
    /// tutti i Test), POI si applicano i filtri non-identità (qui Assegnatario) e si proiettano gli assi. La
    /// versione precedente (filtri per operazione PRIMA della riduzione, "Totali_FiltriApplicatiPrima") è
    /// superata: l'OK storico di Mario non deve riapparire quando il corrente è NOK di Luigi (nessuna resurrezione).</summary>
    [Fact]
    public void Totali_FiltroNonIdentita_ApplicatoDopoLaRiduzione_NessunaResurrezione()
    {
        var mario = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", "Mario", new DateTime(2026, 1, 1), Esito.Conforme);
        var luigi = CreaTest("T-2", "Lettura guasto", "BCM", "Daily A", "Luigi", new DateTime(2026, 1, 2), Esito.NonConforme);

        var configMario = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Componente,
            AsseY = DimensioniMatrice.Assegnatario,
            Filtri = new() { [DimensioniMatrice.Assegnatario] = ["Mario"] }
        };

        var conMario = ReportMatriceService.Genera([mario, luigi], configMario, [], [], []);

        // Il rappresentante corrente (Luigi, NonConforme, più recente) non passa il filtro: la combinazione esce
        // dal totale generale e dalle colonne. Il Test di Mario (Conforme, storico) non riappare.
        Assert.Equal(0, conMario.TotaleGenerale.Valutati);
        Assert.Equal(0, conMario.TotaleGenerale.Conformi);
        Assert.DoesNotContain("BCM", conMario.Colonne);

        // Controllo: con Assegnatario = Luigi il rappresentante corrente passa il filtro e mostra il suo stato NOK.
        var configLuigi = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Componente,
            AsseY = DimensioniMatrice.Assegnatario,
            Filtri = new() { [DimensioniMatrice.Assegnatario] = ["Luigi"] }
        };
        var conLuigi = ReportMatriceService.Genera([mario, luigi], configLuigi, [], [], []);
        Assert.Equal(1, conLuigi.TotaleGenerale.Valutati);
        Assert.Equal(0, conLuigi.TotaleGenerale.Conformi);
        var totaleBcm = conLuigi.TotaliColonna[conLuigi.Colonne.IndexOf("BCM")];
        Assert.Equal(1, totaleBcm.Valutati);
        Assert.Equal(0, totaleBcm.Conformi);
    }

    /// <summary>Decisione utente 2026-09-24 (test importati dagli Status UnitLogger): contribuiscono SOLO allo stato
    /// finale (matrice per funzionalità×componente×veicolo, esito più recente) — inclusi nei totali di intestazione
    /// come qualunque altro Test, senza trattamento speciale nella riduzione a combinazione.</summary>
    [Fact]
    public void Totali_TestImportatiDaUdt_InclusiComeOggiNelloStatoDelleFunzionalita()
    {
        var importato = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", "Mario", new DateTime(2026, 1, 1), Esito.Conforme,
            OrigineTest.ImportStatoUdt);

        var risultato = ReportMatriceService.Genera([importato], ConfigEcuAssegnatario, [], [], []);

        var totaleBcm = risultato.TotaliColonna[risultato.Colonne.IndexOf("BCM")];
        Assert.Equal(1, totaleBcm.Valutati);
        Assert.Equal(1, totaleBcm.Conformi);
        Assert.Equal(1, risultato.TotaleGenerale.Valutati);
        Assert.Equal(1, risultato.TotaleGenerale.Conformi);
    }

    /// <summary>Punto 5 dell'addendum: l'export Excel riporta la percentuale complessiva sulle intestazioni di
    /// colonna/riga e il totale generale, stesso formato testuale della cella.</summary>
    [Fact]
    public void ExportExcel_IntestazioniConPercentuale()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoomTotaliMatriceTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = TestStateFactory.Create();
            Directory.CreateDirectory(dir);
            state.InizializzaCartella(dir);
            var mario = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", "Mario", new DateTime(2026, 1, 1), Esito.Conforme);
            state.TestCases.Save(mario, true);

            var bytes = ReportService.GeneraExcel(new ReportFilter(), state, "Operatore Prova A", ConfigEcuAssegnatario);
            using var workbook = new XLWorkbook(new MemoryStream(bytes));
            var foglio = workbook.Worksheet("Matrice configurabile");

            // Riga 4 (intestazioni): la colonna "BCM" riporta anche la distribuzione Stato % ("OK 100%") su una seconda riga di testo.
            var intestazioneBcm = foglio.CellsUsed().Select(c => c.GetString()).Single(v => v.Contains("BCM"));
            Assert.Contains("OK 100%", intestazioneBcm);
            // L'angolo (riga 4, colonna 1) riporta il totale generale.
            var angolo = foglio.Cell(4, 1).GetString();
            Assert.Contains("Totale:", angolo);
            Assert.Contains("OK 100%", angolo);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort, come gli altri test */ }
        }
    }

    /// <summary>Rilievo BASSO della revisione: l'export PDF non è ispezionabile in testo in questo progetto
    /// (nessuna libreria di estrazione, stesso limite già documentato in ReportGenerationTests), quindi si
    /// verifica il MODELLO DATI che PdfReportService passa al renderer (stesso RisultatoMatriceReport prodotto
    /// da ReportMatriceService.Genera, letto identicamente da Excel e PDF — vedi PdfReportService.GeneraPdf):
    /// TotaliColonna resta allineato indice-per-indice a Colonne anche con più di 6 colonne (la soglia
    /// `colonnePerPagina` con cui PdfReportService pagina le colonne in più pagine PDF), e la generazione con
    /// quella stessa configurazione non lancia (esercita il percorso di paginazione TotaliColonna.Skip/Take).</summary>
    [Fact]
    public void ExportPdf_ModelloDatiTotaliColonna_RestaAllineatoAllaPaginaDiColonne()
    {
        const int numeroColonne = 8; // > colonnePerPagina (6) in PdfReportService: forza due pagine PDF.
        var casi = Enumerable.Range(0, numeroColonne)
            .Select(i => CreaTest($"T-{i}", "Lettura guasto", $"Componente-{i:D2}", "Daily A", "Mario", new DateTime(2026, 1, 1), Esito.Conforme))
            .ToList();
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Assegnatario };

        var risultato = ReportMatriceService.Genera(casi, config, [], [], []);

        Assert.Equal(numeroColonne, risultato.Colonne.Count);
        Assert.Equal(numeroColonne, risultato.TotaliColonna.Count);
        for (var i = 0; i < numeroColonne; i++)
        {
            // Ogni colonna ha esattamente un Test Conforme: il totale della colonna i-esima deve riportare
            // distribuzione Stato % "tutto OK" (un solo conforme su uno), stesso indice della colonna i-esima in Colonne.
            Assert.Equal(new StatiMatrice(1, 0, 0, 0, 0, 0), risultato.TotaliColonna[i].StatiPercentuale);
        }

        var dir = Path.Combine(Path.GetTempPath(), "TestLoomTotaliMatricePdfTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = TestStateFactory.Create();
            Directory.CreateDirectory(dir);
            state.InizializzaCartella(dir);
            foreach (var tc in casi) state.TestCases.Save(tc, true);

            var bytes = PdfReportService.GeneraPdf(new ReportFilter(), state, "Operatore Prova A", config);
            Assert.True(bytes.Length > 0); // esercita PdfReportService.GeneraPdf con TotaliColonna paginato su 2 pagine
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>Punto 6 dell'addendum: la marcatura N.A. è stato locale riapplicato "a ogni visita" — un
    /// cambio di marcatura aggiorna i totali anche a dati (Test) invariati, e il Report rivisitato più volte
    /// senza modifiche ai dati mostra sempre gli stessi totali corretti (idempotenza).</summary>
    [Fact]
    public void Totali_InvalidazioneSuCambioMarcaturaNA_ERiportivisitaADatiInvariati()
    {
        var mario = CreaTest("T-1", "Lettura guasto", "BCM", "Daily A", "Mario", new DateTime(2026, 1, 1), Esito.Conforme);
        var luigi = CreaTest("T-2", "Lettura guasto", "BCM", "Daily A", "Luigi", new DateTime(2026, 1, 2), Esito.Conforme);
        var casi = new[] { mario, luigi };

        // Prima "visita": nessuna marcatura N.A., la combinazione conta.
        var senzaNa = ReportMatriceService.Genera(casi, ConfigEcuAssegnatario, [], [], []);
        Assert.Equal(1, senzaNa.TotaleGenerale.Valutati);

        // Marca N.A. una cella che non è del rappresentante corrente (Luigi): la combinazione resta contata e la riga di
        // Luigi non cambia (contratto stato corrente: una sola cella per combinazione).
        var soloMarioNa = Marcature(("BCM", "Mario"));
        var conNaSoloMario = ReportMatriceService.Genera(casi, ConfigEcuAssegnatario, [], [], [], soloMarioNa);
        Assert.Equal(1, conNaSoloMario.TotaleGenerale.Valutati); // ancora contata via Luigi
        Assert.Equal(1, conNaSoloMario.Righe.Single(r => r.Riga == "Luigi").TotaleRiga.Valutati);

        // Marca N.A. ANCHE la riga di Luigi: ora la combinazione è interamente N.A., il totale generale la esclude.
        var entrambeNa = Marcature(("BCM", "Mario"), ("BCM", "Luigi"));
        var conEntrambeNa = ReportMatriceService.Genera(casi, ConfigEcuAssegnatario, [], [], [], entrambeNa);
        Assert.Equal(0, conEntrambeNa.TotaleGenerale.Valutati);

        // "Report rivisitato a dati invariati": richiamare Genera con lo STESSO stato di marcatura (nessuna
        // modifica ai Test) riproduce esattamente lo stesso totale — nessuna cache stantia nascosta altrove.
        var rivisitato = ReportMatriceService.Genera(casi, ConfigEcuAssegnatario, [], [], [], soloMarioNa);
        Assert.Equal(conNaSoloMario.TotaleGenerale.Testo, rivisitato.TotaleGenerale.Testo);
    }
}

/// <summary>Revisione indipendente 2026-09-26, rilievo MEDIO: MatricePreparata.CalcolaTotali (O(combinazioni
/// core)) veniva rieseguito ad OGNI clic di paginazione riga/colonna (Finestra() chiamato una volta per clic
/// da ReportViewModel), perché il vecchio predicato N.A. non era confrontabile fra due chiamate. Ora
/// MatricePreparata.Finestra confronta la FIRMA dell'elenco marcature con quella dell'ultima chiamata e riusa
/// i Totali se non è cambiata — questi test verificano il comportamento della cache tramite PerfContatori
/// (IncrementaTotaliMatriceRicalcolati/Riusati), non solo il risultato numerico.</summary>
public sealed class ReportMatriceTotaliCacheTests
{
    private readonly ITestOutputHelper _output;
    public ReportMatriceTotaliCacheTests(ITestOutputHelper output) => _output = output;

    private static readonly ConfigurazioneMatriceReport Config = new() { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Assegnatario };

    private static TestCase CreaTest(string id, string componente, string assegnatario) => new()
    {
        Id = id, Componente = new EcuSelection([componente]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"], Assegnatario = assegnatario
    };

    /// <summary>Due Finestra() successive con finestre DIVERSE (simula il clic "pagina successiva") ma la
    /// STESSA marcatura N.A. (qui: nessuna) riusano i Totali già calcolati — un solo ricalcolo, non due.</summary>
    [Fact]
    public void Finestra_PaginazionePuraStessaMarcatura_RiusaITotaliSenzaRicalcolare()
    {
        var preparata = ReportMatriceService.Prepara(
            [CreaTest("T-1", "BCM", "Mario"), CreaTest("T-2", "EDC", "Luigi")], Config, [], [], []);

        PerfContatori.Reset();
        preparata.Finestra(0, 1, 0, 1); // "pagina" 1
        preparata.Finestra(0, 1, 1, 1); // "pagina" 2 (colonna diversa, stessa marcatura: nessuna)
        var snapshot = PerfContatori.LeggiSnapshot();

        Assert.Equal(1, snapshot.TotaliMatriceRicalcolati);
        Assert.Equal(1, snapshot.TotaliMatriceRiusati);
    }

    /// <summary>Cambiare l'elenco di marcature N.A. fra due chiamate forza un ricalcolo (mai una cache
    /// stantia): qui la seconda chiamata marca una cella in più rispetto alla prima.</summary>
    [Fact]
    public void Finestra_CambioMarcaturaNA_ForzaIlRicalcolo()
    {
        var preparata = ReportMatriceService.Prepara(
            [CreaTest("T-1", "BCM", "Mario"), CreaTest("T-2", "EDC", "Luigi")], Config, [], [], []);

        PerfContatori.Reset();
        preparata.Finestra(0, 2, 0, 2, []);
        preparata.Finestra(0, 2, 0, 2, [new CellaNonApplicabile { ValoreAsseX = "BCM", ValoreAsseY = "Mario" }]);
        var snapshot = PerfContatori.LeggiSnapshot();

        Assert.Equal(2, snapshot.TotaliMatriceRicalcolati);
        Assert.Equal(0, snapshot.TotaliMatriceRiusati);
    }

    /// <summary>Micro-benchmark in-process (non un gate CI, solo evidenza numerica per la revisione): costo di
    /// CalcolaTotali su una matrice con molte combinazioni core, a confronto fra un ricalcolo "a freddo" e una
    /// chiamata successiva con la stessa marcatura (riuso di cache) — quest'ultima deve restare nell'ordine
    /// dei microsecondi, non more proporzionale al numero di combinazioni.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void MicroBenchmark_CalcolaTotali_RicalcoloVsRiuso()
    {
        const int numeroCombinazioni = 5_000;
        var casi = Enumerable.Range(0, numeroCombinazioni)
            .Select(i => CreaTest($"T-{i:D5}", $"Componente-{i:D5}", $"Op-{i % 200:D3}"))
            .ToList();
        var preparata = ReportMatriceService.Prepara(casi, Config, [], [], []);
        var marcature = new List<CellaNonApplicabile> { new() { ValoreAsseX = "Componente-00000", ValoreAsseY = "Op-000" } };

        // Riscalda la JIT sul percorso di ricalcolo prima di misurare.
        preparata.Finestra(0, 1, 0, 1, marcature);

        var swRicalcolo = Stopwatch.StartNew();
        // Nuova MatricePreparata: nessuna cache pregressa, forza il ricalcolo completo.
        var preparataFredda = ReportMatriceService.Prepara(casi, Config, [], [], []);
        preparataFredda.Finestra(0, 1, 0, 1, marcature);
        swRicalcolo.Stop();

        var swRiuso = Stopwatch.StartNew();
        preparataFredda.Finestra(0, 1, 1, 1, marcature); // stessa marcatura, finestra diversa: riuso
        swRiuso.Stop();

        _output.WriteLine($"BENCHMARK|CalcolaTotali|combinazioni={numeroCombinazioni}|ricalcolo_ms={swRicalcolo.Elapsed.TotalMilliseconds:F3}|riuso_ms={swRiuso.Elapsed.TotalMilliseconds:F3}");
        Assert.True(swRiuso.Elapsed < swRicalcolo.Elapsed,
            $"Il riuso ({swRiuso.Elapsed.TotalMilliseconds:F3}ms) dovrebbe essere piu' rapido del ricalcolo ({swRicalcolo.Elapsed.TotalMilliseconds:F3}ms) su {numeroCombinazioni} combinazioni.");
    }
}
