using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>TIME (6.6.1), review finding 3: semantica del filtro "Tempo operazione" per modalita (Stato e Stato % per combinazione
/// vincente, Test % per Test, Tempo per tentativo o per operazione vincente) e del filtro "Tempo totale Test" a livello di Test (somma
/// di tutti i tentativi eseguiti, ripetizioni e guasto incluse). Ogni modalita ha il suo test.</summary>
public sealed class TempoFiltroSemanticaTests
{
    private static readonly DateTime Giorno = new(2026, 2, 1);

    private static ConfigurazioneMatriceReport Config(ModalitaCellaMatrice modalita, BaseTempoMatrice baseTempo = BaseTempoMatrice.TutteLeEsecuzioni) => new()
    {
        CampiX = [DimensioniMatrice.Componente],
        CampiY = [DimensioniMatrice.Funzionalita],
        ModalitaCella = modalita,
        BaseTempo = baseTempo,
    };

    private static Condizione Op(string funzionalita, string componente, Esito esito, int? minuti, Guid? origine = null, Guid? uid = null) => new()
    {
        Uid = uid ?? Guid.NewGuid(),
        Funzionalita = funzionalita,
        Esito = esito,
        Componente = new EcuSelection([componente]),
        DurataMinuti = minuti,
        OperazioneOrigineUid = origine,
    };

    private static Esecuzione Rev(DateTime data, params Condizione[] condizioni) => new()
    {
        Data = data,
        Operatore = "Op",
        Esito = Esito.Conforme,
        Condizioni = [.. condizioni],
    };

    private static TestCase Test(string id, string[] funzionalita, params Esecuzione[] revisioni)
    {
        var tc = new TestCase
        {
            Id = id,
            Uid = Guid.NewGuid(),
            Componente = new EcuSelection(["BCM"]),
            Veicoli = ["Daily"],
            Funzionalita = [.. funzionalita],
        };
        tc.Esecuzioni.AddRange(revisioni);
        return tc;
    }

    private static RisultatoMatriceReport Genera(ConfigurazioneMatriceReport config, params TestCase[] test) =>
        ReportMatriceService.Genera(test, config, [], [], []);

    private static CellaMatriceReport? CellaOpzionale(RisultatoMatriceReport risultato, string riga, string colonna) =>
        risultato.Righe.SingleOrDefault(r => r.Riga == riga)?.Celle.SingleOrDefault(c => c.Colonna == colonna);

    private static readonly FiltroDurataMinuti OltreQuindici = new(OperatoreDurata.Maggiore, 15);

    // ----- Stato e Stato %: operazione vincente per combinazione, dopo la riduzione -----

    [Theory]
    [InlineData(ModalitaCellaMatrice.Stato)]
    [InlineData(ModalitaCellaMatrice.Percentuale)]
    public void StatoEStatoPercentuale_LaSolaOperazioneVincenteDecide_NonUnTentativoQualsiasi(ModalitaCellaMatrice modalita)
    {
        // Il tentativo da 30 minuti e' superato dal tentativo da 10 (piu' recente): la combinazione F1 x BCM vale 10.
        var vecchio = Test("T-OLD", ["F1"], Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 30)));
        var nuovo = Test("T-NEW", ["F1"], Rev(Giorno.AddDays(5), Op("F1", "BCM", Esito.Conforme, 10)));
        var config = Config(modalita);
        config.FiltroTempoOperazione = OltreQuindici;

        var escluso = CellaOpzionale(Genera(config, vecchio, nuovo), "F1", "BCM");
        Assert.True(escluso is null || escluso.Totali == 0);     // il 30 superato non fa entrare la combinazione

        config.FiltroTempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Minore, 15);
        Assert.Equal(1, CellaOpzionale(Genera(config, vecchio, nuovo), "F1", "BCM")!.Conformi);   // il vincente (10) corrisponde
    }

    [Fact]
    public void Stato_CombinazioneSenzaOperazioneVincente_NonCorrispondeAlFiltroSullaDurata()
    {
        // F2 e' pianificata (Da eseguire, senza durata): la combinazione esiste ma non ha un'operazione eseguita. Senza filtro e' una
        // combinazione "non valutata"; con il filtro non corrisponde.
        var t = Test("T-PARZIALE", ["F1", "F2"], Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 30), Op("F2", "BCM", Esito.DaEseguire, null)));
        var config = Config(ModalitaCellaMatrice.Stato);
        Assert.NotNull(CellaOpzionale(Genera(config, t), "F2", "BCM"));

        config.FiltroTempoOperazione = OltreQuindici;
        var f2 = CellaOpzionale(Genera(config, t), "F2", "BCM");
        Assert.True(f2 is null || f2.Totali == 0);
        Assert.Equal(1, CellaOpzionale(Genera(config, t), "F1", "BCM")!.Conformi);
    }

    [Fact]
    public void Origine_StatoConFiltroSullaDurata_RiportaSoloLeCombinazioniAmmesse()
    {
        // La vista Origine (foglio Origine dell'Excel) segue la matrice: F1 vale 10 (vincente), F2 vale 30. Con il filtro > 15 resta F2.
        var t = Test("T-ORIGINE", ["F1", "F2"], Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 10), Op("F2", "BCM", Esito.Conforme, 30)));
        var config = Config(ModalitaCellaMatrice.Stato);

        Assert.Equal(2, ReportMatriceService.Origine([t], config, [], [], [], 100).Totale);

        config.FiltroTempoOperazione = OltreQuindici;
        var (righe, _, totale) = ReportMatriceService.Origine([t], config, [], [], [], 100);
        var elenco = righe.ToList();
        Assert.Equal(1, totale);
        Assert.Single(elenco);
        Assert.Equal("F2", elenco[0].Funzionalita);
    }

    // ----- Test %: il Test conta se almeno una sua operazione corrente effettiva corrisponde -----

    [Fact]
    public void TestPercentuale_UnTestConta_SeUnaSuaOperazioneCorrenteCorrisponde()
    {
        var t = Test("T-MISTO", ["F1", "F2"], Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 5), Op("F2", "BCM", Esito.Conforme, 50)));
        var config = Config(ModalitaCellaMatrice.TestPercentuale);

        config.FiltroTempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 20);   // la F2 da 50 corrisponde
        Assert.Equal(1, CellaOpzionale(Genera(config, t), "F1", "BCM")!.Conformi);

        config.FiltroTempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 60);   // nessuna operazione corrisponde
        var nessuno = CellaOpzionale(Genera(config, t), "F1", "BCM");
        Assert.True(nessuno is null || nessuno.Valutati == 0);
    }

    // ----- Tempo (base Tutte le esecuzioni): per tentativo; base Ultimo stato: per operazione vincente -----

    [Fact]
    public void TempoTutteLeEsecuzioni_FiltroPerTentativo_LaFunzionalitaCorta_NonEntra()
    {
        var t = Test("T-MISTO", ["F1", "F2"], Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 5), Op("F2", "BCM", Esito.Conforme, 50)));
        var config = Config(ModalitaCellaMatrice.TempoTotale);
        config.FiltroTempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 20);

        var risultato = Genera(config, t);

        Assert.Equal(0, CellaOpzionale(risultato, "F1", "BCM")?.Tempo.Eleggibili ?? 0);   // 5 non supera 20: nessun tentativo
        var f2 = CellaOpzionale(risultato, "F2", "BCM")!.Tempo;
        Assert.Equal(50, f2.SommaMinuti);
        Assert.Equal(1, f2.Eleggibili);
    }

    [Fact]
    public void TempoUltimoStato_FiltroPerOperazioneVincente()
    {
        var vecchio = Test("T-OLD", ["F1"], Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 30)));
        var nuovo = Test("T-NEW", ["F1"], Rev(Giorno.AddDays(5), Op("F1", "BCM", Esito.Conforme, 10)));
        var config = Config(ModalitaCellaMatrice.TempoTotale, BaseTempoMatrice.UltimoStato);
        config.FiltroTempoOperazione = OltreQuindici;

        var f1 = CellaOpzionale(Genera(config, vecchio, nuovo), "F1", "BCM");
        Assert.True(f1 is null || f1.Tempo.Eleggibili == 0);   // il vincente e' 10: il 30 superato non torna
    }

    // ----- Tempo totale Test (livello Test): somma di tutti i tentativi eseguiti, ripetizioni e guasto incluse -----

    [Fact]
    public void TempoTotaleTest_SommaTuttiITentativiEseguiti_LivelloTest()
    {
        var t = Test("T-SOMMA", ["F1", "F2"], Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 20), Op("F2", "BCM", Esito.Conforme, 25)));
        Assert.Equal(45, ReportMatriceService.TotaleMinutiTest(t));

        var sopra = Config(ModalitaCellaMatrice.TempoTotale);
        sopra.FiltroTempoTotaleTest = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 40);
        Assert.Equal(20, CellaOpzionale(Genera(sopra, t), "F1", "BCM")!.Tempo.SommaMinuti);   // il Test entra, la cella resta per operazione

        var sotto = Config(ModalitaCellaMatrice.TempoTotale);
        sotto.FiltroTempoTotaleTest = new FiltroDurataMinuti(OperatoreDurata.Minore, 40);
        var nessuno = CellaOpzionale(Genera(sotto, t), "F1", "BCM");
        Assert.True(nessuno is null || nessuno.Tempo.Eleggibili == 0);
    }

    [Fact]
    public void TempoTotaleTest_RipetizioneNellaStessaRevisione_ContaEntrambeLeProve()
    {
        // Decisione utente: la prova (10, non conforme) e la sua ripetizione (20) nella stessa revisione sono due tentativi eseguiti: 30.
        // Il filtro "Tempo operazione" resta sulle operazioni correnti effettive: la ripetizione (20) e' quella che conta.
        var primo = Op("F1", "BCM", Esito.NonConforme, 10);
        var secondo = Op("F1", "BCM", Esito.Conforme, 20, origine: primo.Uid);
        var t = Test("T-RIPETUTO", ["F1"], Rev(Giorno, primo, secondo));

        Assert.Equal(30, ReportMatriceService.TotaleMinutiTest(t));
        Assert.False(ReportMatriceService.HaOperazioneConDurata(t, new FiltroDurataMinuti(OperatoreDurata.Minore, 11)));   // il 10 superato non corrisponde
        Assert.True(ReportMatriceService.HaOperazioneConDurata(t, new FiltroDurataMinuti(OperatoreDurata.Maggiore, 15)));
        // La cella "Tempo totale" (base Tutte le esecuzioni) somma gli stessi due tentativi: 30, come il filtro sul totale.
        Assert.Equal(30, CellaOpzionale(Genera(Config(ModalitaCellaMatrice.TempoTotale), t), "F1", "BCM")!.Tempo.SommaMinuti);
    }

    [Fact]
    public void TempoTotaleTest_RipetizioneInRevisioneSuccessiva_ContaEntrambeLeProve()
    {
        var primo = Op("F1", "BCM", Esito.Conforme, 20);
        var ripetizione = Op("F1", "BCM", Esito.Conforme, 10, origine: primo.Uid);
        var t = Test("T-PIU-REVISIONI", ["F1"], Rev(Giorno, primo), Rev(Giorno.AddDays(1), ripetizione));

        Assert.Equal(30, ReportMatriceService.TotaleMinutiTest(t));
    }

    [Fact]
    public void TempoTotaleTest_IncludeLeOperazioniDtc()
    {
        var guasto = new Condizione { Funzionalita = "", Esito = Esito.Conforme, Componente = new EcuSelection(["BCM"]), DurataMinuti = 30, Guasto = "P0100" };
        var t = Test("T-guasto", ["F1"], Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 10), guasto));

        Assert.Equal(40, ReportMatriceService.TotaleMinutiTest(t));
        Assert.True(ReportMatriceService.HaOperazioneConDurata(t, new FiltroDurataMinuti(OperatoreDurata.Maggiore, 25)));   // la guasto da 30 corrisponde
    }

    // ----- Completezza: eleggibili = operazioni eseguite (Tutte le esecuzioni) o vincenti eseguite (Ultimo stato); le non testate no -----

    [Fact]
    public void Completezza_TutteLeEsecuzioni_EleggibiliSonoSoloLeOperazioniEseguite()
    {
        // F1 eseguita con 20 minuti; F2 pianificata (Da eseguire) nella stessa revisione: non e' un'operazione eseguita.
        var t = Test("T-COMPLETEZZA", ["F1", "F2"], Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 20), Op("F2", "BCM", Esito.DaEseguire, null)));
        var risultato = Genera(Config(ModalitaCellaMatrice.TempoTotale), t);

        var f1 = CellaOpzionale(risultato, "F1", "BCM")!.Tempo;
        Assert.Equal(1, f1.Eleggibili);
        Assert.Equal(1, f1.Conosciuti);
        Assert.True(f1.Completo);
        var f2 = CellaOpzionale(risultato, "F2", "BCM");
        Assert.True(f2 is null || f2.Tempo.Eleggibili == 0);
    }

    [Fact]
    public void Completezza_UltimoStato_EleggibiliSonoLeVincentiEseguite()
    {
        var t = Test("T-VINCENTI", ["F1", "F2"], Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 20), Op("F2", "BCM", Esito.DaEseguire, null)));
        var risultato = Genera(Config(ModalitaCellaMatrice.TempoTotale, BaseTempoMatrice.UltimoStato), t);

        Assert.Equal(1, CellaOpzionale(risultato, "F1", "BCM")!.Tempo.Eleggibili);
        var f2 = CellaOpzionale(risultato, "F2", "BCM");
        Assert.True(f2 is null || f2.Tempo.Eleggibili == 0);   // una combinazione senza operazione vincente eseguita non e' eleggibile
    }

    [Fact]
    public void Completezza_EseguitaSenzaDurata_ContaEleggibileMaNonNota()
    {
        var t = Test("T-SENZA-DURATA", ["F1"], Rev(Giorno, Op("F1", "BCM", Esito.Conforme, null)));
        var cella = CellaOpzionale(Genera(Config(ModalitaCellaMatrice.TempoMedio), t), "F1", "BCM")!.Tempo;

        Assert.Equal(1, cella.Eleggibili);
        Assert.Equal(0, cella.Conosciuti);
        Assert.False(cella.HaValore);             // blank: non e' uno zero noto
        Assert.False(cella.Completo);
    }

    // ----- Liste e matrice: due predicati diversi, un solo punto in cui si applica la durata per combinazione -----

    [Fact]
    public void ListaEMatrice_HannoPredicatiDiversiPerLaDurataDellaSingolaOperazione()
    {
        var vecchio = Test("T-OLD", ["F1"], Rev(Giorno, Op("F1", "BCM", Esito.Conforme, 10)));
        var nuovo = Test("T-NEW", ["F1"], Rev(Giorno.AddDays(5), Op("F1", "BCM", Esito.Conforme, 30)));
        var filtro = new ReportFilter { TempoOperazione = new FiltroDurataMinuti(OperatoreDurata.Maggiore, 25) };

        Assert.False(filtro.Corrisponde(vecchio));            // in lista: il Test senza operazione sopra 25 non compare
        Assert.True(filtro.CorrispondeMatrice(vecchio));      // in matrice: la durata si decide sulla combinazione, non sul Test

        var config = Config(ModalitaCellaMatrice.Stato);
        config.FiltroTempoOperazione = OltreQuindici;
        var matrice = Genera(config, vecchio, nuovo);
        Assert.Equal(1, CellaOpzionale(matrice, "F1", "BCM")!.Conformi);   // la combinazione vale 30 (vincente, piu' recente) e corrisponde
    }
}
