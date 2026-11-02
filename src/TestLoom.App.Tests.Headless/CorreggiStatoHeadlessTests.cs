using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

/// <summary>Correzione manuale dello stato nel Report in headless (Category=LayoutHeadless, 6.6.1 fase 2): legenda e filtro
/// "Solo celle con !" in modalita' Stato; pannello in sola lettura per chi non e' Master; salvataggio di un valore manuale da parte
/// di un Master con motivazione. Nessun controllo sui tempi.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class CorreggiStatoHeadlessTests
{
    [Fact]
    public void Legenda_eFiltroDaDecidereVisibiliInModalitaStato() => LayoutHeadlessSessione.Esegui(() =>
    {
        using var stato = CreaStato(master: false);
        var vm = new ReportViewModel(stato.State);
        vm.IndiceModalitaMatrice = 1;
        var vista = new ReportView { DataContext = vm };
        var finestra = new Window { Width = 1280, Height = 672, Content = vista };
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            AttendiMatrice(vm, finestra);
            Assert.True(PerId(vista, "Report_MatriceLegenda_DaDecidere").Any());
            Assert.True(PerId(vista, "Report_MatriceLegenda_Manuale").Any());
            Assert.True(PerId(vista, "Report_MatriceLegenda_Superato").Any());
            Assert.True(PerId(vista, "Report_MatriceSoloDaDecidere_CheckBox").Single().IsVisible);
        }
        finally
        {
            finestra.Hide();
        }
    });

    [Theory]
    [InlineData(RuoloOperatore.Standard)]
    [InlineData(RuoloOperatore.Viewer)]
    public void TooltipValoreManualeVisibileInSolaLettura(RuoloOperatore ruolo) => LayoutHeadlessSessione.Esegui(() =>
    {
        using var stato = CreaStato(master: true);
        stato.State.Master.StatiFunzionalitaEcuVeicolo.Save(new List<StatoFunzionalitaEcuVeicolo>
        {
            new()
            {
                Funzionalita = "Lettura guasto", Componente = "BCM", Veicolo = "Daily A", Esito = Esito.Conforme,
                Nota = "Banco <A&B> \"ok\"", Operatore = "Master <Prova>",
                DataDecisione = new DateTime(2025, 12, 31, 13, 45, 0, DateTimeKind.Utc),
            },
            new()
            {
                Funzionalita = "Lettura guasto", Componente = "BCM", Veicolo = "Daily B", Esito = Esito.NonConforme,
                Nota = "", Operatore = "", DataDecisione = default,
            },
        });

        var testRecente = new TestCase
        {
            Id = "T-RECENTE", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"],
        };
        testRecente.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(2026, 1, 1),
            Operatore = "Tester",
            Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.NonConforme, Componente = new EcuSelection(["BCM"]) }],
        });
        stato.State.TestCases.Save(testRecente, isNew: true);

        var operatore = new Operatore
        {
            Nome = ruolo == RuoloOperatore.Standard ? "Operatore lettura" : "Viewer lettura",
            Ruolo = ruolo,
            PinHash = PinService.Hash("1234"),
        };
        RuoloSigilloService.Sigilla(operatore);
        stato.State.Master.Operatori.Save(new List<Operatore> { operatore });
        Assert.True(stato.State.ImpostaOperatore(operatore.Nome, "1234"));

        var vm = new ReportViewModel(stato.State);
        vm.IndiceModalitaMatrice = 1;
        var vista = new ReportView { DataContext = vm };
        var finestra = new Window { Width = 1280, Height = 672, Content = vista };
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            AttendiMatrice(vm, finestra);
            var celle = vm.RigheMatrice.SelectMany(r => r.Celle).Where(c => c.ValoriManuali.Count > 0).ToArray();
            var tooltips = celle.Select(c =>
            {
                var controllo = vista.GetVisualDescendants().OfType<Control>()
                    .Single(x => AutomationProperties.GetAutomationId(x) == c.AutomationId);
                return ToolTip.GetTip(controllo) as string;
            }).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();

            Assert.False(vm.PuoCorreggereStato);
            Assert.NotEmpty(tooltips);
            var testoTooltip = string.Join(Environment.NewLine, tooltips);
            Assert.Contains("Master <Prova>", testoTooltip);
            Assert.Contains("31/12/2025", testoTooltip);
            Assert.Contains("Banco <A&B> \"ok\"", testoTooltip);
            Assert.Contains("—", testoTooltip);
            Assert.Contains(LocalizationService.Instance.Traduci("MatriceValoreManualeTooltipSuperato"), testoTooltip);
        }
        finally
        {
            finestra.Hide();
        }
    });

    [Fact]
    public void RigheCorrezioneCella_ManualeVinceLaParitaComeNellaMatrice() => LayoutHeadlessSessione.Esegui(() =>
    {
        using var stato = CreaStato(master: true);
        var data = new DateTime(2026, 2, 1);
        stato.State.Master.StatiFunzionalitaEcuVeicolo.Save(new List<StatoFunzionalitaEcuVeicolo>
        {
            new()
            {
                Funzionalita = "Lettura guasto", Componente = "BCM", Veicolo = "Daily A", Esito = Esito.Conforme,
                Nota = "Decisione del Master", Operatore = "Master",
                DataDecisione = data,
            },
        });

        var testParita = new TestCase
        {
            Id = "T-PARITA", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"],
        };
        testParita.Esecuzioni.Add(new Esecuzione
        {
            Data = data,
            Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.NonConforme, Componente = new EcuSelection(["BCM"]) }],
        });
        stato.State.TestCases.Save(testParita, isNew: true);

        var vm = new ReportViewModel(stato.State);
        vm.IndiceModalitaMatrice = 1;
        var finestra = new Window { Width = 1280, Height = 672, Content = new ReportView { DataContext = vm } };
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            AttendiMatrice(vm, finestra);
            var cella = Assert.Single(vm.RigheMatrice.SelectMany(r => r.Celle));
            var righe = vm.RigheCorrezioneCella(cella);
            var sorgente = Assert.Single(righe, r => r.Veicolo == "Daily A");
            var destinazione = Assert.Single(righe, r => r.Veicolo == "Daily B");

            Assert.Equal(StatoCombinazioneMatrice.Conforme, sorgente.Stato);
            Assert.Equal("manuale", destinazione.Fonte?.TestIdSorgente);
            Assert.Equal(Esito.Conforme, destinazione.Fonte?.EsitoSorgente);
        }
        finally
        {
            finestra.Hide();
        }
    });

    [Fact]
    public void IndiceFontiStatoPropagato_EnumeraUnaVoltaLaCronologiaPerCelleAggregate()
    {
        const int combinazioni = 32;
        var testCases = Enumerable.Range(0, combinazioni).Select(i =>
        {
            var functionality = $"Funzione {i:00}";
            var test = new TestCase
            {
                Id = $"T-{i:00}", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = [functionality],
            };
            test.Esecuzioni.Add(new Esecuzione
            {
                Data = new DateTime(2026, 1, 1).AddDays(i),
                Condizioni = [new Condizione { Funzionalita = functionality, Esito = Esito.NonConforme, Componente = new EcuSelection(["BCM"]) }],
            });
            return test;
        }).ToArray();
        var destinazioni = Enumerable.Range(0, combinazioni)
            .Select(i => new CombinazioneCellaMatrice($"Funzione {i:00}", "BCM", "Daily B", default, false, false, true))
            .ToArray();
        var enumerazioni = 0;

        IEnumerable<TestCase> CronologiaUnaPassata()
        {
            enumerazioni++;
            Assert.Equal(1, enumerazioni);
            foreach (var test in testCases) yield return test;
        }

        var indice = IndiceFontiStatoPropagato.Crea(
            CronologiaUnaPassata(),
            [new Componente { Nome = "BCM", VeicoliAssociati = ["Daily A", "Daily B"] }],
            [],
            destinazioni);

        Assert.Equal(1, enumerazioni);
        foreach (var destinazione in destinazioni)
        {
            var fonte = indice.FonteDi(destinazione.Funzionalita, destinazione.Componente, destinazione.Veicolo);
            Assert.Equal($"T-{destinazione.Funzionalita[^2..]}", fonte?.TestIdSorgente);
            Assert.Equal(Esito.NonConforme, fonte?.EsitoSorgente);
        }
    }


    [Fact]
    public void IndiceFontiStatoPropagato_100000CandidatiERisoluzioneDestinazioniLineare()
    {
        const int candidati = 100_000;
        const int veicoliDestinazione = 2_000;
        var test = new TestCase
        {
            Id = "T-SCALA",
            Componente = new EcuSelection(["BCM"]),
            Veicoli = ["V0000", "V0001"],
            Funzionalita = ["Funzione"],
        };
        var dataBase = new DateTime(2026, 1, 1);
        for (var i = 0; i < candidati; i++)
        {
            test.Esecuzioni.Add(new Esecuzione
            {
                Data = dataBase.AddTicks(i),
                Condizioni = [new Condizione
                {
                    Funzionalita = "Funzione",
                    Esito = Esito.NonConforme,
                    Componente = new EcuSelection(["BCM"]),
                }],
            });
        }

        var veicoli = Enumerable.Range(0, veicoliDestinazione).Select(i => $"V{i:D4}").ToList();
        var destinazioni = veicoli
            .Select(v => new CombinazioneCellaMatrice("Funzione", "BCM", v, default, false, false, true))
            .ToArray();

        var indice = IndiceFontiStatoPropagato.Crea(
            [test],
            [new Componente { Nome = "BCM", VeicoliAssociati = veicoli }],
            [],
            destinazioni);

        Assert.Equal(candidati, indice.CandidatiConsiderati);
        Assert.Equal(veicoliDestinazione, indice.DestinazioniRisolte);
        Assert.True(indice.ConfrontiCandidati <= 3 * candidati + veicoliDestinazione,
            $"La selezione ha effettuato {indice.ConfrontiCandidati} confronti per {candidati} candidati e {veicoliDestinazione} destinazioni.");
        Assert.True(indice.ConfrontiCandidati < (long)candidati * veicoliDestinazione / 100,
            "La risoluzione non deve confrontare ogni candidato con ogni veicolo destinazione.");
        Assert.Equal(("T-SCALA", "V0001"), (
            indice.FonteDi("Funzione", "BCM", "V0000")?.TestIdSorgente,
            indice.FonteDi("Funzione", "BCM", "V0000")?.VeicoloSorgente));
        Assert.Equal(("T-SCALA", "V0000"), (
            indice.FonteDi("Funzione", "BCM", "V1999")?.TestIdSorgente,
            indice.FonteDi("Funzione", "BCM", "V1999")?.VeicoloSorgente));
    }

    [Fact]
    public void Pannello_soloLetturaPerChiNonEMaster() => LayoutHeadlessSessione.Esegui(() =>
    {
        using var stato = CreaStato(master: false);
        var vm = new ReportViewModel(stato.State);
        vm.IndiceModalitaMatrice = 1;
        var finestra0 = new Window { Width = 1280, Height = 672, Content = new ReportView { DataContext = vm } };
        LayoutHeadlessSessione.Mostra(finestra0);
        AttendiMatrice(vm, finestra0);
        var cella = vm.RigheMatrice.First().Celle.First(c => c.Colonna == "BCM");
        var pannello = new CorreggiStatoWindow(vm, "Verifica", vm.RigheCorrezioneCella(cella));
        LayoutHeadlessSessione.Mostra(pannello);
        try
        {
            Assert.False(PerId(pannello, "CorreggiStato_Esito_0").Single().IsEnabled);
            Assert.False(PerId(pannello, "CorreggiStato_Salva_Button").Single().IsEnabled);
        }
        finally
        {
            pannello.Close();
            finestra0.Hide();
        }
    });

    [Fact]
    public void Pannello_masterSalvaUnValoreManualeConMotivazione() => LayoutHeadlessSessione.Esegui(() =>
    {
        using var stato = CreaStato(master: true);
        var vm = new ReportViewModel(stato.State);
        vm.IndiceModalitaMatrice = 1;
        var finestra0 = new Window { Width = 1280, Height = 672, Content = new ReportView { DataContext = vm } };
        LayoutHeadlessSessione.Mostra(finestra0);
        AttendiMatrice(vm, finestra0);
        var cella = vm.RigheMatrice.First().Celle.First(c => c.Colonna == "BCM");
        var pannello = new CorreggiStatoWindow(vm, "Verifica", vm.RigheCorrezioneCella(cella));
        LayoutHeadlessSessione.Mostra(pannello);
        try
        {
            var salva = PerId(pannello, "CorreggiStato_Salva_Button").Single();
            Assert.False(salva.IsEnabled);
            PerId(pannello, "CorreggiStato_Esito_0").OfType<ComboBox>().Single().SelectedIndex = 1; // Conforme
            PerId(pannello, "CorreggiStato_Nota_TextBox").OfType<TextBox>().Single().Text = "verificato sul banco";
            LayoutHeadlessSessione.Assesta(pannello);
            Assert.True(salva.IsEnabled);
            ((Button)salva).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LayoutHeadlessSessione.Assesta(pannello);

            var manuali = stato.State.Master.StatiFunzionalitaEcuVeicolo.Load();
            Assert.Single(manuali);
            Assert.Equal(Esito.Conforme, manuali[0].Esito);
            Assert.Equal("verificato sul banco", manuali[0].Nota);
            Assert.Equal(OrigineStatoFunzionalita.Manuale, manuali[0].Origine);
        }
        finally
        {
            pannello.Close();
            finestra0.Hide();
        }
    });

    private static void AttendiMatrice(ReportViewModel vm, Window finestra)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (vm.RigheMatrice.Count == 0 && DateTime.UtcNow < limite)
        {
            LayoutHeadlessSessione.Assesta(finestra);
            Thread.Sleep(50);
        }
        LayoutHeadlessSessione.Assesta(finestra);
        Assert.NotEmpty(vm.RigheMatrice);
    }

    private static IEnumerable<Control> PerId(Visual radice, string id) =>
        radice.GetVisualDescendants().OfType<Control>().Where(c => AutomationProperties.GetAutomationId(c) == id);

    /// <summary>Stato di prova: due veicoli sulla stessa componente, un Test da eseguire su Daily A. Con master=true si accede come Master.</summary>
    private static StatoCorrezione CreaStato(bool master)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_CorreggiStato_" + Guid.NewGuid().ToString("N"));
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily A", GammaNome = "LCV" }, new() { Nome = "Daily B", GammaNome = "LCV" } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM", VeicoliAssociati = ["Daily A", "Daily B"] } });
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Lettura guasto", Categorie = ["Diagnostica"] } });
        var tc = new TestCase { Id = "T-A", Componente = new EcuSelection(["BCM"]), Veicoli = ["Daily A"], Funzionalita = ["Lettura guasto"] };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(2026, 1, 1),
            Condizioni = [new Condizione { Funzionalita = "Lettura guasto", Esito = Esito.DaEseguire, Componente = new EcuSelection(["BCM"]) }],
        });
        state.TestCases.Save(tc, isNew: true);
        var ruolo = master ? RuoloOperatore.Master : RuoloOperatore.Viewer;
        var op = new Operatore { Nome = "Op Prova", Ruolo = ruolo, PinHash = PinService.Hash("1234") };
        RuoloSigilloService.Sigilla(op);
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return new StatoCorrezione(state, dir);
    }

    private sealed class StatoCorrezione(AppState state, string dir) : IDisposable
    {
        public AppState State { get; } = state;

        public void Dispose()
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
