using System.Diagnostics;
using System.Linq;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Un test con MOLTE anomalie collegate (segnalazioni dalla rete): l'elenco compatto nella pagina Test e'
/// UN SOLO ListBox virtualizzato (nessun contenitore alternativo/doppio). I contenitori vengono RICICLATI durante
/// lo scorrimento: si verifica che ogni riga mostri i propri valori (owner/stato/priorita' mai vuoti o scambiati)
/// e che la modifica di una riga lontana, fatta nel pop-up, resti sull'anomalia giusta.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
public sealed class AnomalieCollegateNelTestVirtualizzazioneTests : UiTestBase
{
    private const int Anomalie = 60;

    public AnomalieCollegateNelTestVirtualizzazioneTests() : base(AnomalieCollegateNelTestTests.CreaWorkspace(Anomalie, ownerUnivoco: true)) { }

    private static string OwnerAtteso(string id) => "Owner-" + id["NC-QA-".Length..];
    private static int Indice(string id) => int.Parse(id["NC-QA-".Length..]);

    private TestAnomaliePanel Apri(out Stopwatch cronometro)
    {
        var window = App.WaitForMainWindow();
        AnomalieCollegateNelTestTests.Massimizza(window);
        var shell = new MainShellPage(window);
        var lista = shell.GoToTestList();
        cronometro = Stopwatch.StartNew();
        lista.OpenOnlyRow();
        var pannello = new TestAnomaliePanel(window);
        Wait.ForCondition(() => pannello.NumeroRigheRealizzate > 0, "prime righe anomalia realizzate", TimeSpan.FromSeconds(30));
        cronometro.Stop();
        pannello.PortaInVista();
        return pannello;
    }

    [Fact]
    public void ElencoConMolteAnomalie_UnSoloContenitore_RealizzaSoloUnaFrazione_EApreInTempiRagionevoli()
    {
        try
        {
            var pannello = Apri(out var cronometro);

            Assert.Equal(1, pannello.NumeroContenitoriElenco());
            // Soglia assoluta: 60 anomalie, righe compatte ad altezza fissa; non virtualizzato = 60 righe realizzate.
            var realizzate = pannello.NumeroRigheRealizzate;
            Assert.InRange(realizzate, 1, 29);
            Assert.Equal(realizzate, pannello.IdVisibili().Distinct().Count()); // nessuna riga doppia (un solo contenitore vivo)
            Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(20),
                $"apertura del test con {Anomalie} anomalie troppo lenta: {cronometro.Elapsed.TotalSeconds:0.0}s");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ElencoConMolteAnomalie_UnSoloContenitore_RealizzaSoloUnaFrazione_EApreInTempiRagionevoli), ex);
        }
    }

    [Fact]
    public void ScorrimentoERiciclo_ValoriPerRigaCorretti_EModificaDiUnaRigaLontanaNelPopupRestaSuQuellaAnomalia()
    {
        try
        {
            var pannello = Apri(out _);
            Wait.ForCondition(() => pannello.SnapshotVisibili().Count > 0, "snapshot della prima riga");
            var baseline = pannello.SnapshotVisibili()[0];
            Assert.False(string.IsNullOrWhiteSpace(baseline.Stato));
            Assert.False(string.IsNullOrWhiteSpace(baseline.Priorita));

            var indiceMinimoVisto = Anomalie;
            string? idModificato = null;

            void Verifica(string contesto)
            {
                Wait.ForCondition(() => pannello.SnapshotVisibili().Count > 0, "righe visibili dopo lo scorrimento");
                foreach (var snap in pannello.SnapshotVisibili())
                {
                    indiceMinimoVisto = Math.Min(indiceMinimoVisto, Indice(snap.Id));
                    var ownerAtteso = snap.Id == idModificato ? "Modificato-QA" : OwnerAtteso(snap.Id);
                    Assert.True(ownerAtteso == snap.Owner, $"{contesto}: owner di {snap.Id} = '{snap.Owner}', atteso '{ownerAtteso}'");
                    Assert.True(baseline.Stato == snap.Stato, $"{contesto}: stato di {snap.Id} = '{snap.Stato}'");
                    Assert.True(baseline.Priorita == snap.Priorita, $"{contesto}: priorita di {snap.Id} = '{snap.Priorita}'");
                }
            }

            pannello.UsaCache = true;
            pannello.FocusPrimaRiga();
            for (var passo = 0; passo < 14 && indiceMinimoVisto > Anomalie - 45; passo++)
            {
                pannello.PaginaGiu();
                Verifica($"scorrimento passo {passo}");
            }
            Assert.True(indiceMinimoVisto <= Anomalie - 20, $"scorrimento insufficiente: riga piu' bassa vista NC-QA-{indiceMinimoVisto:0000}");

            // Riga lontana (a piu' di 20 righe dalla prima): modifica dell'assegnatario nel pop-up "Modifica stato".
            Wait.ForCondition(() => pannello.SnapshotVisibili().Any(s => Indice(s.Id) <= Anomalie - 21), "riga lontana visibile");
            idModificato = pannello.SnapshotVisibili().First(s => Indice(s.Id) <= Anomalie - 21).Id;
            var dettaglio = pannello.ApriDettaglioDi(App, idModificato);
            Assert.Equal(OwnerAtteso(idModificato), dettaglio.Owner);
            dettaglio.ImpostaOwner("Modificato-QA");
            dettaglio.Salva();
            Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId), "il pop-up si chiude dopo Salva");

            // Il test si ricarica (nuova vista): si riparte dall'alto e si scende fino alla riga modificata.
            pannello.UsaCache = false;
            Wait.ForCondition(() => pannello.IdVisibili().Contains("NC-QA-0060"), "elenco ricaricato in cima");
            pannello.UsaCache = true;
            pannello.FocusPrimaRiga();
            for (var i = 0; i < 14 && pannello.TrySnapshot(idModificato) == null; i++)
            {
                pannello.PaginaGiu();
                Verifica("discesa verso la riga modificata");
            }
            Wait.ForCondition(() => pannello.TrySnapshot(idModificato) != null, "riga modificata di nuovo visibile");
            Assert.Equal("Modificato-QA", pannello.TrySnapshot(idModificato)!.Owner);
            Verifica("riga modificata di nuovo visibile");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ScorrimentoERiciclo_ValoriPerRigaCorretti_EModificaDiUnaRigaLontanaNelPopupRestaSuQuellaAnomalia), ex);
        }
    }
}
