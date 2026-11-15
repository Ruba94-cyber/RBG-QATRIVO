using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Tutti i conflitti (Test, NC, CR, Segnalazioni, copie di file, cataloghi master) stanno SOLO nella scheda Conflitti di
/// Impostazioni: nessuno nella scheda "Accesso e cartella" (cartella condivisa) e nessuno in Diagnostica. La copia di file
/// lasciata dalla sincronizzazione compare nell'elenco della sezione, non nelle cartelle dei dati.</summary>
[Collection("ConflictResolverUi")]
public sealed class ConflittiSezioneDedicataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ConflittiSezione_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private static string Sorgenti()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "TestLoom.App", "Views"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, "src", "TestLoom.App");
    }

    private static string Scheda(string xaml, string intestazione, string successiva)
    {
        var a = xaml.IndexOf(intestazione, StringComparison.Ordinal);
        Assert.True(a >= 0, intestazione);
        var b = xaml.IndexOf(successiva, a, StringComparison.Ordinal);
        Assert.True(b > a, successiva);
        return xaml[a..b];
    }

    [Fact]
    public void SchedaCartellaCondivisa_NonContieneNessunConflitto_LaSchedaConflittiLiContieneTutti()
    {
        var xaml = File.ReadAllText(Path.Combine(Sorgenti(), "Views", "ImpostazioniView.axaml"));
        var cartella = Scheda(xaml, "Header=\"{Binding TabCartella}\"", "Header=\"{Binding TabConflitti}\"");
        var conflitti = Scheda(xaml, "Header=\"{Binding TabConflitti}\"", "Header=\"{Binding TabManutenzione}\"");
        var diagnostica = Scheda(xaml, "Header=\"{Binding TabDiagnostica}\"", "</TabControl>");

        foreach (var marcatore in new[] { "ForkSyncTitolo", "RisolviForkJournalCommand", "ForkJournal", "RecordFork_", "MasterConflict_", "ConflittoCopia_" })
            Assert.DoesNotContain(marcatore, cartella);
        foreach (var marcatore in new[] { "ForkSyncTitolo", "RisolviForkJournalCommand", "RecordFork_Card", "MasterConflict_Card", "ConflittoCopia_Card",
                     "RisolviRecordForkConEditorCommand", "RisolviConflittoMasterConEditorCommand", "RisolviConflittoCopiaCommand" })
            Assert.Contains(marcatore, conflitti);
        Assert.DoesNotContain("CopieDiConflitto", diagnostica);
        Assert.DoesNotContain("HaCopieDiConflitto", xaml);
    }

    [Fact]
    public async Task CopiaDiConflitto_RitiratadallaCartella_CompareNellElencoDellaSezioneConflitti()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Master", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        state.NonConformita.Save(new NonConformita { Id = "NC-SEZ-1", Owner = "A", Descrizione = "x", Severita = SeveritaAnomalia.Medium, StatoWorkflow = StatoAnomalia.Confermata, DataApertura = DateTime.Now });
        var nc = state.NonConformita.GetById("NC-SEZ-1")!;
        var canonico = state.NonConformita.FilePathOf(nc);
        var altra = EntityStateFingerprint.Clone(nc);
        altra.Owner = "B";
        var copia = canonico.Replace(".json", " (Laptop).json", StringComparison.OrdinalIgnoreCase);
        JsonIo.WriteAtomic(copia, altra);

        var vm = new ImpostazioniViewModel(state, () => { });
        vm.AggiornaConflittiCopieCommand.Execute(null);
        for (var i = 0; i < 100 && !vm.HaConflittiCopie; i++) await Task.Delay(50);

        Assert.True(vm.HaConflittiCopie);
        var riga = Assert.Single(vm.ConflittiCopie);
        Assert.Contains("NC-SEZ-1", riga.Titolo);
        Assert.True(riga.Risolvibile);
        Assert.False(File.Exists(copia)); // fuori dalla cartella condivisa dei record
        Assert.DoesNotContain(WorkspaceOperationalFileEnumerator.EnumerateManagedJson(_dir), f => f.Contains("(Laptop)", StringComparison.Ordinal));
    }
}
