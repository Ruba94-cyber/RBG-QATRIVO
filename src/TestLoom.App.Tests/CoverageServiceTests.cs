using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public class CoverageServiceTests
{
    private static TestCase ConRequisiti(Esito? ultimoEsito, params string[] requisiti)
    {
        var tc = new TestCase { Id = Guid.NewGuid().ToString(), RequisitoId = requisiti.ToList() };
        if (ultimoEsito.HasValue)
            tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Esito = ultimoEsito.Value, Data = DateTime.Now, Matricola = "Matricola" });
        return tc;
    }

    [Fact]
    public void EsitoCorrente_PrendeLEsecuzionePiuRecenteTraPiuTestCase()
    {
        var vecchio = new TestCase { Id = "A" };
        vecchio.Esecuzioni.Add(new Esecuzione { Esito = Esito.NonConforme, Data = new DateTime(2026, 1, 1), Matricola = "V" });
        var recente = new TestCase { Id = "B" };
        recente.Esecuzioni.Add(new Esecuzione { Esito = Esito.Conforme, Data = new DateTime(2026, 6, 1), Matricola = "V" });

        var esito = CoverageService.EsitoCorrente(new[] { vecchio, recente });

        Assert.Equal(Esito.Conforme, esito);
    }

    [Fact]
    public void EsitoCorrente_NullSeNessunTestHaEsecuzioni()
    {
        var senzaEsecuzioni = new TestCase { Id = "X" };
        Assert.Null(CoverageService.EsitoCorrente(new[] { senzaEsecuzioni }));
    }

    [Fact]
    public void PerRequisito_ConformeConDeviazioni_NonContaComeConformeNeComeNonTestato()
    {
        var requisiti = new List<Requisito> { new() { Id = "R1" } };
        var testCases = new List<TestCase> { ConRequisiti(Esito.ConformeConDeviazioni, "R1") };

        var bucket = CoverageService.PerRequisito(requisiti, testCases, soloBanco: false, soloVeicolo: false);

        Assert.Equal(0, bucket.Conformi);
        Assert.Equal(1, bucket.ConformiConDeviazioni);
        Assert.Equal(0, bucket.NonTestati);
        // ma conta come "completato" ai fini della percentuale
        Assert.Equal(100.0, bucket.PercentualeCompletamento);
    }

    [Fact]
    public void PerRequisito_RequisitoArchiviato_EscludeDalConteggio()
    {
        var requisiti = new List<Requisito> { new() { Id = "R1", Archiviata = true } };
        var testCases = new List<TestCase> { ConRequisiti(Esito.Conforme, "R1") };

        var bucket = CoverageService.PerRequisito(requisiti, testCases, soloBanco: false, soloVeicolo: false);

        Assert.Equal(0, bucket.Totale);
    }

    [Fact]
    public void PerRequisito_SoloBanco_EscludeRequisitiNonApplicabiliABanco()
    {
        var requisiti = new List<Requisito> { new() { Id = "R1", ApplicabileBanco = false, ApplicabileVeicolo = true } };
        var testCases = new List<TestCase> { ConRequisiti(Esito.Conforme, "R1") };

        var bucket = CoverageService.PerRequisito(requisiti, testCases, soloBanco: true, soloVeicolo: false);

        Assert.Equal(0, bucket.Totale);
    }

    [Fact]
    public void PerRequisito_RequisitoSenzaTest_ContaComeNonTestato()
    {
        var requisiti = new List<Requisito> { new() { Id = "R1" } };
        var bucket = CoverageService.PerRequisito(requisiti, new List<TestCase>(), soloBanco: false, soloVeicolo: false);

        Assert.Equal(1, bucket.NonTestati);
        Assert.Equal(0, bucket.PercentualeCompletamento);
    }

    [Fact]
    public void PerFunzionalita_UsaSoloUltimoTentativoDellaCatena()
    {
        var radice = new Condizione { Funzionalita = "A", Esito = Esito.NonConforme };
        var ripetizione = new Condizione { Funzionalita = "B", Esito = Esito.Conforme, OperazioneOrigineUid = radice.Uid };
        var tc = new TestCase { Funzionalita = { "A", "B" } };
        tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.Now, Esito = Esito.Conforme, Condizioni = { radice, ripetizione } });

        var risultato = CoverageService.PerFunzionalita(
            new[] { new Funzionalita { Nome = "A" }, new Funzionalita { Nome = "B" } }, new[] { tc });

        Assert.Equal(1, risultato.Conformi);
        Assert.Equal(1, risultato.NonTestati);
    }

    [Fact]
    public void PerFunzionalita_OperazioneDaEseguireMescolataAConforme_NonDominaSulConforme()
    {
        // Decisione utente 6.4.0, punto 1: prima del filtro EEseguito() in CoverageService, "Da eseguire" (o
        // il legacy NonEseguitoNA) aveva gravità numericamente inferiore a Conforme e avrebbe abbassato
        // l'esito calcolato per la Funzionalità anche con un'operazione realmente eseguita presente.
        var eseguita = new Condizione { Funzionalita = "A", Esito = Esito.Conforme };
        var nonEseguita = new Condizione { Funzionalita = "A", Esito = Esito.DaEseguire };
        var tc = new TestCase { Funzionalita = { "A" } };
        tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.Now, Condizioni = { eseguita, nonEseguita } });

        var risultato = CoverageService.PerFunzionalita(new[] { new Funzionalita { Nome = "A" } }, new[] { tc });

        Assert.Equal(1, risultato.Conformi);
        Assert.Equal(0, risultato.NonTestati);
    }

    [Fact]
    public void PerFunzionalita_SoloOperazioniDaEseguire_ContaComeNonTestato()
    {
        var tc = new TestCase { Funzionalita = { "A" } };
        tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.Now, Condizioni = { new Condizione { Funzionalita = "A", Esito = Esito.DaEseguire } } });

        var risultato = CoverageService.PerFunzionalita(new[] { new Funzionalita { Nome = "A" } }, new[] { tc });

        Assert.Equal(0, risultato.Conformi);
        Assert.Equal(0, risultato.ConformiConDeviazioni);
        Assert.Equal(0, risultato.NonConformi);
        Assert.Equal(1, risultato.NonTestati);
    }

    [Fact]
    public void PerEcu_IncludeOgniEcuDellaSelezioneMultipla()
    {
        var tc = new TestCase { Id = "X", Componente = new EcuSelection(new[] { "Componente-A", "Componente-B" }) };
        tc.Esecuzioni.Add(new Esecuzione { Esito = Esito.Conforme, Matricola = "V" });

        var bucket = CoverageService.PerEcu(new[] { tc }, "Componente-B");

        Assert.Equal(1, bucket.Conformi);
    }

    [Fact]
    public void PunteggioCorrentePerRequisito_PrendeIlPunteggioDellEsecuzionePiuRecente()
    {
        var vecchia = new TestCase { Id = "A", RequisitoId = { "R1" } };
        vecchia.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 1, 1), Esito = Esito.NonConforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.NonConforme } } });
        var recente = new TestCase { Id = "B", RequisitoId = { "R1" } };
        recente.Esecuzioni.Add(new Esecuzione { Data = new DateTime(2026, 6, 1), Esito = Esito.Conforme, Matricola = "V", PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = "R1", Valore = PunteggioConformita.Conforme } } });

        var punteggio = CoverageService.PunteggioCorrentePerRequisito("R1", new[] { vecchia, recente });

        Assert.Equal(PunteggioConformita.Conforme, punteggio);
    }

    [Fact]
    public void PunteggioCorrentePerRequisito_NullSeNessunaEsecuzioneHaUnPunteggioPerQuelRequisito()
    {
        var tc = new TestCase { Id = "A", RequisitoId = { "R1" } };
        tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.Now, Esito = Esito.Conforme, Matricola = "V" });

        Assert.Null(CoverageService.PunteggioCorrentePerRequisito("R1", new[] { tc }));
    }

}
