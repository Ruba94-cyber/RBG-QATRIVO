using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>6.5.1 fase 3, regole pure: UnitLogger/Easy (DaTestare) lavorano per funzionalita, la coppia Validazione+Devalidazione spetta
/// solo ai Comportamento (guasto Injection), Supporto non genera nulla; raggruppamento per strumento con contatore.</summary>
public sealed class OperazioniStrumentoRegoleTests
{
    private static Condizione Op(string? strumento, string funz = "", Esito e = Esito.DaEseguire) =>
        new() { Strumento = strumento, Funzionalita = funz, Esito = e };

    private static Condizione Guasto(string? strumento, string codice, PassoDtc? passo, string componente = "ECM", string? req = "R1", string? veicolo = null) =>
        new() { Strumento = strumento, Guasto = codice, DtcPasso = passo, DtcRequisitoId = req, DtcVeicolo = veicolo, Componente = new EcuSelection(new[] { componente }) };

    [Theory]
    [InlineData(TipoStrumento.DaTestare, 2)]
    [InlineData(TipoStrumento.Comportamento, 0)]
    [InlineData(TipoStrumento.Supporto, 0)]
    public void FunzionalitaAttese_SoloDaTestare_SenzaDuplicatiNeVuote(TipoStrumento tipo, int attese)
    {
        var f = DtcOperazioneRegole.FunzionalitaAttese(tipo, new[] { "F1", " f1 ", "", "F2" });
        Assert.Equal(attese, f.Count);
        if (attese > 0) Assert.Equal(new[] { "F1", "F2" }, f);
    }

    [Fact]
    public void FunzionalitaMancanti_SaltaQuelleGiaCoperteDalloStrumento_ENonDaAltri()
    {
        var ops = new[] { Op("UnitLogger", "F1"), Op("Easy", "F2"), Op(null, "F3") };
        Assert.Equal(new[] { "F2", "F3" }, OperazioniStrumentoRegole.FunzionalitaMancanti(TipoStrumento.DaTestare, "UnitLogger", ops, new[] { "F1", "F2", "F3" }));
        Assert.Empty(OperazioniStrumentoRegole.FunzionalitaMancanti(TipoStrumento.Supporto, "ValidationTool", ops, new[] { "F1" }));
    }

    [Fact]
    public void CoppieDtcMancanti_CompletaIPassiSenzaDuplicare_ancheSeCopertiDaOperazioniLegacy()
    {
        var ops = new[]
        {
            Guasto(null, "P1", PassoDtc.Validazione),               // legacy: copre la Validazione di P1
            Guasto("Guasto Injection", "P2", PassoDtc.Validazione),    // gia di proprieta: manca la Devalidazione
            Op(null, "F1"),                                       // non guasto: ignorata
        };
        var m = OperazioniStrumentoRegole.CoppieDtcMancanti(TipoStrumento.Comportamento, "Guasto Injection", ops);
        Assert.Equal(new[] { ("P1", PassoDtc.Devalidazione), ("P2", PassoDtc.Devalidazione) }, m.Select(x => (x.Attesa.Codice, x.Attesa.Passo)).ToArray());
        Assert.Equal("ECM", m[0].Attesa.Componente);
        Assert.Empty(OperazioniStrumentoRegole.CoppieDtcMancanti(TipoStrumento.DaTestare, "UnitLogger", ops));
        Assert.Empty(OperazioniStrumentoRegole.CoppieDtcMancanti(TipoStrumento.Supporto, "ValidationTool", ops));
    }

    [Fact]
    public void CoppieDtcMancanti_DistingueVeicoloEcuERequisito()
    {
        var ops = new[]
        {
            Guasto(null, "P1", PassoDtc.Validazione, "ECM", "R1", "A"),
            Guasto(null, "P1", PassoDtc.Validazione, "ECM", "R1", "B"),
            Guasto(null, "P1", PassoDtc.Validazione, "BCM", "R2"),
        };
        var m = OperazioniStrumentoRegole.CoppieDtcMancanti(TipoStrumento.Comportamento, "Guasto Injection", ops);
        Assert.Equal(3, m.Count);
        Assert.All(m, x => Assert.Equal(PassoDtc.Devalidazione, x.Attesa.Passo));
        Assert.Equal(new string?[] { "A", "B", null }, m.Select(x => x.Attesa.Veicolo).ToArray());
    }

    [Fact]
    public void Raggruppa_PerStrumento_ContatoreSuOperazioniEffettive_LegacyEscluso()
    {
        var a = Op("UnitLogger", "F1", Esito.Conforme);
        var b = Op("UnitLogger", "F2", Esito.NonConforme);
        var c = Op("UnitLogger", "F3", Esito.ConformeConDeviazioni);
        var d = Op("Guasto Injection", "", Esito.DaEseguire);
        var legacy = Op(null, "F9", Esito.Conforme);
        var tutte = new[] { a, b, legacy, c, d };
        var g = OperazioniStrumentoRegole.Raggruppa(tutte, tutte);
        Assert.Equal(new[] { "UnitLogger", "Guasto Injection" }, g.Select(x => x.Strumento).ToArray());
        Assert.Equal((3, 2), (g[0].Totale, g[0].Ok));
        Assert.Equal((1, 0), (g[1].Totale, g[1].Ok));
        Assert.Equal(new[] { a, b, c }, g[0].Operazioni);
        Assert.Equal("UnitLogger 2/3 OK", g[0].Contatore.Replace("  ", " "));
    }

    [Fact]
    public void Raggruppa_UnaCatenaDiRipetizioni_ContaSoloIlTentativoEffettivo()
    {
        var origine = Op("UnitLogger", "F1", Esito.NonConforme);
        var ripetuta = Op("UnitLogger", "F1", Esito.Conforme);
        ripetuta.OperazioneOrigineUid = origine.Uid;
        var tutte = new[] { origine, ripetuta };
        var g = Assert.Single(OperazioniStrumentoRegole.Raggruppa(tutte, new[] { ripetuta }));
        Assert.Equal((1, 1), (g.Totale, g.Ok));
        Assert.Equal(2, g.Operazioni.Count);
    }
}

/// <summary>Editor Strumenti: combo Tipo (TipoScelto) legge il valore effettivo e scrive quello esplicito; etichette IT/EN.</summary>
[Collection("Localization")]
public sealed class StrumentoTipoScelto_Tests
{
    [Fact]
    public void TipoScelto_LeggeEffettivo_ScriveEsplicito_NonPersisteLaProprietaDiVista()
    {
        var s = new StrumentoDiagnostico { Nome = "Nuovo" };
        Assert.Equal(TipoStrumento.Supporto, s.TipoScelto);         // default dei nuovi
        Assert.Null(s.Tipo);
        s.TipoScelto = TipoStrumento.Comportamento;
        Assert.Equal(TipoStrumento.Comportamento, s.Tipo);
        var json = System.Text.Json.JsonSerializer.Serialize(s, JsonIo.Options);
        Assert.DoesNotContain("TipoScelto", json);
        Assert.Contains("\"Tipo\"", json);
        Assert.Equal(TipoStrumento.DaTestare, new StrumentoDiagnostico { Nome = "UnitLogger" }.TipoScelto);   // classificazione nota per nome
        Assert.Equal(new[] { TipoStrumento.DaTestare, TipoStrumento.Comportamento, TipoStrumento.Supporto }, StrumentoDiagnostico.TipiDisponibili);
    }

    [Fact]
    public void EtichetteTipo_ItEn()
    {
        var loc = LocalizationService.Instance;
        var vecchia = loc.Lingua;
        try
        {
            loc.Lingua = "it";
            Assert.Equal("Da testare", (string?)TestLoom.App.TipoStrumentoLabelConverter.Instance.Convert(TipoStrumento.DaTestare, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
            loc.Lingua = "en";
            Assert.Equal("Support", (string?)TestLoom.App.TipoStrumentoLabelConverter.Instance.Convert(TipoStrumento.Supporto, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("Behavior", loc.Traduci("TipoStrumentoComportamento"));
        }
        finally { loc.Lingua = vecchia; }
    }
}

/// <summary>6.5.1 fase 3, editor Test: selezionando uno strumento si creano subito le operazioni mancanti nella REV corrente;
/// deselezionare e bloccato se hanno esito o REV registrate, altrimenti elimina dopo conferma. Le operazioni legacy non si toccano.</summary>
[Collection("Localization")]
public sealed class OperazioniStrumentoEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_opstr_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "S1", Testo = "t" } });
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico>
        {
            new() { Nome = "UnitLogger" }, new() { Nome = "ValidationTool" }, new() { Nome = "Guasto Injection" },
            new() { Nome = "Altro", Tipo = TipoStrumento.DaTestare },
        });
        return state;
    }

    private static TestCase Test(string[] funz, params Condizione[] ops)
    {
        var tc = new TestCase { Id = "T-1", Componente = new EcuSelection(new[] { "ECM" }), Veicolo = "V", Veicoli = { "V" }, Funzionalita = funz.ToList() };
        tc.RequisitoId.Add("S1");
        var rev = new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.DaEseguire, Matricola = "x" };
        rev.Condizioni.AddRange(ops);
        tc.Esecuzioni.Add(rev);
        return tc;
    }

    private static Condizione Legacy() => new() { Esito = Esito.DaEseguire, Nota = "legacy" };

    private TestCaseEditorViewModel Apri(AppState state, TestCase tc)
    {
        state.TestCases.Save(tc, true);
        return new TestCaseEditorViewModel(state, "T-1", () => { }, _ => { });
    }

    private static StrumentoOption Opt(TestCaseEditorViewModel vm, string nome) => vm.StrumentiOptions.Single(o => o.Valore == nome);
    private static List<Condizione> Proprie(TestCaseEditorViewModel vm, string nome) =>
        vm.RevisioneCorrente!.Condizioni.Where(c => c.Strumento == nome).ToList();

    [Fact]
    public void SelezioneDaTestare_CreaUnaOperazionePerFunzionalita_SubitoNellaRevCorrente_Idempotente()
    {
        var vm = Apri(CreaStato(), Test(new[] { "F1", "F2" }, Legacy()));
        Opt(vm, "UnitLogger").Selezionato = true;
        var ops = Proprie(vm, "UnitLogger");
        Assert.Equal(new[] { "F1", "F2" }, ops.Select(o => o.Funzionalita).ToArray());
        Assert.All(ops, o => { Assert.Equal(Esito.DaEseguire, o.Esito); Assert.False(o.RiguardaDtc); Assert.Equal(new[] { "ECM" }, o.Componente.ToArray()); });
        Assert.Equal(3, vm.CondizioniVisual.Count);                 // legacy + 2
        Assert.True(vm.ModificheNonSalvate);
        // rimarcare lo strumento (deselezione bloccata/riselezione) non duplica
        vm.GetType().GetMethod("AllineaOperazioniStrumentoSelezionato", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, new object[] { "UnitLogger" });
        Assert.Equal(2, Proprie(vm, "UnitLogger").Count);
    }

    [Fact]
    public void SelezioneTipoEsplicito_VinceSulNome_ESupportoNonCreaNulla()
    {
        var vm = Apri(CreaStato(), Test(new[] { "F1" }));
        Opt(vm, "ValidationTool").Selezionato = true;
        Assert.Empty(vm.RevisioneCorrente!.Condizioni);
        Opt(vm, "Altro").Selezionato = true;                         // Tipo esplicito DaTestare
        Assert.Equal("F1", Assert.Single(Proprie(vm, "Altro")).Funzionalita);
    }

    [Fact]
    public void SelezioneComportamento_CreaLeCoppieSoloPerIDtcGiaPresenti()
    {
        var guasto = new Condizione { Esito = Esito.DaEseguire, Guasto = "P0001", DtcRequisitoId = "S1", DtcPasso = PassoDtc.Validazione, Componente = new EcuSelection(new[] { "ECM" }) };
        var vm = Apri(CreaStato(), Test(new[] { "F1" }, guasto));
        Opt(vm, "Guasto Injection").Selezionato = true;
        var nuova = Assert.Single(Proprie(vm, "Guasto Injection"));     // la Validazione e coperta dall'operazione legacy
        Assert.Equal((PassoDtc.Devalidazione, "P0001", "S1"), (nuova.DtcPasso!.Value, nuova.Guasto, nuova.DtcRequisitoId));
        Assert.Null(vm.RevisioneCorrente!.Condizioni[0].Strumento);   // la legacy non e stata adottata ne toccata
        Assert.Equal(2, vm.RevisioneCorrente!.Condizioni.Count);
    }

    [Fact]
    public void SelezioneComportamento_SenzaDtc_NonCreaNulla()
    {
        var vm = Apri(CreaStato(), Test(new[] { "F1" }));
        Opt(vm, "Guasto Injection").Selezionato = true;
        Assert.Empty(vm.RevisioneCorrente!.Condizioni);
    }

    [Fact]
    public async Task Deselezione_ConSoloDaEseguire_EliminaLeProprie_DopoConferma_LegacyIntatta()
    {
        var legacy = Legacy();
        var vm = Apri(CreaStato(), Test(new[] { "F1", "F2" }, legacy));
        Opt(vm, "UnitLogger").Selezionato = true;
        (string, int)? chiesto = null;
        vm.ConfermaRimozioneOperazioniStrumento = (n, c) => { chiesto = (n, c); return Task.FromResult(true); };
        Opt(vm, "UnitLogger").Selezionato = false;
        await vm.DeselezioneDtcInCorso;
        Assert.Equal(("UnitLogger", 2), chiesto);
        Assert.Empty(Proprie(vm, "UnitLogger"));
        Assert.Equal("legacy", Assert.Single(vm.RevisioneCorrente!.Condizioni).Nota);
        Assert.Single(vm.CondizioniVisual);
        Assert.False(Opt(vm, "UnitLogger").Selezionato);
        Assert.False(vm.HaAvvisoStrumentiOperazioni);
    }

    [Fact]
    public async Task Deselezione_ConfermaNegata_RiselezionaENulla_Rimosso()
    {
        var vm = Apri(CreaStato(), Test(new[] { "F1" }));
        vm.InModifica = true;
        Opt(vm, "UnitLogger").Selezionato = true;
        vm.ModificheNonSalvate = false; // Baseline dopo aver salvato la selezione dello strumento.
        vm.ConfermaRimozioneOperazioniStrumento = (_, _) => Task.FromResult(false);
        Opt(vm, "UnitLogger").Selezionato = false;
        await vm.DeselezioneDtcInCorso;
        Assert.True(Opt(vm, "UnitLogger").Selezionato);
        Assert.Single(Proprie(vm, "UnitLogger"));
        Assert.False(vm.ModificheNonSalvate);
    }

    [Fact]
    public async Task Deselezione_Bloccata_SeUnaOperazioneHaEsito_MessaggioENulla_Rimosso()
    {
        var vm = Apri(CreaStato(), Test(new[] { "F1", "F2" }));
        Opt(vm, "UnitLogger").Selezionato = true;
        Proprie(vm, "UnitLogger")[0].Esito = Esito.NonConforme;
        vm.ConfermaRimozioneOperazioniStrumento = (_, _) => throw new InvalidOperationException("non deve chiedere");
        Opt(vm, "UnitLogger").Selezionato = false;
        await vm.DeselezioneDtcInCorso;
        Assert.True(Opt(vm, "UnitLogger").Selezionato);
        Assert.Equal(2, Proprie(vm, "UnitLogger").Count);
        Assert.True(vm.HaAvvisoStrumentiOperazioni);
        Assert.Contains("UnitLogger", vm.AvvisoStrumentiOperazioni);
    }

    [Fact]
    public async Task Deselezione_Bloccata_SeUnaRevPrecedenteContieneOperazioniDelloStrumento()
    {
        var tc = Test(new[] { "F1" });
        var storica = new Esecuzione { Iterazione = 1, Data = DateTime.Now.AddDays(-1), Esito = Esito.Conforme, Matricola = "x",
            Condizioni = { new Condizione { Strumento = "UnitLogger", Funzionalita = "F1", Esito = Esito.Conforme } } };
        tc.Esecuzioni[0].Iterazione = 2;
        tc.Esecuzioni[0].Condizioni.Add(new Condizione { Strumento = "UnitLogger", Funzionalita = "F1", Esito = Esito.DaEseguire });
        tc.Esecuzioni.Insert(0, storica);
        var vm = Apri(CreaStato(), tc);
        vm.ConfermaRimozioneOperazioniStrumento = (_, _) => throw new InvalidOperationException("non deve chiedere");
        Opt(vm, "UnitLogger").Selezionato = true;    // spunta (idempotente: le operazioni esistono gia) e poi la toglie
        Opt(vm, "UnitLogger").Selezionato = false;
        await vm.DeselezioneDtcInCorso;
        Assert.Equal(2, vm.TestCase.Esecuzioni.Count);
        Assert.Single(Proprie(vm, "UnitLogger"));
        Assert.Single(vm.TestCase.Esecuzioni[0].Condizioni);                    // storico intatto
    }

    [Fact]
    public async Task Deselezione_SoloOperazioniLegacy_NienteDaRimuovere_NessunaConfermaOperazioni()
    {
        var vm = Apri(CreaStato(), Test(new[] { "F1" }, Legacy()));
        Opt(vm, "UnitLogger").Selezionato = true;
        Proprie(vm, "UnitLogger").ForEach(c => vm.RevisioneCorrente!.Condizioni.Remove(c));   // nessuna propria
        vm.ConfermaRimozioneOperazioniStrumento = (_, _) => throw new InvalidOperationException("non deve chiedere");
        Opt(vm, "UnitLogger").Selezionato = false;
        await vm.DeselezioneDtcInCorso;
        Assert.Equal("legacy", Assert.Single(vm.RevisioneCorrente!.Condizioni).Nota);
    }

    [Fact]
    public async Task DtcInjection_Deselezione_RimuoveLeProprie_EChiedeSoloPerLeDtcLegacy()
    {
        var legacy = new Condizione { Esito = Esito.DaEseguire, Guasto = "P0001", DtcRequisitoId = "S1", DtcPasso = PassoDtc.Validazione, Componente = new EcuSelection(new[] { "ECM" }) };
        var vm = Apri(CreaStato(), Test(new[] { "F1" }, legacy));
        Opt(vm, "Guasto Injection").Selezionato = true;
        Assert.Single(Proprie(vm, "Guasto Injection"));
        vm.ConfermaRimozioneOperazioniStrumento = (_, _) => Task.FromResult(true);
        var chiestoDtc = 0;
        vm.ConfermaRimozioneDtc = n => { chiestoDtc = n; return Task.FromResult(true); };
        Opt(vm, "Guasto Injection").Selezionato = false;
        await vm.DeselezioneDtcInCorso;
        Assert.Equal(1, chiestoDtc);                                  // solo la legacy: la propria e gia rimossa con lo strumento
        Assert.Empty(Proprie(vm, "Guasto Injection"));
        Assert.Null(vm.RevisioneCorrente!.Condizioni.Single().Guasto);   // comportamento preesistente: guasto azzerato sulle non eseguite legacy
    }

    [Fact]
    public async Task DtcInjection_MantieniLeDtcLegacy_LoStrumentoResta_ELeProprieNonSiRimuovono()
    {
        var legacy = new Condizione { Esito = Esito.DaEseguire, Guasto = "P0001", DtcRequisitoId = "S1", DtcPasso = PassoDtc.Validazione, Componente = new EcuSelection(new[] { "ECM" }) };
        var vm = Apri(CreaStato(), Test(new[] { "F1" }, legacy));
        Opt(vm, "Guasto Injection").Selezionato = true;
        vm.ConfermaRimozioneOperazioniStrumento = (_, _) => Task.FromResult(true);
        vm.ConfermaRimozioneDtc = _ => Task.FromResult(false);
        Opt(vm, "Guasto Injection").Selezionato = false;
        await vm.DeselezioneDtcInCorso;
        Assert.True(Opt(vm, "Guasto Injection").Selezionato);
        Assert.Single(Proprie(vm, "Guasto Injection"));                  // nulla rimosso
        Assert.Equal("P0001", vm.RevisioneCorrente!.Condizioni[0].Guasto);
        Assert.Equal(2, vm.RevisioneCorrente!.Condizioni.Count);      // la riselezione non ha ricreato nulla
    }

    [Fact]
    public void RaggruppamentoEContatore_SiAggiornanoConGliEsiti_SenzaNuovoAggregato()
    {
        var vm = Apri(CreaStato(), Test(new[] { "F1", "F2", "F3" }, Legacy()));
        Opt(vm, "UnitLogger").Selezionato = true;
        var g = Assert.Single(vm.GruppiOperazioniStrumento);
        Assert.Equal((3, 0), (g.Totale, g.Ok));
        var ops = Proprie(vm, "UnitLogger");
        ops[0].Esito = Esito.Conforme; ops[1].Esito = Esito.Conforme; ops[2].Esito = Esito.NonConforme;
        vm.SincronizzaEsitoCondizioni();
        g = Assert.Single(vm.GruppiOperazioniStrumento);
        Assert.Equal((3, 2), (g.Totale, g.Ok));
        Assert.Equal(Esito.NonConforme, vm.RevisioneCorrente!.EsitoCalcolato);   // esito del test sempre per operazione
        Assert.True(vm.HaGruppiOperazioniStrumento);
    }

    [Fact]
    public async Task Persistenza_RoundTrip_ConservaStrumentoEFunzionalitaDelleOperazioniCreate()
    {
        var state = CreaStato();
        var vm = Apri(state, Test(new[] { "F1", "F2" }, new Condizione { Esito = Esito.DaEseguire, Nota = "legacy", Funzionalita = "F1", Componente = new EcuSelection(new[] { "ECM" }) }));
        vm.IniziaModificaCommand.AttendiModifica();
        typeof(TestCaseEditorViewModel).GetField("_sopprimiRitornoAllaLista", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(vm, true);
        Opt(vm, "UnitLogger").Selezionato = true;
        await vm.SalvaSicuroCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErroriValidazione);
        var riletto = state.TestCases.GetById("T-1")!;
        var ultima = riletto.Esecuzioni[^1];
        Assert.Equal(new string?[] { null, "UnitLogger", "UnitLogger" }, ultima.Condizioni.Select(c => c.Strumento).ToArray());
        Assert.Equal(new[] { "F1", "F1", "F2" }, ultima.Condizioni.Select(c => c.Funzionalita).ToArray());
        vm.Dispose();
    }
}
