using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Copertura della sola matematica/throttling di BootstrapProgress(Reporter), indipendente da AppState e da
/// qualunque I/O: la schermata di avvio (feedback utente: "dovrebbe dirmi anche quanto tempo manca alla
/// fine") dipende da questa stima essendo coerente con il ritmo osservato, mai una percentuale inventata.
/// </summary>
public class BootstrapProgressTests
{
    [Fact]
    public void StimaRimanente_RitmoCostante_ProiettaLineareSulResiduo()
    {
        // 50 elementi in 5s => 10 elementi/s; restano 150 elementi => 15s attesi.
        var stima = BootstrapProgressReporter.StimaRimanente(50, 200, TimeSpan.FromSeconds(5));

        Assert.NotNull(stima);
        Assert.Equal(TimeSpan.FromSeconds(15), stima!.Value);
    }

    [Theory]
    [InlineData(0, 200)]     // niente ancora elaborato: nessun ritmo osservabile.
    [InlineData(-5, 200)]    // difensivo: valore inatteso, mai una stima da un negativo.
    [InlineData(200, 200)]   // già finito: zero da stimare.
    [InlineData(250, 200)]   // incoerente (non dovrebbe accadere): mai proiettare comunque.
    public void StimaRimanente_SenzaRitmoUtilizzabile_NonRestituisceValore(int elaborati, int totale)
    {
        var stima = BootstrapProgressReporter.StimaRimanente(elaborati, totale, TimeSpan.FromSeconds(5));
        Assert.Null(stima);
    }

    [Fact]
    public void StimaRimanente_TotaleOElaboratiIgnoti_NonInventaUnaStima()
    {
        Assert.Null(BootstrapProgressReporter.StimaRimanente(null, 200, TimeSpan.FromSeconds(5)));
        Assert.Null(BootstrapProgressReporter.StimaRimanente(50, null, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void StimaRimanente_TrascorsoNulloONegativo_NonRestituisceValore()
    {
        Assert.Null(BootstrapProgressReporter.StimaRimanente(50, 200, TimeSpan.Zero));
        Assert.Null(BootstrapProgressReporter.StimaRimanente(50, 200, TimeSpan.FromSeconds(-1)));
    }

    private sealed class SinkDiSpia : IProgress<BootstrapProgress>
    {
        public readonly List<BootstrapProgress> Ricevuti = new();
        public void Report(BootstrapProgress value) => Ricevuti.Add(value);
    }

    [Fact]
    public void Nessuno_NonInvocaAlcunSink_EdENoOp()
    {
        // BootstrapProgressReporter.Nessuno è quello usato da AppState per ogni refresh dopo il primo bootstrap:
        // deve essere sicuro chiamarlo quante volte si vuole, senza eccezioni e senza alcun sink da invocare.
        var reporter = BootstrapProgressReporter.Nessuno;
        Assert.False(reporter.Attivo);

        var eccezione = Record.Exception(() =>
        {
            reporter.Fase(FaseBootstrap.CaricamentoTest, "irrilevante");
            reporter.Avanzamento(FaseBootstrap.CaricamentoTest, "irrilevante", 1, 10);
        });
        Assert.Null(eccezione);
    }

    [Fact]
    public void Fase_NonESoggettaAlThrottling_OgniCambioFaseArrivaSempre()
    {
        var spia = new SinkDiSpia();
        var reporter = new BootstrapProgressReporter(spia);

        // Due cambi di fase consecutivi, senza attese: un cambio di fase deve sempre arrivare all'utente anche
        // se arrivano più veloci dell'intervallo minimo di throttling degli Avanzamento().
        reporter.Fase(FaseBootstrap.VerificaWorkspace, "fase 1");
        reporter.Fase(FaseBootstrap.CaricamentoTest, "fase 2");

        Assert.Equal(2, spia.Ricevuti.Count);
        Assert.Equal(FaseBootstrap.VerificaWorkspace, spia.Ricevuti[0].Fase);
        Assert.Equal(FaseBootstrap.CaricamentoTest, spia.Ricevuti[1].Fase);
    }

    [Fact]
    public void Avanzamento_RaffficaRavvicinata_VieneAccorpataDalThrottling()
    {
        var spia = new SinkDiSpia();
        var reporter = new BootstrapProgressReporter(spia);

        // 500 notifiche di avanzamento sparate senza pause: senza throttling sommergerebbero il dispatcher UI
        // (è esattamente lo scenario reale a 100k file). Devono arrivare molte meno chiamate al sink.
        for (var i = 1; i <= 500; i++)
            reporter.Avanzamento(FaseBootstrap.CaricamentoTest, "ricostruzione", i, 500);

        Assert.True(spia.Ricevuti.Count < 500,
            $"Il throttling non ha accorpato le notifiche: arrivate {spia.Ricevuti.Count} su 500 invocazioni.");
    }

    [Fact]
    public void Avanzamento_ContieneStimaCoerenteConIlRitmoOsservato()
    {
        var spia = new SinkDiSpia();
        var reporter = new BootstrapProgressReporter(spia);

        reporter.Fase(FaseBootstrap.CaricamentoTest, "inizio", 0, 1000);
        Thread.Sleep(200); // trascorso misurabile, oltre l'intervallo minimo di throttling degli Avanzamento().
        reporter.Avanzamento(FaseBootstrap.CaricamentoTest, "meta", 500, 1000);

        var ultimo = spia.Ricevuti[^1];
        Assert.Equal(500, ultimo.Elaborati);
        Assert.Equal(1000, ultimo.Totale);
        Assert.NotNull(ultimo.StimaRimanente);
        Assert.True(ultimo.StimaRimanente!.Value > TimeSpan.Zero);
        // Coerenza debole ma reale: a metà del lavoro, il tempo stimato rimanente non può essere assurdo
        // (ordini di grandezza) rispetto al tempo già trascorso per la stessa quantità di lavoro.
        Assert.True(ultimo.StimaRimanente.Value < TimeSpan.FromSeconds(30));
    }
}
