using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Filtri dei campi di sistema nella Lista Test, come in Home e Report: un filtro per ogni campo usabile come filtro, opzioni dal
/// catalogo piu' i valori presenti senza distinzione di maiuscole, selezione multipla (OR dentro un campo, AND fra campi), un Test senza il
/// campo non corrisponde. I campi mostrati dipendono dal tipo di test scelto, e la selezione di un campo non pertinente resta in attesa.
/// Persistenza nello snapshot e nelle viste salvate. Percorso indicizzato (SQLite) e percorso in memoria danno gli stessi Test.</summary>
/// <remarks>[Collection("Localization")]: come TestCaseListFiltriMultiSelectTests, usa TextEditService.Handler statico.</remarks>
[Collection("Localization")]
public sealed class ListaTestFiltriCampiSistemaTests : IDisposable
{
    private const string VciChiave = "vci-connection";
    private const string FlottaChiave = "flotta";
    private const string NoteChiave = "note-interne";
    private const string UnitLogger = "UnitLogger di prova";
    private const string Standard = "Standard di prova";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ListaTestCampi_" + Guid.NewGuid().ToString("N"));
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "TestLoom_ListaTestCampiCfg_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, Task<string?>>? _textEditOriginale = TextEditService.Handler;

    public void Dispose()
    {
        TextEditService.Handler = _textEditOriginale;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_configDir, recursive: true); } catch { }
    }

    private AppState NuovaSessione() =>
        new(new AppConfigService(Path.Combine(_configDir, "config.json")), runBackgroundMaintenance: false);

    /// <summary>Master con operatore attivo, catalogo dei campi di sistema (VCI Connection, Lista, usabile come filtro; Flotta, Testo, usabile;
    /// Note interne, non usabile) e due tipi di test: <see cref="UnitLogger"/> usa il campo VCI, <see cref="Standard"/> usa il campo Flotta.</summary>
    private AppState CreaStato()
    {
        var state = NuovaSessione();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Mario Rossi", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Mario Rossi", "1234");

        var campi = state.Master.CampiSistemaDettagli.Load();
        campi.Add(Campo(VciChiave, "VCI Connection", TipoCampoStrumento.Lista, usabileComeFiltro: true, "VCI USB", "VCI LAN", "VCI Wi-Fi", "VCI Bluetooth"));
        campi.Add(Campo(FlottaChiave, "Flotta", TipoCampoStrumento.Testo, usabileComeFiltro: true));
        campi.Add(Campo(NoteChiave, "Note interne", TipoCampoStrumento.Testo, usabileComeFiltro: false));
        state.Master.CampiSistemaDettagli.Save(campi);

        // Si aggiungono ai template gia' seminati (la cartella nuova ne crea di predefiniti, con chiave business propria).
        var modelli = state.Master.TestTemplate.Load();
        modelli.Add(new TestTemplate { Nome = UnitLogger, CampiPianificazione = { CampoDiSistema(VciChiave) } });
        modelli.Add(new TestTemplate { Nome = Standard, Sezioni = { new SezioneTemplate { Campi = { CampoDiSistema(FlottaChiave) } } } });
        state.Master.TestTemplate.Save(modelli);
        return state;
    }

    private static CampoSistemaDefinizione Campo(string chiave, string etichetta, TipoCampoStrumento tipo, bool usabileComeFiltro, params string[] opzioni) =>
        new()
        {
            Chiave = chiave, Etichetta = etichetta, Gruppo = GruppoCampoSistema.Dettagli, Tipo = tipo,
            OpzioniLista = opzioni.ToList(), UsabileComeFiltro = usabileComeFiltro, Ruolo = RuoloCampoSistema.Nessuno,
        };

    private static CampoTemplate CampoDiSistema(string chiave) => new() { Tipo = TipoCampoTemplate.CampoSistema, CampoSistemaChiave = chiave };

    private static Guid Uid(AppState state, string nomeTemplate) =>
        state.Master.TestTemplate.Load().Single(t => t.Nome == nomeTemplate).Uid;

    /// <summary>Test con i valori dei campi di sistema indicati (null = campo senza valore) e, se indicato, il tipo di test.</summary>
    private static void SalvaTest(AppState state, string id, string? tipoTest = null, string? vci = null, string? flotta = null, string? note = null)
    {
        var tc = new TestCase
        {
            Id = id, Componente = "BCM", Assegnatario = "Mario Rossi", CreatoDa = "Mario Rossi",
            TestTemplateUid = tipoTest == null ? Guid.Empty : Uid(state, tipoTest),
        };
        if (vci != null) tc.CampiCustom[VciChiave] = vci;
        if (flotta != null) tc.CampiCustom[FlottaChiave] = flotta;
        if (note != null) tc.CampiCustom[NoteChiave] = note;
        tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Operatore = "Mario Rossi", Data = DateTime.Now, Esito = Esito.Conforme });
        state.TestCases.Save(tc, isNew: true);
    }

    private static string[] Ids(TestCaseListViewModel vm) => vm.Righe.Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>Chiavi dei filtri mostrati, ordinate: l'ordine dei campi e' quello del catalogo (come Home e Report), qui non rilevante.</summary>
    private static string[] Chiavi(TestCaseListViewModel vm) =>
        vm.FiltriCampiCustomLista.Select(f => f.Chiave).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static FiltroCampoCustom FiltroCampo(TestCaseListViewModel vm, string chiave) => vm.FiltriCampiCustomLista.Single(f => f.Chiave == chiave);

    private static void Deseleziona(FiltroMultiSelect filtro, string valore) => filtro.Opzioni.Single(o => o.Valore == valore).Selezionato = false;

    private static void DeselezionaTutti(TestCaseListViewModel vm)
    {
        foreach (var campo in vm.FiltriCampiCustomLista) campo.Filtro.DeselezionaTutto();
    }

    private static void ImpostaArchiviataCampo(AppState state, string chiave, bool archiviata)
    {
        var campi = state.Master.CampiSistemaDettagli.Load();
        campi.Single(c => c.Chiave == chiave).Archiviata = archiviata;
        state.Master.CampiSistemaDettagli.Save(campi);
    }

    private static Dictionary<string, string> Valori(params string[] coppie)
    {
        var valori = new Dictionary<string, string>();
        for (var i = 0; i < coppie.Length; i += 2) valori[coppie[i]] = coppie[i + 1];
        return valori;
    }

    [Fact]
    public void ListaTest_CampiSistema_UnFiltroPerCampoUsabile_OpzioniDalCatalogoEValoriPresenti()
    {
        var state = CreaStato();
        SalvaTest(state, "UnitLogger-A", UnitLogger,vci: "VCI USB");
        SalvaTest(state, "UnitLogger-C", UnitLogger,vci: "vci usb");                              // grafia diversa dall'opzione del catalogo
        SalvaTest(state, "UnitLogger-G", UnitLogger,vci: "Zigbee", flotta: "Gamma", note: "x");     // valore presente che non e' fra le opzioni
        SalvaTest(state, "STD-H", Standard,flotta: "Alpha");
        Assert.True(state.TestCases.HasPersistentSnapshot);

        var vm = new TestCaseListViewModel(state, _ => { });

        // Solo i campi usabili come filtro, nell'ordine del catalogo: il campo Note non compare.
        Assert.Equal(new[] { FlottaChiave, VciChiave }, Chiavi(vm));
        Assert.Equal("VCI Connection", FiltroCampo(vm, VciChiave).Etichetta);
        // Prima le opzioni del catalogo, poi i valori dei Test non gia' tra quelle; "vci usb" e' la voce "VCI USB" del catalogo.
        Assert.Equal(new[] { "VCI USB", "VCI LAN", "VCI Wi-Fi", "VCI Bluetooth", "Zigbee" },
            FiltroCampo(vm, VciChiave).Filtro.Opzioni.Select(o => o.Valore));
        // Un campo senza opzioni nel catalogo offre solo i valori presenti.
        Assert.Equal(new[] { "Alpha", "Gamma" }, FiltroCampo(vm, FlottaChiave).Filtro.Opzioni.Select(o => o.Valore));
    }

    [Fact]
    public void ListaTest_CampiSistema_SelezioneMultipla_OrNelCampoAndFraCampi_SenzaMaiuscole()
    {
        var state = CreaStato();
        SalvaTest(state, "UnitLogger-A", UnitLogger,vci: "VCI USB", flotta: "Alpha");
        SalvaTest(state, "UnitLogger-B", UnitLogger,vci: "VCI LAN", flotta: "Beta");
        SalvaTest(state, "UnitLogger-C", UnitLogger,vci: "vci usb", flotta: "Beta");
        SalvaTest(state, "UnitLogger-D", UnitLogger,vci: "VCI Bluetooth");
        SalvaTest(state, "UnitLogger-E", UnitLogger,flotta: "Alpha");     // senza il campo VCI
        SalvaTest(state, "STD-F", Standard,flotta: "Alpha"); // senza il campo VCI
        var vm = new TestCaseListViewModel(state, _ => { });

        // Stesso scenario sul percorso indicizzato (SQLite) e su quello in memoria: stessi Test, stesso ordine.
        foreach (var indicizzato in new[] { true, false })
        {
            vm.PercorsoIndicizzatoAttivo = indicizzato;
            DeselezionaTutti(vm);

            // Piu' valori dello stesso campo sono alternativi (OR); "vci usb" corrisponde a "VCI USB" senza distinzione di maiuscole.
            FiltroCampo(vm, VciChiave).Filtro.Seleziona("VCI USB", "VCI LAN");
            Assert.Equal(new[] { "UnitLogger-A", "UnitLogger-B", "UnitLogger-C" }, Ids(vm));
            Assert.True(vm.HaFiltriAttivi);
            Assert.Contains(vm.ChipFiltriAttivi, chip => chip.Etichetta.StartsWith("VCI Connection:", StringComparison.Ordinal));

            // Campi diversi sono in AND.
            FiltroCampo(vm, FlottaChiave).Filtro.Seleziona("Beta");
            Assert.Equal(new[] { "UnitLogger-B", "UnitLogger-C" }, Ids(vm));
            FiltroCampo(vm, FlottaChiave).Filtro.DeselezionaTutto();
            Assert.Equal(new[] { "UnitLogger-A", "UnitLogger-B", "UnitLogger-C" }, Ids(vm));

            // Un valore che nessun Test ha: nessun risultato, senza errori.
            FiltroCampo(vm, VciChiave).Filtro.DeselezionaTutto();
            FiltroCampo(vm, VciChiave).Filtro.Seleziona("VCI Wi-Fi");
            Assert.Empty(Ids(vm));

            // Un Test senza il campo non corrisponde mai quando un valore e' scelto (UnitLogger-E e STD-F non hanno VCI).
            FiltroCampo(vm, VciChiave).Filtro.DeselezionaTutto();
            FiltroCampo(vm, VciChiave).Filtro.Seleziona("VCI Bluetooth");
            Assert.Equal(new[] { "UnitLogger-D" }, Ids(vm));
            Assert.DoesNotContain("UnitLogger-E", Ids(vm));
            Assert.DoesNotContain("STD-F", Ids(vm));
        }
        vm.PercorsoIndicizzatoAttivo = true;
    }

    [Fact]
    public void ListaTest_CampiSistema_PertinentiAlTipoDiTest_SelezioneNonPertinenteRestaInAttesa()
    {
        var state = CreaStato();
        SalvaTest(state, "UnitLogger-A", UnitLogger,vci: "VCI USB");
        SalvaTest(state, "UnitLogger-B", UnitLogger,vci: "VCI LAN");
        SalvaTest(state, "UnitLogger-N", UnitLogger);
        SalvaTest(state, "STD-F", Standard,flotta: "Alpha");
        SalvaTest(state, "STD-G", Standard,flotta: "Beta");
        var vm = new TestCaseListViewModel(state, _ => { });
        var unitlogger = Uid(state, UnitLogger).ToString();
        var standard = Uid(state, Standard).ToString();

        // Nessun tipo scelto: tutti i campi usabili come filtro.
        Assert.Equal(new[] { FlottaChiave, VciChiave }, Chiavi(vm));
        Assert.Equal(5, vm.Righe.Count);

        // Tipo UnitLogger: compare solo il campo VCI, che i suoi template usano.
        vm.FiltroTemplate.Seleziona(unitlogger);
        Assert.Equal(new[] { VciChiave }, Chiavi(vm));
        Assert.Equal(new[] { "UnitLogger-A", "UnitLogger-B", "UnitLogger-N" }, Ids(vm));
        FiltroCampo(vm, VciChiave).Filtro.Seleziona("VCI USB");
        Assert.Equal(new[] { "UnitLogger-A" }, Ids(vm));

        // UnitLogger e Standard: l'unione dei campi; la selezione VCI resta e filtra ancora.
        vm.FiltroTemplate.Seleziona(standard);
        Assert.Equal(new[] { FlottaChiave, VciChiave }, Chiavi(vm));
        Assert.Equal(new[] { "UnitLogger-A" }, Ids(vm));

        // Solo Standard: il campo VCI non e' pertinente, quindi non compare e non filtra; la sua selezione resta in attesa.
        Deseleziona(vm.FiltroTemplate, unitlogger);
        Assert.Equal(new[] { FlottaChiave }, Chiavi(vm));
        Assert.Equal(new[] { "STD-F", "STD-G" }, Ids(vm));

        // Di nuovo UnitLogger: il campo VCI torna con la selezione di prima.
        vm.FiltroTemplate.Seleziona(unitlogger);
        Assert.Equal(new[] { "VCI USB" }, FiltroCampo(vm, VciChiave).Filtro.Selezionati);
        Assert.Equal(new[] { "UnitLogger-A" }, Ids(vm));

        // Nessun tipo: di nuovo tutti i campi, con la selezione VCI ancora attiva.
        Deseleziona(vm.FiltroTemplate, unitlogger);
        Deseleziona(vm.FiltroTemplate, standard);
        Assert.Equal(new[] { FlottaChiave, VciChiave }, Chiavi(vm));
        Assert.Equal(new[] { "UnitLogger-A" }, Ids(vm));
    }

    [Fact]
    public async Task ListaTest_CampiSistema_SelezioniSopravvivonoAlRiavvioEAlleViste()
    {
        var sessione1 = CreaStato();
        SalvaTest(sessione1, "UnitLogger-A", UnitLogger,vci: "VCI USB", flotta: "Alpha");
        SalvaTest(sessione1, "UnitLogger-B", UnitLogger,vci: "VCI LAN", flotta: "Beta");
        SalvaTest(sessione1, "STD-F", Standard,flotta: "Beta");
        var lista1 = new TestCaseListViewModel(sessione1, _ => { });
        FiltroCampo(lista1, VciChiave).Filtro.Seleziona("VCI LAN");
        FiltroCampo(lista1, FlottaChiave).Filtro.Seleziona("Beta");
        Assert.Equal(new[] { "UnitLogger-B" }, Ids(lista1));

        // Vista salvata con nome: le selezioni dei campi di sistema entrano nei valori, come gli altri filtri.
        TextEditService.Handler = (_, _) => Task.FromResult<string?>("Vista CS");
        await lista1.SalvaVistaCorrenteCommand.ExecuteAsync(null);
        var vista = lista1.VisteSalvate.Single(v => v.Nome == "Vista CS");
        Assert.Equal("VCI LAN", vista.Valori["CampoSistema." + VciChiave]);
        Assert.Equal("Beta", vista.Valori["CampoSistema." + FlottaChiave]);
        sessione1.Dispose();

        // Riavvio: lo snapshot automatico ripristina le selezioni.
        var sessione2 = NuovaSessione();
        sessione2.InizializzaCartella(_dir);
        var lista2 = new TestCaseListViewModel(sessione2, _ => { });
        Assert.Equal(new[] { "UnitLogger-B" }, Ids(lista2));
        Assert.Equal(new[] { "VCI LAN" }, FiltroCampo(lista2, VciChiave).Filtro.Selezionati);

        // Reset, poi la vista salvata riapplica le stesse selezioni.
        lista2.ReimpostaFiltriCommand.Execute(null);
        Assert.Equal(3, lista2.Righe.Count);
        lista2.VistaSelezionata = lista2.VisteSalvate.Single(v => v.Nome == "Vista CS");
        Assert.Equal(new[] { "UnitLogger-B" }, Ids(lista2));
    }

    [Fact]
    public void ListaTest_CampiSistema_CampoArchiviato_LaSelezioneTornaQuandoIlCampoERiattivato()
    {
        var state = CreaStato();
        SalvaTest(state, "UnitLogger-A", UnitLogger,vci: "VCI USB");
        SalvaTest(state, "UnitLogger-B", UnitLogger,vci: "VCI LAN");
        SalvaTest(state, "STD-F", Standard,flotta: "Alpha");
        var vm = new TestCaseListViewModel(state, _ => { });
        FiltroCampo(vm, VciChiave).Filtro.Seleziona("VCI USB");
        Assert.Equal(new[] { "UnitLogger-A" }, Ids(vm));

        // Campo archiviato nel catalogo: scompare dai filtri e non filtra, ma la selezione non si perde.
        ImpostaArchiviataCampo(state, VciChiave, archiviata: true);
        vm.Aggiorna();
        Assert.Equal(new[] { FlottaChiave }, Chiavi(vm));
        Assert.Equal(3, vm.Righe.Count);

        ImpostaArchiviataCampo(state, VciChiave, archiviata: false);
        vm.Aggiorna();
        Assert.Equal(new[] { "VCI USB" }, FiltroCampo(vm, VciChiave).Filtro.Selezionati);
        Assert.Equal(new[] { "UnitLogger-A" }, Ids(vm));
    }

    [Fact]
    public void ListaTest_CampiSistema_ReimpostaFiltri_AzzeraAncheLeSelezioniNonPertinenti()
    {
        var state = CreaStato();
        SalvaTest(state, "UnitLogger-A", UnitLogger,vci: "VCI USB");
        SalvaTest(state, "UnitLogger-B", UnitLogger,vci: "VCI LAN");
        SalvaTest(state, "STD-F", Standard,flotta: "Alpha");
        var vm = new TestCaseListViewModel(state, _ => { });
        var unitlogger = Uid(state, UnitLogger).ToString();
        var standard = Uid(state, Standard).ToString();
        vm.FiltroTemplate.Seleziona(unitlogger);
        FiltroCampo(vm, VciChiave).Filtro.Seleziona("VCI USB");
        // Tipo Standard: il campo VCI non e' pertinente, quindi non filtra (STD-F non ha VCI e resta).
        Deseleziona(vm.FiltroTemplate, unitlogger);
        vm.FiltroTemplate.Seleziona(standard);
        Assert.Equal(new[] { "STD-F" }, Ids(vm));

        vm.ReimpostaFiltriCommand.Execute(null);
        vm.FiltroTemplate.Seleziona(unitlogger);
        Assert.Equal(new[] { "UnitLogger-A", "UnitLogger-B" }, Ids(vm)); // nessuna selezione VCI residua, anche quella non pertinente
    }

    [Fact]
    public void ListaTest_CampiSistema_ValoriDalIndice_SiRileggonoDopoUnaScrittura()
    {
        var state = CreaStato();
        SalvaTest(state, "UnitLogger-A", UnitLogger,vci: "VCI USB");
        Assert.True(state.TestCases.HasPersistentSnapshot);
        var indice = new LocalTestArchiveQueryService(state.TestCases.LocalIndexDatabasePath!, state.TestCases.IndexRecovery);

        Assert.Equal(new[] { "VCI USB" }, indice.GetCustomFieldValues(VciChiave));
        Assert.Equal(new[] { "VCI USB" }, indice.GetCustomFieldValues(VciChiave)); // rivisita a indice invariato: dalla cache

        SalvaTest(state, "UnitLogger-B", UnitLogger,vci: "VCI Wi-Fi");
        Assert.Equal(new[] { "VCI USB", "VCI Wi-Fi" }, indice.GetCustomFieldValues(VciChiave)); // la scrittura invalida la cache
    }

    [Fact]
    public void ListaTest_CampiSistema_CriterioPuro_OrNelCampoAndFraCampiSenzaMaiuscole()
    {
        var selezioni = new Dictionary<string, IReadOnlyCollection<string>>
        {
            [VciChiave] = new[] { "VCI USB", "VCI LAN" },
            [FlottaChiave] = new[] { "Alpha" },
        };

        Assert.True(CampoSistemaCatalogoService.CorrispondeCampiCustom(selezioni, Valori(VciChiave, "vci usb", FlottaChiave, "ALPHA")));
        Assert.False(CampoSistemaCatalogoService.CorrispondeCampiCustom(selezioni, Valori(VciChiave, "VCI Bluetooth", FlottaChiave, "Alpha")));
        Assert.False(CampoSistemaCatalogoService.CorrispondeCampiCustom(selezioni, Valori(VciChiave, "VCI LAN"))); // senza il campo Flotta
        Assert.False(CampoSistemaCatalogoService.CorrispondeCampiCustom(selezioni, Valori(FlottaChiave, "Alpha"))); // senza il campo VCI
        // Un campo senza valori scelti non filtra.
        Assert.True(CampoSistemaCatalogoService.CorrispondeCampiCustom(
            new Dictionary<string, IReadOnlyCollection<string>> { [VciChiave] = Array.Empty<string>() }, Valori()));
    }

    [Fact]
    public void ListaTest_CampiSistema_ChiaviDeiTemplate_LeggonoPianificazioneSezioniERiferimenti()
    {
        var template = new TestTemplate
        {
            Nome = "Tutti gli elenchi",
            CampiPianificazione = { CampoDiSistema("pianificazione"), new CampoTemplate { Tipo = TipoCampoTemplate.CampoLibero, CampoSistemaChiave = "non-di-sistema" } },
            Sezioni = { new SezioneTemplate { Campi = { CampoDiSistema("sezione") } } },
            CampiRiferimenti = { CampoDiSistema("riferimenti"), CampoDiSistema(" ") },
        };

        var chiavi = CampoSistemaCatalogoService.ChiaviCampiSistemaDeiTemplate(new[] { template });

        Assert.Equal(new[] { "pianificazione", "riferimenti", "sezione" }, chiavi.OrderBy(k => k, StringComparer.Ordinal));
    }
}
