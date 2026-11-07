using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public sealed class TestCaseEditorQuickModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_QuickMode_" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _opened = new();

    private TestCaseEditorViewModel CreaVm(bool rapido = true)
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var vm = new TestCaseEditorViewModel(state, null, () => { }, _ => { }) { ModoEditorRapido = rapido };
        _opened.Add(state);
        _opened.Add(vm);
        return vm;
    }

    [Fact]
    public void TwoModesPreserveOriginalFullWizardAndRevealAdvancedSections()
    {
        var vm = CreaVm(rapido: false);
        Assert.False(vm.ModoEditorRapido); // complete version is the default
        vm.AttivaModalitaRapidaCommand.Execute(null);
        Assert.True(vm.ModoEditorRapido);
        Assert.False(vm.MostraSchedaDettagli); // template not chosen yet
        Assert.False(vm.MostraSchedaProve);
        vm.MostraSezioneRapida("Pianificazione");
        Assert.False(vm.MostraSchedaPianificazione); // template selection is mandatory before navigation
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.MostraSezioneRapida("Revisioni");
        Assert.False(vm.MostraSchedaRevisioni); // no history for a new, unsaved Test
        Assert.True(vm.MostraSchedaDettagli);
        Assert.True(vm.MostraSchedaProve);
        Assert.False(vm.MostraSchedaPianificazione);
        Assert.False(vm.MostraSchedaRiferimenti);
        vm.MostraSezioneRapida("Riferimenti");
        Assert.True(vm.MostraSchedaRiferimenti);
        vm.AttivaModalitaCompletaCommand.Execute(null);
        Assert.False(vm.ModoEditorRapido);
        Assert.True(vm.MostraSchedaPianificazione);
        Assert.False(vm.MostraSchedaRiferimenti); // wizard starts at the first tab
        Assert.False(vm.EspandiParametriOperazione); // default chiuso anche nella modalità completa
        vm.AttivaModalitaRapidaCommand.Execute(null);
        Assert.True(vm.MostraSchedaRiferimenti); // earlier on-demand choice remains available
        Assert.False(vm.EspandiParametriOperazione);
        vm.ApriParametriPerValidazione();
        Assert.True(vm.EspandiParametriOperazione);
        Assert.False(vm.MostraCronologiaEditor);
    }

    [Fact]
    public void ValidazioneApreIParametriAncheNellaModalitaCompleta()
    {
        var vm = CreaVm(rapido: false);
        Assert.False(vm.EspandiParametriOperazione);
        vm.ApriParametriPerValidazione();
        Assert.True(vm.EspandiParametriOperazione);
        Assert.False(vm.ModificheNonSalvate);
    }

    [Fact]
    public void ModePreferenceSurvivesReopeningEditorWithoutChangingTestData()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        _opened.Add(state);
        var first = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        _opened.Add(first);
        Assert.False(first.ModoEditorRapido); // no stored preference: complete version
        Assert.False(first.ModificheNonSalvate);
        first.ModoEditorRapido = true;
        Assert.False(first.ModificheNonSalvate); // switching editor presentation is not a data edit

        var next = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        _opened.Add(next);
        Assert.True(next.ModoEditorRapido);

        next.ModoEditorRapido = false;
        var secondo = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        _opened.Add(secondo);
        Assert.False(secondo.ModoEditorRapido);
        Assert.False(secondo.EspandiParametriOperazione); // ogni apertura parte collassata

        next.ModoEditorRapido = true;
        var third = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        _opened.Add(third);
        Assert.True(third.ModoEditorRapido);
        // UI mode is not a serialized property of a TestCase.
        Assert.DoesNotContain("ModoEditorRapido",
            System.Text.Json.JsonSerializer.Serialize(third.TestCase));
    }

    [Fact]
    public void PasteCreatesUnexecutedOperationsWithoutCloningOutcomes()
    {
        var vm = CreaVm();
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        var existing = vm.RevisioneCorrente!.Condizioni.Count;
        vm.TestoOperazioniRapide = "First test note\nSecond test note";
        vm.PreparaAnteprimaOperazioniRapideCommand.Execute(null);
        vm.IncollaOperazioniRapideCommand.Execute(null);
        Assert.Equal(existing + 2, vm.RevisioneCorrente.Condizioni.Count);
        Assert.Equal("First test note", vm.RevisioneCorrente.Condizioni[^2].Nota);
        Assert.Equal("Second test note", vm.RevisioneCorrente.Condizioni[^1].Nota);
        Assert.All(vm.RevisioneCorrente.Condizioni.TakeLast(2), x => Assert.Equal(Esito.DaEseguire, x.Esito));
        Assert.True(vm.ModificheNonSalvate);
        Assert.Empty(vm.TestoOperazioniRapide);
    }

    [Fact]
    public async Task QuickOutcomeButtonsUpdateTheSameOperationAndTestProgress()
    {
        var vm = CreaVm();
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        var operazione = vm.RevisioneCorrente!.Condizioni[0];
        var durataPrecedente = operazione.DurataMinuti;
        vm.ModificheNonSalvate = false;
        Assert.False(operazione.RichiedeGestioneSegnalazione);

        await vm.EsitoRapidoNonConformeCommand.ExecuteAsync(operazione);
        Assert.True(operazione.RichiedeGestioneSegnalazione);
        Assert.Equal(Esito.NonConforme, operazione.Esito);
        Assert.True(vm.ModificheNonSalvate);
        Assert.Equal(1, vm.OperazioniRapideEseguite);
        Assert.False(vm.ECambioEsitoRapidoInCorso(operazione));

        await vm.EsitoRapidoDeviazioneCommand.ExecuteAsync(operazione);
        Assert.Equal(Esito.ConformeConDeviazioni, operazione.Esito);
        Assert.True(operazione.RichiedeGestioneSegnalazione);
        await vm.EsitoRapidoConformeCommand.ExecuteAsync(operazione);
        Assert.Equal(Esito.Conforme, operazione.Esito);
        Assert.False(operazione.RichiedeGestioneSegnalazione);
        Assert.False(vm.ECambioEsitoRapidoInCorso(operazione));
        // Quick outcomes do not fabricate duration, descriptions or evidence.
        Assert.Equal(durataPrecedente, operazione.DurataMinuti);
        Assert.Empty(operazione.DescrizioneProblema);
    }

    [Fact]
    public void QuickAnomalyHintDoesNotChangeStoredSchema()
    {
        var condition = new Condizione { Esito = Esito.DaEseguire };
        var notificationCount = 0;
        condition.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Condizione.RichiedeGestioneSegnalazione))
                notificationCount++;
        };
        condition.Esito = Esito.NonConforme;
        Assert.True(condition.RichiedeGestioneSegnalazione);
        condition.Esito = Esito.ConformeConDeviazioni;
        Assert.True(condition.RichiedeGestioneSegnalazione);
        condition.Esito = Esito.Conforme;
        Assert.False(condition.RichiedeGestioneSegnalazione);
        Assert.Equal(3, notificationCount);
        Assert.DoesNotContain("RichiedeGestioneSegnalazione",
            System.Text.Json.JsonSerializer.Serialize(condition));
    }

    [Fact]
    public async Task QuickOutcomeIsNoOpForUnchangedOutcomeAndFullOrReadOnlyModes()
    {
        var vm = CreaVm();
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        var operazione = vm.RevisioneCorrente!.Condizioni[0];
        vm.ModificheNonSalvate = false;

        await vm.EsitoRapidoConformeCommand.ExecuteAsync(operazione);
        Assert.True(vm.ModificheNonSalvate);
        vm.ModificheNonSalvate = false;
        await vm.EsitoRapidoConformeCommand.ExecuteAsync(operazione);
        Assert.False(vm.ModificheNonSalvate);

        vm.AttivaModalitaCompletaCommand.Execute(null);
        await vm.EsitoRapidoNonConformeCommand.ExecuteAsync(operazione);
        Assert.Equal(Esito.Conforme, operazione.Esito);
        Assert.False(vm.ModificheNonSalvate);

        vm.AttivaModalitaRapidaCommand.Execute(null);
        vm.InModifica = false;
        await vm.EsitoRapidoNonConformeCommand.ExecuteAsync(operazione);
        Assert.Equal(Esito.Conforme, operazione.Esito);
        Assert.False(vm.ModificheNonSalvate);
    }

    [Fact]
    public async Task QuickOutcomeEchoIsSuppressedOnceButNeverSuppressesManualDifferentSelection()
    {
        var vm = CreaVm();
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        var operazione = vm.RevisioneCorrente!.Condizioni[0];

        await vm.EsitoRapidoNonConformeCommand.ExecuteAsync(operazione);
        Assert.True(vm.ConsumaEcoEsitoRapido(operazione));
        Assert.False(vm.ConsumaEcoEsitoRapido(operazione));

        await vm.EsitoRapidoDeviazioneCommand.ExecuteAsync(operazione);
        operazione.Esito = Esito.NonApplicabile; // simulate an actual later ComboBox choice
        Assert.False(vm.ConsumaEcoEsitoRapido(operazione));
        Assert.False(vm.ConsumaEcoEsitoRapido(operazione));
    }

    [Fact]
    public async Task QuickOutcomeRejectsDetachedOperationEvenWhenUidMatches()
    {
        var vm = CreaVm();
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        var original = vm.RevisioneCorrente!.Condizioni[0];
        var detached = new Condizione { Uid = original.Uid, Esito = Esito.DaEseguire };
        vm.ModificheNonSalvate = false;

        await vm.EsitoRapidoNonConformeCommand.ExecuteAsync(detached);
        Assert.Equal(Esito.DaEseguire, detached.Esito);
        Assert.Equal(Esito.DaEseguire, original.Esito);
        Assert.False(vm.ModificheNonSalvate);
        Assert.False(vm.ECambioEsitoRapidoInCorso(detached));
    }

    [Fact]
    public void OperationProgressReflectsCurrentRevisionAndOutcomes()
    {
        var vm = CreaVm();
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        var initialTotal = vm.RevisioneCorrente!.Condizioni.Count;
        Assert.Equal(initialTotal, vm.OperazioniRapideTotali);
        Assert.Equal(initialTotal, vm.OperazioniRapideDaEseguire);
        Assert.Equal(0, vm.OperazioniRapideEseguite);

        vm.RevisioneCorrente.Condizioni[0].Esito = Esito.Conforme;
        vm.SincronizzaEsitoCondizioni();
        Assert.Equal(1, vm.OperazioniRapideEseguite);
        Assert.Equal(initialTotal - 1, vm.OperazioniRapideDaEseguire);

        vm.AggiungiCondizioneCommand.Execute(null);
        Assert.Equal(initialTotal + 1, vm.OperazioniRapideTotali);
        Assert.Equal(1, vm.OperazioniRapideEseguite);
        Assert.Equal(initialTotal, vm.OperazioniRapideDaEseguire);
    }

    [Fact]
    public void UndoLastPasteRemovesOnlyNewUnmodifiedOperations()
    {
        var vm = CreaVm();
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        var originals = vm.RevisioneCorrente!.Condizioni.Select(c => c.Uid).ToList();
        vm.TestoOperazioniRapide = "First pasted note\nSecond pasted note";
        vm.PreparaAnteprimaOperazioniRapideCommand.Execute(null);
        vm.IncollaOperazioniRapideCommand.Execute(null);
        Assert.True(vm.PuoAnnullareUltimoIncollaRapido);
        Assert.Equal(originals.Count + 2, vm.RevisioneCorrente.Condizioni.Count);
        vm.AnnullaUltimoIncollaRapidoCommand.Execute(null);
        Assert.False(vm.PuoAnnullareUltimoIncollaRapido);
        Assert.Equal(originals, vm.RevisioneCorrente.Condizioni.Select(c => c.Uid).ToList());
    }

    [Fact]
    public void UndoDoesNotDeleteEditedPastedOperations()
    {
        var vm = CreaVm();
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.TestoOperazioniRapide = "One pasted note";
        vm.PreparaAnteprimaOperazioniRapideCommand.Execute(null);
        vm.IncollaOperazioniRapideCommand.Execute(null);
        var count = vm.RevisioneCorrente!.Condizioni.Count;
        var last = vm.RevisioneCorrente.Condizioni[^1];
        last.Nota = "Operator edited this";
        vm.AnnullaUltimoIncollaRapidoCommand.Execute(null);
        Assert.Equal(count, vm.RevisioneCorrente.Condizioni.Count);
        Assert.Equal("Operator edited this", last.Nota);
    }

    [Fact]
    public void UndoDoesNotOrphanRepeatedAttempts()
    {
        var vm = CreaVm();
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.TestoOperazioniRapide = "Initial attempt";
        vm.PreparaAnteprimaOperazioniRapideCommand.Execute(null);
        vm.IncollaOperazioniRapideCommand.Execute(null);
        var original = vm.RevisioneCorrente!.Condizioni[^1];
        var repeated = new Condizione
        {
            OperazioneOrigineUid = original.Uid,
            Esito = Esito.DaEseguire,
            Nota = "Repeated later"
        };
        vm.RevisioneCorrente.Condizioni.Add(repeated);
        var count = vm.RevisioneCorrente.Condizioni.Count;
        vm.AnnullaUltimoIncollaRapidoCommand.Execute(null);
        Assert.Equal(count, vm.RevisioneCorrente.Condizioni.Count);
        Assert.Contains(original, vm.RevisioneCorrente.Condizioni);
        Assert.Contains(repeated, vm.RevisioneCorrente.Condizioni);
    }

    [Fact]
    public void InvalidQuickEvidenceShowsLocalizedFeedbackWithoutDirtyingTest()
    {
        var vm = CreaVm();
        var warningsBefore = vm.Avvisi.Count;
        var dirtyBefore = vm.ModificheNonSalvate;

        vm.SegnalaScreenshotNonDisponibile();
        vm.SegnalaAllegatiRapidiNonValidi(troppiFile: false);
        vm.SegnalaAllegatiRapidiNonValidi(troppiFile: true);

        Assert.Equal(warningsBefore + 3, vm.Avvisi.Count);
        Assert.All(vm.Avvisi.TakeLast(3), message => Assert.False(string.IsNullOrWhiteSpace(message)));
        Assert.Equal(dirtyBefore, vm.ModificheNonSalvate);
    }

    [Fact]
    public void PreviewIsReadOnlyAndCanBeCancelledWithoutDirtyingTest()
    {
        var vm = CreaVm();
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        var original = vm.RevisioneCorrente!.Condizioni.Count;
        var dirtyBefore = vm.ModificheNonSalvate;
        vm.TestoOperazioniRapide = "First preview\nSecond preview";
        vm.PreparaAnteprimaOperazioniRapideCommand.Execute(null);

        Assert.True(vm.MostraAnteprimaOperazioniRapide);
        Assert.True(vm.PuoConfermareAnteprimaRapida);
        Assert.Equal(2, vm.RigheAnteprimaRapida.Count);
        Assert.Equal(original, vm.RevisioneCorrente.Condizioni.Count);
        Assert.Equal(dirtyBefore, vm.ModificheNonSalvate);

        vm.AnnullaAnteprimaOperazioniRapideCommand.Execute(null);
        Assert.False(vm.MostraAnteprimaOperazioniRapide);
        Assert.False(vm.PuoConfermareAnteprimaRapida);
        Assert.Equal(original, vm.RevisioneCorrente.Condizioni.Count);
        Assert.Equal(dirtyBefore, vm.ModificheNonSalvate);
    }

    [Fact]
    public void InsertionRequiresPreviewAndEditedTextInvalidatesIt()
    {
        var vm = CreaVm();
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        var original = vm.RevisioneCorrente!.Condizioni.Count;
        vm.TestoOperazioniRapide = "First note";
        vm.IncollaOperazioniRapideCommand.Execute(null);
        Assert.Equal(original, vm.RevisioneCorrente.Condizioni.Count);

        vm.PreparaAnteprimaOperazioniRapideCommand.Execute(null);
        Assert.True(vm.PuoConfermareAnteprimaRapida);
        vm.TestoOperazioniRapide = "Changed note";
        Assert.False(vm.PuoConfermareAnteprimaRapida);
        vm.IncollaOperazioniRapideCommand.Execute(null);
        Assert.Equal(original, vm.RevisioneCorrente.Condizioni.Count);

        vm.PreparaAnteprimaOperazioniRapideCommand.Execute(null);
        vm.IncollaOperazioniRapideCommand.Execute(null);
        Assert.Equal(original + 1, vm.RevisioneCorrente.Condizioni.Count);
        Assert.Equal("Changed note", vm.RevisioneCorrente.Condizioni[^1].Nota);
    }

    [Fact]
    public void InvalidExcelRowsAppearBesidePasteBoxAndClearAfterEditing()
    {
        var vm = CreaVm();
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        var before = vm.RevisioneCorrente!.Condizioni.Count;
        var dirtyBefore = vm.ModificheNonSalvate;
        vm.TestoOperazioniRapide = "Valid note\nInvalid\tToo\tMany";
        vm.PreparaAnteprimaOperazioniRapideCommand.Execute(null);

        Assert.True(vm.HaErroreAnteprimaRapida);
        Assert.Contains("2", vm.ErroreAnteprimaRapida);
        Assert.False(vm.PuoConfermareAnteprimaRapida);
        Assert.Equal(before, vm.RevisioneCorrente.Condizioni.Count);
        Assert.Equal(dirtyBefore, vm.ModificheNonSalvate);

        vm.TestoOperazioniRapide = "Corrected note";
        Assert.False(vm.HaErroreAnteprimaRapida);
        Assert.False(vm.MostraAnteprimaOperazioniRapide);
        vm.PreparaAnteprimaOperazioniRapideCommand.Execute(null);
        Assert.True(vm.PuoConfermareAnteprimaRapida);
    }

    [Fact]
    public void LongPreviewIsShortenedOnlyInDisplayAndKeepsFullNotesAfterCommit()
    {
        var vm = CreaVm();
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        var note = new string('X', 180);
        vm.TestoOperazioniRapide = note;
        vm.PreparaAnteprimaOperazioniRapideCommand.Execute(null);
        Assert.True(Assert.Single(vm.RigheAnteprimaRapida).Nota.Length < note.Length);

        vm.IncollaOperazioniRapideCommand.Execute(null);
        Assert.Equal(note, vm.RevisioneCorrente!.Condizioni[^1].Nota);
        Assert.False(vm.MostraAnteprimaOperazioniRapide);
    }

    [Fact]
    public void TooManyPastedLinesAreRejectedAtomically()
    {
        var vm = CreaVm();
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        var before = vm.RevisioneCorrente!.Condizioni.Count;
        vm.TestoOperazioniRapide = string.Join("\n", Enumerable.Range(0, 51).Select(i => "operation " + i));
        vm.PreparaAnteprimaOperazioniRapideCommand.Execute(null);
        vm.IncollaOperazioniRapideCommand.Execute(null);
        Assert.Equal(before, vm.RevisioneCorrente.Condizioni.Count);
    }

    public void Dispose()
    {
        foreach (var item in _opened.AsEnumerable().Reverse()) item.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
