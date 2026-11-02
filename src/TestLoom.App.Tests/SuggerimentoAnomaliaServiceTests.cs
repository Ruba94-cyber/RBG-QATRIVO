using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Regole del suggerimento "stessa anomalia" (funzione pura, nessuno stato): positivi, negativi e pareggi.
/// Il pareggio non suggerisce nulla di proposito: meglio creare una NC nuova che collegare quella sbagliata.</summary>
public sealed class SuggerimentoAnomaliaServiceTests
{
    private const string Titolo = "Lettura guasto (BCM): Risposta errata";
    private static readonly Guid RadiceA = Guid.NewGuid();
    private static readonly Guid RadiceB = Guid.NewGuid();

    private static OperazioneAnomalia Op(string componente, string funzionalita, Guid? radice = null) => new(radice, componente, funzionalita);

    private static CandidataAnomalia Cand(string id, string descrizione, bool stessoTest = false, params OperazioneAnomalia[] operazioni) =>
        new(Guid.NewGuid(), id, descrizione, stessoTest, operazioni);

    private static SuggerimentoAnomalia? Migliore(OperazioneAnomalia nuova, string titolo, params CandidataAnomalia[] candidate) =>
        SuggerimentoAnomaliaService.Migliore(nuova, titolo, candidate);

    [Fact]
    public void StessaOperazioneNelMedesimoTest_VincePiuDiUnaStessaEcuFunzionalita()
    {
        var nuova = Op("BCM", "Lettura guasto", RadiceA);
        var stessaEcu = Cand("NC-0001", Titolo, stessoTest: true, Op("BCM", "Lettura guasto", RadiceB));
        var stessaOperazione = Cand("NC-0002", Titolo, stessoTest: true, Op("BCM", "Lettura guasto", RadiceA));

        var s = Migliore(nuova, Titolo, stessaEcu, stessaOperazione);

        Assert.NotNull(s);
        Assert.Equal(stessaOperazione.Uid, s!.AnomaliaUid);
        Assert.Equal(MotivoSuggerimentoAnomalia.StessaOperazione, s.Motivo);
    }

    [Fact]
    public void StessoTestConStessaEcuEFunzionalita_SuggeriscePerLeOperazioniCollegate()
    {
        var nuova = Op("BCM", "Lettura guasto", RadiceA);
        var collegata = Cand("NC-0001", "Titolo qualsiasi", stessoTest: true, Op("BCM", "Lettura guasto", RadiceB));

        var s = Migliore(nuova, Titolo, collegata);

        Assert.NotNull(s);
        Assert.Equal(collegata.Uid, s!.AnomaliaUid);
        Assert.Equal(MotivoSuggerimentoAnomalia.StessaEcuFunzionalita, s.Motivo);
    }

    [Fact]
    public void AltroTestConStessaEcuFunzionalitaEProblemaUguale_SuggeriscePerTitolo()
    {
        var nuova = Op("BCM", "Lettura guasto");
        var altra = Cand("NC-0001", "Lettura guasto (BCM): Risposta errata");

        var s = Migliore(nuova, Titolo, altra);

        Assert.NotNull(s);
        Assert.Equal(MotivoSuggerimentoAnomalia.StessaEcuFunzionalitaDescrizioneSimile, s!.Motivo);
        Assert.Equal(1.0, s.Similarita, precision: 9);
    }

    [Fact]
    public void AltroTestConOperazioniSegnalateStessaEcuFunzionalita_SuggeriscePerLaSimilaritaDelProblema()
    {
        var nuova = Op("BCM", "Lettura guasto");
        var rete = Cand("NC-0001", "Risposta errata al comando", operazioni: Op("BCM", "Lettura guasto"));

        var s = Migliore(nuova, Titolo, rete);

        Assert.NotNull(s);
        Assert.Equal(rete.Uid, s!.AnomaliaUid);
        Assert.Equal(MotivoSuggerimentoAnomalia.StessaEcuFunzionalitaDescrizioneSimile, s.Motivo);
    }

    [Fact]
    public void AltroTestConStessaEcuFunzionalitaEProblemaDiverso_NonSuggerisce()
    {
        var nuova = Op("BCM", "Lettura guasto");
        var altra = Cand("NC-0001", "Lettura guasto (BCM): Timeout di comunicazione con il bus");

        Assert.Null(Migliore(nuova, Titolo, altra));
    }

    [Theory]
    [InlineData("BCM2", "Lettura guasto")]
    [InlineData("BCM", "Scrittura guasto")]
    public void EcuOFunzionalitaDiverse_NonSuggerisceNemmenoConDescrizioneUguale(string componente, string funzionalita)
    {
        var nuova = Op("BCM", "Lettura guasto");
        var altra = Cand("NC-0001", $"{funzionalita} ({componente}): Risposta errata", operazioni: Op(componente, funzionalita));

        Assert.Null(Migliore(nuova, Titolo, altra));
    }

    [Fact]
    public void OperazioneSenzaFunzionalita_NonCercaPerEcuEFunzionalita_MaLaStessaOperazioneVale()
    {
        var altra = Cand("NC-0001", Titolo, operazioni: Op("BCM", "Lettura guasto"));
        Assert.Null(Migliore(Op("BCM", ""), Titolo, altra));

        var stessoTest = Cand("NC-0002", Titolo, stessoTest: true, Op("BCM", "", RadiceA));
        var s = Migliore(Op("BCM", "", RadiceA), Titolo, stessoTest);
        Assert.NotNull(s);
        Assert.Equal(MotivoSuggerimentoAnomalia.StessaOperazione, s!.Motivo);
    }

    [Fact]
    public void DueCandidateStessoTestIndistinguibili_NonSuggerisceNulla()
    {
        var nuova = Op("BCM", "Lettura guasto");
        var prima = Cand("NC-0001", "Lettura guasto (BCM): Risposta errata", stessoTest: true, Op("BCM", "Lettura guasto"));
        var seconda = Cand("NC-0002", "Lettura guasto (BCM): Risposta errata", stessoTest: true, Op("BCM", "Lettura guasto"));

        Assert.Null(Migliore(nuova, Titolo, prima, seconda));
    }

    [Fact]
    public void DueAltriTestConStessoProblema_Pareggio_NonSuggerisceNulla()
    {
        var nuova = Op("BCM", "Lettura guasto");
        var a = Cand("NC-0001", "Lettura guasto (BCM): Risposta errata");
        var b = Cand("NC-0002", "Lettura guasto (BCM): Risposta errata");

        Assert.Null(Migliore(nuova, Titolo, a, b));
    }

    [Fact]
    public void AltriTestConLaStessaEcuFunzionalita_VinceLaDescrizionePiuSimile()
    {
        var nuova = Op("BCM", "Lettura guasto");
        var identica = Cand("NC-0001", "Lettura guasto (BCM): Risposta errata al comando");
        var parziale = Cand("NC-0002", "Lettura guasto (BCM): Risposta errata comando ricevuta valida");

        var s = Migliore(nuova, "Lettura guasto (BCM): Risposta errata al comando", parziale, identica);

        Assert.NotNull(s);
        Assert.Equal(identica.Uid, s!.AnomaliaUid);
    }

    [Fact]
    public void SogliaDiSimilarita_Esatta60PerCento_Suggerisce_Sotto_NonSuggerisce()
    {
        // Tre parole in comune su cinque in totale: 0.6 esatto, quindi ancora sopra la soglia conservativa.
        var nuova = Op("BCM", "Lettura guasto");
        var alSogliaDi = Cand("NC-0001", "Lettura guasto (BCM): risposta errata comando ricevuta valida");
        const string titoloNuovo = "Lettura guasto (BCM): risposta errata comando";
        Assert.Equal(SuggerimentoAnomaliaService.SogliaSimilaritaDescrizione, SuggerimentoAnomaliaService.Similarita("risposta errata comando", "risposta errata comando ricevuta valida"));
        Assert.NotNull(Migliore(nuova, titoloNuovo, alSogliaDi));

        // Tre su sei: 0.5, sotto soglia.
        var sotto = Cand("NC-0002", "Lettura guasto (BCM): risposta errata comando ricevuta valida lunga");
        Assert.Null(Migliore(nuova, titoloNuovo, sotto));
    }

    [Fact]
    public void NessunaCandidata_NonSuggerisceNulla()
    {
        Assert.Null(SuggerimentoAnomaliaService.Migliore(Op("BCM", "Lettura guasto"), Titolo, Array.Empty<CandidataAnomalia>()));
    }

    [Theory]
    [InlineData("Risposta errata al comando", "risposta ERRATA al comando")]
    [InlineData("Lettura funzionalità", "lettura funzionalita")]
    [InlineData("Errore di comunicazione con componente", "errore comunicazione componente")]
    public void Similarita_IgnoraMaiuscoleAccentiPunteggiaturaEParoleComuni(string a, string b)
    {
        Assert.Equal(1.0, SuggerimentoAnomaliaService.Similarita(a, b), precision: 9);
    }

    [Fact]
    public void Similarita_TestiSenzaParoleInComuneOVuoti_E_Zero()
    {
        Assert.Equal(0.0, SuggerimentoAnomaliaService.Similarita("Risposta errata", "Timeout comunicazione"));
        Assert.Equal(0.0, SuggerimentoAnomaliaService.Similarita("", "qualcosa"));
        Assert.Equal(0.0, SuggerimentoAnomaliaService.Similarita(null, null));
    }

    [Theory]
    [InlineData("Lettura guasto (BCM): Risposta errata", "Risposta errata")]
    [InlineData("Lettura guasto (BCM)", "")]
    [InlineData("Lettura guasto (BCM) extra", "Lettura guasto (BCM) extra")]
    [InlineData("Altro titolo: Risposta errata", "Altro titolo: Risposta errata")]
    public void ProblemaDaTitolo_TogliePrefissoFunzionalitaEcu(string titolo, string atteso)
    {
        Assert.Equal(atteso, SuggerimentoAnomaliaService.ProblemaDaTitolo(titolo, "Lettura guasto", "BCM"));
    }
}
