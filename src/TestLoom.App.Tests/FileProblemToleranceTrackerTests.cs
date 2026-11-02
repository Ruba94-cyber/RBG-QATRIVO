using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Multi-PC 6.4.2: un refresh periodico o un evento watcher possono osservare un JSON OneDrive/SharePoint a
/// metà scrittura. Prima di FileProblemToleranceTracker un solo errore di lettura bastava a far comparire
/// l'avviso "File con problemi", anche se il file si sistemava da solo un istante dopo. Questi test verificano
/// direttamente la soglia (>= 2 tentativi consecutivi, >= 2 minuti dal primo errore) e i casi limite.
/// </summary>
public sealed class FileProblemToleranceTrackerTests : IDisposable
{
    public void Dispose() => FileProblemToleranceTracker.NowUtcOverrideForTests = null;

    private static FileConProblemi Problema(string percorso, string errore = "json troncato") =>
        new() { Percorso = percorso, Errore = errore, RilevatoIl = DateTime.Now };

    [Fact]
    public void UnSingoloErrore_NonEntraSubitoNellElencoVisibile()
    {
        var now = DateTime.UtcNow;
        FileProblemToleranceTracker.NowUtcOverrideForTests = () => now;
        var tracker = new FileProblemToleranceTracker();

        var visibili = tracker.AggiornaCompleto([Problema("c:/dati/testcase_T-0001.json")]);

        Assert.Empty(visibili);
    }

    [Fact]
    public void SecondoTentativoPrimaDeiDueMinuti_RestaTollerato()
    {
        var now = DateTime.UtcNow;
        FileProblemToleranceTracker.NowUtcOverrideForTests = () => now;
        var tracker = new FileProblemToleranceTracker();
        var path = "c:/dati/testcase_T-0001.json";

        tracker.AggiornaCompleto([Problema(path)]);
        now = now.AddSeconds(30); // secondo tentativo consecutivo, ma sotto MinElapsed
        var visibili = tracker.AggiornaCompleto([Problema(path)]);

        Assert.Empty(visibili);
    }

    [Fact]
    public void DueTentativiConsecutiviOltreDueMinuti_DiventaVisibileConPrimoErroreComeRilevatoIl()
    {
        var now = DateTime.UtcNow;
        FileProblemToleranceTracker.NowUtcOverrideForTests = () => now;
        var tracker = new FileProblemToleranceTracker();
        var path = "c:/dati/testcase_T-0001.json";

        tracker.AggiornaCompleto([Problema(path, "primo errore")]);
        now = now.AddMinutes(3);
        var visibili = tracker.AggiornaCompleto([Problema(path, "secondo errore")]);

        var voce = Assert.Single(visibili);
        Assert.Equal(path, voce.Percorso);
        // RilevatoIl riflette il PRIMO errore osservato, non l'ultimo giro: l'utente vede da quanto è rotto.
        Assert.Equal(now.AddMinutes(-3).ToLocalTime(), voce.RilevatoIl, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void FileTornatoLeggibile_EsceDalTrackingEUnNuovoErroreRipartaDaZero()
    {
        var now = DateTime.UtcNow;
        FileProblemToleranceTracker.NowUtcOverrideForTests = () => now;
        var tracker = new FileProblemToleranceTracker();
        var path = "c:/dati/testcase_T-0001.json";

        tracker.AggiornaCompleto([Problema(path)]);
        now = now.AddMinutes(3);
        Assert.NotEmpty(tracker.AggiornaCompleto([Problema(path)]));

        // Il file torna leggibile: assente dall'elenco grezzo di questo giro.
        var risolto = tracker.AggiornaCompleto([]);
        Assert.Empty(risolto);

        // Un nuovo errore sullo stesso path è un episodio nuovo: di nuovo tollerato, non subito visibile.
        var nuovoEpisodio = tracker.AggiornaCompleto([Problema(path)]);
        Assert.Empty(nuovoEpisodio);
    }

    [Fact]
    public void AggiornaIncrementale_NonToccaIPathNonRiverificatiInQuestoGiro()
    {
        var now = DateTime.UtcNow;
        FileProblemToleranceTracker.NowUtcOverrideForTests = () => now;
        var tracker = new FileProblemToleranceTracker();
        var rotto = "c:/dati/testcase_T-0001.json";
        var altro = "c:/dati/testcase_T-0002.json";

        tracker.AggiornaIncrementale([], [Problema(rotto)]);
        now = now.AddMinutes(3);
        var visibili = tracker.AggiornaIncrementale([], [Problema(rotto)]);
        Assert.Contains(visibili, p => p.Percorso == rotto);

        // Un giro incrementale che tocca SOLO un altro path non deve far sparire ne' rinnovare "rotto".
        var altroVisibile = tracker.AggiornaIncrementale([altro], []);
        Assert.Contains(altroVisibile, p => p.Percorso == rotto);
    }

    [Fact]
    public void AggiornaIncrementale_PercorsoConfermatoOk_EsceDalTracking()
    {
        var now = DateTime.UtcNow;
        FileProblemToleranceTracker.NowUtcOverrideForTests = () => now;
        var tracker = new FileProblemToleranceTracker();
        var path = "c:/dati/testcase_T-0001.json";

        tracker.AggiornaIncrementale([], [Problema(path)]);
        now = now.AddMinutes(3);
        Assert.NotEmpty(tracker.AggiornaIncrementale([], [Problema(path)]));

        var risolto = tracker.AggiornaIncrementale([path], []);
        Assert.Empty(risolto);
        Assert.DoesNotContain(path, tracker.PercorsiTracciati());
    }
}
